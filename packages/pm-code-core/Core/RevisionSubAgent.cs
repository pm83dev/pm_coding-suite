using System.Diagnostics;
using System.Text.Json;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Core;

/// <summary>
/// Sotto-agente di REVISIONE/COORDINAMENTO (tool opzionale "ask_revision"). Delega a Claude, tramite il
/// CLI di Claude Code in modalità non interattiva (<c>claude -p</c>) con il login dell'utente — nessuna
/// chiave API — la revisione di un piano, di una modifica o di una decisione su un lavoro complesso.
/// Diverso da <see cref="ConsultSubAgent"/> (ask_second_opinion, modello locale, per sbloccarsi): qui si
/// chiede un giudizio esterno su cosa si sta per fare/si è fatto. In SOLA LETTURA: il CLI può usare solo
/// Read/Grep/Glob nel workspace (niente shell, scrittura, web) e non può leggere file di segreti. Il tool
/// viene esposto al modello solo se <see cref="RevisionSettings.Enabled"/>.
/// </summary>
public class RevisionSubAgent(WorkspaceContext workspace, RevisionSettings settings)
{
    public const string ToolName = "ask_revision";

    private static readonly string[] DefaultDenyRead =
    [
        "**/appsettings*.json", "**/.env", "**/.env.*", "**/*.pem", "**/*.pfx", "**/*.key", "**/id_rsa*",
    ];

