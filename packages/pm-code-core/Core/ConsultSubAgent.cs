using System.Diagnostics;
using System.Text.Json;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Core;

/// <summary>
/// Eccezione interna: la chiamata al modello di supporto ha superato <see cref="SecondOpinionSettings.TimeoutSeconds"/>.
/// Distinta da una cancellazione reale dell'utente (vedi <see cref="ConsultSubAgent.AskAsync"/>): solo questa produce
/// il messaggio di fallback "non disponibile (timeout)", una cancellazione vera deve invece propagare.
/// </summary>
internal sealed class SecondOpinionTimeoutException : Exception;

/// <summary>
/// Eccezione interna: errore di rete/HTTP/parsing verso il modello di supporto (connessione rifiutata,
/// 5xx, risposta non JSON valido, ecc.) — mai un'eccezione non gestita che risale al loop principale.
/// </summary>
internal sealed class SecondOpinionCallException(string message) : Exception(message);

/// <summary>
/// Sotto-agente di SECONDO PARERE (tool "ask_second_opinion"). Quando l'agente principale è
/// bloccato su un problema (ha ritentato più volte lo stesso comando, o non capisce un
/// comportamento) può chiedere a un modello indipendente — endpoint/modello dedicati e
/// configurabili, vedi <see cref="SecondOpinionSettings"/> (INDIPENDENTE da quello di
/// <see cref="ReviewSubAgent"/>/<see cref="CoderLlmSettings"/>, anche se può ricadere sullo
/// stesso endpoint se non configurato separatamente — vedi il chiamante) — un'ipotesi diversa,
/// con contesto separato: niente del loop (tentativi falliti, frustrazione del modello) passa
/// al sotto-agente, solo il problema descritto. Come <see cref="ExploreSubAgent"/>, in sola
/// lettura: può leggere codice per contesto ma non modificare nulla né eseguire comandi.
/// </summary>
public class ConsultSubAgent(LlamaClient llm, ToolDispatcher dispatcher, WorkspaceContext workspace, SecondOpinionSettings settings)
{
    public const string ToolName = "ask_second_opinion";

    private const int MaxToolResultChars = 6000;

    private static readonly HashSet<string> AllowedTools =
    [
        "read_file", "read_file_range", "glob_files", "search_in_files", "sym_search",
        "list_directory", "get_workspace_tree", "dir_info",
        "git_status", "git_diff", "git_log",
    ];

