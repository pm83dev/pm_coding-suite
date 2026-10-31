using System.Text.Json;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using AgentBackend.Hubs;
using AgentBackend.Models.Dto;

namespace AgentBackend.Services;

/// <summary>
/// Sostituisce <c>ToolExecutionService</c>: tiene un <see cref="AgentLoop"/> per sessione
/// e gestisce il ciclo di vita di un turno (permessi, approvazione, eventi WS).
/// Il loop ReAct è interamente nel core (<c>AgentLoop.RunTurnAsync</c>); qui si aggiunge
/// solo la logica di permesso specifica dell'agent-backend (readonly/ask/auto) e la
/// traduzione degli eventi in <see cref="AgentEventDto"/> via <see cref="WebSocketSink"/>.
/// </summary>
public class AgentLoopSession
{
    private readonly AgentSession _session;
    private readonly Action<AgentEventDto> _onEvent;
    private readonly AgentConfig _config;

    public AgentLoopSession(AgentSession session, Action<AgentEventDto> onEvent, AgentConfig config)
    {
        _session = session;
        _onEvent = onEvent;
        _config = config;
    }

    /// <summary>
    /// Esegue un turno completo (loop ReAct). Restituisce <c>(Success, TurnId)</c>.
    /// </summary>
    public async Task<(bool Success, string TurnId)> RunTurnAsync(string userMessage)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var llm = _session.LlmClient;
        var history = _session.History;
        var dispatcher = _session.Dispatcher;
        var sessionManager = _session.SessionManager;
        var workspace = _session.Workspace;

        if (llm == null || dispatcher == null || sessionManager == null || workspace == null)
            return (false, turnId);

        // Build system prompt (stesso formato di pm-code)
        var agentsMdPath = Path.Combine(workspace.Root, "AGENTS.md");
        var agentsContext = File.Exists(agentsMdPath)
            ? $"\nCONTESTO DEL PROGETTO (da AGENTS.md):\n{File.ReadAllText(agentsMdPath)}\n"
            : "\nNessun AGENTS.md trovato. Usa /init per generarlo.\n";

        var plan = dispatcher.PlanSummary();
        var planContext = plan == null ? "" : $"\nPIANO IN CORSO (gestito con manage_todo):\n{plan}\nSe la richiesta riguarda questo piano: riparti dal passo [IN CORSO]. Se è un task diverso: crea un nuovo piano.\n";

        var lessons = dispatcher.LessonsSummary();
        var lessonsContext = lessons == null ? "" : $"\nLEZIONI APPRESE (gestite con lessons; rispettale):\n{lessons}\n";

        var notes = dispatcher.ProjectNotesSummary();
        var notesContext = notes == null
            ? "\nMEMORIA DEL PROGETTO: vuota. Salva con project_notes quando scopri come avviare/testare.\n"
            : $"\nMEMORIA DEL PROGETTO (gestite con project_notes — usale):\n{notes}\n";

        var tree = workspace.GetTreeSnapshot();

        var systemPrompt = $"Sei un agente di sviluppo software autonomo. Workspace: {workspace.Root}{agentsContext}{lessonsContext}{notesContext}{planContext}" +
            $"\nSTRUTTURA DEL WORKSPACE:\n{tree}\n" +
            "METODO DI LAVORO — PICCOLI PASSI:\n" +
            "1. Crea il piano con manage_todo action=\"create\": passi piccoli e verificabili.\n" +
            "2. manage_todo update → in_progress per il passo corrente.\n" +
            "3. Leggi SOLO il codice necessario: search_in_files/sym_search + read_file_range.\n" +
            "4. Esegui la modifica con edit_file (blocchi piccoli).\n" +
            "5. Verifica: sol_analyze per .NET, oppure build/test del progetto.\n" +
            "6. manage_todo update → completed con note breve.\n" +
            "7. Passa al passo successivo senza rispondere all'utente.\n" +
            "Rispondi solo quando tutti i passi sono completi o ti serve una decisione.\n" +
            "\nVALIDAZIONE FUNZIONALE:\n" +
            "- La build dice solo che compila. Esegui il programma e controlla il comportamento.\n" +
            "- Se il programma richiede input: usa run_command con timeout e input via pipeline.\n" +
            "- Se non riesci a eseguirlo: dichiaralo esplicitamente nella risposta.\n" +
            "\nSTILE: risposte brevi e dirette. Non ripetere il piano a ogni passo.\n";

