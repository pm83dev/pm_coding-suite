using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalCodeAgent.Core;

/// <summary>
/// Trace STRUTTURATO (NDJSON, un file per sessione/conversazione) — ADDITIVO: osserva soltanto,
/// non decide nulla e non cambia il loop dell'agente. Coesiste con il vecchio
/// <c>pm-code-trace.log</c> (invariato, vedi Program.cs) e con le modalità --stdin-protocol e REPL
/// (attivo in entrambe, a differenza del vecchio trace file che scrive solo in --stdin-protocol).
///
/// Config da variabili d'ambiente (nessuna in appsettings.json: va letta prima che qualunque
/// altra cosa dell'agente si avvii, e non deve richiedere modifiche al file di configurazione
/// esistente):
///   PM_TRACE=off          disattiva completamente (default: attivo)
///   PM_TRACE_DIR=<path>   cartella dei file .ndjson (default: "traces" accanto all'eseguibile)
///   PM_TRACE_MAX_FILES=N  conserva solo gli ultimi N file (default 200)
///   PM_TRACE_MAX_MB=X     se impostato, in aggiunta cancella i file più vecchi finché il totale
///                         della cartella non sta sotto X MB
///
/// Scrittura BUFFERIZZATA (non un thread dedicato: la prima versione lo usava con una coda
/// concorrente, ma c'era una finestra in cui un evento già "preso" dalla coda e non ancora
/// scritto poteva finire nel file della sessione SUCCESSIVA, aperta nel frattempo — osservato
/// nel test di verifica di questo stesso file, vedi cronologia). Ogni Emit() scrive subito nel
/// buffer dello StreamWriter (AutoFlush=false, Flush() esplicito dopo ogni riga: il volume è
/// basso — qualche evento per tool call/turno — quindi il costo è trascurabile e la riga è
/// sempre leggibile anche se il processo muore subito dopo). Qualunque eccezione (qui o nella
/// scrittura) viene ingoiata dopo UN SOLO avviso su stderr, e il trace si autodisattiva per il
/// resto del processo: il requisito esplicito è che il logging non deve MAI far fallire l'agente.
/// </summary>
public static class StructuredTrace
{
    private static bool _enabled;
    private static string _dir = "";
    private static int _maxFiles = 200;
    private static long _maxTotalBytes;
    private static bool _warned;

    private static readonly object _fileLock = new();
    private static StreamWriter? _writer;
    private static string _sessionId = "";
    private static int _turnCounter;
    private static int _currentTurnId;
    private static bool _shutdown;

    // ── Avvio/spegnimento ──────────────────────────────────────────────────────

    public static void Init()
    {
        var mode = Environment.GetEnvironmentVariable("PM_TRACE");
        _enabled = !string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase);
        if (!_enabled) return;

        var dir = Environment.GetEnvironmentVariable("PM_TRACE_DIR");
        _dir = string.IsNullOrWhiteSpace(dir) ? Path.Combine(AppContext.BaseDirectory, "traces") : dir;