    public static ToolDefinition Definition => new()
    {
        Function = new()
        {
            Name = ToolName,
            Description = "Chiedi un secondo parere a un modello indipendente, con contesto separato dal tuo (non vede i " +
                          "tuoi tentativi falliti, solo quello che descrivi). Usalo quando sei bloccato su un problema (hai " +
                          "ritentato più volte senza risultati nuovi, o un comportamento non ti è chiaro) PRIMA di arrenderti " +
                          "o di chiedere all'utente — può leggere il codice per contesto ma non modificare nulla. Non usarlo " +
                          "per domande semplici con risposta ovvia dal codice già letto.\n\n" +
                          "Struttura il parametro 'problem' in queste sezioni (scrivi solo quelle che hai davvero, non inventare " +
                          "le altre):\n" +
                          "GOAL: cosa stavi cercando di ottenere.\n" +
                          "CONSTRAINTS: vincoli noti (versioni, API da non cambiare, requisiti dell'utente).\n" +
                          "ATTEMPTS: ogni tentativo già fatto → perché è fallito (non solo l'elenco, anche il motivo).\n" +
                          "EXACT ERROR/OUTPUT: il testo ESATTO dell'errore o dell'output, non un riassunto.\n" +
                          "RELEVANT CODE: percorso:riga del codice rilevante, se lo conosci già.\n" +
                          "QUESTION: la domanda precisa a cui ti serve risposta.\n\n" +
                          "Il parere va VERIFICATO prima di applicarlo: la risposta includerà un passo VERIFY — eseguilo, non " +
                          "applicare il fix suggerito alla cieca.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    problem = new { type = "string", description = "Briefing strutturato: GOAL / CONSTRAINTS / ATTEMPTS (tentativo → perché è fallito) / EXACT ERROR (testo esatto, non riassunto) / RELEVANT CODE / QUESTION. Sintetizza prima i tentativi falliti invece di incollare l'intera cronologia." }
                },
                required = new[] { "problem" }
            }
        }
    };

    public async Task<string> RunAsync(string argumentsJson, Action<string>? onStatus = null, CancellationToken ct = default)
    {
        string problem;
        try
        {
            var el = JsonDocument.Parse(argumentsJson).RootElement;
            problem = el.TryGetProperty("problem", out var p) ? p.GetString()?.Trim() ?? "" : "";
        }
        catch { problem = ""; }
        if (problem.Length == 0) return "ERRORE: 'problem' vuoto.";

        dispatcher.ClearCache();

        var tools = dispatcher.AllDefinitions.Where(t => AllowedTools.Contains(t.Function.Name)).ToList();
        var history = new List<ChatMessage> { ChatMessage.System(BuildSystemPrompt()), ChatMessage.User(problem) };
        var seenCalls = new HashSet<string>();
        var sw = Stopwatch.StartNew();

        try
        {
            for (var step = 1; step <= settings.MaxStepsSubAgent; step++)
            {
                onStatus?.Invoke($"second opinion: step {step}/{settings.MaxStepsSubAgent}…");
                var message = await AskAsync(history, tools, ct);
                history.Add(message);

                if (message.ToolCalls is not { Count: > 0 })
                    return Finish(message.Content, sw.ElapsedMilliseconds, "ok");

                foreach (var tc in message.ToolCalls)
                {
                    onStatus?.Invoke($"second opinion → {tc.Function.Name} {Short(tc.Function.Arguments, 80)}");
                    history.Add(ChatMessage.ToolResult(tc.Id, Execute(tc.Function.Name, tc.Function.Arguments, seenCalls, ct)));
                }
            }

            history.Add(ChatMessage.User("Limite di step raggiunto: non puoi più usare tool. Rispondi ORA con la tua " +
                                         "ipotesi/opinione nel formato richiesto, indicando cosa non sei riuscito a verificare."));
            return Finish((await AskAsync(history, null, ct)).Content, sw.ElapsedMilliseconds, "ok");
        }
        catch (SecondOpinionTimeoutException)
        {
            LogOutcome("timeout", sw.ElapsedMilliseconds, 0);
            return "[SECOND OPINION] non disponibile (timeout). Procedi con la tua ipotesi migliore o chiedi all'utente.";
        }
        catch (SecondOpinionCallException ex)
        {
            LogOutcome("errore", sw.ElapsedMilliseconds, 0);
            return $"[SECOND OPINION] non disponibile (errore: {ex.Message}). Procedi con la tua ipotesi migliore o chiedi all'utente.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellazione reale del turno (utente): deve propagare, non essere inghiottita come
            // un errore qualunque — altrimenti un "Annulla" durante ask_second_opinion risulterebbe
            // in un tool_result di errore invece che in un turno davvero cancellato.
            throw;
        }
        catch (Exception ex)
        {
            LogOutcome("errore", sw.ElapsedMilliseconds, 0);
            return $"ERRORE: il sotto-agente ask_second_opinion si è interrotto ({ex.Message}).";
        }
    }

    private async Task<ChatMessage> AskAsync(List<ChatMessage> history, List<ToolDefinition>? tools, CancellationToken ct)
    {
        var request = new ChatRequest
        {
            Messages = history,
            Tools = tools,
            ToolChoice = tools == null ? null : "auto",
            MaxTokens = settings.MaxTokens,
            // Nessun parametro di sampling ereditato dall'agente principale: solo quelli
            // esplicitamente configurati in SecondOpinionSettings vengono inviati.
            Temperature = settings.Temperature,
            FrequencyPenalty = null,
            PresencePenalty = null,
            TopP = settings.TopP,
            TopK = settings.TopK,
            MinP = settings.MinP,
            RepeatPenalty = settings.RepeatPenalty,
        };

        // ReasoningMode: "auto" = nessun campo extra (decide il server/chat-template). "on"/"off" =
        // chat_template_kwargs.enable_thinking, convenzione adottata da più chat template
        // "thinking-capable" (non specifica di un modello) — vedi nota su SecondOpinionSettings.
        if (string.Equals(settings.ReasoningMode, "on", StringComparison.OrdinalIgnoreCase))
            request.ChatTemplateKwargs = new Dictionary<string, object> { ["enable_thinking"] = true };
        else if (string.Equals(settings.ReasoningMode, "off", StringComparison.OrdinalIgnoreCase))
            request.ChatTemplateKwargs = new Dictionary<string, object> { ["enable_thinking"] = false };

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        ChatResponse? response;
        try
        {
            response = await llm.ChatAsync(request, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new SecondOpinionTimeoutException();
        }
        catch (HttpRequestException ex)
        {
            throw new SecondOpinionCallException($"errore di rete ({ex.Message})");
        }
        catch (JsonException ex)
        {
            throw new SecondOpinionCallException($"risposta non valida (JSON malformato: {ex.Message})");
        }

        var choice = response?.Choices.FirstOrDefault();
        var message = choice?.Message ?? new ChatMessage { Role = "assistant" };
        message.TryExtractFallbackToolCalls();

        var hasToolCalls = message.ToolCalls is { Count: > 0 };
        var isLengthTruncated = string.Equals(choice?.FinishReason, "length", StringComparison.OrdinalIgnoreCase);

        // Leggi SOLO 'content'. Se è vuoto ma il modello ha prodotto 'reasoning_content' (alcuni
        // modelli reasoning separano i due campi), non restituire mai una stringa vuota: è un
        // segnale di configurazione/endpoint da segnalare chiaramente, non un "nessuna risposta"
        // generico — altrimenti sembra un fallimento del modello quando in realtà ha "pensato"
        // ma non ha scritto la risposta finale nel campo giusto. Se il troncamento è per limite di
        // token (finish_reason "length"), questo è più specifico e prevale sul caso reasoning-only.
        if (string.IsNullOrWhiteSpace(message.Content) && !hasToolCalls)
        {
            message.Content = isLengthTruncated
                ? "ERRORE: il modello di supporto ha raggiunto il limite di token (MaxTokens) senza produrre alcuna " +
                  "risposta finale in 'content'. Aumenta SecondOpinionSettings.MaxTokens o imposta ReasoningMode=off."
                : !string.IsNullOrWhiteSpace(message.ReasoningContent)
                    ? "ERRORE: il modello di supporto ha prodotto solo ragionamento interno ('reasoning_content'), nessuna " +
                      "risposta finale in 'content'. Verifica ReasoningMode/la configurazione del server di supporto."
                    : "(nessuna risposta dal modello)";
        }
        else if (isLengthTruncated && !hasToolCalls)
        {
            message.Content += "\n\n[SECOND OPINION TRONCATO: limite di token raggiunto]";
        }
        return message;
    }

    private string Execute(string name, string argsJson, HashSet<string> seenCalls, CancellationToken ct = default)
    {
        if (!AllowedTools.Contains(name))
            return $"ERRORE: '{name}' non disponibile — sei in sola lettura. Usa solo: {string.Join(", ", AllowedTools)}.";
        if (!seenCalls.Add($"{name}|{argsJson}"))
            return "ERRORE: chiamata identica già eseguita, il risultato è più sopra. Cerca altrove o concludi.";

        var result = dispatcher.Execute(name, argsJson, ct);
        return result.Length <= MaxToolResultChars
            ? result
            : result[..MaxToolResultChars] + $"\n…(troncato: {result.Length - MaxToolResultChars} caratteri omessi — restringi la ricerca o il range)";
    }

    private string Finish(string? answer, long durationMs, string outcome)
    {
        var text = string.IsNullOrWhiteSpace(answer) ? "(il sotto-agente non ha prodotto una conclusione)" : answer.Trim();
        var wasTruncated = text.Length > settings.MaxAnswerChars;
        if (wasTruncated) text = text[..settings.MaxAnswerChars] + "…(troncato)";
        LogOutcome(outcome, durationMs, text.Length);
        return "[SECOND OPINION] " + text;
    }

    /// <summary>
    /// Log a livello "info" (su stderr, come le altre righe diagnostiche di questo backend — non
    /// esiste un framework di logging strutturato qui): endpoint/modello usati, durata, esito,
    /// lunghezza risposta. Persistere anche gli status intermedi del sotto-agente (quale tool ha
    /// chiamato, cosa ha letto) richiederebbe di far attraversare quegli eventi al sink della
    /// sessione (AgentEventDto) invece che al solo onStatus effimero — non fatto qui: invasivo
    /// (toccherebbe il protocollo verso la UI) e fuori dallo scopo di questo intervento.
    /// </summary>
    private void LogOutcome(string outcome, long durationMs, int answerLength) =>
        Console.Error.WriteLine($"[SECOND-OPINION] endpoint={llm.EndpointOverride ?? "(default)"} model={llm.Model} " +
                                 $"outcome={outcome} durationMs={durationMs} answerLen={answerLength}");

    private string BuildSystemPrompt()
    {
        if (!string.IsNullOrWhiteSpace(settings.SystemPromptPath))
        {
            try
            {
                var fromFile = File.ReadAllText(settings.SystemPromptPath).Trim();
                if (fromFile.Length > 0) return fromFile;
                Console.Error.WriteLine($"[SECOND-OPINION] SystemPromptPath '{settings.SystemPromptPath}' è vuoto, uso il prompt di default.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SECOND-OPINION] SystemPromptPath '{settings.SystemPromptPath}' non leggibile ({ex.Message}), uso il prompt di default.");
            }
        }
        return DefaultSystemPrompt;
    }

    // Default in inglese, model-agnostic (nessun riferimento a un modello specifico): il parere
    // deve VERIFICARE la diagnosi dell'agente principale, non fidarsi, e produrre un formato
    // fisso facile da leggere per un modello (DIAGNOSIS/WRONG ASSUMPTIONS/ROOT CAUSE/FIX/VERIFY/
    // CONFIDENCE) invece di prosa libera.
    private string DefaultSystemPrompt => $"""
        You are a senior engineer reviewing a coding agent that is stuck in a loop.
        You did not take part in its work: do not trust its conclusions, verify them.
        Workspace: {workspace.Root}

        You receive GOAL, CONSTRAINTS, ATTEMPTS, EXACT ERROR/OUTPUT, RELEVANT CODE, QUESTION (the
        agent may omit sections it doesn't have).

        1) Check whether the agent's diagnosis is consistent with the error and the code; list wrong assumptions.
        2) Identify the most likely root cause; rank alternatives if more than one is plausible.
        3) Propose a fix DIFFERENT from the failed attempts listed.
        4) If information is missing, say exactly what to inspect or run instead of guessing.

        You can read code for context (search_in_files, sym_search, read_file_range, git log/diff/status,
        up to {settings.MaxStepsSubAgent} steps total) but cannot modify anything or run commands.

        Rules: do not agree to be polite; do not invent APIs, files or versions — mark uncertainty
        explicitly; be concise.

        Answer format (final text, no tool call):
        DIAGNOSIS: <1-2 sentences>
        WRONG ASSUMPTIONS: <what the agent got wrong, or "none found">
        ROOT CAUSE (ranked): <most likely first, alternatives after>
        FIX: <concrete, different from ATTEMPTS>
        VERIFY: <exact step to confirm the fix before applying it blindly>
        CONFIDENCE: low|medium|high
        """;

    private static string Short(string s, int max)
    {
        var flat = s.Replace("\r", "").Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
