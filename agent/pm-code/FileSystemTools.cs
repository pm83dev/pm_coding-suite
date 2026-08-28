using System.Text.Json;
using System.Text.RegularExpressions;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Tools;

public class FileSystemTools(WorkspaceContext workspace, Func<string, string, bool>? proposeWrite = null)
{
    // Invariante stile Claude Code: non si modifica alla cieca un file mai visto in questa
    // conversazione. read_file/read_file_range lo aggiungono qui; edit_file lo richiede
    // sempre, write_file lo richiede quando il file esiste già (per un file nuovo non c'è
    // nulla da leggere prima). Si azzera insieme alla cache in ToolDispatcher.ClearCache()
    // (/reset, /cd, nuova chat) — altrimenti un file letto in una conversazione precedente
    // risulterebbe "già visto" anche dopo un reset di contesto.
    private readonly HashSet<string> _readPaths = new(StringComparer.OrdinalIgnoreCase);

    public void ClearReadCache() => _readPaths.Clear();

    // Prefisso "  97 | " che read_file antepone a ogni riga: usato in EditFile per
    // recuperare quando il modello lo copia per errore dentro old_string/new_string.
    private static readonly Regex LineGutterRx = new(@"^[ \t]*\d+[ \t]*\|[ \t]?", RegexOptions.Multiline | RegexOptions.Compiled);

    private string? RequireRead(string path, string absPath)
    {
        if (_readPaths.Contains(absPath)) return null;
        return $"ERRORE: {path} non è stato letto in questa conversazione. " +
               "Chiama read_file su questo path prima di modificarlo — non modificare codice che non hai visto.";
    }

    // In modalità CLI/REPL (proposeWrite == null) scrive subito su disco, comportamento
    // invariato rispetto a prima. In modalità --stdin-protocol, Program.cs inietta un
    // delegate che emette edit_proposal (con path RELATIVO, come lo intende l'extension
    // per risolverlo dentro il workspace VS Code) e blocca finché non arriva l'edit_decision
    // dell'utente — mai scrivere su disco senza conferma in quel caso.
    //
    // Dopo l'accettazione, l'estensione applica la modifica con vscode.workspace.applyEdit,
    // che aggiorna SOLO il buffer dell'editor (documento "dirty") senza salvarlo su disco.
    // Senza lo scrivi qui, un edit_file successivo sullo stesso path rilegge da disco
    // (vedi EditFile sopra) e trova ancora il contenuto PRE-modifica — il file risulta
    // "non aver ricevuto" l'edit precedente pur essendo stato accettato in chat. Scriviamo
    // quindi SEMPRE il file reale qui, esattamente come fa già WriteFile più sotto: la
    // stringa è la stessa che l'utente ha appena approvato nel diff, quindi non introduce
    // alcuna divergenza di contenuto rispetto a quanto mostrato.
    private bool ApplyOrPropose(string relPath, string absPath, string content)
    {
        if (proposeWrite == null)
        {
            File.WriteAllText(absPath, content);
            return true;
        }
        if (!proposeWrite(relPath, content)) return false;
        File.WriteAllText(absPath, content);
        return true;
    }

