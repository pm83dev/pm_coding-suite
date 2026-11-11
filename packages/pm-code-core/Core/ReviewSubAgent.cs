using System.Text.Json;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Core;

/// <summary>
/// Sotto-agente di REVISIONE del codice (tool "review_changes"). Prende il git diff pendente
/// dell'area di lavoro, lo passa a un modello dedicato (tipicamente un modello "coder" forte,
/// su un endpoint separato — vedi <see cref="CoderLlmSettings"/>) e restituisce un elenco di
/// problemi trovati con severità, in sola lettura: come <see cref="ExploreSubAgent"/>, non può
/// modificare nulla — può solo leggere altro codice per contesto se il diff da solo non basta.
/// </summary>
public class ReviewSubAgent(LlamaClient llm, ToolDispatcher dispatcher, WorkspaceContext workspace, Action<ChatRequest> applySampling)
{
    public const string ToolName = "review_changes";

    private const int MaxSteps = 8;
    private const int MaxToolResultChars = 6000;
    private const int MaxAnswerChars = 4000;
    private const int MaxDiffChars = 20000;

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
            Description = "Delega la revisione del diff pendente (git diff) a un sotto-agente dedicato, in sola lettura, " +
                          "con contesto separato: cerca bug di correttezza, problemi di sicurezza o incoerenze rispetto al " +
                          "resto del codice e restituisce SOLO l'elenco dei problemi trovati (file:riga, severità, spiegazione). " +
                          "Usalo prima di concludere un task con modifiche non banali, o quando l'utente chiede esplicitamente " +
                          "una revisione.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    focus = new { type = "string", description = "Opzionale: aspetto specifico su cui concentrare la revisione (es. \"concorrenza\", \"gestione errori\"). Se omesso, revisione generale." }
                }
            }
        }
    };

    public async Task<string> RunAsync(string argumentsJson, Action<string>? onStatus = null, CancellationToken ct = default)
    {
        string focus;
        try
        {
            var el = JsonDocument.Parse(argumentsJson).RootElement;
            focus = el.TryGetProperty("focus", out var f) ? f.GetString()?.Trim() ?? "" : "";
        }
        catch { focus = ""; }

        dispatcher.ClearCache();

        var diff = dispatcher.Execute("git_diff", "{}", ct);
        if (string.IsNullOrWhiteSpace(diff))
            return "[REVIEW] Nessuna modifica pendente da revisionare (git diff vuoto).";
        if (diff.Length > MaxDiffChars)
            diff = diff[..MaxDiffChars] + $"\n…(troncato: {diff.Length - MaxDiffChars} caratteri omessi — il diff è molto grande, valuta di revisionarlo a pezzi)";

        var tools = dispatcher.AllDefinitions.Where(t => AllowedTools.Contains(t.Function.Name)).ToList();
        var userMsg = focus.Length > 0
            ? $"Rivedi queste modifiche concentrandoti su: {focus}\n\n```diff\n{diff}\n```"
            : $"Rivedi queste modifiche.\n\n```diff\n{diff}\n```";
        var history = new List<ChatMessage> { ChatMessage.System(BuildSystemPrompt()), ChatMessage.User(userMsg) };
        var seenCalls = new HashSet<string>();

        try
        {
            for (var step = 1; step <= MaxSteps; step++)
            {
                onStatus?.Invoke($"review: step {step}/{MaxSteps}…");
                var message = await AskAsync(history, tools, ct);
                history.Add(message);

                if (message.ToolCalls is not { Count: > 0 })
                    return Finish(message.Content);

                foreach (var tc in message.ToolCalls)
                {
                    onStatus?.Invoke($"review → {tc.Function.Name} {Short(tc.Function.Arguments, 80)}");
                    history.Add(ChatMessage.ToolResult(tc.Id, Execute(tc.Function.Name, tc.Function.Arguments, seenCalls, ct)));
                }
            }

            history.Add(ChatMessage.User("Limite di step raggiunto: non puoi più usare tool. Rispondi ORA con quello che hai " +
                                         "trovato nel formato richiesto, indicando cosa non sei riuscito a verificare."));
            return Finish((await AskAsync(history, null, ct)).Content);
        }
        catch (Exception ex)
        {
            return $"ERRORE: il sotto-agente review_changes si è interrotto ({ex.Message}).";
        }
    }

    private async Task<ChatMessage> AskAsync(List<ChatMessage> history, List<ToolDefinition>? tools, CancellationToken ct = default)
    {
        var request = new ChatRequest
        {
            Messages = history,
            Tools = tools,
            ToolChoice = tools == null ? null : "auto",
            MaxTokens = 4096,
        };
        applySampling(request);

        var response = await llm.ChatAsync(request, ct);
        var message = response?.Choices.FirstOrDefault()?.Message ?? new ChatMessage { Role = "assistant" };
        message.TryExtractFallbackToolCalls();
        if (string.IsNullOrEmpty(message.Content) && message.ToolCalls is not { Count: > 0 })
            message.Content = "(nessuna risposta dal modello)";
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
        return "[REVIEW] " + text;
    }

    private string BuildSystemPrompt() => $"""
        Sei un sotto-agente di REVISIONE del codice, in sola lettura. Workspace: {workspace.Root}
        Il tuo compito è analizzare il diff fornito e trovare problemi reali: bug di correttezza, edge case non gestiti,
        problemi di sicurezza, incoerenze con il resto del codice. Non puoi modificare nulla né eseguire comandi.

        METODO:
        - Analizza prima il diff da solo. Se non basta a capire se una riga è corretta (es. serve vedere come viene
          chiamata una funzione modificata, o il contesto attorno a una riga), usa search_in_files/sym_search/read_file_range.
        - Non segnalare problemi di stile o preferenze soggettive: solo cose che possono causare un comportamento
          sbagliato, un crash, o una vulnerabilità.
        - Hai al massimo {MaxSteps} step.

        RISPOSTA FINALE (testo, niente tool call), in italiano:
        - Se non trovi problemi: dillo chiaramente in una riga.
        - Altrimenti, un elenco di problemi, ognuno con: "percorso:riga — [severità: alta/media/bassa] descrizione e
          scenario concreto in cui si manifesta".
        - Separa ciò che hai VERIFICATO leggendo il codice da ciò che è un dubbio/ipotesi.
        """;

    private static string Short(string s, int max)
    {
        var flat = s.Replace("\r", "").Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
