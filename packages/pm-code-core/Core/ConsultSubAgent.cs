using System.Text.Json;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Core;

/// <summary>
/// Sotto-agente di SECONDO PARERE (tool "ask_second_opinion"). Quando l'agente principale è
/// bloccato su un problema (ha ritentato più volte lo stesso comando, o non capisce un
/// comportamento) può chiedere a un modello indipendente — tipicamente un modello "coder" forte
/// su un endpoint separato, vedi <see cref="CoderLlmSettings"/> (stesso usato da
/// <see cref="ReviewSubAgent"/>) — un'ipotesi diversa, con contesto separato: niente del loop
/// (tentativi falliti, frustrazione del modello) passa al sotto-agente, solo il problema
/// descritto. Come <see cref="ExploreSubAgent"/>, in sola lettura: può leggere codice per
/// contesto ma non modificare nulla né eseguire comandi.
/// </summary>
public class ConsultSubAgent(LlamaClient llm, ToolDispatcher dispatcher, WorkspaceContext workspace, Action<ChatRequest> applySampling)
{
    public const string ToolName = "ask_second_opinion";

    private const int MaxSteps = 8;
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
            Description = "Chiedi un secondo parere a un modello indipendente, con contesto separato dal tuo (non vede i " +
                          "tuoi tentativi falliti, solo il problema che descrivi). Usalo quando sei bloccato su un " +
                          "problema (hai ritentato più volte senza risultati nuovi, o un comportamento non ti è chiaro) " +
                          "PRIMA di arrenderti o di chiedere all'utente — può leggere il codice per contesto ma non " +
                          "modificare nulla. Non usarlo per domande semplici con risposta ovvia dal codice già letto.",
            Parameters = new
            {
                type = "object",
                properties = new
                {
                    problem = new { type = "string", description = "Descrizione autosufficiente: cosa stavi cercando di fare, cosa hai già scoperto/verificato, cosa non ti è chiaro o cosa hai già provato senza successo." }
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

        try
        {
            for (var step = 1; step <= MaxSteps; step++)
            {
                onStatus?.Invoke($"second opinion: step {step}/{MaxSteps}…");
                var message = await AskAsync(history, tools, ct);
                history.Add(message);

                if (message.ToolCalls is not { Count: > 0 })
                    return Finish(message.Content);

                foreach (var tc in message.ToolCalls)
                {
                    onStatus?.Invoke($"second opinion → {tc.Function.Name} {Short(tc.Function.Arguments, 80)}");
                    history.Add(ChatMessage.ToolResult(tc.Id, Execute(tc.Function.Name, tc.Function.Arguments, seenCalls, ct)));
                }
            }

            history.Add(ChatMessage.User("Limite di step raggiunto: non puoi più usare tool. Rispondi ORA con la tua " +
                                         "ipotesi/opinione nel formato richiesto, indicando cosa non sei riuscito a verificare."));
            return Finish((await AskAsync(history, null, ct)).Content);
        }
        catch (Exception ex)
        {
            return $"ERRORE: il sotto-agente ask_second_opinion si è interrotto ({ex.Message}).";
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
        return "[SECOND OPINION] " + text;
    }

    private string BuildSystemPrompt() => $"""
        Sei un secondo revisore indipendente, chiamato in causa perché un altro agente è bloccato su un problema.
        Non vedi la sua conversazione né i suoi tentativi falliti: solo la descrizione che ti ha dato. Workspace: {workspace.Root}
        Il tuo compito è dare un'ipotesi o un approccio DIVERSO da quello (presumibilmente) già tentato, non ripetere
        l'ovvio. Puoi leggere codice per contesto (search_in_files, sym_search, read_file_range, git log/diff/status) ma
        non puoi modificare nulla né eseguire comandi.

        METODO:
        - Leggi la descrizione del problema con attenzione: cosa è già stato scoperto/provato.
        - Se ti serve contesto sul codice, cercalo (non indovinare percorsi/nomi).
        - Proponi un'ipotesi concreta e verificabile, o un modo diverso di guardare al problema.
        - Hai al massimo {MaxSteps} step.

        RISPOSTA FINALE (testo, niente tool call), max 20 righe, in italiano:
        - L'ipotesi/opinione in 1-3 frasi.
        - Se rilevante, "percorso:riga" di quanto hai letto a supporto.
        - Se non hai un'idea migliore di quella già tentata, dillo chiaramente invece di inventare.
        """;

    private static string Short(string s, int max)
    {
        var flat = s.Replace("\r", "").Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
