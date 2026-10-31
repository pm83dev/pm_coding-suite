using System.Text.Json;
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

    public async Task<(bool Success, string TurnId)> ExecuteTurnAsync(string userMessage)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var llm = _session.LlmClient;
        var history = _session.History;
        var dispatcher = _session.Dispatcher;
        var sessionManager = _session.SessionManager;
        var maxSteps = _config.AgentSettings.MaxSteps;

        if (llm == null || dispatcher == null || sessionManager == null) return (false, turnId);

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
        // Ensure system message is always first (replace if already present)
        if (history.Count > 0 && history[0].Role == "system")
            history[0] = new ChatMessage { Role = "system", Content = systemPrompt };
        else
            history.Insert(0, new ChatMessage { Role = "system", Content = systemPrompt });

        history.Add(new ChatMessage { Role = "user", Content = userMessage });
        _onEvent(new AgentEventDto { Type = "turn_started", TurnId = turnId });

        // Token di cancellazione: se il client invia "cancel", il loop si ferma
        // al prossimo step (o subito, se la chiamata LLM lo rispetta).
        var ct = _session.TurnCancellationToken;

        for (int step = 1; step <= maxSteps; step++)
        {
            // Check cancel PRIMA di ogni step: se è stato richiesto stop, usciamo subito.
            if (ct.IsCancellationRequested)
            {
                _onEvent(new AgentEventDto { Type = "turn_finished", TurnId = turnId, Reason = "cancelled" });
                return (false, turnId);
            }

            var request = new ChatRequest
            {
                Messages = history.ToList(),
                Tools = dispatcher.AllDefinitions,
                ToolChoice = "auto"
            };

            var messageId = $"msg-{Guid.NewGuid():N}"[..12];
            var (response, usage, _) = await llm.StreamChatAsync(request, token =>
            {
                _onEvent(new AgentEventDto { Type = "assistant_delta", TurnId = turnId, MessageId = messageId, Text = token });
            }, ct: ct);
            history.Add(response);

            // Check cancel DOPO la risposta LLM (prima di eseguire i tool).
            if (ct.IsCancellationRequested)
            {
                _onEvent(new AgentEventDto { Type = "turn_finished", TurnId = turnId, Reason = "cancelled" });
                return (false, turnId);
            }

            // Emetti assistant_done con usage
            _onEvent(new AgentEventDto
            {
                Type = "assistant_done",
                TurnId = turnId,
                MessageId = messageId,
                Usage = new UsageInfoDto { PromptTokens = usage?.PromptTokens ?? 0, CompletionTokens = usage?.CompletionTokens ?? 0 }
            });

            if (response.ToolCalls == null || response.ToolCalls.Count == 0)
            {
                _onEvent(new AgentEventDto { Type = "turn_finished", TurnId = turnId, Reason = "completed" });
                return (true, turnId);
            }

            foreach (var tc in response.ToolCalls)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Parse args from JSON string to object for frontend
                object? argsObj = null;
                if (!string.IsNullOrEmpty(tc.Function.Arguments))
                {
                    try
                    {
                        argsObj = System.Text.Json.JsonSerializer.Deserialize<object>(tc.Function.Arguments);
                    }
                    catch
                    {
                        argsObj = new { raw = tc.Function.Arguments };
                    }
                }

                _onEvent(new AgentEventDto
                {
                    Type = "tool_call_started",
                    TurnId = turnId,
                    CallId = tc.Id,
                    ToolName = tc.Function.Name,
                    Args = argsObj ?? new { }
                });

                // ── Gestione permessi: readonly blocca le modifiche, ask chiede conferma, auto esegue. ──
                string result;
                if (tc.Function.Name == "edit_file" || tc.Function.Name == "write_file")
                {
                    result = await ExecuteWriteWithPermissionAsync(tc, turnId, sw);
                }
                else if (tc.Function.Name == "run_command")
                {
                    result = await ExecuteCommandWithPermissionAsync(tc, turnId, sw);
                }
                else
                {
                    // Strumenti di sola lettura / di supporto (read_file, search, manage_todo, ...).
                    if (_session.PermissionMode == "readonly")
                    {
                        result = $"ERRORE: modalità solo lettura (readonly) — lo strumento '{tc.Function.Name}' non è consentito. " +
                                 "Cambia la modalità permessi nelle Impostazioni per consentire modifiche.";
                    }
                    else
                    {
                        result = dispatcher.Execute(tc.Function.Name, tc.Function.Arguments, CancellationToken.None);
                    }
                }
                sw.Stop();

                _onEvent(new AgentEventDto
                {
                    Type = "tool_call_finished",
                    TurnId = turnId,
                    CallId = tc.Id,
                    Success = true,
                    Summary = result.Length > 200 ? result[..200] + "..." : result,
                    DurationMs = sw.Elapsed.TotalMilliseconds
                });

                history.Add(new ChatMessage { Role = "tool", Content = result, ToolCallId = tc.Id });
            }
        }

        _onEvent(new AgentEventDto { Type = "turn_finished", TurnId = turnId, Reason = "max_steps" });
        return (false, turnId);
    }

    // ── Esecuzione di write_file/edit_file con controllo permessi e diff ──────────────

    private async Task<string> ExecuteWriteWithPermissionAsync(
        ToolCall tc, string turnId, System.Diagnostics.Stopwatch sw)
    {
        var dispatcher = _session.Dispatcher!;
        var workspace = _session.Workspace!;

        JsonElement args;
        try { args = JsonSerializer.Deserialize<JsonElement>(tc.Function.Arguments)!; }
        catch { return "ERRORE: argomenti tool non validi per la modifica."; }

        var path = args.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
        var absPath = workspace.Resolve(path);
        var relPath = SafeRel(workspace, absPath);
        var oldContent = File.Exists(absPath) ? File.ReadAllText(absPath) : "";

        string newContent;
        if (tc.Function.Name == "write_file")
        {
            newContent = args.TryGetProperty("content", out var cp) ? cp.GetString() ?? "" : "";
        }
        else // edit_file
        {
            var oldString = args.TryGetProperty("old_string", out var op) ? op.GetString() ?? "" : "";
            var newString = args.TryGetProperty("new_string", out var np) ? np.GetString() ?? "" : "";
            newContent = oldContent.Contains(oldString, StringComparison.Ordinal)
                ? oldContent.Replace(oldString, newString, StringComparison.Ordinal)
                : oldContent; // match non risolto: il tool restituirà comunque l'errore appropriato
        }

        var diff = UnifiedDiff.Build(oldContent, newContent, relPath);
        var title = $"{tc.Function.Name}: {relPath}";

        if (_session.PermissionMode == "readonly")
            return $"ERRORE: modalità solo lettura (readonly) — la modifica a '{relPath}' non è consentita. " +
                   "Cambia la modalità permessi nelle Impostazioni per consentire modifiche.";

        if (_session.PermissionMode == "ask")
        {
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
            _onEvent(new AgentEventDto { Type = "approval_resolved", ApprovalId = approvalId, Reason = outcome });
            if (!approved)
                return $"⛔ Modifica a '{relPath}' rifiutata o scaduta: NON applicata.";
        }

        return dispatcher.Execute(tc.Function.Name, tc.Function.Arguments, CancellationToken.None);
    }

    // ── Esecuzione di run_command con controllo permessi ──────────────────────────────

    private async Task<string> ExecuteCommandWithPermissionAsync(
        ToolCall tc, string turnId, System.Diagnostics.Stopwatch sw)
    {
        var dispatcher = _session.Dispatcher!;

        JsonElement args;
        try { args = JsonSerializer.Deserialize<JsonElement>(tc.Function.Arguments)!; }
        catch { return "ERRORE: argomenti tool non validi per il comando."; }
        var command = args.TryGetProperty("command", out var cp) ? cp.GetString() ?? "" : "";

        if (_session.PermissionMode == "readonly")
            return $"ERRORE: modalità solo lettura (readonly) — l'esecuzione di comandi non è consentita.";

        if (_session.PermissionMode == "ask")
        {
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
            _onEvent(new AgentEventDto { Type = "approval_resolved", ApprovalId = approvalId, Reason = outcome });
            if (!approved)
                return "⛔ Comando rifiutato o scaduto: NON eseguito.";
        }

        return dispatcher.Execute(tc.Function.Name, tc.Function.Arguments, CancellationToken.None);
    }

    private static string SafeRel(WorkspaceContext workspace, string absPath)
    {
        try { return Path.GetRelativePath(workspace.Root, absPath).Replace('\\', '/'); }
        catch { return absPath; }
    }
}