
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Tools;

public class TerminalTools(WorkspaceContext workspace)
{
    // Su Windows usiamo PowerShell (già presente ovunque); su Linux/macOS bash.
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // Windows PowerShell 5.1 (powershell.exe) non supporta gli operatori '&&'/'||'
    // (introdotti in PowerShell 7): un LLM li usa comunemente (sintassi bash-like) e
    // il comando fallisce con un errore di parsing. Se PowerShell 7+ (pwsh.exe) è
    // installato lo preferiamo per compatibilità, altrimenti ripieghiamo su powershell.exe.
    private static readonly string ShellExe = ResolveWindowsShellExe();

    /// <summary>True se run_command su Windows usa PowerShell 7+ (supporta '&&' e '||').</summary>
    public static bool UsesPwsh => ShellExe == "pwsh.exe";

    private static string ResolveWindowsShellExe()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "pwsh.exe")))
                    return "pwsh.exe";
            }
            catch { /* ignora voci PATH non valide */ }
        }
        return "powershell.exe";
    }
    // Comandi esplicitamente bloccati per sicurezza. Regex a parola intera, non sottostringhe:
    // con Contains("format") venivano bloccati anche comandi innocui con "LogInformation",
    // "Format-Table", "-Format", ecc. (il modello riprovava varianti dello stesso comando
    // senza capire perché fosse rifiutato).
    private static readonly System.Text.RegularExpressions.Regex[] _blocklist =
    [
        .. new[]
        {
            @"\brm\s+-(rf|fr)\b",
            @"\bdel\s+/f\b",
            @"(^|[;&|]|\s)format(\.com)?\s+[a-z]:",
            @"\bformat-volume\b",
            @"\bshutdown(\.exe)?\b",
            @"\b(rd|rmdir)\s+/s\b",
        }.Select(p => new System.Text.RegularExpressions.Regex(p,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled))
    ];

    // Cancellazioni da shell: vietate del tutto, passano solo da delete_file (cestino
    // recuperabile). Osservato: il modello ha usato Remove-Item per cancellare un file di
    // codice e aggirare il rifiuto di write_file, e poi per eliminare 5 servizi senza conferma.
    private static readonly System.Text.RegularExpressions.Regex FileDeleteRegex = new(
        @"(^|[;&|{(\s])(Remove-Item|ri|rm|rmdir|rd|del|erase|Clear-Content|clc|unlink|shred)(\s|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Here-string PowerShell e stringhe tra apici/virgolette (contenuto di body JSON, messaggi).
    private static readonly System.Text.RegularExpressions.Regex QuotedRx = new(
        @"@'[\s\S]*?'@|@""[\s\S]*?""@|'[^'\n]*'|""(?:[^""\\\n]|\\.)*""",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Scrittura da shell sopra file di codice/config: aggira edit_file (e la conferma in chat).
    // Scrivere su .txt/.log (es. salvare un diff o un output) resta permesso.
    private static readonly System.Text.RegularExpressions.Regex CodeOverwriteRegex = new(
        @"(Set-Content|Add-Content|Out-File|Copy-Item|Move-Item|\bcp\b|\bmv\b|>>?)[^;|\n]*\.(cs|csproj|sln|ts|tsx|js|jsx|mjs|py|java|go|rs|cpp|c|h|hpp|html|css|scss|less|vue|svelte|razor|json|xml|ya?ml)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Terminare processi da shell: permesso solo sui job in background avviati dall'agente (e sui
    // loro figli). Osservato: l'API avviata dall'utente teneva bloccato l'exe in build e il modello
    // l'ha terminata con Stop-Process -Force, nonostante la nota di sol_analyze dicesse di chiedere.
    private static readonly System.Text.RegularExpressions.Regex KillRegex = new(
        @"(^|[;&|{(\s])(Stop-Process|spps|kill|taskkill|pkill|killall)(\.exe)?(?=\s|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // Working directory persistente tra chiamate, come Claude Code Bash tool
    private string _cwd = workspace.Root;

    // Processi avviati in background (es. dev server) che restano vivi tra le chiamate
    private readonly Dictionary<int, BackgroundJob> _jobs = new();

    private sealed class BackgroundJob
    {
        public required Process Process { get; init; }
        public required string Command { get; init; }
        public System.Text.StringBuilder Output { get; } = new();
        public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
    }

    // Registro su disco dei job e dei loro discendenti (PID + ora di avvio, per non confondere
    // un PID riutilizzato). Il Job Object con kill-on-close non basta: il pwsh dello Store crea i
    // figli fuori dal job, e quando l'estensione termina pm-code "dotnet run" e l'app restano vivi.
    // Osservato: un LlmLab.Api orfano teneva la porta 5000 e l'exe bloccato in build, e nella
    // sessione successiva l'agente non poteva più fermarlo (non era un suo job).
    private string JobsFile => Path.Combine(workspace.Root, AgentStateDir.DirName, "jobs.json");
    private sealed record ProcRef(int Pid, long StartTicksUtc);
    private sealed record JobRecord(ProcRef Root, string Command, List<ProcRef> Descendants);

    private static ProcRef? Ref(int pid)
    {
        try { return new ProcRef(pid, Process.GetProcessById(pid).StartTime.ToUniversalTime().Ticks); }
        catch { return null; }
    }

    /// <summary>Riscrive il registro con i job vivi e i loro discendenti attuali.</summary>
    public void SnapshotJobs()
    {
        try
        {
            var records = new List<JobRecord>();
            foreach (var (pid, job) in _jobs)
            {
                if (job.Process.HasExited || Ref(pid) is not { } root) continue;
                var descendants = ProcessJob.Descendants(pid, job.Process.StartTime)
                    .Select(Ref).OfType<ProcRef>().ToList();
                records.Add(new JobRecord(root, job.Command, descendants));
            }
            if (records.Count == 0)
            {
                if (File.Exists(JobsFile)) File.Delete(JobsFile);
                return;
            }
            AgentStateDir.Ensure(workspace.Root);
            File.WriteAllText(JobsFile, JsonSerializer.Serialize(records));
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// All'avvio: termina i processi di una sessione precedente ancora vivi (registrati in
    /// jobs.json e con la stessa ora di avvio). Restituisce una descrizione, o null se non c'era nulla.
    /// </summary>
    public string? ReapOrphanJobs()
    {
        try
        {
            if (!File.Exists(JobsFile)) return null;
            var records = JsonSerializer.Deserialize<List<JobRecord>>(File.ReadAllText(JobsFile)) ?? [];
            File.Delete(JobsFile);

            var killed = new List<string>();
            foreach (var rec in records)
            {
                var targets = new List<ProcRef>(rec.Descendants);
                if (SameProcess(rec.Root))
                {
                    var since = new DateTime(rec.Root.StartTicksUtc, DateTimeKind.Utc).ToLocalTime();
                    targets.AddRange(ProcessJob.Descendants(rec.Root.Pid, since).Select(Ref).OfType<ProcRef>());
                    targets.Add(rec.Root);
                }
                foreach (var p in targets.DistinctBy(t => t.Pid).Where(SameProcess))
                {
                    try
                    {
                        var proc = Process.GetProcessById(p.Pid);
                        var name = proc.ProcessName;
                        proc.Kill(entireProcessTree: true);
                        killed.Add($"{name} (PID {p.Pid})");
                    }
                    catch { /* già uscito o non accessibile */ }
                }
            }
            return killed.Count == 0 ? null
                : "terminati processi rimasti attivi da una sessione precedente: " + string.Join(", ", killed);
        }
        catch { return null; }
    }

    private static bool SameProcess(ProcRef p) => Ref(p.Pid) is { } now && now.StartTicksUtc == p.StartTicksUtc;

    /// <summary>Job in background ancora in esecuzione (PID e comando).</summary>
    public List<(int Pid, string Command)> RunningJobs() =>
        _jobs.Where(j => !j.Value.Process.HasExited).Select(j => (j.Key, j.Value.Command)).ToList();

    // Regex statiche per performance
    private static readonly System.Text.RegularExpressions.Regex DiagRx = new(
        @"(error|warning)\s+[A-Z]+\d+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex FileRx = new(
        @"^(.+?)\(\d+,\d+\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Sentinel unico per evitare collisioni con l'output del comando
    private const string Sentinel = "___TERMINAL_TOOL_EXIT_CWD____";

    // Sequenze ANSI (spinner/progress bar di tool come ng, npm) e caratteri di controllo residui:
    // se stampati crudi sulla console reale possono corromperne lo stato (cursore nascosto,
    // buffer alternato) e dare l'impressione che l'input da tastiera si sia bloccato.
    private static readonly System.Text.RegularExpressions.Regex AnsiRx = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string SanitizeOutput(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = AnsiRx.Replace(s, "");
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            if (c == '\n' || c == '\r' || c == '\t' || !char.IsControl(c))
                sb.Append(c);
        return sb.ToString();
    }

    public List<ToolDefinition> Definitions =>
    [
        new() { Function = new() {
            Name = "run_command",
            Description = $"Esegue un comando nella shell di sistema ({(IsWindows ? "PowerShell" : "bash")}). La working directory persiste tra le chiamate; usa 'cd <path>' per cambiarla. Per processi che restano in esecuzione (server, watch, dev server) usa background=true: NON usare l'operatore '&' della shell per eseguire in background, non ha l'effetto atteso.",
            Parameters = new { type = "object",
                properties = new {
                    command    = new { type = "string",  description = $"Comando {(IsWindows ? "PowerShell" : "bash")} da eseguire" },
                    timeout_ms = new { type = "integer", description = "Timeout in ms (default 60000)" },
                    background = new { type = "boolean", description = "Se true, avvia il comando senza attendere la fine (es. dotnet run, npm start) e ritorna subito il PID" }
                },
                required = new[] { "command" } }
        }},
        new() { Function = new() {
            Name = "get_background_output",
            Description = "Legge l'output (stdout/stderr) accumulato finora da un processo avviato con run_command background=true.",
            Parameters = new { type = "object",
                properties = new {
                    pid = new { type = "integer", description = "PID del processo restituito da run_command" }
                },
                required = new[] { "pid" } }
        }},
        new() { Function = new() {
            Name = "stop_background_job",
            Description = "Termina un processo avviato in background.",
            Parameters = new { type = "object",
                properties = new {
                    pid = new { type = "integer", description = "PID del processo da terminare" }
                },
                required = new[] { "pid" } }
        }},
        new() { Function = new() {
            Name = "list_background_jobs",
            Description = "Elenca i processi avviati in background e il loro stato.",
            Parameters = new { type = "object",
                properties = new { },
                required = Array.Empty<string>() }
        }},
        new() { Function = new() {
            Name = "run_dotnet",
            Description = "Esegue dotnet build/test/run/add.",
            Parameters = new { type = "object",
                properties = new {
                    args    = new { type = "string" },
                    project = new { type = "string" }
                },
                required = new[] { "args" } }
        }},
        new() { Function = new() {
            Name = "sol_analyze",
            Description = "dotnet build: solo errori/warning. Senza 'project' usa la root o, se lì non c'è, l'unico .sln/.csproj delle sottocartelle.",
            Parameters = new { type = "object",
                properties = new {
                    project = new { type = "string" }
                },
                required = Array.Empty<string>() }
        }}
    ];

    public void ResetCwd() => _cwd = workspace.Root;

    public string Execute(string toolName, string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            return toolName switch
            {
                "run_command"     => RunCommand(args),
                "run_dotnet"      => RunDotnet(args),
                "sol_analyze"     => AnalyzeSolution(args),
                "get_background_output" => GetBackgroundOutput(args),
                "stop_background_job"   => StopBackgroundJob(args),
                "list_background_jobs"  => ListBackgroundJobs(),
                _                 => $"Tool '{toolName}' non trovato."
            };
        }
        catch (Exception ex) { return $"ERRORE: {ex.Message}"; }
    }

    private string RunCommand(JsonElement args)
    {
        var command = args.GetProperty("command").GetString()!;
        var timeoutMs = args.TryGetProperty("timeout_ms", out var t) ? t.GetInt32() : 60_000;

        if (_blocklist.Any(b => b.IsMatch(command)))
            return $"ERRORE: comando bloccato per sicurezza: {command}";

        if (CheckForeignKill(command, out var killNote) is { } killRefusal)
            return killRefusal;

        // Ripristinare un file dal cestino dell'agente è l'opposto di una cancellazione: permesso.
        var restoringFromTrash = System.Text.RegularExpressions.Regex.IsMatch(command, @"\.pm-agent[\\/]+trash",
                                     System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                                 && !FileDeleteRegex.IsMatch(command);
        // I comandi di cancellazione si cercano fuori dalle stringhe: il testo di un body JSON in
        // italiano ("il nome del tool") faceva scattare "del" e bloccava una semplice chiamata HTTP.
        // La sovrascrittura resta sul comando intero, perché il path di destinazione è spesso tra virgolette.
        var deleteMatch = FileDeleteRegex.Match(QuotedRx.Replace(command, "''"));
        var overwriteMatch = CodeOverwriteRegex.Match(command);
        if (!restoringFromTrash && (deleteMatch.Success || overwriteMatch.Success))
            return $"ERRORE: run_command non può cancellare file né scrivere/sovrascrivere file di codice (trovato: " +
                   $"\"{(deleteMatch.Success ? deleteMatch.Value : overwriteMatch.Value).Trim()}\") " +
                   "(Remove-Item, del, rm, Clear-Content, Set-Content/Out-File/'>' su file di codice). " +
                   "Per cancellare usa delete_file (sposta nel cestino recuperabile); per modificare codice usa edit_file. " +
                   "Non aggirare il rifiuto di un altro tool: se write_file è stato rifiutato, procedi con edit_file a blocchi.";

        var background = args.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.True;
        if (background)
            return StartBackground(command);

        // Aggiorna la working directory se specificata
        if (args.TryGetProperty("working_directory", out var wd) && !string.IsNullOrEmpty(wd.GetString()))
        {
            var dirStr = wd.GetString()!;
            try { _cwd = workspace.Resolve(dirStr); }
            catch { if (Directory.Exists(dirStr)) _cwd = Path.GetFullPath(dirStr); }
        }

        var trimmed = command.Trim();
        var isCd = (trimmed.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals("cd", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Set-Location ", StringComparison.OrdinalIgnoreCase))
                    && !trimmed.Contains('\n') && !trimmed.Contains(';')
                    && !trimmed.Contains("&&") && !trimmed.Contains("||") && !trimmed.Contains('|');

        if (isCd)
        {
            string dirArg;
            if (trimmed.StartsWith("Set-Location ", StringComparison.OrdinalIgnoreCase))
                dirArg = trimmed["Set-Location ".Length..].Trim().Trim('\'', '"');
            else if (trimmed.Length > 3)
                dirArg = trimmed[3..].Trim().Trim('\'', '"');
            else
                dirArg = workspace.Root;

            var target = Path.IsPathRooted(dirArg) ? dirArg : Path.GetFullPath(Path.Combine(_cwd, dirArg));

            if (!Directory.Exists(target))
                return $"ERRORE: directory non trovata: {dirArg}";

            _cwd = target;
            SyncWorkspace();
            return $"✓ cwd: {_cwd}";
        }

        var raw = IsWindows
            ? RunCommandWindows(command, timeoutMs)
            : RunCommandUnix(command, timeoutMs);

        // Estrai e aggiorna CWD dal sentinel
        var lines = raw.Split('\n').ToList();
        var sentinelIdx = lines.FindLastIndex(l => l.Contains(Sentinel));

        if (sentinelIdx >= 0)
        {
            var sl = lines[sentinelIdx];
            var start = sl.IndexOf(Sentinel, StringComparison.Ordinal) + Sentinel.Length;
            var end = sl.LastIndexOf("___", StringComparison.Ordinal);
            if (start < end)
            {
                var extracted = sl[start..end].Trim();
                if (Directory.Exists(extracted))
                {
                    _cwd = extracted;
                    SyncWorkspace();
                }
            }
            lines.RemoveAt(sentinelIdx);
            if (sentinelIdx > 0 && string.IsNullOrWhiteSpace(lines[sentinelIdx - 1]))
                lines.RemoveAt(sentinelIdx - 1);
            raw = string.Join('\n', lines);
        }

        var cwdPrefix = !_cwd.Equals(workspace.Root, StringComparison.OrdinalIgnoreCase)
            ? $"[cwd: {_cwd}]\n"
            : "";

        var finalOutput = AgentStateDir.Overflow(workspace.Root, "cmd", SanitizeOutput(raw.Trim()), 3000);
        return killNote + cwdPrefix + (string.IsNullOrEmpty(finalOutput) ? "(nessun output)" : finalOutput);
    }

    // PowerShell con CWD persistente e gestione Encoding.
    // Il comando è racchiuso in & { ... } *>&1 | Out-String perché, quando stderr non è
    // una console reale (processo .NET con redirect), PowerShell serializza gli ErrorRecord
    // (es. da Stop-Process, Get-Process, errori non terminanti) come CLIXML invece di testo.
    // Out-String forza il rendering testuale prima che il flusso esca dalla pipeline.
    // Il try/catch è necessario perché un errore TERMINANTE (es. comando non trovato) blocca
    // lo script prima che la pipe Out-String venga eseguita: l'host scrive allora il CLIXML
    // direttamente sul vero stderr del processo, bypassando il redirect. Il catch intercetta
    // l'eccezione e la forza comunque a testo.
    private string RunCommandWindows(string command, int timeoutMs)
    {
        const string scriptTemplate = "$ProgressPreference = 'SilentlyContinue'\r\nSet-Location '{0}'\r\ntry {{ & {{ {1} }} *>&1 | Out-String -Stream -Width 300 }} catch {{ $_ | Out-String -Stream }}\r\nWrite-Output \"\n{2}{3}\"";
        var script = string.Format(scriptTemplate, _cwd.Replace("'", "''"), command, Sentinel, (Directory.Exists(_cwd) ? _cwd : "UNKNOWN"));
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        // Nessun troncamento qui: lo applica RunCommand (con salvataggio su file), dopo aver
        // estratto il sentinel della cwd in coda all'output.
        return RunProcess(ShellExe,
            $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            _cwd, timeoutMs, maxChars: int.MaxValue, killLeftovers: true);
    }

    // bash -c riceve lo script come singolo elemento di argv (nessuna shell intermedia a
    // rifare il parsing), quindi non serve escaping del comando come su Windows con
    // -EncodedCommand. stderr è unito a stdout nello script stesso per preservare l'ordine
    // cronologico dell'output, come *>&1 fa lato PowerShell.
    private string RunCommandUnix(string command, int timeoutMs)
    {
        var cwdEscaped = _cwd.Replace("'", "'\\''");
        var script = $"cd '{cwdEscaped}' || exit 1\n{{ {command}\n}} 2>&1\nprintf '\\n%s%s' '{Sentinel}' '{(Directory.Exists(_cwd) ? _cwd : "UNKNOWN")}'";

        return RunProcess("/bin/bash", ["-c", script], _cwd, timeoutMs, maxChars: int.MaxValue, killLeftovers: true);
    }

    private string StartBackground(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = _cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Senza questo, il processo (e i suoi figli, es. npm/node) eredita l'handle
            // stdin della console reale. Dev server come Vite (usato da "ng serve" in
            // Angular 17+) attivano scorciatoie da tastiera interattive (r/u/h/q) chiamando
            // SetConsoleMode sullo stdin condiviso, il che ruba l'input della console al
            // processo host e blocca ConsoleInput.ReadInteractive senza errori visibili.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (IsWindows)
        {
            var script = $"$ProgressPreference = 'SilentlyContinue'\r\nSet-Location '{_cwd.Replace("'", "''")}'\r\ntry {{ & {{ {command} }} *>&1 | Out-String -Stream -Width 300 }} catch {{ $_ | Out-String -Stream }}";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            startInfo.FileName = ShellExe;
            startInfo.Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}";
        }
        else
        {
            startInfo.FileName = "/bin/bash";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var job = new BackgroundJob { Process = proc, Command = command };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (job.Output) { job.Output.AppendLine(SanitizeOutput(e.Data)); job.LastActivityUtc = DateTime.UtcNow; } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (job.Output) { job.Output.AppendLine(SanitizeOutput(e.Data)); job.LastActivityUtc = DateTime.UtcNow; } };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.StandardInput.Close(); // nessun input previsto: chiudi per dare EOF immediato ai figli

        _jobs[proc.Id] = job;
        SnapshotJobs();
        return $"✓ avviato in background (PID {proc.Id}): {command}\n" +
               $"Usa get_background_output(pid={proc.Id}) per leggere l'output, stop_background_job(pid={proc.Id}) per terminarlo.";
    }

    private string GetBackgroundOutput(JsonElement args)
    {
        var pid = args.GetProperty("pid").GetInt32();
        if (!_jobs.TryGetValue(pid, out var job))
            return $"ERRORE: nessun job in background con PID {pid}";

        string output;
        double idleSeconds;
        lock (job.Output)
        {
            output = job.Output.ToString();
            idleSeconds = (DateTime.UtcNow - job.LastActivityUtc).TotalSeconds;
        }
        if (output.Length > 3000)
            output = "...(inizio troncato)\n" + output[^3000..];

        var status = job.Process.HasExited
            ? $"terminato (exit code {job.Process.ExitCode})"
            // Nessun nuovo output da un po': può essere normale (server già avviato e silente)
            // o un processo bloccato — il chiamante può usarlo per decidere se aspettare o killarlo.
            : idleSeconds > 20
                ? $"in esecuzione, nessun nuovo output da {idleSeconds:F0}s"
                : "in esecuzione";
        return $"[{status}]\n{(string.IsNullOrWhiteSpace(output) ? "(nessun output)" : output)}";
    }

    // Rifiuta il comando se termina un processo che non è un job dell'agente: PID non suoi,
    // per nome (-Name, /IM, pkill) o da pipeline (Get-Process x | Stop-Process), dove non si
    // può sapere a priori cosa verrà terminato.
    private string? CheckForeignKill(string command, out string? note)
    {
        note = null;
        var matches = KillRegex.Matches(command);
        if (matches.Count == 0) return null;

        var own = OwnPids();
        var projectApps = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var start = m.Index + m.Length;
            var end = command.IndexOfAny([';', '|', '&', '\n'], start);
            var segment = end < 0 ? command[start..] : command[start..end];
            var piped = command[..m.Index].TrimEnd().EndsWith('|');
            // Numeri non preceduti da '-' (esclude "kill -9") né da lettere/cifre.
            var pids = System.Text.RegularExpressions.Regex.Matches(segment, @"(?<![-\w])\d+\b")
                .Select(x => int.Parse(x.Value)).ToList();
            // Per nome: Stop-Process -Name X, taskkill /IM X.exe
            var names = System.Text.RegularExpressions.Regex.Matches(segment,
                    @"(?:-Name|-ProcessName|/IM)\s+['""]?([\w.\-]+?)(?:\.exe)?['""]?(?=[\s,]|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(x => x.Groups[1].Value).ToList();

            var ok = !piped && (pids.Count > 0 || names.Count > 0);
            foreach (var pid in pids.Where(p => !own.Contains(p)))
            {
                if (ProjectAppName(pid) is { } app) projectApps.Add($"{app} (PID {pid})");
                else ok = false;
            }
            foreach (var name in names)
            {
                if (IsProjectName(name)) projectApps.Add(name);
                else ok = false;
            }

            if (!ok)
            {
                var ownList = _jobs.Count == 0 ? "nessuno" : string.Join(", ", _jobs.Keys);
                return "ERRORE: comando rifiutato — run_command può terminare solo i processi che hai avviato tu in background " +
                       $"(PID: {ownList}, usa stop_background_job) o l'eseguibile di un progetto di questo workspace (per PID o per nome, " +
                       "es. Stop-Process -Name <NomeProgetto>). Questo processo non è nessuno dei due, quindi NON va terminato. " +
                       "Non controllare a ripetizione se si è chiuso: o prosegui senza (es. avvia l'app su un'altra porta libera), " +
                       "o chiedi all'utente di chiuderlo con una domanda precisa (PID, nome, perché ti serve) e chiudi il turno.";
            }
        }
        if (projectApps.Count > 0)
            note = $"[NOTA] Terminato {string.Join(", ", projectApps)}: è l'app di un progetto di questo workspace ma non l'avevi " +
                   "avviata tu (forse l'utente la stava usando). Scrivilo all'utente nella risposta finale.\n";
        return null;
    }

    // Nomi dei progetti .NET del workspace: un processo con lo stesso nome è l'app che l'agente
    // sta compilando/avviando (tipicamente un'istanza precedente che blocca exe e porta), non un
    // programma qualsiasi dell'utente. "dotnet", "node" ecc. non sono mai nomi di progetto.
    private HashSet<string> ProjectNames()
    {
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(workspace.Root, "*.csproj", opts)
                .Where(p => !p.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or "node_modules" or ".git"))
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { return []; }
    }

    private bool IsProjectName(string name) => ProjectNames().Contains(name);

    private string? ProjectAppName(int pid)
    {
        try
        {
            var name = Process.GetProcessById(pid).ProcessName;
            return IsProjectName(name) ? name : null;
        }
        catch { return null; }
    }

    private HashSet<int> OwnPids()
    {
        var pids = new HashSet<int>();
        foreach (var (pid, job) in _jobs)
        {
            pids.Add(pid);
            try
            {
                if (!job.Process.HasExited)
                    pids.UnionWith(ProcessJob.Descendants(pid, job.Process.StartTime));
            }
            catch { /* processo terminato nel frattempo */ }
        }
        return pids;
    }

    private string StopBackgroundJob(JsonElement args)
    {
        var pid = args.GetProperty("pid").GetInt32();
        if (!_jobs.TryGetValue(pid, out var job))
            return $"ERRORE: nessun job in background con PID {pid}";

        if (!job.Process.HasExited)
        {
            // I discendenti vanno raccolti prima: il pwsh dello Store li crea fuori dall'albero
            // che Kill(entireProcessTree) riesce a vedere.
            var descendants = ProcessJob.Descendants(pid, job.Process.StartTime);
            try { job.Process.Kill(entireProcessTree: true); } catch { /* già terminato */ }
            foreach (var d in descendants)
                try { Process.GetProcessById(d).Kill(entireProcessTree: true); } catch { /* già terminato */ }
        }
        _jobs.Remove(pid);
        SnapshotJobs();
        return $"✓ job PID {pid} terminato.";
    }

    private string ListBackgroundJobs()
    {
        if (_jobs.Count == 0) return "Nessun job in background.";

        var sb = new System.Text.StringBuilder();
        foreach (var (pid, job) in _jobs)
        {
            string status;
            if (job.Process.HasExited)
                status = "terminato";
            else
            {
                var idleSeconds = (DateTime.UtcNow - job.LastActivityUtc).TotalSeconds;
                status = idleSeconds > 20 ? $"in esecuzione, silente da {idleSeconds:F0}s" : "in esecuzione";
            }
            sb.AppendLine($"PID {pid} [{status}]: {job.Command}");
        }
        return sb.ToString().TrimEnd();
    }

    private string RunDotnet(JsonElement args)
    {
        var dotnetArgs = args.GetProperty("args").GetString()!;
        var workDir = workspace.Root;
        if (args.TryGetProperty("project", out var proj) && !string.IsNullOrEmpty(proj.GetString()))
        {
            var projPath = workspace.Resolve(proj.GetString()!);
            workDir = Path.GetDirectoryName(projPath) ?? workDir;
        }
        return RunProcess("dotnet", dotnetArgs, workDir);
    }

    private string AnalyzeSolution(JsonElement args)
    {
        var workDir = workspace.Root;
        var buildArg = "";
        var autoNote = "";
        if (args.TryGetProperty("project", out var proj) && !string.IsNullOrEmpty(proj.GetString()))
        {
            var abs = workspace.Resolve(proj.GetString()!);
            if (File.Exists(abs))
            {
                workDir = Path.GetDirectoryName(abs) ?? workDir;
                buildArg = Path.GetFileName(abs);
            }
            else if (Directory.Exists(abs))
            {
                workDir = abs;
            }
        }
        else if (FindBuildTargets(workDir) is { Count: > 0 } found)
        {
            // Nessun progetto indicato e nessun .sln/.csproj nella root (es. repo con api/ e web/):
            // "dotnet build" fallirebbe con MSB1003. Si usa l'unico .sln, o in mancanza l'unico
            // .csproj, delle sottocartelle; se sono più di uno si chiede al modello quale.
            // Con più progetti si sceglie quello con la modifica più recente: prima si restituiva
            // l'elenco e il modello spendeva sempre un passo per richiamare sol_analyze con project.
            var target = found.Count == 1 ? found[0] : PickRecentlyChanged(found);
            if (found.Count > 1)
                autoNote = $"[progetto scelto automaticamente: {Path.GetRelativePath(workspace.Root, target)} (contiene o referenzia l'ultima modifica). " +
                           "Altri: " + string.Join(", ", found.Where(f => f != target).Select(f => Path.GetRelativePath(workspace.Root, f))) +
                           " — per compilarne un altro richiama sol_analyze con project=<path>]\n";
            workDir = Path.GetDirectoryName(target)!;
            buildArg = Path.GetFileName(target);
        }
        var raw = RunProcess("dotnet", $"build {buildArg}".TrimEnd(), workDir, timeoutMs: 120_000);
        var filtered = autoNote + FilterBuildOutput(raw);
        if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"MSB302[1-7]")
            && !System.Text.RegularExpressions.Regex.IsMatch(raw, @"error CS\d+"))
            filtered += "\n[NOTA] Il codice compila: fallisce solo la copia dell'eseguibile perché è in uso da un processo " +
                        "in esecuzione (es. l'app avviata da VS o in background). Non è un errore di codice e non va corretto. " +
                        "Per testare la versione nuova devi prima fermare quel processo (stop_background_job se l'hai avviato tu; " +
                        "se è l'exe di questo progetto puoi terminarlo con Stop-Process -Name <NomeProgetto>, altrimenti chiedi " +
                        "all'utente) e riavviare l'app.";
        return filtered;
    }

    // Candidati per "dotnet build" quando la root non ne contiene: prima i .sln, altrimenti i
    // .csproj delle sottocartelle. Lista vuota se la root ha già un target (build normale).
    private static readonly string[] SourceExtensions = [".cs", ".csproj", ".razor", ".cshtml", ".json"];

    /// <summary>
    /// Il progetto (o la solution) che contiene il file sorgente modificato più di recente. Se è
    /// una libreria referenziata da un altro candidato (es. Shared usata da Api), si compila chi la
    /// referenzia: così la build copre anche il codice che la usa.
    /// </summary>
    private static string PickRecentlyChanged(List<string> candidates)
    {
        static DateTime LastChange(string target)
        {
            try
            {
                var dir = Path.GetDirectoryName(target)!;
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                return Directory.EnumerateFiles(dir, "*", opts)
                    .Where(f => SourceExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                                && !f.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or "node_modules"))
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
            }
            catch { return DateTime.MinValue; }
        }

        var newest = candidates.OrderByDescending(LastChange).First();
        if (!newest.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return newest;
        var newestFile = Path.GetFileName(newest);
        var referencing = candidates
            .Where(c => c != newest && c.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        && SafeRead(c).Contains(newestFile, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(LastChange)
            .FirstOrDefault();
        return referencing ?? newest;

        static string SafeRead(string f) { try { return File.ReadAllText(f); } catch { return ""; } }
    }

    private static List<string> FindBuildTargets(string root)
    {
        static bool InRoot(string dir) =>
            Directory.EnumerateFiles(dir, "*.sln").Any() || Directory.EnumerateFiles(dir, "*.slnx").Any() ||
            Directory.EnumerateFiles(dir, "*.csproj").Any();
        try
        {
            if (InRoot(root)) return [];
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            static bool Skip(string p) => p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(s => s is "bin" or "obj" or "node_modules" or ".git" or AgentStateDir.DirName or "publish");
            var slns = Directory.EnumerateFiles(root, "*.sln", opts).Where(p => !Skip(p)).Take(10).ToList();
            if (slns.Count > 0) return slns;
            return Directory.EnumerateFiles(root, "*.csproj", opts).Where(p => !Skip(p)).Take(10).ToList();
        }
        catch { return []; }
    }

    private void SyncWorkspace()
    {
        if (!_cwd.StartsWith(workspace.Root, StringComparison.OrdinalIgnoreCase))
            try { workspace.SetRoot(_cwd); } catch { }
    }

    private static string FilterBuildOutput(string raw)
    {
        var lines = raw.Split('\n');
        var relevant = new List<string>();
        var filesInvolved = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var t = line.Trim();
            if (string.IsNullOrEmpty(t)) continue;

            if (DiagRx.IsMatch(t))
            {
                relevant.Add(t);
                var m = FileRx.Match(t);
                if (m.Success) filesInvolved.Add(Path.GetFileName(m.Groups[1].Value.Trim()));
            }
            else if (t.StartsWith("Build succeeded", StringComparison.OrdinalIgnoreCase) ||
                     t.StartsWith("Build FAILED", StringComparison.OrdinalIgnoreCase) ||
                     t.Contains("Error(s)") || 
                     t.Contains("Warning(s)") ||
                     t.StartsWith("[ESITO", StringComparison.OrdinalIgnoreCase))
            {
                relevant.Add(t);
            }
        }

        if (relevant.Count == 0) return raw;

        var sb = new System.Text.StringBuilder();
        if (filesInvolved.Count > 0)
            sb.AppendLine($"File coinvolti: {string.Join(", ", filesInvolved)}");
        
        foreach (var l in relevant) sb.AppendLine(l);
        
        var result = sb.ToString().TrimEnd();
        if (result.Length > 3000)
            return "...(inizio troncato)\n" + result[^3000..];
        
        return result;
    }

    private static string RunProcess(string executable, string arguments, string workDir, int timeoutMs = 60_000, int maxChars = 3000, bool killLeftovers = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return RunProcess(startInfo, timeoutMs, maxChars, killLeftovers);
    }

    // Passa gli argomenti come argv (nessuna shell intermedia a rifare il parsing): evita
    // problemi di escaping per script multilinea con virgolette annidate, es. bash -c "<script>".
    private static string RunProcess(string executable, string[] argumentList, string workDir, int timeoutMs = 60_000, int maxChars = 3000, bool killLeftovers = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in argumentList) startInfo.ArgumentList.Add(a);
        return RunProcess(startInfo, timeoutMs, maxChars, killLeftovers);
    }

    // killLeftovers: per run_command in primo piano. Tutto ciò che il comando ha avviato e che
    // è ancora vivo quando il comando finisce (tipicamente "Start-Process ..." senza -Wait) viene
    // terminato e segnalato: i processi che devono restare vivi vanno avviati con background=true,
    // dove l'agente li vede e li può fermare. Senza questo restavano orfani per ore.
    private static void KillAll(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* già terminato */ }
        }
    }

    private static string RunProcess(ProcessStartInfo startInfo, int timeoutMs, int maxChars, bool killLeftovers = false)
    {
        using var proc = new Process { StartInfo = startInfo };

        var output = new System.Text.StringBuilder();
        var errors = new System.Text.StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };

        if (killLeftovers)
        {
            // Server di build condivisi (nodi MSBuild riusabili, compilatore Roslyn, MSBuild server):
            // restano vivi apposta dopo un "dotnet build" e verrebbero scambiati per processi orfani.
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            startInfo.Environment["UseSharedCompilation"] = "false";
            startInfo.Environment["UseRazorBuildServer"] = "false";
        }

        proc.Start();
        var startedAt = DateTime.Now.AddSeconds(-1);
        using var job = killLeftovers ? ProcessJob.ForProcess(proc) : null;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.StandardInput.Close();

        bool finished = proc.WaitForExit(timeoutMs);
        if (!finished)
        {
            var detached = killLeftovers ? ProcessJob.Descendants(proc.Id, startedAt) : [];
            proc.Kill(entireProcessTree: true);
            job?.Terminate();
            KillAll(detached);
            var partial = SanitizeOutput(output.ToString().Trim());
            if (partial.Length > 1500) partial = "...(inizio troncato)\n" + partial[^1500..];
            return $"ERRORE: timeout ({timeoutMs / 1000}s) — processo terminato forzatamente." +
                   (partial.Length > 0 ? $"\nUltimo output prima del timeout:\n{partial}" : "") +
                   "\nSe il programma è interattivo (legge input da tastiera in un ciclo) non termina mai da solo: " +
                   "non rilanciarlo uguale. Avvialo con background=true e leggi l'output con get_background_output, " +
                   "oppure verifica che esca quando l'input finisce (ReadLine() == null → uscita dal ciclo).";
        }

        string? leftoverNote = null;
        if (killLeftovers)
        {
            var leftovers = (job?.ActivePids() ?? []).Union(ProcessJob.Descendants(proc.Id, startedAt))
                .Where(pid => pid != proc.Id).ToList();
            var names = leftovers.Select(pid =>
            {
                try { return Process.GetProcessById(pid).ProcessName; }
                catch { return null; }
            }).Zip(leftovers)
              .Where(x => x.First != null && !x.First.Equals("conhost", StringComparison.OrdinalIgnoreCase))
              .Select(x => $"{x.First} (PID {x.Second})").ToList();
            job?.Terminate();
            KillAll(leftovers);
            if (names.Count > 0)
            {
                leftoverNote = $"\n[PROCESSI TERMINATI] Il comando ha lasciato in esecuzione {string.Join(", ", names)}: " +
                               "sono stati terminati. Un comando normale deve finire da solo; per un processo che deve restare " +
                               "attivo (server, app da interrogare) usa run_command con background=true, NON Start-Process.";
            }
        }

        var result = new System.Text.StringBuilder();
        if (output.Length > 0) result.Append(output);
        if (errors.Length > 0) result.AppendLine($"\n[STDERR]\n{errors}");
        if (leftoverNote != null) result.AppendLine(leftoverNote);

        if (proc.ExitCode != 0) result.AppendLine($"\n[EXIT CODE: {proc.ExitCode}]");

        var text = SanitizeOutput(result.ToString().Trim());
        text += proc.ExitCode == 0 ? "\n[ESITO: SUCCESSO]" : $"\n[ESITO: FALLITO exit code {proc.ExitCode}]";

        if (text.Length > maxChars)
            text = "...(inizio troncato)\n" + text[^maxChars..];

        return string.IsNullOrEmpty(text) ? "(nessun output)" : text;
    }
}