using System.Text.Json;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Core;

/// <summary>
/// Eccezione interna: la chiamata al modello di lettura ha superato <see cref="ExploreLlmSettings.TimeoutSeconds"/>.
/// Distinta da una cancellazione reale dell'utente (vedi <see cref="ExploreSubAgent.AskAsync"/>): solo questa produce
/// il messaggio di fallback "non disponibile (timeout)", una cancellazione vera deve invece propagare.
/// </summary>
internal sealed class ExploreTimeoutException : Exception;

/// <summary>
/// Eccezione interna: errore di rete/HTTP/parsing verso il modello di lettura (connessione rifiutata,
/// 5xx, risposta non JSON valido, ecc.) — mai un'eccezione non gestita che risale al loop principale.
/// </summary>
internal sealed class ExploreCallException(string message) : Exception(message);

/// <summary>
/// Sotto-agente di esplorazione in sola lettura (tool "explore"). Risponde a una domanda sul
/// codice ("dove si gestisce X", "come passa Y da A a B") con una conversazione PROPRIA: le
/// decine di letture e ricerche restano qui e all'agente principale torna solo la conclusione,
/// così la sua history resta corta. Usa un ToolDispatcher separato: le letture del sotto-agente
/// non devono contare come "file già letto" per l'agente principale, che altrimenti potrebbe
/// modificare codice mai visto (vedi FileSystemTools.RequireRead).
/// </summary>
public class ExploreSubAgent(LlamaClient llm, ToolDispatcher dispatcher, WorkspaceContext workspace, ExploreLlmSettings settings)
{
    public const string ToolName = "explore";

