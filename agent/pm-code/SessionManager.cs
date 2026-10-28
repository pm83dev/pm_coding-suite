using LocalCodeAgent.Models;

namespace LocalCodeAgent.Core;

/// <summary>
/// Gestisce il rollover di sessione quando il contesto si avvicina al limite.
/// Genera un riassunto LLM della sessione corrente e apre una nuova sessione
/// che parte dal riassunto, evitando la saturazione della KV cache.
/// </summary>
public class SessionManager
{
    private readonly LlamaClient _llm;
    private readonly int _maxTokens;
    private readonly int _rolloverTokens;
    private readonly Func<string> _buildSystemPrompt;
    private const float Threshold = 0.70f;
    // All'inizio di un turno senza piano aperto non c'è lavoro in corso: è il momento più sicuro
    // per ripartire, quindi basta una frazione della soglia.
    private const float TurnStartFraction = 0.6f;
    private Guid _currentId = Guid.NewGuid();

    // Soglia di QUALITÀ (AgentSettings:RolloverTokens), separata dal contesto del server: un
    // modello locale perde precisione molto prima di riempirlo (osservato: 43K token con 128K
    // disponibili, quando il rollover sarebbe scattato solo a ~90K). Il 70% di MaxContextTokens
    // resta come limite rigido.
    private int HardLimit => Math.Min((int)(_maxTokens * Threshold), _rolloverTokens);

    // char/4 era una stima generica per prosa naturale: questo agente scambia soprattutto
    // JSON di tool call e codice, dove il rapporto caratteri/token reale è diverso (spesso
    // più token per carattere) e char/4 arriva a sottostimare parecchio il consumo reale —
    // ritardando il rollover ben oltre il punto in cui il modello ha già iniziato a perdere
    // coerenza sul contesto lungo. Ricalibriamo il rapporto ad ogni risposta con il conteggio
    // REALE restituito dal server (usage.prompt_tokens) rapportato ai caratteri della history
    // effettivamente inviata in quella richiesta, così la stima converge al comportamento
    // reale di questo modello/contenuto invece di restare fissa su un'assunzione generica.
    // Fallback a 4.0 finché non arriva la prima misura reale (nessuna richiesta ancora fatta).
    private double _charsPerToken = 4.0;

    public SessionManager(LlamaClient llm, int maxTokens, int rolloverTokens, Func<string> buildSystemPrompt)
    {
        _llm = llm;
        _maxTokens = maxTokens;
        _rolloverTokens = rolloverTokens;
        _buildSystemPrompt = buildSystemPrompt;
    }

    /// <summary>
    /// Controlla se il contesto supera la soglia e, se sì, esegue il rollover:
    /// chiama il LLM per un riassunto e restituisce una history pulita.
    /// Se il LLM non è disponibile, usa un riassunto testuale di fallback.
    /// <paramref name="currentRequest"/> è la richiesta dell'utente del turno in corso: va
    /// conservata per intero, altrimenti dopo il rollover il modello la conoscerebbe solo dai
    /// 200 caratteri del riassunto.
    /// </summary>
    public async Task<List<ChatMessage>> MaybeRolloverAsync(
        List<ChatMessage> history, bool atTurnStart, bool hasPendingPlan, string? currentRequest,
        Action<string>? onNotice = null)
    {
        var estimated = EstimateTokens(history);
        var limit = atTurnStart && !hasPendingPlan ? (int)(HardLimit * TurnStartFraction) : HardLimit;
        if (estimated < limit)
            return history;

        WriteStatus($"  [sessione: ~{estimated} token stimati (soglia {limit}) — rollover in corso...]");

        // A inizio turno l'ultimo messaggio è la nuova richiesta, non lavoro da riassumere.
        var toSummarize = atTurnStart && history.Count > 1 && history[^1].Role == "user"
            ? history.Take(history.Count - 1).ToList()
            : history;
        var summary    = await GenerateSummaryAsync(toSummarize);
        var previousId = _currentId;
        _currentId     = Guid.NewGuid();

        WriteStatus($"  [rollover {previousId.ToString("N")[..8]}→{_currentId.ToString("N")[..8]}, {history.Count - 1} msg compressi]");
        onNotice?.Invoke($"Contesto a ~{estimated / 1000}K token: ho compresso la conversazione in un riassunto" +
                         (atTurnStart ? "" : " a metà lavoro (il piano resta salvato)") + " e continuo da lì.");

        // Ricostruito da zero (non riusa history[0]): se nel frattempo il workspace è
        // cambiato tramite il tool set_workspace — che aggiorna solo WorkspaceContext e non
        // passa da /cd, quindi non rigenera il system message — riusare history[0] farebbe
        // "dimenticare" al modello il workspace corrente e ripartire da quello di default,
        // perché il riassunto generato di solito non ripete esplicitamente il path attuale.
        List<ChatMessage> fresh =
        [
            ChatMessage.System(_buildSystemPrompt()),
            new ChatMessage { Role = "user",      Content = $"[SESSIONE PRECEDENTE #{previousId}]\n{summary}" },
            new ChatMessage { Role = "assistant",  Content = "Contesto sessione precedente acquisito. Continuo." }
        ];
        if (!string.IsNullOrWhiteSpace(currentRequest))
            fresh.Add(ChatMessage.User(atTurnStart
                ? currentRequest
                : "[RICHIESTA IN CORSO — il contesto è stato compresso a metà lavoro: riprendi dal punto indicato nel " +
                  "riassunto e nel piano, senza rifare i passi già completati. Prima di modificare un file rileggi le " +
                  "righe interessate.]\n" + currentRequest));
        return fresh;
    }