    public static ToolDefinition Definition => new()
    {
        Function = new()
        {
            Name = ToolName,
            Description = "Chiedi a Claude (revisore esterno, più capace di te) di rivedere un PIANO, una MODIFICA già fatta o una " +
                          "DECISIONE su un lavoro complesso. Usalo con parsimonia: PRIMA di iniziare un lavoro grande e ambiguo (revisione " +
                          "del piano) o DOPO modifiche rilevanti prima di dichiarare il lavoro finito (revisione del risultato). Non usarlo " +
                          "per problemi piccoli, e se sei solo bloccato su un errore usa ask_second_opinion. Può LEGGERE il codice del " +
                          "workspace ma non modificare nulla né eseguire comandi, e non vede la tua cronologia: descrivi tutto nel parametro.\n\n" +
                          "Struttura 'request' in queste sezioni (solo quelle che hai davvero):\n" +
                          "GOAL: cosa vuole l'utente.\n" +
                          "PLAN o CHANGES: il piano a passi, oppure i file toccati + il diff/riassunto delle modifiche (incollane i punti chiave).\n" +
                          "CONSTRAINTS: vincoli noti.\n" +
                          "RISKS/DOUBTS: cosa ti preoccupa o non sei sicuro.\n" +
                          "QUESTION: cosa vuoi che verifichi.\n\n" +
                          "La risposta ha un VERDICT e punti da correggere: trattala come un parere da verificare, non come un ordine.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    request = new { type = "string", description = "Briefing strutturato: GOAL / PLAN o CHANGES / CONSTRAINTS / RISKS / QUESTION. Includi i punti chiave del diff, non l'intera cronologia." }
                },
                required = new[] { "request" }
            }
        }
    };

    /// <summary>Argomenti di <c>claude -p</c>: sola lettura (Read/Grep/Glob), niente shell/scrittura/web, nessuna sessione salvata.</summary>
    public static List<string> BuildCliArgs(RevisionSettings s)
    {
        var deny = new List<string> { "Bash", "Edit", "Write", "NotebookEdit", "WebFetch", "WebSearch" };
        deny.AddRange(DefaultDenyRead.Concat(s.DenyRead).Select(p => $"Read({p})"));
        var args = new List<string>
        {
            "-p", "--output-format", "json", "--no-session-persistence",
            "--max-turns", Math.Max(1, s.MaxTurns).ToString(),
            "--allowedTools", "Read,Grep,Glob",
            "--disallowedTools", string.Join(",", deny),
        };
        if (!string.IsNullOrWhiteSpace(s.Model)) { args.Add("--model"); args.Add(s.Model!.Trim()); }
        return args;
    }

    /// <summary>Estrae il testo dalla risposta JSON del CLI; null + <paramref name="error"/> se è un errore o è vuota.</summary>
    public static string? ParseCliOutput(string stdout, out string? error, out string meta)
    {
        error = null; meta = "";
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
            var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            var turns = root.TryGetProperty("num_turns", out var nt) ? nt.ToString() : "?";
            var cost = root.TryGetProperty("total_cost_usd", out var c) ? c.ToString() : "?";
            meta = $"turns={turns} costUsd={cost}";
            if (isError || string.IsNullOrWhiteSpace(result))
            {
                var sub = root.TryGetProperty("subtype", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : "unknown";
                error = isError ? $"il CLI ha segnalato un errore ({sub}: {result})" : $"risposta vuota dal CLI ({sub})";
                return null;
            }
            return result;
        }
        catch (JsonException ex)
        {
            error = $"output del CLI non valido ({ex.Message}): {stdout[..Math.Min(200, stdout.Length)]}";
            return null;
        }
    }

    public async Task<string> RunAsync(string argumentsJson, Action<string>? onStatus = null, CancellationToken ct = default)
    {
        string request;
        try
        {
            var el = JsonDocument.Parse(argumentsJson).RootElement;
            request = el.TryGetProperty("request", out var p) ? p.GetString()?.Trim() ?? "" : "";
        }
        catch { request = ""; }
        if (request.Length == 0) return "ERRORE: 'request' vuoto.";

        var sw = Stopwatch.StartNew();
        onStatus?.Invoke("revisione (Claude CLI)…");

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workspace.Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        // Su Windows il CLI installato da npm è un .cmd: va lanciato tramite cmd.exe. Gli argomenti sono
        // privi di spazi (il prompt passa da stdin), quindi il quoting di cmd non è un problema.
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(settings.ClaudeCliPath);
        }
        else psi.FileName = settings.ClaudeCliPath;
        foreach (var a in BuildCliArgs(settings)) psi.ArgumentList.Add(a);

        // Con ANTHROPIC_API_KEY nell'ambiente il CLI fatturerebbe a consumo via API: la togliamo, così usa
        // SOLO il login dell'utente (abbonamento).
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment.Remove("ANTHROPIC_AUTH_TOKEN");

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex)
        {
            LogOutcome("errore", sw.ElapsedMilliseconds, 0);
            return Unavailable($"Claude CLI non avviabile: {ex.Message}");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            await proc.StandardInput.WriteAsync(SystemPrompt + "\n\n=== BRIEFING ===\n" + request);
            proc.StandardInput.Close();
        }
        catch (IOException) { /* il processo è già uscito: lo si vede dall'exit code */ }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* già terminato */ }
            if (ct.IsCancellationRequested) throw; // cancel reale del turno: il processo muore e l'eccezione propaga
            LogOutcome("timeout", sw.ElapsedMilliseconds, 0);
            return Unavailable("timeout");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var text = ParseCliOutput(stdout, out var error, out var meta);
        if (text == null)
        {
            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(error))
                error = $"exit code {proc.ExitCode}: {stderr[..Math.Min(300, stderr.Length)]}";
            error ??= $"exit code {proc.ExitCode}";
            LogOutcome("errore", sw.ElapsedMilliseconds, 0);
            return Unavailable($"errore: {error}");
        }

        text = text.Trim();
        if (text.Length > settings.MaxAnswerChars) text = text[..settings.MaxAnswerChars] + "…(troncato)";
        Console.Error.WriteLine($"[REVISION] {meta}");
        LogOutcome("ok", sw.ElapsedMilliseconds, text.Length);
        return "[REVISION] " + text;
    }

    private static string Unavailable(string reason) =>
        $"[REVISION] non disponibile ({reason}). Procedi con il tuo giudizio o chiedi all'utente.";

    private static void LogOutcome(string outcome, long durationMs, int answerLength) =>
        Console.Error.WriteLine($"[REVISION] provider=claude-cli outcome={outcome} durationMs={durationMs} answerLen={answerLength}");

    private string SystemPrompt => $"""
        You are an external senior reviewer and technical lead. A smaller local coding agent asks you to review
        its PLAN, its CHANGES or a DECISION on a complex task. You did not take part in its work: verify claims
        in the code instead of trusting them.
        Workspace (current directory): {workspace.Root}
        You may ONLY read code (Read, Grep, Glob). You cannot modify files or run commands. Never read secrets.

        The briefing may contain GOAL, PLAN or CHANGES, CONSTRAINTS, RISKS/DOUBTS, QUESTION.
        Check: does the plan/change actually achieve the GOAL? Missing steps, wrong order, risky or irreversible
        actions, regressions, edge cases, tests not covered. Be concrete (file:line when you can) and concise.
        Do not agree to be polite; do not invent APIs, files or versions; mark uncertainty explicitly.

        Answer format (final text only):
        VERDICT: approve | approve-with-changes | reject
        ISSUES (most severe first): <numbered, each with what, where, why>
        SUGGESTED NEXT STEPS: <ordered, small, actionable for a smaller model>
        VERIFY: <how to confirm the result before declaring it done>
        CONFIDENCE: low|medium|high
        """;
}
