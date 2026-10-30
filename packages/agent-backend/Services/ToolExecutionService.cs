using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using AgentBackend.Models.Dto;

namespace AgentBackend.Services;

public class ToolExecutionService
{
    private readonly AgentSession _session;
    private readonly Action<AgentEventDto> _onEvent;
    private readonly AgentConfig _config;

    public ToolExecutionService(AgentSession session, Action<AgentEventDto> onEvent, AgentConfig config)
    {
        _session = session;
        _onEvent = onEvent;
        _config = config;
    }

    public async Task<bool> ExecuteTurnAsync(string userMessage)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var llm = _session.LlmClient;
        var history = _session.History;
        var dispatcher = _session.Dispatcher;
        var sessionManager = _session.SessionManager;
        var maxSteps = _config.AgentSettings.MaxSteps;

        if (llm == null || dispatcher == null || sessionManager == null) return false;

        // Build system prompt (stesso formato di pm-code)
        var workspace = _session.Workspace!;
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
            "- La build dice solo che compila. Esegui il programma e controlla l'esito reale.\n" +
            "- Ultimo passo del piano: \"Test funzionale: <comando> → atteso: <risultato>\".\n" +
            "- Se il test fallisce: correggi e riesegui. Fermati dopo 3 tentativi.\n" +
            "- Salva con project_notes come si avvia/testa il progetto.\n";
        history.Insert(0, new ChatMessage { Role = "system", Content = systemPrompt });

        history.Add(new ChatMessage { Role = "user", Content = userMessage });
        _onEvent(new AgentEventDto { Type = "turn_started", TurnId = turnId });

        for (int step = 1; step <= maxSteps; step++)
        {
            var request = new ChatRequest
            {
                Messages = history.ToList(),
                Tools = dispatcher.AllDefinitions,
                ToolChoice = "auto"
            };

            var (response, usage, _) = await llm.StreamChatAsync(request, token =>
            {
                _onEvent(new AgentEventDto { Type = "assistant_delta", TurnId = turnId, Text = token });
            }, ct: CancellationToken.None);
            history.Add(response);

            if (response.ToolCalls == null || response.ToolCalls.Count == 0)
            {
                _onEvent(new AgentEventDto { Type = "turn_finished", TurnId = turnId, Reason = "completed" });
                return true;
            }

            foreach (var tc in response.ToolCalls)
            {
                _onEvent(new AgentEventDto { Type = "tool_call_start", TurnId = turnId, CallId = tc.Id, ToolName = tc.Function.Name });

                var result = dispatcher.Execute(tc.Function.Name, tc.Function.Arguments, CancellationToken.None);

                _onEvent(new AgentEventDto { Type = "tool_call_done", TurnId = turnId, CallId = tc.Id, ToolName = tc.Function.Name, Summary = result.Length > 200 ? result[..200] + "..." : result });

                history.Add(new ChatMessage { Role = "tool", Content = result, ToolCallId = tc.Id });
            }
        }

        _onEvent(new AgentEventDto { Type = "turn_finished", TurnId = turnId, Reason = "max_steps" });
        return false;
    }
}