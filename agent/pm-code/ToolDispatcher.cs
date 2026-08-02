using System.Text.Json;
using System.Text.RegularExpressions;
using LocalCodeAgent.Models;
using LocalCodeAgent.Tools;

namespace LocalCodeAgent.Core;

/// <summary>
/// Registro e dispatcher centralizzato per tutti i tool dell'agente.
/// Include soft-block su read_file duplicati e invalidazione cache su write/edit.
/// </summary>
public class ToolDispatcher
{
    private readonly FileSystemTools _fs;
    private readonly TerminalTools _terminal;
    private readonly AgentTools _agent;
    private readonly GitTools _git;
    private readonly WebTools _web;
    private readonly TodoTools _todo;

    // Cache read_file: path → contenuto già letto (soft-block su duplicati)
    private readonly Dictionary<string, string> _fileCache =
        new(StringComparer.OrdinalIgnoreCase);

    public ToolDispatcher(WorkspaceContext workspace, string? braveApiKey = null, Func<string, string, bool>? proposeWrite = null)
    {
        _fs = new FileSystemTools(workspace, proposeWrite);
        _terminal = new TerminalTools(workspace);
        _agent = new AgentTools(workspace);
        _git = new GitTools(workspace);
        _web = new WebTools(braveApiKey);
        _todo = new TodoTools();
    }

    public List<ToolDefinition> AllDefinitions =>
    [
        .._fs.Definitions,
        .._terminal.Definitions,
        .._git.Definitions,
        .._agent.Definitions,
        .._web.Definitions,
        .._todo.Definitions
    ];

    public List<ToolDefinition> GetDefinitionsByCategory(string category) =>
        category.ToLowerInvariant() switch
        {
            "filesystem" => [.. _fs.Definitions],
            "terminal" => [.. _terminal.Definitions],
            "git" => [.. _git.Definitions],
            "agent" => [.. _agent.Definitions],
            "web" => [.. _web.Definitions],
            "todo" => [.. _todo.Definitions],
            _ => []
        };

