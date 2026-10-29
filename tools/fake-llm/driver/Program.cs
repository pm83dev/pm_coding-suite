using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Driver per i test di verifica del trace strutturato di pm-code (vedi tools/fake-llm/README.md):
// avvia il finto server fake-llm, poi pm-code.exe in modalità REPL (NON --stdin-protocol: è
// l'unica modalità usata ora) con stdin/stdout/stderr rediretti, gli scrive le righe di uno
// script, ed eventualmente lo interrompe con Ctrl+C o Kill a comando. Non modifica pm-code:
// lo guida solo da fuori, come farebbe una persona al terminale.

var opts = ParseArgs(args);
string Req(string name) => opts.TryGetValue(name, out var v) ? v : throw new ArgumentException($"--{name} è obbligatorio");
string? Opt(string name) => opts.TryGetValue(name, out var v) ? v : null;

var pmcodeSrc = Req("pmcode");
var workdir = Req("workdir");
var fakeLlmDll = Req("fakellm");
var scenario = Req("scenario");
var scriptFile = Req("script");
var port = int.TryParse(Opt("port"), out var pp) ? pp : 8999;
var trace = Opt("trace") ?? "on";
var killAfterMs = int.TryParse(Opt("kill-after-ms"), out var k) ? k : (int?)null;
var ctrlcAfterMs = int.TryParse(Opt("ctrlc-after-ms"), out var c) ? c : (int?)null;
var timeoutMs = int.TryParse(Opt("timeout-ms"), out var t) ? t : 20000;

Directory.CreateDirectory(workdir);
var exeDir = Path.Combine(workdir, "exe");
var wsDir = Path.Combine(workdir, "workspace");
var tracesDir = Path.Combine(workdir, "traces");
var logsDir = Path.Combine(workdir, "logs");
Directory.CreateDirectory(exeDir);
Directory.CreateDirectory(wsDir);
Directory.CreateDirectory(logsDir);

// pm-code.exe è solo l'apphost nativo: il vero assembly è pm-code.dll, più .deps.json,
// .runtimeconfig.json e le dipendenze NuGet nella stessa cartella — senza queste il runtime
// fallisce con "The application to execute does not exist" (osservato nel primo tentativo,
// quando si copiavano solo .exe/.pdb). Si copia tutta la cartella di build di pm-code.
foreach (var f in Directory.GetFiles(Path.GetDirectoryName(pmcodeSrc)!))
    File.Copy(f, Path.Combine(exeDir, Path.GetFileName(f)), overwrite: true);

File.WriteAllText(Path.Combine(exeDir, "appsettings.json"), $$"""
{
  "LlmSettings": { "BaseUrl": "http://127.0.0.1:{{port}}", "Model": "FAKE", "Temperature": 0.0, "FrequencyPenalty": 0.3,
    "PresencePenalty": null, "TopP": null, "TopK": null, "MinP": null, "RepeatPenalty": null, "RepeatLastN": null },
  "AgentSettings": { "Workspace": ".", "MaxSteps": 30, "MaxContextTokens": 81920, "RolloverTokens": 40000,
    "MaxRepeats": 3, "MaxConsecutiveReadOnly": 6, "MaxLinesPerFile": 400 },
  "WebSearchSettings": { "BraveApiKey": "" }
}
""");

if (!File.Exists(Path.Combine(wsDir, "hello.txt")))
    File.WriteAllText(Path.Combine(wsDir, "hello.txt"), "ciao mondo\n");

Console.WriteLine($"[driver] workdir={workdir}");

// ── Avvia il finto server ────────────────────────────────────────────────────
var fakeLlmLog = Path.Combine(logsDir, "fake-llm.log");
var fakeLlm = Process.Start(new ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"\"{fakeLlmDll}\" \"{scenario}\" {port} --log \"{fakeLlmLog}\" --log-requests",
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true,
}) ?? throw new InvalidOperationException("avvio fake-llm fallito");
fakeLlm.OutputDataReceived += (_, e) => { };
fakeLlm.ErrorDataReceived += (_, e) => { };
fakeLlm.BeginOutputReadLine();
fakeLlm.BeginErrorReadLine();
await Task.Delay(1200); // tempo per il bind della porta

// ── Avvia pm-code ────────────────────────────────────────────────────────────
var psi = new ProcessStartInfo
{
    FileName = Path.Combine(exeDir, "pm-code.exe"),
    Arguments = $"--workspace \"{wsDir}\"",
    WorkingDirectory = exeDir,
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    // Encoding.UTF8 (statico) include il preambolo BOM, che .NET scrive una sola volta alla
    // prima Write sullo stream: pm-code in REPL (a differenza di --stdin-protocol, che usa
    // esplicitamente un UTF8Encoding(false) — vedi Program.cs) legge Console.In con la
    // codifica di default e non lo riconosce, decodificandolo come testo visibile ("∩╗┐")
    // davanti alla prima riga — osservato nel primo giro di questo stesso test. Non è un bug
    // del trace né di pm-code: è un artefatto di come .NET Process imposta lo stream se non
    // si specifica un'istanza senza BOM.
    StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    StandardOutputEncoding = Encoding.UTF8,
    StandardErrorEncoding = Encoding.UTF8,
};
psi.Environment["PM_TRACE"] = trace;
psi.Environment["PM_TRACE_DIR"] = tracesDir;

