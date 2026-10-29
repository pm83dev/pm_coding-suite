using System.Text.Json;
using SupervisorLab;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var cmd = args[0];
var rest = args.Skip(1).ToArray();

try
{
    return cmd switch
    {
        "sessions" => CmdSessions(rest),
        "digest" => CmdDigest(rest),
        "signals" => CmdSignals(rest),
        "labels" => CmdLabels(rest),
        "classify" => await CmdClassifyAsync(rest),
        "plans" => await CmdPlansAsync(rest),
        _ => Fail($"Comando sconosciuto: '{cmd}'.")
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Errore: {ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        supervisor-lab <comando> [opzioni]

        <logfile> è il vecchio pm-code-trace.log (testo), OPPURE un singolo file .ndjson,
        OPPURE una cartella di file .ndjson (il nuovo trace strutturato di pm-code, uno per
        sessione — es. PM_TRACE_DIR). Stessa interfaccia per sessions/digest/signals.

        Comandi:
          sessions <logfile> [--out data/sessions.csv] [--copy-redacted <dir>]
          digest <logfile> --session <id> [--turn <n>]
          signals <logfile> [--thresholds data/thresholds.json] [--out <csv>]
          labels <logfile> [--out data/labels.csv] [--max 10]
          classify --labels data/labels.csv [--out <csv>] [--disable-thinking]
              (richiede SUPERVISOR_LAB_9B_ENDPOINT e SUPERVISOR_LAB_9B_MODEL)
          plans --tasks data/tasks.txt [--out data/plans]
              (richiede SUPERVISOR_LAB_9B_ENDPOINT/_MODEL e SUPERVISOR_LAB_AGENT_ENDPOINT/_MODEL)
        """);
}

static string? GetOpt(string[] a, string name) =>
    Array.IndexOf(a, name) is int i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;

static int Fail(string msg)
{
    Console.Error.WriteLine(msg);
    return 1;
}

// <logfile> accetta, con la STESSA interfaccia a riga di comando (sessions/digest/signals):
//   - il vecchio pm-code-trace.log in testo (comportamento invariato);
//   - un singolo file .ndjson (il nuovo trace strutturato, un file = una sessione);
//   - una cartella di file .ndjson (es. PM_TRACE_DIR di pm-code): ogni file è una sessione.
static List<Session> LoadSessions(string logfile)
{
    if (Directory.Exists(logfile) || logfile.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
        return NdjsonLogParser.LoadFromPathOrDirectory(logfile);

    var lines = File.ReadLines(logfile);
    var turns = LogParser.ParseTurns(lines);
    return LogParser.GroupIntoSessions(turns, logfile);
}

static int CmdSessions(string[] a)
{
    if (a.Length == 0) return Fail("Uso: sessions <logfile> [--out <csv>] [--copy-redacted <dir>]");
    var logfile = a[0];
    var outPath = GetOpt(a, "--out") ?? "data/sessions.csv";
    var redactDir = GetOpt(a, "--copy-redacted");

    var sessions = LoadSessions(logfile);

    var rows = new List<string>
    {
        CsvUtil.Row("id", "source_file", "start", "duration", "turn_count", "tools_used", "has_errors", "apparent_outcome")
    };
    foreach (var s in sessions)
    {
        var tools = string.Join("|", s.AllToolCalls.Select(c => c.Tool).Distinct());
        rows.Add(CsvUtil.Row(s.Id, Path.GetFileName(s.SourceFile), s.Start.ToString("yyyy-MM-dd HH:mm:ss"),
            s.Duration.ToString(), s.TurnCount.ToString(), tools, s.HasErrors.ToString(), s.ApparentOutcome));
    }
    CsvUtil.WriteAllLines(outPath, rows);
    Console.WriteLine($"{sessions.Count} sessioni trovate in '{logfile}' -> {outPath}");

    if (redactDir != null)
    {
        Directory.CreateDirectory(redactDir);
        // Una cartella di .ndjson (vedi LoadSessions) ha un file per sessione: redatto uno
        // per uno, non la cartella stessa (Redactor.RedactFile apre un singolo file).
        var sources = Directory.Exists(logfile)
            ? new DirectoryInfo(logfile).GetFiles("*.ndjson").Select(f => f.FullName)
            : [logfile];
        foreach (var src in sources)
        {
            var dest = Path.Combine(redactDir, Path.GetFileName(src) + ".redacted.txt");
            Redactor.RedactFile(src, dest);
            Console.WriteLine($"Copia redatta: {dest}");
        }
    }

    return 0;
}

static int CmdDigest(string[] a)
{
    if (a.Length == 0) return Fail("Uso: digest <logfile> --session <id> [--turn <n>]");
    var logfile = a[0];
    var sessionId = GetOpt(a, "--session") ?? throw new ArgumentException("--session è obbligatorio");
    var turnArg = GetOpt(a, "--turn");

    var sessions = LoadSessions(logfile);
    var session = sessions.FirstOrDefault(s => s.Id == sessionId)
        ?? throw new ArgumentException($"Sessione '{sessionId}' non trovata (trovate: {string.Join(", ", sessions.Select(s => s.Id))})");

    int? turn = turnArg != null ? int.Parse(turnArg) : null;
    var digest = DigestBuilder.Build(session, turn, gitDiffStat: null); // workspace non noto da riga di comando: vedi README
    Console.WriteLine(digest);
    return 0;
}

static int CmdSignals(string[] a)
{
    if (a.Length == 0) return Fail("Uso: signals <logfile> [--thresholds <json>] [--out <csv>]");
    var logfile = a[0];
    var thPath = GetOpt(a, "--thresholds") ?? "data/thresholds.json";
    var outPath = GetOpt(a, "--out");

    var th = File.Exists(thPath)
        ? JsonSerializer.Deserialize<Thresholds>(File.ReadAllText(thPath)) ?? new Thresholds()
        : new Thresholds();

    var sessions = LoadSessions(logfile);
    var rows = new List<string> { CsvUtil.Row("session_id", "signal", "turn_index", "detail") };

    foreach (var s in sessions)
    {
        var hits = SignalDetector.Evaluate(s, th);
        if (hits.Count == 0)
        {
            Console.WriteLine($"{s.Id}: nessun segnale.");
            continue;
        }
        Console.WriteLine($"{s.Id}:");
        foreach (var h in hits)
        {
            Console.WriteLine($"  [{h.Signal}] al turno {h.TurnIndex}: {h.Detail}");
            rows.Add(CsvUtil.Row(s.Id, h.Signal, h.TurnIndex.ToString(), h.Detail));
        }
    }

    if (outPath != null) CsvUtil.WriteAllLines(outPath, rows);
    return 0;
}

static int CmdLabels(string[] a)
{
    if (a.Length == 0) return Fail("Uso: labels <logfile> [--out data/labels.csv] [--max 10]");
    var logfile = a[0];
    var outPath = GetOpt(a, "--out") ?? "data/labels.csv";
    var max = int.TryParse(GetOpt(a, "--max"), out var m) ? m : 10;

    var sessions = LoadSessions(logfile);

    // Privilegia sessioni lunghe (più turni) o con esito apparente non "completata",
    // come richiesto. Ordine: esito non pulito prima, poi per numero di turni decrescente.
    var selected = sessions
        .OrderBy(s => s.ApparentOutcome == "completata" ? 1 : 0)
        .ThenByDescending(s => s.TurnCount)
        .Take(max)
        .ToList();

    var rows = new List<string> { CsvUtil.Row("session_id", "point", "turn_index", "digest", "label") };
    foreach (var s in selected)
    {
        var points = new SortedSet<int> { s.TurnCount };
        if (s.TurnCount >= 3) { points.Add(s.TurnCount / 3); points.Add(2 * s.TurnCount / 3); }
        else if (s.TurnCount == 2) points.Add(1);
        points.RemoveWhere(p => p <= 0);

        foreach (var p in points)
        {
            // Newline sostituiti con "\n" letterale: un CSV letto riga per riga (vedi
            // ParseCsv/CsvSplit più sotto, usato da 'classify') non gestisce campi quotati
            // che attraversano più righe fisiche, quindi il digest multi-riga va appiattito.
            var digest = DigestBuilder.Build(s, p, gitDiffStat: null).Replace("\r\n", "\n").Replace("\n", "\\n");
            var label = p == s.TurnCount ? "fine_sessione" : $"turno_{p}";
            rows.Add(CsvUtil.Row(s.Id, label, p.ToString(), digest, "")); // label VUOTA: la compila l'utente
        }
    }

    CsvUtil.WriteAllLines(outPath, rows);
    Console.WriteLine($"{selected.Count} sessioni selezionate ({rows.Count - 1} righe digest) -> {outPath}");
    Console.WriteLine("Colonna 'label' lasciata vuota: va compilata manualmente con on_track/looping/off_task/stuck.");
    return 0;
}

static async Task<int> CmdClassifyAsync(string[] a)
{
    var labelsPath = GetOpt(a, "--labels") ?? "data/labels.csv";
    var outPath = GetOpt(a, "--out") ?? "data/classify-results.csv";
    var disableThinking = a.Contains("--disable-thinking");

    var endpoint = Environment.GetEnvironmentVariable("SUPERVISOR_LAB_9B_ENDPOINT")
        ?? throw new InvalidOperationException("Variabile d'ambiente SUPERVISOR_LAB_9B_ENDPOINT non impostata.");
    var model = Environment.GetEnvironmentVariable("SUPERVISOR_LAB_9B_MODEL")
        ?? throw new InvalidOperationException("Variabile d'ambiente SUPERVISOR_LAB_9B_MODEL non impostata.");

    if (!File.Exists(labelsPath)) return Fail($"File labels non trovato: {labelsPath}. Esegui prima 'labels'.");

    var client = new LlamaServerClient(endpoint, model);
    const string systemPrompt = """
        Sei un supervisore che osserva il digest di una sessione di un agente di coding.
        Classifica lo stato della sessione. Rispondi SOLO con l'oggetto JSON richiesto dallo schema.
        """;

    var inRows = ParseCsv(labelsPath);
    var header = inRows[0];
    var sessionIdx = Array.IndexOf(header, "session_id");
    var pointIdx = Array.IndexOf(header, "point");
    var digestIdx = Array.IndexOf(header, "digest");
    var labelIdx = Array.IndexOf(header, "label");

    var outRows = new List<string>
    {
        CsvUtil.Row("session_id", "point", "human_label", "model_state", "model_reason", "model_suggestion", "error")
    };

    foreach (var row in inRows.Skip(1))
    {
        var digest = row[digestIdx];
        string modelState = "", reason = "", suggestion = "", error = "";
        try
        {
            var raw = await client.ChatRawAsync(systemPrompt, digest, ClassificationSchema.Schema, disableThinking);
            var parsed = JsonSerializer.Deserialize<ClassificationResult>(raw);
            modelState = parsed?.State ?? "";
            reason = (parsed?.Reason ?? "").Replace("\r\n", " ").Replace('\n', ' ');
            suggestion = (parsed?.Suggestion ?? "").Replace("\r\n", " ").Replace('\n', ' ');
        }
        catch (Exception ex) { error = ex.Message.Replace("\r\n", " ").Replace('\n', ' '); }

        outRows.Add(CsvUtil.Row(row[sessionIdx], row[pointIdx], row[labelIdx], modelState, reason, suggestion, error));
        Console.WriteLine($"{row[sessionIdx]}/{row[pointIdx]}: {(error != "" ? "ERRORE " + error : modelState)}");
    }

    CsvUtil.WriteAllLines(outPath, outRows);
    Console.WriteLine($"Risposte salvate in {outPath}");

    var labeled = outRows.Skip(1).Select(ParseRow).Where(r => !string.IsNullOrWhiteSpace(r[2])).ToList();
    if (labeled.Count == 0)
    {
        Console.WriteLine("Nessuna etichetta umana compilata ancora: matrice di concordanza non calcolata.");
        return 0;
    }

    Console.WriteLine("\nMatrice di confusione (etichetta umana x stato modello):");
    var states = new[] { "on_track", "looping", "off_task", "stuck" };
    var matrix = states.ToDictionary(h => h, _ => states.ToDictionary(m => m, _ => 0));
    foreach (var r in labeled)
    {
        var human = r[2]; var modelState = r[3];
        if (matrix.TryGetValue(human, out var col) && col.ContainsKey(modelState)) col[modelState]++;
    }
    Console.WriteLine("human\\model".PadRight(12) + string.Join("", states.Select(s => s.PadRight(12))));
    foreach (var h in states)
        Console.WriteLine(h.PadRight(12) + string.Join("", states.Select(m => matrix[h][m].ToString().PadRight(12))));

    return 0;

    static string[] ParseRow(string csvLine) => CsvSplit(csvLine);
}

static async Task<int> CmdPlansAsync(string[] a)
{
    var tasksPath = GetOpt(a, "--tasks") ?? "data/tasks.txt";
    var outDir = GetOpt(a, "--out") ?? "data/plans";

    var endpoint9b = Environment.GetEnvironmentVariable("SUPERVISOR_LAB_9B_ENDPOINT")
        ?? throw new InvalidOperationException("SUPERVISOR_LAB_9B_ENDPOINT non impostata.");
    var model9b = Environment.GetEnvironmentVariable("SUPERVISOR_LAB_9B_MODEL")
        ?? throw new InvalidOperationException("SUPERVISOR_LAB_9B_MODEL non impostata.");
    var endpointAgent = Environment.GetEnvironmentVariable("SUPERVISOR_LAB_AGENT_ENDPOINT")
        ?? throw new InvalidOperationException("SUPERVISOR_LAB_AGENT_ENDPOINT non impostata.");
    var modelAgent = Environment.GetEnvironmentVariable("SUPERVISOR_LAB_AGENT_MODEL")
        ?? throw new InvalidOperationException("SUPERVISOR_LAB_AGENT_MODEL non impostata.");

    if (!File.Exists(tasksPath)) return Fail($"File tasks non trovato: {tasksPath}.");
    var tasks = File.ReadAllLines(tasksPath).Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')).ToList();
    if (tasks.Count == 0) return Fail($"'{tasksPath}' non contiene compiti (righe non vuote, non commentate). Compilalo prima di eseguire 'plans'.");

    Directory.CreateDirectory(outDir);

    const string systemPrompt = """
        Rispondi SOLO con un oggetto JSON: {"steps": [...], "files": [...], "verify_command": "...", "max_turns": N}.
        "steps" è una lista di passi testuali brevi, "files" i file che prevedi di toccare,
        "verify_command" il comando per verificare il risultato, "max_turns" un intero.
        """;

    var client9b = new LlamaServerClient(endpoint9b, model9b);
    var clientAgent = new LlamaServerClient(endpointAgent, modelAgent);

    for (var i = 0; i < tasks.Count; i++)
    {
        var task = tasks[i];
        var n = i + 1;
        Console.WriteLine($"Task {n}: {task}");

        string plan9b = "", planAgent = "";
        try { plan9b = await client9b.ChatRawAsync(systemPrompt, task); }
        catch (Exception ex) { plan9b = $"ERRORE: {ex.Message}"; }
        try { planAgent = await clientAgent.ChatRawAsync(systemPrompt, task); }
        catch (Exception ex) { planAgent = $"ERRORE: {ex.Message}"; }

        File.WriteAllText(Path.Combine(outDir, $"task{n}_9b.json"), plan9b);
        File.WriteAllText(Path.Combine(outDir, $"task{n}_agent.json"), planAgent);
        File.WriteAllText(Path.Combine(outDir, $"task{n}_task.txt"), task);
        Console.WriteLine($"  -> {outDir}/task{n}_9b.json, task{n}_agent.json");
    }

    Console.WriteLine("Piani salvati, nessuna valutazione automatica eseguita (richiesto dalle regole dell'esperimento).");
    return 0;
}

// Parser CSV minimale coerente con CsvUtil.Escape (virgolette doppiate per escape,
// campi quotati solo se contengono virgola/virgoletta/newline).
static List<string[]> ParseCsv(string path) => File.ReadAllLines(path).Select(CsvSplit).ToList();

static string[] CsvSplit(string line)
{
    var fields = new List<string>();
    var sb = new System.Text.StringBuilder();
    var inQuotes = false;
    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];
        if (inQuotes)
        {
            if (c == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = false;
            }
            else sb.Append(c);
        }
        else
        {
            if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
    }
    fields.Add(sb.ToString());
    return fields.ToArray();
}
