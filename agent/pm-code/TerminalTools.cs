
using System.Diagnostics;
using System.Text.Json;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Tools;

public class TerminalTools(WorkspaceContext workspace)
{
    // Comandi esplicitamente bloccati per sicurezza
    private static readonly string[] _blocklist =
    [
        "rm -rf", "del /f", "format", "shutdown", "rd /s", "rmdir /s"
    ];

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
            Description = "Esegue comando PowerShell. La working directory persiste tra le chiamate; usa 'cd <path>' per cambiarla. Per processi che restano in esecuzione (server, watch, dev server) usa background=true: NON usare l'operatore '&' di PowerShell, che non avvia processi in background come in Bash.",
            Parameters = new { type = "object",
                properties = new {
                    command    = new { type = "string",  description = "Comando PowerShell da eseguire" },
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
            Description = "dotnet build: solo errori/warning.",
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

        if (_blocklist.Any(b => command.Contains(b, StringComparison.OrdinalIgnoreCase)))
            return $"ERRORE: comando bloccato per sicurezza: {command}";

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
                    && !trimmed.Contains('\n') && !trimmed.Contains(';');

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

        // PowerShell con CWD persistente e gestione Encoding.
        // Il comando è racchiuso in & { ... } *>&1 | Out-String perché, quando stderr non è
        // una console reale (processo .NET con redirect), PowerShell serializza gli ErrorRecord
        // (es. da Stop-Process, Get-Process, errori non terminanti) come CLIXML invece di testo.
        // Out-String forza il rendering testuale prima che il flusso esca dalla pipeline.
        // Il try/catch è necessario perché un errore TERMINANTE (es. comando non trovato) blocca
        // lo script prima che la pipe Out-String venga eseguita: l'host scrive allora il CLIXML
        // direttamente sul vero stderr del processo, bypassando il redirect. Il catch intercetta
        // l'eccezione e la forza comunque a testo.
        const string scriptTemplate = "$ProgressPreference = 'SilentlyContinue'\r\nSet-Location '{0}'\r\ntry {{ & {{ {1} }} *>&1 | Out-String -Stream -Width 300 }} catch {{ $_ | Out-String -Stream }}\r\nWrite-Output \"\n{2}{3}\"";
        var script = string.Format(scriptTemplate, _cwd.Replace("'", "''"), command, Sentinel, (Directory.Exists(_cwd) ? _cwd : "UNKNOWN"));
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        var raw = RunProcess("powershell.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            _cwd, timeoutMs);

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

        var finalOutput = SanitizeOutput(raw.Trim());
        return cwdPrefix + (string.IsNullOrEmpty(finalOutput) ? "(nessun output)" : finalOutput);
    }

    private string StartBackground(string command)
    {
        var script = $"$ProgressPreference = 'SilentlyContinue'\r\nSet-Location '{_cwd.Replace("'", "''")}'\r\ntry {{ & {{ {command} }} *>&1 | Out-String -Stream -Width 300 }} catch {{ $_ | Out-String -Stream }}";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
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
            },
            EnableRaisingEvents = true
        };

        var job = new BackgroundJob { Process = proc, Command = command };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (job.Output) { job.Output.AppendLine(SanitizeOutput(e.Data)); job.LastActivityUtc = DateTime.UtcNow; } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (job.Output) { job.Output.AppendLine(SanitizeOutput(e.Data)); job.LastActivityUtc = DateTime.UtcNow; } };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.StandardInput.Close(); // nessun input previsto: chiudi per dare EOF immediato ai figli

        _jobs[proc.Id] = job;
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

    private string StopBackgroundJob(JsonElement args)
    {
        var pid = args.GetProperty("pid").GetInt32();
        if (!_jobs.TryGetValue(pid, out var job))
            return $"ERRORE: nessun job in background con PID {pid}";

        if (!job.Process.HasExited)
        {
            try { job.Process.Kill(entireProcessTree: true); } catch { /* già terminato */ }
        }
        _jobs.Remove(pid);
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
        var raw = RunProcess("dotnet", $"build {buildArg}".TrimEnd(), workDir, timeoutMs: 120_000);
        return FilterBuildOutput(raw);
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

    private static string RunProcess(string executable, string arguments, string workDir, int timeoutMs = 60_000)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var output = new System.Text.StringBuilder();
        var errors = new System.Text.StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.StandardInput.Close();

        bool finished = proc.WaitForExit(timeoutMs);
        if (!finished)
        {
            proc.Kill(entireProcessTree: true);
            return "ERRORE: timeout (60s) — processo terminato forzatamente.";
        }

        var result = new System.Text.StringBuilder();
        if (output.Length > 0) result.Append(output);
        if (errors.Length > 0) result.AppendLine($"\n[STDERR]\n{errors}");
        
        if (proc.ExitCode != 0) result.AppendLine($"\n[EXIT CODE: {proc.ExitCode}]");

        var text = SanitizeOutput(result.ToString().Trim());
        text += proc.ExitCode == 0 ? "\n[ESITO: SUCCESSO]" : $"\n[ESITO: FALLITO exit code {proc.ExitCode}]";

        if (text.Length > 3000)
            text = "...(inizio troncato)\n" + text[^3000..];

        return string.IsNullOrEmpty(text) ? "(nessun output)" : text;
    }
}