var pm = Process.Start(psi) ?? throw new InvalidOperationException("avvio pm-code fallito");
Console.WriteLine($"[driver] pm-code avviato, PID={pm.Id}");

var stdoutSb = new StringBuilder();
var stderrSb = new StringBuilder();
var stdoutTask = DrainAsync(pm.StandardOutput, stdoutSb, Path.Combine(logsDir, "pm-code.stdout.log"));
var stderrTask = DrainAsync(pm.StandardError, stderrSb, Path.Combine(logsDir, "pm-code.stderr.log"));

var sw = Stopwatch.StartNew();

// Scrive le righe dello script con un piccolo intervallo, per lasciare al processo il tempo
// di stampare il prompt tra una e l'altra (come farebbe davvero una persona).
var lines = File.ReadAllLines(scriptFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
var writerTask = Task.Run(async () =>
{
    foreach (var line in lines)
    {
        if (ctrlcAfterMs is int ms1 && sw.ElapsedMilliseconds >= ms1) break; // non scrivere oltre il momento del Ctrl+C
        try
        {
            await pm.StandardInput.WriteLineAsync(line);
            await pm.StandardInput.FlushAsync();
        }
        catch { break; } // stdin già chiuso (processo uscito/ucciso nel frattempo)
        await Task.Delay(400);
    }
});

if (ctrlcAfterMs is int ctrlMs)
{
    await Task.Delay(Math.Max(0, ctrlMs - (int)sw.ElapsedMilliseconds));
    Console.WriteLine($"[driver] invio CTRL_C_EVENT a PID={pm.Id} dopo {sw.ElapsedMilliseconds} ms");
    var sent = SendCtrlC(pm.Id);
    Console.WriteLine($"[driver] SendCtrlC risultato: {sent}");
}

if (killAfterMs is int killMs)
{
    await Task.Delay(Math.Max(0, killMs - (int)sw.ElapsedMilliseconds));
    Console.WriteLine($"[driver] kill di PID={pm.Id} dopo {sw.ElapsedMilliseconds} ms (nessuna chance di flush)");
    try { pm.Kill(entireProcessTree: true); } catch { }
}

var exited = pm.WaitForExit(timeoutMs);
Console.WriteLine(exited ? $"[driver] pm-code uscito con codice {pm.ExitCode}" : "[driver] TIMEOUT: pm-code non è uscito, forzo Kill");
if (!exited) { try { pm.Kill(entireProcessTree: true); } catch { } }

await Task.WhenAll(stdoutTask, stderrTask, writerTask);

try { fakeLlm.Kill(entireProcessTree: true); } catch { }

Console.WriteLine($"[driver] traces: {tracesDir}");
Console.WriteLine($"[driver] log pm-code: {logsDir}");
return 0;

static async Task DrainAsync(StreamReader reader, StringBuilder sb, string logPath)
{
    using var writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
    string? line;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        sb.AppendLine(line);
        await writer.WriteLineAsync(line);
    }
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var d = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var name = args[i][2..];
        var value = i + 1 < args.Length ? args[i + 1] : "";
        d[name] = value;
        i++;
    }
    return d;
}

// Invia CTRL_C_EVENT al processo pid, come se qualcuno avesse premuto Ctrl+C nella sua console.
// Richiede che il driver si stacchi dalla propria console e si agganci a quella del processo
// target (pm-code, non avviato con --stdin-protocol, si aggancia alla console del padre via
// AttachConsole(ATTACH_PARENT_PROCESS) — qui il "padre" è QUESTO driver). Funziona solo se il
// driver stesso ha una console reale (fallisce se lanciato senza terminale, es. da un altro
// redirect totale): in quel caso si ritorna "no-console", riportato come limite non verificato.
static string SendCtrlC(int pid)
{
    if (!NativeMethods.FreeConsole())
        return $"FreeConsole falso (GetLastError={Marshal.GetLastWin32Error()})";
    try
    {
        if (!NativeMethods.AttachConsole((uint)pid))
            return $"AttachConsole({pid}) falso (GetLastError={Marshal.GetLastWin32Error()})";
        try
        {
            // Il processo corrente deve ignorare CTRL_C per non uscire anche lui: registriamo
            // un handler che restituisce true (gestito, nessuna azione) PRIMA dell'evento.
            NativeMethods.SetConsoleCtrlHandler(null!, true);
            if (!NativeMethods.GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0))
                return $"GenerateConsoleCtrlEvent falso (GetLastError={Marshal.GetLastWin32Error()})";
            return "ok";
        }
        finally { NativeMethods.FreeConsole(); }
    }
    catch (Exception ex) { return $"eccezione: {ex.Message}"; }
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler? handler, bool add);
    public delegate bool ConsoleCtrlHandler(uint ctrlType);
}
