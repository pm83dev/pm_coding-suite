using System.Text.Json;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Core;

/// <summary>
/// Sotto-agente di esplorazione in sola lettura (tool "explore"). Risponde a una domanda sul
/// codice ("dove si gestisce X", "come passa Y da A a B") con una conversazione PROPRIA: le
/// decine di letture e ricerche restano qui e all'agente principale torna solo la conclusione,
/// così la sua history resta corta. Usa un ToolDispatcher separato: le letture del sotto-agente
/// non devono contare come "file già letto" per l'agente principale, che altrimenti potrebbe
/// modificare codice mai visto (vedi FileSystemTools.RequireRead).
/// </summary>
public class ExploreSubAgent(LlamaClient llm, ToolDispatcher dispatcher, WorkspaceContext workspace, Action<ChatRequest> applySampling)
{
    public const string ToolName = "explore";

    private const int MaxSteps = 12;
    private const int MaxToolResultChars = 6000;
    private const int MaxAnswerChars = 4000;

    private static readonly HashSet<string> AllowedTools =
    [
        "read_file", "read_file_range", "glob_files", "search_in_files", "sym_search",
        "list_directory", "get_workspace_tree", "get_directory_details",
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

    public async Task<string> RunAsync(string argumentsJson, Action<string>? onStatus = null)
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
                var message = await AskAsync(history, tools);
                history.Add(message);

                if (message.ToolCalls is not { Count: > 0 })
                    return Finish(message.Content);

                foreach (var tc in message.ToolCalls)
                {
                    onStatus?.Invoke($"explore → {tc.Function.Name} {Short(tc.Function.Arguments, 80)}");
                    history.Add(ChatMessage.ToolResult(tc.Id, Execute(tc.Function.Name, tc.Function.Arguments, seenCalls)));
                }
            }

            // Step finiti: si chiede la conclusione senza tool, invece di restituire il nulla.
            history.Add(ChatMessage.User("Limite di step raggiunto: non puoi più usare tool. Rispondi ORA con quello che hai " +
                                         "trovato, nel formato richiesto, indicando cosa non sei riuscito a verificare."));
            return Finish((await AskAsync(history, null)).Content);
        }
        catch (Exception ex)
        {
            return $"ERRORE: il sotto-agente explore si è interrotto ({ex.Message}). Procedi con search_in_files/read_file_range.";
        }
    }

    private async Task<ChatMessage> AskAsync(List<ChatMessage> history, List<ToolDefinition>? tools)
    {
        var request = new ChatRequest
        {
            Messages = history,
            Tools = tools,
            ToolChoice = tools == null ? null : "auto",
            MaxTokens = 4096,
        };
        applySampling(request);

        var response = await llm.ChatAsync(request);
        var message = response?.Choices.FirstOrDefault()?.Message ?? new ChatMessage { Role = "assistant" };
        message.TryExtractFallbackToolCalls();
        // Come nel loop principale: un assistant senza content né tool_calls viene rifiutato
        // dal server al giro successivo.
        if (string.IsNullOrEmpty(message.Content) && message.ToolCalls is not { Count: > 0 })
            message.Content = "(nessuna risposta dal modello)";
        return message;
    }

    private string Execute(string name, string argsJson, HashSet<string> seenCalls)
    {
        if (!AllowedTools.Contains(name))
            return $"ERRORE: '{name}' non disponibile — sei in sola lettura. Usa solo: {string.Join(", ", AllowedTools)}.";
        if (!seenCalls.Add($"{name}|{argsJson}"))
            return "ERRORE: chiamata identica già eseguita, il risultato è più sopra. Cerca altrove o concludi.";

        var result = dispatcher.Execute(name, argsJson);
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

    private string BuildSystemPrompt() => $"""
        Sei un sotto-agente di ESPLORAZIONE del codice, in sola lettura. Workspace: {workspace.Root}
        Il tuo compito è rispondere alla domanda dell'utente cercando nel codice. Non puoi modificare nulla né eseguire comandi.

        METODO:
        - Localizza prima (search_in_files, sym_search, glob_files), poi leggi solo le righe utili con read_file_range.
          Non leggere file interi se non sono piccoli.
        - Segui il filo: definizione → chiamanti → dove finisce il dato. Fermati appena hai la risposta.
        - Hai al massimo {MaxSteps} step: puoi fare più tool call nello stesso step se sono indipendenti.

        RISPOSTA FINALE (testo, niente tool call), max 25 righe, in italiano:
        - La conclusione in 1-3 frasi.
        - I punti rilevanti come "percorso:riga — cosa fa", con i numeri di riga restituiti dai tool (mai stimati).
        - Separa ciò che hai VERIFICATO leggendo il codice da ciò che è un'ipotesi.
        - Se non hai trovato la risposta, dillo e indica dove hai cercato.
        """;

    private static string Short(string s, int max)
    {
        var flat = s.Replace("\r", "").Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