        // Aggiorna il system prompt nella history (primo messaggio)
        if (history.Count > 0 && history[0].Role == "system")
            history[0].Content = systemPrompt;
        else
            history.Insert(0, ChatMessage.System(systemPrompt));

        // Aggiunge il messaggio utente
        history.Add(ChatMessage.User(userMessage));

        // Costruisce l'AgentLoop con il sink WebSocket
        var sink = new WebSocketSink(_onEvent);
        var options = new AgentLoopOptions
        {
            IsMachineMode = true,
            IsServeMode = false,
            MaxSteps = _config.AgentSettings.MaxSteps,
            MaxContextTokens = _config.AgentSettings.MaxContextTokens,
            MaxRepeats = _config.AgentSettings.MaxRepeats,
            MaxConsecutiveReadOnly = _config.AgentSettings.MaxConsecutiveReadOnly,
            MaxLinesPerFile = _config.AgentSettings.MaxLinesPerFile,
            Temperature = _config.LlmSettings.Temperature,
            FrequencyPenalty = _config.LlmSettings.FrequencyPenalty,
            PresencePenalty = _config.LlmSettings.PresencePenalty,
            TopP = _config.LlmSettings.TopP,
            TopK = _config.LlmSettings.TopK,
            MinP = _config.LlmSettings.MinP,
            RepeatPenalty = _config.LlmSettings.RepeatPenalty,
            RepeatLastN = _config.LlmSettings.RepeatLastN
        };

        var loop = new AgentLoop(
            llm,
            dispatcher,
            sessionManager,
            sink,
            workspace,
            null, // exploreAgent: non usato in agent-backend
            options,
            _ => { }, // ui: no-op (gli eventi vanno via sink)
            _ => { }, // trace: no-op
            _ => { }, // notice: già via sink
            text => _onEvent(new AgentEventDto { Type = "status", TurnId = turnId, Text = text }),
            (toolName, argsJson, ct) => CheckPermissionAsync(toolName, argsJson, turnId)
        );