    public List<ToolDefinition> GetDefinitionsByName(string name) =>
        AllDefinitions.Where(t => t.Function.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();


    // Restituisce solo i tool rilevanti per il contesto del messaggio utente.
    // Riduce il prompt del 60-80% rispetto ad AllDefinitions.
    public List<ToolDefinition> GetRelevantDefinitions(string context)
    {
        var lc = context.ToLowerInvariant();
        var defs = new List<ToolDefinition>();

        // Core — sempre inclusi (operazioni file + ricerca + workspace + build + shell)
        defs.AddRange(_fs.Definitions.Where(t => t.Function.Name is
            "read_file" or "edit_file" or "write_file" or
            "search_in_files" or "sym_search" or "glob_files"));
        defs.Add(_agent.Definitions.First(t => t.Function.Name == "get_workspace_tree"));
        defs.Add(_terminal.Definitions.First(t => t.Function.Name == "sol_analyze"));
        defs.AddRange(_terminal.Definitions.Where(t => t.Function.Name is
            "run_command" or "get_background_output" or "stop_background_job" or "list_background_jobs"));
        // Core — sempre inclusi (operazioni file + ricerca + workspace + build + shell)
        defs.AddRange(_fs.Definitions.Where(t => t.Function.Name is
            "read_file" or "edit_file" or "write_file" or
            "search_in_files" or "sym_search" or "glob_files" or
            "read_file_range"));
        defs.AddRange(_todo.Definitions);

        // Terminal — su task che richiedono dotnet specifico
        if (Has(lc, "test", "package", "nuget", "add ", "pubbl", "run_dotnet"))
            defs.Add(_terminal.Definitions.First(t => t.Function.Name == "run_dotnet"));

        // Navigazione directory — su task che richiedono gestione cartelle/workspace
        if (Has(lc, "dir", "cartell", "folder", "list", "sposta", "rinomina", "elimina", "cancella", "struttura", "workspace", "init"))
        {
            defs.AddRange(_fs.Definitions.Where(t => t.Function.Name is
                "list_directory" or "create_directory" or "move_file" or "delete_file"));
            defs.AddRange(_agent.Definitions.Where(t => t.Function.Name is "dir_info" or "set_workspace"));
        }



        // Git — su task git
        if (Has(lc, "git", "commit", "push", "branch", "diff", "merge", "stash", "log", "stage"))
            defs.AddRange(_git.Definitions);

        // Web — su task che richiedono accesso internet o documentazione online
        if (Has(lc, "web", "http", "url", "cerca online", "internet", "documentazion", "fetch", "download", "sito", "api key", "nuget.org"))
            defs.AddRange(_web.Definitions);

        return [.. defs.DistinctBy(t => t.Function.Name)];
    }

    private static bool Has(string s, params string[] kw) =>
        kw.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

    public void ClearCache() => _fileCache.Clear();

    public void ResetCwd() => _terminal.ResetCwd();

    // Il piano è legato alla conversazione, non al workspace: va azzerato insieme alla
    // history su /reset (CLI) o su nuova chat/cambio workspace (stdin-protocol) — altrimenti
    // un vecchio piano di un task concluso resterebbe visibile nel prossimo task scollegato.
    public void ResetTodo() => _todo.Reset();

    public string Execute(string toolName, string argumentsJson)
    {
        if (toolName == "manage_todo")
            return _todo.Execute(argumentsJson);

        // ── Gestione read_file_range ──────────────────────────────────────
        if (toolName == "read_file_range")
        {
            try
            {
                var el = JsonDocument.Parse(argumentsJson).RootElement;
                var path = el.GetProperty("path").GetString() ?? "";
                var startLine = el.GetProperty("start_line").GetInt32();
                var endLine = el.GetProperty("end_line").GetInt32();

                var result = _fs.Execute(toolName, argumentsJson);
                return result;
            }
            catch { /* Passa al routing normale */ }
        }

        // ── Invalidazione cache su write/edit ─────────────────────────────────
        if (toolName is "write_file" or "edit_file" or "delete_file" or "move_file")
        {
            try
            {
                var el = JsonDocument.Parse(argumentsJson).RootElement;
                if (el.TryGetProperty("path", out var pathProp))
                    _fileCache.Remove(pathProp.GetString() ?? "");
                // move_file: invalida anche sorgente
                if (toolName == "move_file" && el.TryGetProperty("source", out var srcProp))
                    _fileCache.Remove(srcProp.GetString() ?? "");
            }
            catch { }
        }

        // ── Validazione JSON pre-dispatch ────────────────────────────────────
        // Intercetta JSON malformato (es. virgolette non escapate in content/old_string)
        // prima che raggiunga il singolo tool, dove l'errore sarebbe generico e inutile.
        if (toolName is "write_file" or "edit_file" or "move_file" or "delete_file" or "create_directory")
        {
            try { JsonDocument.Parse(argumentsJson); }
            catch (System.Text.Json.JsonException ex)
            {
                return $"ERRORE JSON argomenti (colonna ~{ex.BytePositionInLine}): virgolette o caratteri speciali non escapati " +
                       $"in 'content' o 'old_string'.\n" +
                       $"REGOLE anti-errore:\n" +
                       $"• edit_file: usa old_string con MAX 10 righe — non copiare interi metodi\n" +
                       $"• write_file: solo per file senza virgolette nel codice (config, plain text)\n" +
                       $"• Per codice C# con $\", [Attr(\"..\")], ecc.: crea prima la struttura " +
                       $"base con write_file (classe vuota), poi aggiungi il contenuto con edit_file riga per riga.";
            }
        }

        // ── Routing ───────────────────────────────────────────────────────────
        if (_fs.Definitions.Any(t => t.Function.Name == toolName))
            return _fs.Execute(toolName, argumentsJson);

        if (_terminal.Definitions.Any(t => t.Function.Name == toolName))
            return _terminal.Execute(toolName, argumentsJson);

        if (_git.Definitions.Any(t => t.Function.Name == toolName))
            return _git.Execute(toolName, argumentsJson);

        if (_agent.Definitions.Any(t => t.Function.Name == toolName))
        {
            var result = _agent.Execute(toolName, argumentsJson);
            if (toolName == "set_workspace") _terminal.ResetCwd();
            return result;
        }

        if (_web.Definitions.Any(t => t.Function.Name == toolName))
            return _web.Execute(toolName, argumentsJson);

        return $"Tool '{toolName}' non registrato nel dispatcher.";
    }

    private static string BuildFileSummary(string path, string cachedContent)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var lines = cachedContent.Split('\n');
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"File già letto: '{path}' ({lines.Length} righe)");
        sb.AppendLine("Riepilogo:");

        if (ext == ".cs")
        {
            var types = Regex.Matches(cachedContent,
                @"(?:public|private|protected|internal|static|abstract|sealed)\s+(?:class|interface|enum|record|struct)\s+(\w+)",
                RegexOptions.Multiline)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct();

            var typesStr = string.Join(", ", types);
            if (!string.IsNullOrEmpty(typesStr))
                sb.AppendLine($"- Tipi: {typesStr}");

            var methods = Regex.Matches(cachedContent,
                @"(?:public|private|protected|internal|static|async|override|virtual)\s+[\w<>\[\]?]+\s+(\w+)\s*\(",
                RegexOptions.Multiline)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(n => n is not ("if" or "while" or "for" or "foreach" or "switch" or "return"))
                .Distinct()
                .Take(15);

            var methodsStr = string.Join(", ", methods);
            if (!string.IsNullOrEmpty(methodsStr))
                sb.AppendLine($"- Metodi: {methodsStr}");
        }
        else
        {
            var preview = lines
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Take(4)
                .Select(l => $"  {l.Trim()[..Math.Min(80, l.Trim().Length)]}");
            sb.AppendLine(string.Join('\n', preview));
        }

        sb.AppendLine("Usa edit_file per modificarlo, o read_file con start_line/end_line per una sezione.");
        return sb.ToString().TrimEnd();
    }
}