        _maxFiles = int.TryParse(Environment.GetEnvironmentVariable("PM_TRACE_MAX_FILES"), out var mf) && mf > 0 ? mf : 200;
        _maxTotalBytes = long.TryParse(Environment.GetEnvironmentVariable("PM_TRACE_MAX_MB"), out var mb) && mb > 0 ? mb * 1024L * 1024L : 0;

        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex) { Disable($"impossibile creare la cartella trace '{_dir}': {ex.Message}"); return; }

        // Flush alla chiusura: sia un'uscita normale (fine del processo) sia Ctrl+C. Nessuno dei
        // due impedisce la terminazione (non si imposta e.Cancel): questo trace osserva, non governa.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        try { Console.CancelKeyPress += (_, _) => Shutdown(); }
        catch { /* stdin non è una vera console (pipe dell'estensione): nessun Ctrl+C possibile lì */ }
    }

    private static void Disable(string reason)
    {
        _enabled = false;
        WarnOnce(reason);
    }

    private static void WarnOnce(string reason)
    {
        if (_warned) return;
        _warned = true;
        try { Console.Error.WriteLine($"[pm-trace] disattivato: {reason}"); } catch { }
    }

    // Chiamata sia da ProcessExit sia (se la console lo consente) da CancelKeyPress: con
    // entrambi registrati, un Ctrl+C che termina il processo la invocherebbe due volte.
    // Idempotente apposta.
    public static void Shutdown()
    {
        if (!_enabled || _shutdown) return;
        _shutdown = true;
        try { lock (_fileLock) { EndSessionLocked("process_exit"); } }
        catch { /* mai propagare un errore di spegnimento: il processo sta già uscendo */ }
    }

    // ── Sessione ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Apre un nuovo file di sessione (chiudendo quello corrente, se c'è, con un evento
    /// session_end). Va chiamata una volta per ogni NUOVA conversazione: all'avvio del processo
    /// e a ogni reset esplicito (REPL: /reset, /cd; --stdin-protocol: nuova chat in VS Code).
    /// </summary>
    public static void StartSession(string workspace, string model, string endpoint, string coreVersion)
    {
        if (!_enabled) return;
        lock (_fileLock)
        {
            EndSessionLocked("new_session");

            _sessionId = Guid.NewGuid().ToString("N")[..8];
            _turnCounter = 0;
            _currentTurnId = 0;

            var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{_sessionId}.ndjson";
            var path = Path.Combine(_dir, fileName);
            try { _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = false }; }
            catch (Exception ex) { Disable($"impossibile creare il file di sessione '{path}': {ex.Message}"); return; }

            EnforceRotationLocked();

            WriteLocked("session_start", new()
            {
                ["workspace"] = Redact(workspace),
                ["model"] = Redact(model),
                ["endpoint"] = Redact(endpoint),
                ["coreVersion"] = coreVersion,
            });
        }
    }

    // _writer è già nullo se Init()/una StartSession precedente ha fallito: no-op, mai
    // un'eccezione propagata. Va chiamata SEMPRE sotto _fileLock (vedi StartSession/Shutdown).
    private static void EndSessionLocked(string reason)
    {
        if (_writer == null) return;
        WriteLocked("session_end", new() { ["reason"] = reason });
        try { _writer.Flush(); _writer.Dispose(); } catch { }
        _writer = null;
    }

    // ── Rotazione ─────────────────────────────────────────────────────────────

    private static void EnforceRotationLocked()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.ndjson")
                .OrderBy(f => f.Name) // nome inizia con yyyyMMdd_HHmmss: ordine lessicografico = ordine cronologico
                .ToList();

            while (files.Count > _maxFiles)
            {
                TryDelete(files[0]);
                files.RemoveAt(0);
            }

            if (_maxTotalBytes > 0)
            {
                var total = files.Sum(f => f.Length);
                var i = 0;
                while (total > _maxTotalBytes && i < files.Count)
                {
                    total -= files[i].Length;
                    TryDelete(files[i]);
                    i++;
                }
            }
        }
        catch (Exception ex) { WarnOnce($"pulizia vecchi file di trace fallita (non bloccante): {ex.Message}"); }

        static void TryDelete(FileInfo f) { try { f.Delete(); } catch { } }
    }

    // ── Eventi ────────────────────────────────────────────────────────────────

    public static int BeginTurn()
    {
        if (!_enabled) return 0;
        lock (_fileLock)
        {
            _currentTurnId = ++_turnCounter;
            WriteLocked("turn_start", new());
            return _currentTurnId;
        }
    }

    public static void EndTurn(string reason) => Emit("turn_end", new() { ["reason"] = reason });

    public static void UserMessage(string text) => Emit("user_message", new() { ["text"] = Redact(Truncate(text, 500)) });

    /// <param name="errorKind">null se la chiamata è andata a buon fine.</param>
    public static void ToolCall(string name, string argsAbbrev, bool ok, string? errorKind, long durationMs, int resultBytes) =>
        Emit("tool_call", new()
        {
            ["name"] = name,
            ["args"] = Redact(Truncate(argsAbbrev, 300)),
            ["ok"] = ok,
            ["errorKind"] = errorKind,
            ["durationMs"] = durationMs,
            ["bytes"] = resultBytes,
        });

    /// <param name="ok">false per una risposta incompleta/un errore del server — vedi <paramref name="errorKind"/>.</param>
    /// <param name="errorKind">null se <paramref name="ok"/> è true; altrimenti "stream_truncated" | "length" | "http_error".</param>
    /// <param name="contextPercent">promptTokens/maxContextTokens*100, se entrambi noti — null altrimenti (mai calcolato qui su un valore mancante).</param>
    /// <param name="maxTokens">il max_tokens/n_predict richiesto per QUESTA chiamata (budget di risposta, non la finestra di contesto) — null se non passato dal chiamante.</param>
    public static void LlmRequest(int? promptTokens, long durationMs, string? finishReason, bool ok = true,
        string? errorKind = null, double? contextPercent = null, int? maxTokens = null) =>
        Emit("llm_request", new()
        {
            ["promptTokens"] = promptTokens,
            ["durationMs"] = durationMs,
            ["finishReason"] = finishReason,
            ["ok"] = ok,
            ["errorKind"] = errorKind,
            ["contextPercent"] = contextPercent,
            ["maxTokens"] = maxTokens,
        });

    // Un nuovo tentativo con la STESSA richiesta dopo una risposta incompleta/un errore del
    // server (vedi Program.cs: IncompleteLlmResponseException e il retry di trasporto
    // preesistente) — mai dopo un errore "recuperabile" lato client (JSON non valido nel tool
    // call generato, tool call troppo grande), che hanno il proprio meccanismo di retry e la
    // propria correzione iniettata in history, non un semplice "riprova la stessa richiesta".
    public static void LlmRetry(int attempt, int maxAttempts, string reason, int delayMs) =>
        Emit("llm_retry", new() { ["attempt"] = attempt, ["maxAttempts"] = maxAttempts, ["reason"] = reason, ["delayMs"] = delayMs });

    public static void GuardTriggered(string guard, string reason) =>
        Emit("guard_triggered", new() { ["guard"] = guard, ["reason"] = Redact(Truncate(reason, 300)) });

    public static void ContextSize(int tokensUsed, int tokensMax) =>
        Emit("context_size", new() { ["tokensUsed"] = tokensUsed, ["tokensMax"] = tokensMax });

    private static void Emit(string type, Dictionary<string, object?> fields)
    {
        if (!_enabled) return;
        lock (_fileLock) { WriteLocked(type, fields); }
    }

    // Va chiamata SEMPRE sotto _fileLock. Non lancia mai: un errore di scrittura (disco pieno,
    // permessi, file cancellato da qualcun altro) produce un avviso UNA volta e basta.
    private static void WriteLocked(string type, Dictionary<string, object?> fields)
    {
        if (_writer == null) return;

        var evt = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.Now.ToString("o"),
            ["sessionId"] = _sessionId,
            ["turnId"] = _currentTurnId,
            ["type"] = type,
        };
        foreach (var kv in fields) evt[kv.Key] = kv.Value;

        try
        {
            var json = JsonSerializer.Serialize(evt);
            _writer.WriteLine(json);
            _writer.Flush();
        }
        catch (Exception ex) { WarnOnce($"scrittura trace fallita (non bloccante): {ex.Message}"); }
    }

    // ── Redazione ─────────────────────────────────────────────────────────────
    // Pattern generici (chiavi/token/password/variabili d'ambiente), NON specifici di questo
    // repo — gli stessi usati in tools/supervisor-lab/Redactor.cs per lo stesso motivo, duplicati
    // qui apposta: pm-code non deve dipendere da un progetto sotto tools/.
    private static readonly (Regex Pattern, string Replacement)[] RedactRules =
    [
        (new Regex(@"(?i)(api[_-]?key|token|secret|password|pwd)(['""\s:=]+)([A-Za-z0-9_\-\.\/\+]{8,})", RegexOptions.Compiled),
            "$1$2[REDACTED]"),
        (new Regex(@"Bearer\s+[A-Za-z0-9_\-\.]{10,}", RegexOptions.Compiled), "Bearer [REDACTED]"),
        (new Regex(@"sk-[A-Za-z0-9]{10,}", RegexOptions.Compiled), "[REDACTED]"),
        (new Regex(@"(?im)^([A-Z0-9_]*(KEY|TOKEN|SECRET|PASSWORD|PWD)[A-Z0-9_]*)=.+$", RegexOptions.Compiled), "$1=[REDACTED]"),
    ];

    public static string Redact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        foreach (var (pattern, replacement) in RedactRules)
            s = pattern.Replace(s, replacement);
        return s;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"…(+{s.Length - max})";
}
