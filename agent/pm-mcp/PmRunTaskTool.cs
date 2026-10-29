using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalCodeAgent.Core;
using Microsoft.Extensions.AI;

namespace pm_mcp;

/// <summary>
/// Tool MCP opzionale "agente come tool": avvia pm-code.exe in modalità --stdin-protocol
/// (lo stesso protocollo NDJSON già usato dall'estensione VS Code) come processo figlio,
/// gli invia un'unica richiesta e restituisce la risposta finale del loop ReAct del core.
/// Non importa né duplica RunAgentLoopAsync (è una funzione locale di Program.cs, non
/// riutilizzabile come libreria): lo riusa per intero rilanciando l'eseguibile esistente,
/// così il loop, i guard anti-loop e la verifica funzionale restano quelli reali del core.
/// Utile per confrontare "tool uno per uno" (gli altri tool MCP) con "agente come tool" (questo).
///
/// Il figlio, quando propone una scrittura (edit_file/write_file), emette un evento
/// "edit_proposal" e blocca (fino a 5 minuti, timeout interno di pm-code) in attesa di una
/// riga "edit_decision" su stdin — è un protocollo SINCRONO per turno, senza nozione di
/// "conferma differita". Questo tool deve quindi rispondere SEMPRE subito, per coerenza con
/// PM_MCP_WRITE_MODE: readonly → rifiuto immediato; direct → accetto immediato; twostep →
/// rifiuto immediato ma la proposta diventa un token applicabile con apply_edit (il turno del
/// figlio prosegue vedendo l'edit come rifiutato: è un limite del protocollo del figlio, non di
/// questo tool — va descritto all'utente finale di pm_run_task).
/// </summary>
public sealed class PmRunTaskTool : AIFunction
{
    private readonly string _pmCodeExePath;
    private readonly string _workspacePath;
    private readonly WriteGate _gate;
    private readonly WorkspaceContext _workspace;
    private readonly JsonElement _schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            prompt = new { type = "string", description = "Richiesta in linguaggio naturale per l'agente pm-code completo (loop ReAct, tutti i tool, guard anti-loop)." }
        },
        required = new[] { "prompt" }
    });

    public PmRunTaskTool(string pmCodeExePath, string workspacePath, WriteGate gate, WorkspaceContext workspace)
    {
        _pmCodeExePath = pmCodeExePath;
        _workspacePath = workspacePath;
        _gate = gate;
        _workspace = workspace;
    }

    public override string Name => "pm_run_task";

    public override string Description =>
        "Delega un task completo all'agente pm-code (loop ReAct con tutti i suoi tool interni, " +
        "guard anti-loop, verifica funzionale obbligatoria e piano persistito), invece di orchestrare " +
        "i singoli tool MCP uno per uno. Più lento (avvia un processo e attende l'intero turno) ma " +
        "replica esattamente il comportamento dell'agente oggi integrato in VS Code.";

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("prompt", out var promptObj))
            return "ERRORE: parametro 'prompt' mancante.";

        var prompt = promptObj switch
        {
            JsonElement je => je.GetString() ?? "",
            string s => s,
            _ => promptObj?.ToString() ?? ""
        };

        if (!File.Exists(_pmCodeExePath))
            return $"ERRORE: eseguibile pm-code non trovato in '{_pmCodeExePath}'. " +
                   "Configura PM_CODE_EXE_PATH (env var) con il percorso di pm-code.exe pubblicato/buildato.";

        var startInfo = new ProcessStartInfo
        {
            FileName = _pmCodeExePath,
            WorkingDirectory = Path.GetDirectoryName(_pmCodeExePath),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add("--stdin-protocol");
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(_workspacePath);

        using var proc = new Process { StartInfo = startInfo };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginErrorReadLine();

        var requestJson = JsonSerializer.Serialize(new { type = "request", prompt });
        await proc.StandardInput.WriteLineAsync(requestJson);
        await proc.StandardInput.FlushAsync();

        var tokens = new StringBuilder();
        var toolCalls = new List<string>();
        var pendingWriteTokens = new List<string>();
        var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(linked.Token);
                if (line == null) break; // processo terminato
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonElement evt;
                try { evt = JsonSerializer.Deserialize<JsonElement>(line); }
                catch { continue; }

                var type = evt.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "token":
                        if (evt.TryGetProperty("text", out var tx)) tokens.Append(tx.GetString());
                        break;
                    case "tool_call":
                        if (evt.TryGetProperty("tool", out var tn)) toolCalls.Add(tn.GetString() ?? "?");
                        break;
                    case "edit_proposal":
                        await HandleEditProposalAsync(evt, proc, pendingWriteTokens, linked.Token);
                        break;
                    case "done":
                        goto Finished;
                }
            }
        Finished: ;
        }
        catch (OperationCanceledException)
        {
            return "ERRORE: pm_run_task ha superato il timeout (10 minuti) o è stato annullato.";
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* già uscito */ }
        }

        var result = tokens.ToString().Trim();
        var toolsNote = toolCalls.Count > 0 ? $"\n\n[tool usati dal core: {string.Join(", ", toolCalls)}]" : "";
        var writeNote = pendingWriteTokens.Count > 0
            ? "\n\n[scritture proposte dal figlio durante questo task, NON applicate — usa apply_edit per ognuna:\n"
              + string.Join("\n", pendingWriteTokens) + "\n" +
              "NOTA: il protocollo di pm-code è sincrono per turno, quindi il figlio ha visto ciascuna di queste " +
              "proposte come rifiutata e ha continuato il suo turno di conseguenza (può aver riprovato, aggirato " +
              "l'edit, o segnalato l'errore nella risposta finale) — questo è un limite del protocollo del figlio, " +
              "non di questo tool.]"
            : "";
        if (result.Length == 0 && stderr.Length > 0)
            return $"ERRORE: nessuna risposta testuale da pm-code. stderr:\n{stderr}";

        return result + toolsNote + writeNote;
    }

    /// <summary>
    /// Risponde SEMPRE subito al figlio (mai lasciarlo bloccato) secondo PM_MCP_WRITE_MODE:
    /// readonly → rifiuto; direct → accetto; twostep → rifiuto, ma la proposta diventa un
    /// token applicabile con apply_edit (vedi commento sulla classe).
    /// </summary>
    private async Task HandleEditProposalAsync(JsonElement evt, Process proc, List<string> pendingWriteTokens, CancellationToken ct)
    {
        var path = evt.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        var content = evt.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

        bool accepted;
        switch (_gate.Mode)
        {
            case WriteMode.Direct:
                accepted = true;
                break;
            case WriteMode.TwoStep:
                accepted = false;
                try
                {
                    var absPath = _workspace.Resolve(path);
                    var hash = WriteGate.HashOf(absPath);
                    var token = _gate.RegisterProposal(new PendingEdit("", "write_content", path, null, absPath, null, content, hash, default));
                    pendingWriteTokens.Add($"{path} -> {token}");
                }
                catch { /* path non valido/fuori sandbox: rifiuta senza creare un token */ }
                break;
            default: // ReadOnly
                accepted = false;
                break;
        }

        var decisionJson = JsonSerializer.Serialize(new { type = "edit_decision", accepted });
        await proc.StandardInput.WriteLineAsync(decisionJson.AsMemory(), ct);
        await proc.StandardInput.FlushAsync(ct);
    }
}
