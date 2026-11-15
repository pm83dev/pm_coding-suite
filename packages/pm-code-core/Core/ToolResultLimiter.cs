using LocalCodeAgent.Models;

namespace LocalCodeAgent.Core;

/// <summary>
/// Limiti sulla dimensione dei risultati di tool che entrano nella history del modello.
///
/// Motivo: un singolo risultato enorme (osservato: read_file di un SVG da 2 MB su UNA riga, che il
/// limite di righe non intercetta) satura il contesto, sopravvive ai rollover perché la coda recente
/// viene conservata per intero e rende la sessione irrecuperabile: ogni richiesta successiva rimanda
/// lo stesso messaggio al modello.
/// </summary>
public static class ToolResultLimiter
{
    /// <summary>Tetto per un risultato di tool al momento in cui entra nella history (~10K token).</summary>
    public const int MaxToolResultChars = 40_000;

    /// <summary>Tetto per i risultati di tool conservati nella coda recente dopo un rollover o durante un recupero.</summary>
    public const int MaxKeptToolResultChars = 8_000;

    /// <summary>
    /// Accorcia <paramref name="text"/> a circa <paramref name="maxChars"/> caratteri tenendo inizio e fine
    /// (spesso errori e riepilogo stanno in coda) e inserendo un avviso che dice al modello cosa fare.
    /// Restituisce il testo invariato se è già nei limiti.
    /// </summary>
    public static string Clip(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;

        var head = (int)(maxChars * 0.7);
        var tail = Math.Max(0, maxChars - head);
        var omitted = text.Length - head - tail;
        return text[..head] +
               $"\n\n[… OUTPUT TRONCATO: {omitted:N0} caratteri omessi su {text.Length:N0}. " +
               "Non rileggere tutto: usa search_in_files, read_file_range su poche righe o uno script che estragga " +
               "solo ciò che serve …]\n\n" +
               (tail > 0 ? text[^tail..] : "");
    }

    /// <summary>
    /// Accorcia IN PLACE i messaggi "tool" della history più lunghi di <paramref name="maxChars"/>.
    /// Restituisce quanti ne ha accorciati. Serve anche a risanare sessioni già gonfie (caricate da disco).
    /// </summary>
    public static int ShrinkOversizedToolMessages(List<ChatMessage> history, int maxChars)
    {
        var shrunk = 0;
        foreach (var m in history)
        {
            if (m.Role == "tool" && m.Content is { } c && c.Length > maxChars)
            {
                m.Content = Clip(c, maxChars);
                shrunk++;
            }
        }
        return shrunk;
    }
}