    public List<ToolDefinition> Definitions =>
    [
        new() { Function = new() {
            Name = "read_file",
            Description = "Legge file con numeri di riga (formato \"  97 | codice\"). Usa start_line/end_line per sezioni. " +
                          "Il numero e il carattere '|' sono SOLO per riferimento visivo: non fanno parte del file e non vanno " +
                          "copiati in old_string/new_string di edit_file.",
            Parameters = new { type = "object",
                properties = new {
                    path       = new { type = "string" },
                    start_line = new { type = "integer" },
                    end_line   = new { type = "integer" }
                },
                required = new[] { "path" } }
        }},
        new() { Function = new() {
            Name = "edit_file",
            Description = "Sostituisce old_string con new_string. old_string deve essere univoco e contenere ESATTAMENTE " +
                          "il testo del file, senza il prefisso \"numero | \" mostrato da read_file.",
            Parameters = new { type = "object",
                properties = new {
                    path       = new { type = "string" },
                    old_string = new { type = "string" },
                    new_string = new { type = "string" }
                },
                required = new[] { "path", "old_string", "new_string" } }
        }},
        new() { Function = new() {
            Name = "write_file",
            Description = "Crea o sovrascrive file. Per modifiche usa edit_file.",
            Parameters = new { type = "object",
                properties = new {
                    path    = new { type = "string" },
                    content = new { type = "string" }
                },
                required = new[] { "path", "content" } }
        }},
        new() { Function = new() {
            Name = "glob_files",
            Description = "Trova file per NOME tramite pattern glob (es. \"**/*.ts\", \"src/**/gen-dashboard*\"). " +
                           "Risultati ordinati per data di modifica, più recenti prima.",
            Parameters = new { type = "object",
                properties = new {
                    pattern    = new { type = "string" },
                    head_limit = new { type = "integer", description = "Max risultati (default 100)" }
                },
                required = new[] { "pattern" } }
        }},
        new() { Function = new() {
            Name = "search_in_files",
            Description = "Cerca un pattern regex nel CONTENUTO dei file (come grep). Per trovare un file per NOME usa glob_files.",
            Parameters = new { type = "object",
                properties = new {
                    pattern          = new { type = "string", description = "Pattern regex da cercare" },
                    glob             = new { type = "string", description = "Filtro glob sui file, es. \"**/*.ts\". Default: tutti i file testuali." },
                    output_mode      = new { type = "string", description = "\"files_with_matches\" (default), \"content\" (righe con match), \"count\"" },
                    case_insensitive = new { type = "boolean", description = "Default false" },
                    context          = new { type = "integer", description = "Righe di contesto prima/dopo ogni match (solo con output_mode=content)" },
                    head_limit       = new { type = "integer", description = "Max risultati (default 50, max 200)" }
                },
                required = new[] { "pattern" } }
        }},
        new() { Function = new() {
            Name = "list_directory",
            Description = "Elenca file e cartelle in una directory.",
            Parameters = new { type = "object",
                properties = new {
                    path = new { type = "string" }
                },
                required = new[] { "path" } }
        }},
        new() { Function = new() {
            Name = "create_directory",
            Description = "Crea directory.",
            Parameters = new { type = "object",
                properties = new {
                    path = new { type = "string" }
                },
                required = new[] { "path" } }
        }},
        new() { Function = new() {
            Name = "move_file",
            Description = "Sposta o rinomina un file.",
            Parameters = new { type = "object",
                properties = new {
                    source      = new { type = "string" },
                    destination = new { type = "string" }
                },
                required = new[] { "source", "destination" } }
        }},
        new() { Function = new() {
            Name = "delete_file",
            Description = "Elimina un file.",
            Parameters = new { type = "object",
                properties = new {
                    path = new { type = "string" }
                },
                required = new[] { "path" } }
        }},
        new() { Function = new() {
            Name = "sym_search",
            Description = "Trova simbolo → file:riga.",
            Parameters = new { type = "object",
                properties = new {
                    symbol    = new { type = "string" },
                    extension = new { type = "string" }
                },
                required = new[] { "symbol" } }
        }},
        new() { Function = new() {
            Name = "read_file_range",
            Description = "Legge una porzione specifica di un file per risparmiare memoria. Utile per file grandi o per analizzare solo metodi specifici.",
            Parameters = new { type = "object",
                properties = new {
                    path       = new { type = "string" },
                    start_line = new { type = "integer" },
                    end_line   = new { type = "integer" }
                },
                required = new[] { "path", "start_line", "end_line" } }
        }}

    ];

