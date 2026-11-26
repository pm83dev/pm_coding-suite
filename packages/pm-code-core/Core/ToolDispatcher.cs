using System.Text.Json;
using LocalCodeAgent.Models;
using LocalCodeAgent.Tools;

namespace LocalCodeAgent.Core;

/// <summary>
/// Registro e dispatcher centralizzato per tutti i tool dell'agente.
/// </summary>
public class ToolDispatcher
{
    private readonly FileSystemTools _fs;
    private readonly TerminalTools _terminal;
    private readonly AgentTools _agent;
    private readonly GitTools _git;
    private readonly WebTools _web;
    private readonly TodoTools _todo;
    private readonly ProjectNotesTools _notes;
    private readonly ProjectNotesTools _lessons;
    private readonly AskQuestionTool _ask;
    private readonly ImageTools _image;

    public ToolDispatcher(WorkspaceContext workspace, string? braveApiKey = null, Func<string, string, bool>? proposeWrite = null,
        VisionSettings? vision = null)
    {
        _image = new ImageTools(workspace, vision);
        _fs = new FileSystemTools(workspace, proposeWrite);
        _terminal = new TerminalTools(workspace);
        _agent = new AgentTools(workspace);
        _git = new GitTools(workspace);
        _web = new WebTools(braveApiKey);
        _todo = new TodoTools(workspace);
        _todo.Reload();
        _notes = ProjectNotesTools.ForProject(workspace);
        _lessons = ProjectNotesTools.GlobalLessons();
        _ask = new AskQuestionTool();
    }