        try
        {
            var success = await loop.RunTurnAsync(history, _session.TurnCancellationToken, 32768, turnId);
            return (success, turnId);
        }
        catch (OperationCanceledException)
        {
            sink.TurnFinished(turnId, "cancelled");
            return (false, turnId);
        }
        catch (Exception ex)
        {
            sink.Error(ex.Message);
            sink.TurnFinished(turnId, "error");
            return (false, turnId);
        }
    }

    // ── Controllo permessi (readonly/ask/auto) ──────────────────────────────────

    /// <summary>
    /// Hook di permesso chiamato dal core AgentLoop prima di ogni esecuzione tool.
    /// Restituisce null per procedere, oppure una stringa di errore per bloccare.
    /// </summary>
    private async Task<string?> CheckPermissionAsync(string toolName, string argsJson, string turnId)
    {
        var mode = _session.PermissionMode;
        if (mode == "auto") return null; // nessun controllo

        // Strumenti di sola lettura: sempre consentiti
        var readOnlyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "read_file", "read_file_range", "glob_files", "search_in_files", "sym_search",
            "list_directory", "get_workspace_tree", "get_directory_details",
            "git_status", "git_diff", "git_log", "get_background_output", "list_background_jobs",
            "manage_todo", "project_notes", "lessons"
        };
        if (readOnlyTools.Contains(toolName)) return null;

        // In modalità readonly, tutto il resto è bloccato
        if (mode == "readonly")
        {
            return $"ERRORE: modalità solo lettura (readonly) — lo strumento '{toolName}' non è consentito. " +
                   "Cambia la modalità permessi nelle Impostazioni per consentire modifiche.";
        }

        // In modalità ask, le modifiche e i comandi richiedono approvazione
        if (mode == "ask")
        {
            if (toolName is "edit_file" or "write_file")
                return await CheckWritePermissionAsync(toolName, argsJson, turnId);
            if (toolName is "run_command" or "run_dotnet")
                return await CheckCommandPermissionAsync(argsJson, turnId);
        }

        return null; // altri tool: consentiti anche in ask
    }

    private async Task<string?> CheckWritePermissionAsync(string toolName, string argsJson, string turnId)
    {
        var workspace = _session.Workspace;
        if (workspace == null) return null;

        JsonElement args;
        try { args = JsonSerializer.Deserialize<JsonElement>(argsJson)!; }
        catch { return "ERRORE: argomenti tool non validi per la modifica."; }

        var path = args.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
        var absPath = workspace.Resolve(path);
        var relPath = SafeRel(workspace, absPath);
        var oldContent = File.Exists(absPath) ? File.ReadAllText(absPath) : "";

        string newContent;
        if (toolName == "write_file")
        {
            newContent = args.TryGetProperty("content", out var cp) ? cp.GetString() ?? "" : "";
        }
        else // edit_file
        {
            var oldString = args.TryGetProperty("old_string", out var op) ? op.GetString() ?? "" : "";
            var newString = args.TryGetProperty("new_string", out var np) ? np.GetString() ?? "" : "";
            newContent = oldContent.Contains(oldString, StringComparison.Ordinal)
                ? oldContent.Replace(oldString, newString, StringComparison.Ordinal)
                : oldContent;
        }

        var diff = UnifiedDiff.Build(oldContent, newContent, relPath);
        var title = $"{toolName}: {relPath}";

        var approvalId = Guid.NewGuid().ToString("N");
        _onEvent(new AgentEventDto
        {
            Type = "approval_request",
            TurnId = turnId,
            ApprovalId = approvalId,
            Kind = "write",
            Title = title,
            Path = relPath,
            UnifiedDiff = string.IsNullOrEmpty(diff) ? "(nessuna differenza)" : diff,
            ExpiresAt = DateTime.UtcNow.AddSeconds(300).ToString("o")
        });

        var approved = await _session.AwaitApprovalAsync(300);
        var outcome = approved ? "approved" : "rejected";
        _onEvent(new AgentEventDto { Type = "approval_resolved", ApprovalId = approvalId, Outcome = outcome });

        if (!approved)
            return $"⛔ Modifica a '{relPath}' rifiutata o scaduta: NON applicata.";

        return null; // approvato: procedi con l'esecuzione
    }

    private async Task<string?> CheckCommandPermissionAsync(string argsJson, string turnId)
    {
        JsonElement args;
        try { args = JsonSerializer.Deserialize<JsonElement>(argsJson)!; }
        catch { return "ERRORE: argomenti tool non validi per il comando."; }

        var command = args.TryGetProperty("command", out var cp) ? cp.GetString() ?? "" : "";

        var approvalId = Guid.NewGuid().ToString("N");
        _onEvent(new AgentEventDto
        {
            Type = "approval_request",
            TurnId = turnId,
            ApprovalId = approvalId,
            Kind = "command",
            Title = "Esegui comando",
            Command = command,
            ExpiresAt = DateTime.UtcNow.AddSeconds(300).ToString("o")
        });

        var approved = await _session.AwaitApprovalAsync(300);
        var outcome = approved ? "approved" : "rejected";
        _onEvent(new AgentEventDto { Type = "approval_resolved", ApprovalId = approvalId, Outcome = outcome });

        if (!approved)
            return "⛔ Comando rifiutato o scaduto: NON eseguito.";

        return null; // approvato: procedi con l'esecuzione
    }

    private static string SafeRel(WorkspaceContext workspace, string absPath)
    {
        try { return Path.GetRelativePath(workspace.Root, absPath).Replace('\\', '/'); }
        catch { return absPath; }
    }
}