    public string Execute(string toolName, string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            return toolName switch
            {
                "read_file" => ReadFile(args),
                "edit_file" => EditFile(args),
                "write_file" => WriteFile(args),
                "glob_files" => GlobFiles(args),
                "search_in_files" => SearchInFiles(args),
                "sym_search" => SearchSymbol(args),
                "list_directory" => ListDirectory(args),
                "create_directory" => CreateDirectory(args),
                "move_file" => MoveFile(args),
                "delete_file" => DeleteFile(args),
                "read_file_range" => ReadFileRange(args),
                _ => $"Tool '{toolName}' non trovato."
            };
        }
        catch (UnauthorizedAccessException ex) { return $"ERRORE sicurezza: {ex.Message}"; }
        catch (Exception ex) { return $"ERRORE: {ex.Message}"; }
    }

    // ── read_file ──────────────────────────────────────────────────────────────

    private string ReadFile(JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        var absPath = workspace.Resolve(path);

        if (!File.Exists(absPath))
        {
            // Il path è spesso una directory indovinata male — prova a localizzare
            // il file per nome in tutto il workspace prima di arrenderti.
            var fileName = path.Replace('\\', '/').Split('/').Last();
            var matches = GetGlobMatches(workspace.Root, $"**/{fileName}");
            if (matches.Count > 0)
            {
                var rel = matches.Select(f => Path.GetRelativePath(workspace.Root, f));
                return $"File non trovato: {path}. Trovato con questo nome altrove nel workspace:\n" +
                       string.Join('\n', rel) + "\nRichiama read_file con uno di questi path esatti.";
            }
            return $"File non trovato: {path}";
        }

        _readPaths.Add(absPath);

        var lines = File.ReadAllLines(absPath);
        var totalLines = lines.Length;

        bool hasRange = args.TryGetProperty("start_line", out _) || args.TryGetProperty("end_line", out _);

        // File grandi senza range esplicito: il contenuto intero rischia di superare
        // il contesto del modello (visto in pratica con un file da ~980 righe → 400
        // "exceed_context_size_error" sul server llama.cpp). Restituisci un'anteprima
        // e forza l'uso di start_line/end_line per il resto.
        const int maxLinesWithoutRange = 150;
        if (!hasRange && totalLines > maxLinesWithoutRange)
        {
            const int previewLines = 40;
            var preview = lines
                .Take(previewLines)
                .Select((l, i) => $"{i + 1,4} | {l}");

            return $"[WARNING] {path} ha {totalLines} righe — troppo grande per leggerlo interamente " +
                   $"(rischio di superare il contesto del modello).\n" +
                   $"Usa search_in_files/sym_search per individuare la sezione, poi richiama read_file con start_line/end_line.\n\n" +
                   $"Anteprima (righe 1-{Math.Min(previewLines, totalLines)} di {totalLines}):\n" +
                   string.Join('\n', preview);
        }

        int startLine = 1;
        int endLine = totalLines;

        if (args.TryGetProperty("start_line", out var sl) && sl.ValueKind == JsonValueKind.Number)
            startLine = Math.Max(1, sl.GetInt32());
        if (args.TryGetProperty("end_line", out var el) && el.ValueKind == JsonValueKind.Number)
            endLine = Math.Min(totalLines, el.GetInt32());

        if (startLine > endLine)
            return $"ERRORE: start_line ({startLine}) > end_line ({endLine})";

        var selected = lines
            .Skip(startLine - 1)
            .Take(endLine - startLine + 1)
            .Select((l, i) => $"{startLine + i,4} | {l}");

        var header = (startLine > 1 || endLine < totalLines)
            ? $"[{path} — righe {startLine}-{endLine} di {totalLines}]\n"
            : $"[{path} — {totalLines} righe]\n";

        return header + string.Join('\n', selected);
    }

    // ── edit_file ──────────────────────────────────────────────────────────────

    public string EditFile(JsonElement args)
    {
        if (!args.TryGetProperty("path", out var pathProp)
         || !args.TryGetProperty("old_string", out var oldProp)
         || !args.TryGetProperty("new_string", out var newProp))
            return "ERRORE: argomenti mancanti (path, old_string, new_string).";

        var path = pathProp.GetString()!;
        var oldString = oldProp.GetString()!;
        var newString = newProp.GetString()!;

        // Stesso rischio di troncamento di write_file (vedi commento lì): se new_string
        // contiene un blocco enorme (più metodi/funzioni incollati in un colpo solo), il
        // tool call può superare il budget di token della risposta e llama-server risponde
        // 500 prima ancora che questo dispatcher lo veda. Forza un metodo/blocco alla volta.
        if (newString.Length > 16000)
            return $"ERRORE: edit_file rifiutato — new_string per {path} è di {newString.Length} caratteri, " +
                   "oltre il limite di 16000. Blocchi di codice così grandi in una sola tool call rischiano di " +
                   "troncare a metà e causare un errore 500 sul server LLM. Dividi l'aggiunta in più chiamate " +
                   "edit_file separate, una funzione/metodo o un piccolo blocco alla volta.";

        var absPath = workspace.Resolve(path);

        if (!File.Exists(absPath))
            return $"File non trovato: {path}";

        if (RequireRead(path, absPath) is { } notRead) return notRead;

        var content = File.ReadAllText(absPath);

        // Normalizza i terminatori di riga: read_file mostra le righe senza \r,
        // quindi il modello copia old_string/new_string con \n semplice anche
        // quando il file su disco usa CRLF. Senza questo, il match fallisce sempre
        // pur essendo il testo visivamente identico.
        bool useCrlf = content.Contains("\r\n");
        string NormalizeLineEndings(string s) =>
            useCrlf ? s.Replace("\r\n", "\n").Replace("\n", "\r\n") : s.Replace("\r\n", "\n");

        oldString = NormalizeLineEndings(oldString);
        newString = NormalizeLineEndings(newString);

        int count = CountOccurrences(content, oldString);

        // Fallimento tipico: il modello copia old_string/new_string dall'output NUMERATO di
        // read_file (es. "  97 | ...") includendo il numero di riga e il "|" nel testo cercato,
        // che ovviamente non esiste sul disco. Se il match diretto fallisce, ritenta ripulendo
        // ogni riga da questo prefisso prima di arrenderti.
        if (count == 0)
        {
            var strippedOld = LineGutterRx.Replace(oldString, "");
            if (strippedOld != oldString)
            {
                var strippedCount = CountOccurrences(content, strippedOld);
                if (strippedCount > 0)
                {
                    oldString = strippedOld;
                    newString = LineGutterRx.Replace(newString, "");
                    count = strippedCount;
                }
            }
        }

        if (count == 0)
            return $"ERRORE: la stringa cercata non è stata trovata in {path}.\n" +
                   $"Verifica l'indentazione e i caratteri esatti con read_file. old_string deve contenere SOLO il testo " +
                   $"reale del file: non includere il numero di riga né il carattere '|' che read_file mostra a inizio riga.";

        if (count > 1)
            return $"ERRORE: la stringa cercata appare {count} volte in {path} — " +
                   $"è ambigua. Aggiungi più contesto a old_string per renderla univoca.";

        // Find line number of the change for display
        var beforeChange = content[..content.IndexOf(oldString, StringComparison.Ordinal)];
        int lineNum = beforeChange.Count(c => c == '\n') + 1;

        var newContent = content.Replace(oldString, newString, StringComparison.Ordinal);
        // Il modello a volte passa un path già assoluto: normalizza SEMPRE a relativo-al-workspace
        // prima di propagarlo nell'evento edit_proposal — l'extension risolve quel path con
        // vscode.Uri.joinPath(root, relPath), che si romperebbe con un path assoluto.
        var relPath = Path.GetRelativePath(workspace.Root, absPath).Replace('\\', '/');
        if (!ApplyOrPropose(relPath, absPath, newContent))
            return $"⚠️ Modifica proposta per {path} ma NON applicata (rifiutata dall'utente).";

        int delta = newString.Length - oldString.Length;
        var sign = delta >= 0 ? "+" : "";
        return $"✓ {path} modificato (riga ~{lineNum}, {sign}{delta} caratteri)";
    }

    // ── write_file ─────────────────────────────────────────────────────────────

    private string WriteFile(JsonElement args)
    {
        if (!args.TryGetProperty("path", out var pathProp)
         || !args.TryGetProperty("content", out var contentProp))
            return "ERRORE: argomenti mancanti (path o content).";

        var path = pathProp.GetString()!;
        var content = contentProp.GetString()!;
        var absPath = workspace.Resolve(path);
        // Vedi commento analogo in EditFile: normalizza sempre a relativo-al-workspace.
        var relPath = Path.GetRelativePath(workspace.Root, absPath).Replace('\\', '/');

        if (File.Exists(absPath))
        {
            if (RequireRead(path, absPath) is { } notRead) return notRead;

            if (_codeExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                var existingLines = File.ReadAllLines(absPath).Length;
                if (existingLines > 15)
                    return $"ERRORE: write_file rifiutato — {path} esiste già ed è un file di codice di {existingLines} righe. " +
                           "Riscriverlo per intero rischia di perdere o duplicare parti non toccate. Usa edit_file con " +
                           "old_string/new_string mirati (max 8-10 righe) per la modifica specifica.";
            }
        }
        else if (_codeExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
               && content.Length > 16000)
        {
            // Il server LLM genera gli argomenti del tool call come stringa JSON con un tetto
            // di token per risposta: un file di codice grande scritto in un solo write_file
            // tronca a metà stringa e llama-server risponde 500 "invalid string: missing
            // closing quote" prima ancora che questo dispatcher veda la chiamata. Bloccarlo qui
            // per i file NUOVI (non ancora troppo tardi) forza lo scheletro+edit incrementale.
            return $"ERRORE: write_file rifiutato — il contenuto proposto per {path} è di {content.Length} caratteri, " +
                   "oltre il limite di 16000. File di questa dimensione generati in un solo write_file troncano a metà " +
                   "e causano un errore 500 sul server LLM. Chiama write_file SOLO con lo scheletro minimo del file " +
                   "(import essenziali, dichiarazione classe/componente vuota), poi usa edit_file ripetutamente per " +
                   "aggiungere il resto un blocco alla volta.";
        }

        // La cartella va creata solo se la scrittura viene poi effettivamente accettata:
        // altrimenti un rifiuto lascerebbe una cartella vuota orfana nel workspace.
        bool Write()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
            File.WriteAllText(absPath, content);
            return true;
        }

        var accepted = proposeWrite == null ? Write() : proposeWrite(relPath, content) && Write();
        if (!accepted)
            return $"⚠️ Scrittura proposta per {path} ma NON applicata (rifiutata dall'utente).";

        return $"✓ File scritto: {path} ({content.Length} caratteri, {content.Split('\n').Length} righe)";
    }

    // ── glob_files ─────────────────────────────────────────────────────────────

    private string GlobFiles(JsonElement args)
    {
        var pattern = args.GetProperty("pattern").GetString()!;
        var headLimit = args.TryGetProperty("head_limit", out var hl) && hl.ValueKind == JsonValueKind.Number
            ? Math.Clamp(hl.GetInt32(), 1, 500) : 100;

        var files = GetGlobMatches(workspace.Root, pattern);

        if (files.Count == 0)
        {
            // Fallback: il modello spesso indovina una sottodirectory sbagliata
            // (es. "src/app/components/.../x.ts" quando il file è altrove).
            // Riprova con solo il nome file su "**/" prima di arrenderti.
            var fileName = pattern.Replace('\\', '/').Split('/').Last();
            if (fileName != pattern)
            {
                var broaderPattern = $"**/{fileName}";
                var broaderMatches = GetGlobMatches(workspace.Root, broaderPattern);
                if (broaderMatches.Count > 0)
                {
                    var rel = broaderMatches.Select(f => Path.GetRelativePath(workspace.Root, f));
                    return $"Nessun file per '{pattern}'. Trovato cercando in tutto il workspace ({broaderPattern}):\n" +
                           string.Join('\n', rel);
                }
            }

            return $"Nessun file trovato per il pattern: {pattern}";
        }

        var ordered = files
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(headLimit)
            .Select(f => Path.GetRelativePath(workspace.Root, f));

        var suffix = files.Count > headLimit ? $"\n... (+{files.Count - headLimit} altri file)" : "";
        return string.Join('\n', ordered) + suffix;
    }

    private static List<string> GetGlobMatches(string root, string pattern)
    {
        // Normalize separators
        pattern = pattern.Replace('/', Path.DirectorySeparatorChar);

        // Split into directory and file parts
        var parts = pattern.Split(Path.DirectorySeparatorChar);
        var results = new List<string>();
        GlobRecursive(root, root, parts, 0, results);
        return results
            .Where(f => !IsIgnored(f))
            .OrderBy(f => f)
            .ToList();
    }

    private static void GlobRecursive(string root, string current, string[] parts, int index, List<string> results)
    {
        if (index >= parts.Length) return;

        var part = parts[index];
        bool isLast = index == parts.Length - 1;

        if (part == "**")
        {
            // Match zero or more directories
            GlobRecursive(root, current, parts, index + 1, results);
            if (Directory.Exists(current))
            {
                foreach (var dir in Directory.GetDirectories(current, "*", SearchOption.AllDirectories))
                    GlobRecursive(root, dir, parts, index + 1, results);
            }
            return;
        }

        if (!Directory.Exists(current)) return;

        if (isLast)
        {
            foreach (var file in Directory.GetFiles(current, part))
                results.Add(file);
        }
        else
        {
            foreach (var dir in Directory.GetDirectories(current, part))
                GlobRecursive(root, dir, parts, index + 1, results);
        }
    }

    private static readonly string[] _ignoredSegments =
        [".git", "bin", "obj", "node_modules", ".vs", ".venv", "venv", "dist", "build",
         ".svelte-kit", "__pycache__", ".pytest_cache", ".next", ".nuxt", "coverage"];

    // write_file su un file di codice già esistente costringe il modello a rigenerarlo
    // per intero a memoria: è la causa tipica di metodi duplicati/troncati quando il file
    // supera poche righe. edit_file (diff mirato) non ha questo problema.
    private static readonly string[] _codeExtensions =
        [".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".svelte", ".vue", ".go", ".java",
         ".rb", ".php", ".c", ".cpp", ".h", ".hpp", ".rs", ".kt", ".swift",
         ".html", ".scss", ".css", ".less"];
    private static bool IsIgnored(string path) =>
        _ignoredSegments.Any(seg => path.Contains($"{Path.DirectorySeparatorChar}{seg}{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase));

    // ── search_in_files ────────────────────────────────────────────────────────

    private string SearchInFiles(JsonElement args)
    {
        var pattern = args.GetProperty("pattern").GetString()!;
        var glob = args.TryGetProperty("glob", out var g) ? g.GetString() : null;
        var outputMode = args.TryGetProperty("output_mode", out var om) ? om.GetString() : "files_with_matches";
        var caseInsensitive = args.TryGetProperty("case_insensitive", out var ci) && ci.GetBoolean();
        var context = args.TryGetProperty("context", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number
            ? Math.Max(0, ctxEl.GetInt32()) : 0;
        var headLimit = args.TryGetProperty("head_limit", out var hl) && hl.ValueKind == JsonValueKind.Number
            ? Math.Clamp(hl.GetInt32(), 1, 200) : 50;

        Regex regex;
        try
        {
            var opts = RegexOptions.Compiled | (caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(pattern, opts);
        }
        catch (Exception ex) { return $"ERRORE regex non valida: {ex.Message}"; }

        // Senza glob la ricerca scansiona l'intero workspace file per file: su repo grandi
        // (es. fork di progetti web con backend+frontend) può significare decine di migliaia
        // di file e bloccare il processo per minuti in lettura sincrona. Un tetto massimo
        // evita lo "hang" percepito e spinge il modello a restringere con glob invece di
        // continuare a girare a vuoto su ricerche troppo ampie.
        const int maxCandidates = 5000;
        var candidates = string.IsNullOrEmpty(glob)
            ? Directory.GetFiles(workspace.Root, "*.*", SearchOption.AllDirectories).Where(f => !IsIgnored(f)).ToList()
            : GetGlobMatches(workspace.Root, glob);

        if (string.IsNullOrEmpty(glob) && candidates.Count > maxCandidates)
        {
            return $"[ERROR] La ricerca senza 'glob' coprirebbe {candidates.Count} file (limite {maxCandidates}). " +
                   "Restringi con il parametro 'glob' (es. \"**/*.svelte\", \"src/**/*.py\", \"**/*logo*\").";
        }

        var fileMatches = new List<(string Path, List<int> Lines)>();

        foreach (var file in candidates)
        {
            List<int>? lineNumbers = null;
            try
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                        (lineNumbers ??= []).Add(i + 1);
                }
            }
            catch { /* file binario o locked */ }

            if (lineNumbers is { Count: > 0 })
                fileMatches.Add((Path.GetRelativePath(workspace.Root, file), lineNumbers));
        }

        if (fileMatches.Count == 0)
        {
            // Fallback: nessun match nel contenuto — prova a cercare per nome file,
            // utile quando pattern è in realtà un nome/parte di nome file (es. "gen-dashboard").
            var nameMatches = candidates
                .Where(f => Path.GetFileName(f).Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(workspace.Root, f))
                .ToList();

            if (nameMatches.Count > 0)
                return $"Nessun risultato nel contenuto per '{pattern}'. " +
                       $"Trovati questi file per nome (usa glob_files per cercare per nome):\n" +
                       string.Join('\n', nameMatches);

            return $"Nessun risultato per '{pattern}'";
        }

        switch (outputMode)
        {
            case "count":
                return string.Join('\n', fileMatches
                    .OrderByDescending(f => f.Lines.Count)
                    .Take(headLimit)
                    .Select(f => $"{f.Path}: {f.Lines.Count}"));

            case "content":
                {
                    var output = new List<string>();
                    foreach (var (path, lineNumbers) in fileMatches)
                    {
                        var lines = File.ReadAllLines(Path.Combine(workspace.Root, path));
                        foreach (var lineNum in lineNumbers)
                        {
                            int from = Math.Max(1, lineNum - context);
                            int to = Math.Min(lines.Length, lineNum + context);
                            for (int l = from; l <= to; l++)
                                output.Add($"{path}:{l}: {lines[l - 1].Trim()}");
                            if (context > 0) output.Add("--");

                            if (output.Count >= headLimit) break;
                        }
                        if (output.Count >= headLimit) break;
                    }

                    var truncated = output.Count > headLimit;
                    var result = string.Join('\n', output.Take(headLimit));
                    return truncated ? result + $"\n... (troncato a {headLimit} righe)" : result;
                }

            default: // files_with_matches
                {
                    var paths = fileMatches.Select(f => f.Path).Take(headLimit).ToList();
                    var suffix = fileMatches.Count > headLimit ? $"\n... (+{fileMatches.Count - headLimit} altri file)" : "";
                    return string.Join('\n', paths) + suffix;
                }
        }
    }

    // ── list_directory ─────────────────────────────────────────────────────────

    private string ListDirectory(JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        var absPath = workspace.Resolve(path);

        if (!Directory.Exists(absPath))
            return $"Directory non trovata: {path}";

        var entries = Directory.GetFileSystemEntries(absPath)
            .OrderBy(e => File.Exists(e) ? 1 : 0)
            .ThenBy(e => Path.GetFileName(e))
            .Select(e =>
            {
                var name = Path.GetFileName(e);
                return Directory.Exists(e)
                    ? $"[DIR]  {name}/"
                    : $"[FILE] {name} ({new FileInfo(e).Length / 1024.0:F1} KB)";
            });

        return string.Join('\n', entries);
    }

    // ── create_directory ───────────────────────────────────────────────────────

    private string CreateDirectory(JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        var absPath = workspace.Resolve(path);
        Directory.CreateDirectory(absPath);
        return $"✓ Directory creata: {path}";
    }

    // ── move_file ──────────────────────────────────────────────────────────────

    private string MoveFile(JsonElement args)
    {
        var source = args.GetProperty("source").GetString()!;
        var destination = args.GetProperty("destination").GetString()!;
        var absSrc = workspace.Resolve(source);
        var absDst = workspace.Resolve(destination);

        if (!File.Exists(absSrc))
            return $"File sorgente non trovato: {source}";

        Directory.CreateDirectory(Path.GetDirectoryName(absDst)!);
        File.Move(absSrc, absDst, overwrite: false);
        return $"✓ {source} → {destination}";
    }

    // ── delete_file ────────────────────────────────────────────────────────────

    private string DeleteFile(JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        var absPath = workspace.Resolve(path);

        if (!File.Exists(absPath))
            return $"File non trovato: {path}";

        File.Delete(absPath);
        return $"✓ File eliminato: {path}";
    }

    // ── search_symbol ──────────────────────────────────────────────────────────

    private static readonly HashSet<string> _textExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".ts", ".js", ".tsx", ".jsx", ".py", ".razor", ".html", ".css", ".json", ".xml", ".csproj" };

    private string SearchSymbol(JsonElement args)
    {
        var symbol = args.GetProperty("symbol").GetString()!;
        var extension = args.TryGetProperty("extension", out var ext) ? ext.GetString() : null;

        var regex = new Regex($@"\b{Regex.Escape(symbol)}\b", RegexOptions.Compiled);
        var pattern = string.IsNullOrEmpty(extension) ? "*.*" : $"*{extension}";
        var results = new List<string>();

        var files = Directory.GetFiles(workspace.Root, pattern, SearchOption.AllDirectories)
            .Where(f => !IsIgnored(f))
            .Where(f => !string.IsNullOrEmpty(extension) || _textExtensions.Contains(Path.GetExtension(f)));

        foreach (var file in files)
        {
            try
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        results.Add($"{Path.GetRelativePath(workspace.Root, file)}:{i + 1}");
                        if (results.Count >= 50) goto Done;
                    }
                }
            }
            catch { }
        }

    Done:
        if (results.Count == 0)
            return $"Simbolo '{symbol}' non trovato.";

        var suffix = results.Count >= 50 ? "\n... (troncato a 50)" : "";
        return string.Join('\n', results) + suffix;
    }

    // ── read_file_range ──────────────────────────────────────────────────────

    private string ReadFileRange(JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        var startLine = args.GetProperty("start_line").GetInt32();
        var endLine = args.GetProperty("end_line").GetInt32();
        var absPath = workspace.Resolve(path);

        if (!File.Exists(absPath)) return $"File non trovato: {path}";

        _readPaths.Add(absPath);

        var lines = File.ReadAllLines(absPath);
        int start = Math.Max(0, startLine - 1);
        int end = Math.Min(lines.Length, endLine);

        if (start >= lines.Length) return $"[ERROR] Riga di inizio {startLine} fuori dai limiti.";

        var selected = lines
            .Skip(start)
            .Take(end - start)
            .Select((l, i) => $"{start + i,4} | {l}");

        var header = $"[{path} — righe {startLine}-{endLine} di {lines.Length}]\n";

        return header + string.Join('\n', selected);
    }


    // ── helper ─────────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