    private async Task<string> GenerateSummaryAsync(List<ChatMessage> messages)
    {
        try
        {
            var request = new ChatRequest
            {
                Messages =
                [
                    new ChatMessage { Role = "system", Content =
                        "Sei un assistente che crea riassunti concisi delle sessioni di lavoro. Rispondi in italiano." },
                    new ChatMessage { Role = "user", Content =
                        "Riassumi questa sessione di lavoro includendo:\n" +
                        "1) Obiettivo originale dell'utente\n" +
                        "2) Cosa è già stato fatto e risultati ottenuti\n" +
                        "3) Cosa resta da fare\n" +
                        "4) Eventuali errori incontrati e come sono stati gestiti\n" +
                        "5) File modificati e comandi di build/avvio/test usati\n\n" +
                        "Sessione:\n" + FormatHistory(messages) }
                ],
                MaxTokens   = 1024,
                Temperature = 0.0f
            };

            var response = await _llm.ChatAsync(request);
            var content  = response?.Choices[0]?.Message?.Content;
            return !string.IsNullOrWhiteSpace(content) ? content : BuildFallbackSummary(messages);
        }
        catch
        {
            return BuildFallbackSummary(messages);
        }
    }

    private static string FormatHistory(List<ChatMessage> messages)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var msg in messages.Skip(1))
        {
            var content = (msg.Content ?? "").Replace('\n', ' ');
            if (msg.Role == "user" && !string.IsNullOrWhiteSpace(content))
                sb.AppendLine($"U: {content[..Math.Min(200, content.Length)]}");
            else if (msg.Role == "assistant")
            {
                if (msg.ToolCalls is { Count: > 0 })
                    foreach (var tc in msg.ToolCalls)
                    {
                        var a = tc.Function.Arguments.Replace('\n', ' ');
                        sb.AppendLine($"→ {tc.Function.Name}({a[..Math.Min(80, a.Length)]})");
                    }
                else if (!string.IsNullOrWhiteSpace(content))
                    sb.AppendLine($"A: {content[..Math.Min(200, content.Length)]}");
            }
            else if (msg.Role == "tool" && !string.IsNullOrWhiteSpace(content))
                sb.AppendLine($"R: {content[..Math.Min(100, content.Length)]}");
        }
        return sb.ToString();
    }

    private static string BuildFallbackSummary(List<ChatMessage> messages)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Riassunto automatico sessione — generato senza LLM, server non disponibile]");

        var firstGoal = messages.Skip(1).FirstOrDefault(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content));
        if (firstGoal is not null)
        {
            var goal = firstGoal.Content!.Replace('\n', ' ');
            sb.AppendLine($"Obiettivo originale: {goal[..Math.Min(200, goal.Length)]}");
        }

        sb.AppendLine("Azioni e risultati:");
        foreach (var msg in messages.Skip(1))
        {
            var content = (msg.Content ?? "").Replace('\n', ' ');

            if (msg.Role == "assistant" && msg.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in msg.ToolCalls)
                    sb.AppendLine($"  → {tc.Function.Name}");
            }
            // I risultati dei tool venivano ignorati del tutto: senza questo, il riassunto di
            // fallback perdeva ogni traccia di errori incontrati durante la sessione precedente,
            // costringendo il modello a ripetere gli stessi passi falliti dopo il rollover.
            else if (msg.Role == "tool" && !string.IsNullOrWhiteSpace(content))
            {
                var isError = content.Contains("[ERROR]") || content.Contains("ERRORE") ||
                              content.Contains("non trovato") ||
                              content.Contains("fallito", StringComparison.OrdinalIgnoreCase);
                if (isError)
                    sb.AppendLine($"    ✗ errore: {content[..Math.Min(150, content.Length)]}");
            }
            else if (msg.Role == "assistant" && !string.IsNullOrWhiteSpace(content))
                sb.AppendLine($"  A: {content[..Math.Min(150, content.Length)]}");
            else if (msg.Role == "user" && msg != firstGoal && !string.IsNullOrWhiteSpace(content))
                sb.AppendLine($"U: {content[..Math.Min(150, content.Length)]}");
        }

        return sb.ToString();
    }

    private int EstimateTokens(List<ChatMessage> messages) =>
        (int)(TotalChars(messages) / _charsPerToken);

    private static int TotalChars(List<ChatMessage> messages) =>
        messages.Sum(m =>
        {
            var contentLen = m.Content?.Length ?? 0;
            var toolLen    = m.ToolCalls?.Sum(t => t.Function.Arguments.Length + t.Function.Name.Length) ?? 0;
            return contentLen + toolLen;
        });

    /// <summary>
    /// Da chiamare subito dopo ogni risposta del server, con la history esattamente come
    /// inviata in quella richiesta (prima di appendere la risposta) e i prompt_tokens reali
    /// restituiti nell'usage. Ricalibra il rapporto caratteri/token usato dalla stima —
    /// se il server non restituisce usage (alcuni backend lo omettono in streaming) o la
    /// history inviata è vuota, la chiamata è un no-op e resta valida l'ultima calibrazione.
    /// </summary>
    public void RecordRealUsage(int promptTokens, List<ChatMessage> sentHistory)
    {
        if (promptTokens <= 0) return;
        var chars = TotalChars(sentHistory);
        if (chars <= 0) return;
        _charsPerToken = (double)chars / promptTokens;
    }

    // Su stderr, mai su stdout: in modalità --stdin-protocol stdout è riservato
    // esclusivamente alle righe NDJSON di Program.cs (EmitEvent) — vedi lo stesso
    // trattamento applicato a UI.Write in Program.cs.
    private static void WriteStatus(string msg)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Error.WriteLine(msg);
        Console.ResetColor();
    }
}