    public List<ToolDefinition> AllDefinitions =>
    [
        .._fs.Definitions,
        .._terminal.Definitions,
        .._git.Definitions,
        .._agent.Definitions,
        .._web.Definitions,
        .._todo.Definitions,
        .._notes.Definitions,
        .._lessons.Definitions,
        .._ask.Definitions,
        .._image.Definitions
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
            "read_file" or
            "search_in_files" or "sym_search" or "glob_files" or
            "read_file_range"));
        defs.AddRange(_todo.Definitions);
        defs.AddRange(_notes.Definitions);
        defs.AddRange(_lessons.Definitions);
        defs.AddRange(_ask.Definitions);

        // analyze_image: sempre incluso se configurato (Definitions vuoto altrimenti) — niente
        // attivazione a parole chiave, così il blocco tool resta stabile per la prompt cache.
        defs.AddRange(_image.Definitions);

        // Git in sola lettura — sempre inclusi: con un prompt tipo "prosegui" (nessuna parola
        // chiave git) il modello non li aveva e ripiegava su run_command "git diff | Select-Object
        // -Skip/-First", leggendo diff enormi a finestre troncate per decine di step.
        defs.AddRange(_git.Definitions.Where(t => t.Function.Name is "git_status" or "git_diff" or "git_log"));

        // delete_file sempre disponibile: run_command non può più cancellare, e senza questo
        // tool (prima solo su parole chiave come "elimina") il modello non avrebbe nessuna via
        // legittima — delete_file sposta nel cestino recuperabile .pm-agent/trash.
        defs.AddRange(_fs.Definitions.Where(t => t.Function.Name == "delete_file"));

        // Gruppi opzionali: attivati dalle parole chiave del messaggio, poi MAI più disattivati per
        // la durata della sessione (solo aggiunte). Il blocco tool sta all'inizio del prompt reso dal
        // chat template: un tool che compare e scompare tra un turno e l'altro invalida l'intera
        // cache del prompt di llama-server. Sono aggiunti sempre nello stesso ordine fisso
        // (dotnet, nav, git, web), indipendente dall'ordine di attivazione.
        lock (_groupsLock)
        {
            if (Has(lc, "test", "package", "nuget", "add ", "pubbl", "run_dotnet")) _activeGroups.Add("dotnet");
            if (Has(lc, "dir", "cartell", "folder", "list", "sposta", "rinomina", "elimina", "cancella", "struttura", "workspace", "init")) _activeGroups.Add("nav");
            if (Has(lc, "git", "commit", "push", "branch", "diff", "merge", "stash", "log", "stage")) _activeGroups.Add("git");
            if (Has(lc, "web", "http", "url", "cerca online", "internet", "documentazion", "fetch", "download", "sito", "api key", "nuget.org")) _activeGroups.Add("web");

            // Terminal — task che richiedono dotnet specifico
            if (_activeGroups.Contains("dotnet"))
                defs.Add(_terminal.Definitions.First(t => t.Function.Name == "run_dotnet"));

            // Navigazione directory — gestione cartelle/workspace
            if (_activeGroups.Contains("nav"))
            {
                defs.AddRange(_fs.Definitions.Where(t => t.Function.Name is
                    "list_directory" or "create_directory" or "move_file" or "delete_file"));
                defs.AddRange(_agent.Definitions.Where(t => t.Function.Name is "dir_info" or "set_workspace"));
            }

            // Git — tutti i tool git (in sola lettura sono già inclusi sopra)
            if (_activeGroups.Contains("git"))
                defs.AddRange(_git.Definitions);

            // Web — accesso internet o documentazione online
            if (_activeGroups.Contains("web"))
                defs.AddRange(_web.Definitions);
        }

        return [.. defs.DistinctBy(t => t.Function.Name)];
    }

    // Gruppi di tool opzionali già attivati in questa sessione (solo aggiunte, vedi GetRelevantDefinitions).
    private readonly HashSet<string> _activeGroups = [];
    private readonly object _groupsLock = new();

    private static bool Has(string s, params string[] kw) =>
        kw.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

    public void ClearCache() => _fs.ClearReadCache();

    public void ResetCwd() => _terminal.ResetCwd();

    public string? ReapOrphanJobs() => _terminal.ReapOrphanJobs();

    public void SnapshotJobs() => _terminal.SnapshotJobs();

    public List<(int Pid, string Command)> RunningJobs() => _terminal.RunningJobs();

    // Il piano è salvato su disco per workspace (.pm-agent/plan.json): su /reset, nuova chat o
    // cambio workspace viene RICARICATO, non azzerato, così "prosegui" in una chat nuova riparte
    // dal primo passo non completato. Un piano concluso o abbandonato si cancella con
    // manage_todo action="clear" (e il system prompt lo mostra solo se ha passi non completati).
    public void ResetTodo() => _todo.Reload();

    public string? PlanSummary() => _todo.PromptSummary();

    public bool HasPendingPlan => _todo.HasPendingItems;

    public string? ProjectNotesSummary() => _notes.PromptSummary();

    public string? LessonsSummary() => _lessons.PromptSummary();

    // ct: CancellationToken del turno corrente (vedi Program.cs/RunAgentLoopAsync). Oggi nessun
    // tool lo usa per annullarsi davvero (nessun chiamante lo annulla mai) — plumbing per un
    // futuro comando "cancel" lato UI/estensione, non cambia alcun comportamento osservabile.
    public string Execute(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        if (toolName == "manage_todo")
            return _todo.Execute(argumentsJson);

        if (toolName == "project_notes")
            return _notes.Execute(argumentsJson);

        if (toolName == "lessons")
            return _lessons.Execute(argumentsJson);

        if (toolName == AskQuestionTool.ToolName)
            return _ask.Execute(argumentsJson);

        if (toolName == ImageTools.ToolName)
            return _image.Execute(toolName, argumentsJson, ct);

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
            return _terminal.Execute(toolName, argumentsJson, ct);

        if (_git.Definitions.Any(t => t.Function.Name == toolName))
            return _git.Execute(toolName, argumentsJson);

        if (_agent.Definitions.Any(t => t.Function.Name == toolName))
        {
            var result = _agent.Execute(toolName, argumentsJson);
            if (toolName == "set_workspace") _terminal.ResetCwd();
            return result;
        }

        if (_web.Definitions.Any(t => t.Function.Name == toolName))
            return _web.Execute(toolName, argumentsJson, ct);

        return $"Tool '{toolName}' non registrato nel dispatcher.";
    }
}