    private const int MaxSteps = 12;
    private const int MaxToolResultChars = 6000;
    private const int MaxAnswerChars = 4000;

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
            Description = "Delega una ricerca nel codice a un sotto-agente in sola lettura con contesto separato: esegue " +
                          "lui ricerche e letture e ti restituisce SOLO la conclusione (file:riga, come funziona, cosa ha " +
                          "verificato). Usalo per domande che richiederebbero molte letture su più file (\"dove viene " +
                          "gestito X e chi lo chiama\", \"come arriva il dato Y dal controller al DB\"). Per una singola " +
                          "ricerca o un file già noto usa direttamente search_in_files/read_file_range.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    question = new { type = "string", description = "Domanda precisa e autosufficiente: cosa cercare e cosa vuoi sapere (il sotto-agente non vede la tua conversazione)" }
                },
                required = new[] { "question" }
            }
        }
    };

    public async Task<string> RunAsync(string argumentsJson, Action<string>? onStatus = null, CancellationToken ct = default)
    {
        string question;
        try
        {
            var el = JsonDocument.Parse(argumentsJson).RootElement;
            question = el.TryGetProperty("question", out var q) ? q.GetString()?.Trim() ?? "" : "";
        }
        catch { question = ""; }
        if (question.Length == 0) return "ERRORE: 'question' vuota.";

        // Ogni esplorazione parte pulita, come la sua conversazione.
        dispatcher.ClearCache();

        var tools = dispatcher.AllDefinitions.Where(t => AllowedTools.Contains(t.Function.Name)).ToList();
        var history = new List<ChatMessage> { ChatMessage.System(BuildSystemPrompt()), ChatMessage.User(question) };
        var seenCalls = new HashSet<string>();

        try
        {
            for (var step = 1; step <= MaxSteps; step++)
            {
                onStatus?.Invoke($"explore: step {step}/{MaxSteps}…");
                var message = await AskAsync(history, tools, ct);
                history.Add(message);

                if (message.ToolCalls is not { Count: > 0 })
                    return Finish(message.Content);

                foreach (var tc in message.ToolCalls)
                {
                    onStatus?.Invoke($"explore → {tc.Function.Name} {Short(tc.Function.Arguments, 80)}");
                    history.Add(ChatMessage.ToolResult(tc.Id, Execute(tc.Function.Name, tc.Function.Arguments, seenCalls, ct)));
                }
            }

            // Step finiti: si chiede la conclusione senza tool, invece di restituire il nulla.
            history.Add(ChatMessage.User("Limite di step raggiunto: non puoi più usare tool. Rispondi ORA con quello che hai " +
                                         "trovato, nel formato richiesto, indicando cosa non sei riuscito a verificare."));
            return Finish((await AskAsync(history, null, ct)).Content);
        }
        catch (ExploreTimeoutException)
        {
            return "[EXPLORE] non disponibile (timeout). Usa direttamente search_in_files/read_file_range.";
        }
        catch (ExploreCallException ex)
        {
            return $"[EXPLORE] non disponibile (errore: {ex.Message}). Usa direttamente search_in_files/read_file_range.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellazione reale del turno (utente): deve propagare, non essere inghiottita come
            // un errore qualunque — altrimenti un "Annulla" durante explore risulterebbe in un
            // tool_result di errore invece che in un turno davvero cancellato.
            throw;
        }
        catch (Exception ex)
        {
            return $"ERRORE: il sotto-agente explore si è interrotto ({ex.Message}). Procedi con search_in_files/read_file_range.";
        }
    }

    private async Task<ChatMessage> AskAsync(List<ChatMessage> history, List<ToolDefinition>? tools, CancellationToken ct = default)
    {
        var request = new ChatRequest
        {
            Messages = history,
            Tools = tools,
            ToolChoice = tools == null ? null : "auto",
            MaxTokens = settings.MaxTokens,
            // Nessun parametro di sampling ereditato dall'agente coordinatore: solo quelli
            // esplicitamente configurati in ExploreLlmSettings vengono inviati.
            Temperature = settings.Temperature,
            FrequencyPenalty = settings.FrequencyPenalty,
            PresencePenalty = settings.PresencePenalty,
            TopP = settings.TopP,
            TopK = settings.TopK,
            MinP = settings.MinP,
            RepeatPenalty = settings.RepeatPenalty,
        };

        // ReasoningMode: "auto" = nessun campo extra (decide il server/chat-template). "on"/"off" =
        // chat_template_kwargs.enable_thinking, stessa convenzione di ConsultSubAgent (non
        // specifica di un modello).
        if (string.Equals(settings.ReasoningMode, "on", StringComparison.OrdinalIgnoreCase))
            request.ChatTemplateKwargs = new Dictionary<string, object> { ["enable_thinking"] = true };
        else if (string.Equals(settings.ReasoningMode, "off", StringComparison.OrdinalIgnoreCase))
            request.ChatTemplateKwargs = new Dictionary<string, object> { ["enable_thinking"] = false };

        // Timeout dedicato alla chiamata del sotto-agente, collegato al ct del turno: l'HttpClient
        // condiviso (LlamaClient) resta a timeout infinito per gli altri usi, ma qui una richiesta
        // che supera ExploreLlmSettings.TimeoutSeconds viene annullata DAVVERO (il CancellationToken
        // arriva fino a PostAsJsonAsync in LlamaClient.ChatAsync), non resta orfana.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        ChatResponse? response;
        try
        {
            response = await llm.ChatAsync(request, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ExploreTimeoutException();
        }
        catch (HttpRequestException ex)
        {
            throw new ExploreCallException($"errore di rete ({ex.Message})");
        }
        catch (JsonException ex)
        {
            throw new ExploreCallException($"risposta non valida (JSON malformato: {ex.Message})");
        }

        var choice = response?.Choices.FirstOrDefault();
        var message = choice?.Message ?? new ChatMessage { Role = "assistant" };
        message.TryExtractFallbackToolCalls();

        var hasToolCalls = message.ToolCalls is { Count: > 0 };
        var isLengthTruncated = string.Equals(choice?.FinishReason, "length", StringComparison.OrdinalIgnoreCase);

        // Leggi SOLO 'content'. Se è vuoto ma il modello ha prodotto 'reasoning_content' (alcuni
        // modelli reasoning separano i due campi), non restituire mai una stringa vuota: è un
        // segnale di configurazione/endpoint da segnalare chiaramente, non un "nessuna risposta"
        // generico. Se il troncamento è per limite di token (finish_reason "length"), questo è
        // più specifico e prevale sul caso reasoning-only.
        if (string.IsNullOrWhiteSpace(message.Content) && !hasToolCalls)
        {
            message.Content = isLengthTruncated
                ? "ERRORE: il modello di lettura ha raggiunto il limite di token (MaxTokens) senza produrre alcuna " +
                  "risposta finale in 'content'. Aumenta ExploreLlmSettings.MaxTokens o imposta ReasoningMode=off."
                : !string.IsNullOrWhiteSpace(message.ReasoningContent)
                    ? "ERRORE: il modello di lettura ha prodotto solo ragionamento interno ('reasoning_content'), nessuna " +
                      "risposta finale in 'content'. Verifica ReasoningMode/la configurazione del server di lettura."
                    : "(nessuna risposta dal modello)";
        }
        else if (isLengthTruncated && !hasToolCalls)
        {
            message.Content += "\n\n[EXPLORE TRONCATO: limite di token raggiunto]";
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

    private static string Finish(string? answer)
    {
        var text = string.IsNullOrWhiteSpace(answer) ? "(il sotto-agente non ha prodotto una conclusione)" : answer.Trim();
        if (text.Length > MaxAnswerChars) text = text[..MaxAnswerChars] + "…(troncato)";
        return "[EXPLORE] " + text +
               "\n\n(Conclusione del sotto-agente: prima di modificare un file leggi comunque le righe interessate con read_file_range.)";
    }

    private string BuildSystemPrompt()
    {
        if (!string.IsNullOrWhiteSpace(settings.SystemPromptPath))
        {
            try
            {
                var fromFile = File.ReadAllText(settings.SystemPromptPath).Trim();
                if (fromFile.Length > 0) return fromFile;
                Console.Error.WriteLine($"[EXPLORE] SystemPromptPath '{settings.SystemPromptPath}' è vuoto, uso il prompt di default.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EXPLORE] SystemPromptPath '{settings.SystemPromptPath}' non leggibile ({ex.Message}), uso il prompt di default.");
            }
        }
        return DefaultSystemPrompt;
    }

    // Default in italiano (lingua dell'agente coordinatore e dell'utente — vedi risposta finale):
    // la regola di fedeltà è la più importante per un lettore, perché il chiamante può modificare
    // codice basandosi sulla sua risposta senza rileggerlo (vedi Finish, che lo ricorda comunque).
    private string DefaultSystemPrompt => $"""
        Sei un sotto-agente di ESPLORAZIONE del codice, in sola lettura. Workspace: {workspace.Root}
        Il tuo compito è rispondere alla domanda dell'utente cercando nel codice. Non puoi modificare nulla né eseguire comandi.

        METODO:
        - Localizza prima (search_in_files, sym_search, glob_files), poi leggi solo le righe utili con read_file_range.
          Non leggere file interi se non sono piccoli.
        - Segui il filo: definizione → chiamanti → dove finisce il dato. Fermati appena hai la risposta.
        - Hai al massimo {MaxSteps} step: puoi fare più tool call nello stesso step se sono indipendenti.

        FEDELTÀ (regola più importante: chi ti ha chiamato può modificare codice basandosi sulla tua risposta):
        - Cita VERBATIM, tra backtick, firme di funzione/metodo, nomi esatti, messaggi d'errore e le poche righe
          decisive — mai parafrasate — insieme al percorso:riga da cui vengono.
        - Non inventare nomi di funzioni, API, parametri o file che non hai visto nei risultati dei tool: se non
          l'hai letto, non esiste per te.
        - Se qualcosa è plausibile ma non l'hai verificato leggendo il codice, dillo esplicitamente ("non verificato").

        RISPOSTA FINALE (testo, niente tool call), max 25 righe, in italiano:
        - La conclusione in 1-3 frasi.
        - I punti rilevanti come "percorso:riga — cosa fa", con citazioni verbatim tra backtick dove rilevante,
          e i numeri di riga restituiti dai tool (mai stimati).
        - Separa ciò che hai VERIFICATO leggendo il codice da ciò che è un'ipotesi o non hai controllato.
        - Se non hai trovato la risposta, dillo e indica dove hai cercato.
        """;

    private static string Short(string s, int max)
    {
        var flat = s.Replace("\r", "").Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
