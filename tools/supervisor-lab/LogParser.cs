using System.Text.RegularExpressions;

namespace SupervisorLab;

// Legge pm-code-trace.log (solo lettura, mai scritto da questo progetto) e lo spezza in
// Turn/Session. Formato osservato in agent/pm-code/Program.cs (funzione Trace, chiamata
// solo in modalità --stdin-protocol — vedi README §"Dove e come scrive il log"):
//
//   === Turno 2026-10-28 18:23:22 | modello 'PM LLM' | history=2 msg | prompt: <testo troncato>
//    [step 1/30] tool_calls=1 prompt_tokens=5598 testo: <testo troncato>
//     -> tool_name({"arg":"..."})
//     <- tool_name: risultato troncato
//   === Fine turno
//
// Più "  !! testo" (Notice) intercalate tra uno step e l'altro. Nessun id di sessione,
// nessuna rotazione, nessun campo "errore" esplicito: vedi README per i dettagli e per
// cosa di questo formato NON è verificabile solo leggendo il codice.
public static class LogParser
{
    private static readonly Regex TurnoRe = new(
        @"^=== Turno (?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) \| modello '(?<model>[^']*)' \| history=(?<hist>\d+) msg \| prompt: (?<prompt>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex StepRe = new(
        @"^ \[step (?<step>\d+)/(?<max>\d+)\] tool_calls=(?<tc>\d+) prompt_tokens=(?<pt>\d+) testo: (?<text>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex ToolCallRe = new(@"^  -> (?<tool>[^(]+)\((?<args>.*)\)$", RegexOptions.Compiled);
    private static readonly Regex ToolResultRe = new(@"^  <- (?<tool>[^:]+): (?<result>.*)$", RegexOptions.Compiled);
    private static readonly Regex NoticeRe = new(@"^\s*!! (?<text>.*)$", RegexOptions.Compiled);
    private const string FineTurno = "=== Fine turno";

    public static List<Turn> ParseTurns(IEnumerable<string> lines)
    {
        var turns = new List<Turn>();
        Turn? current = null;

        foreach (var raw in lines)
        {
            var m = TurnoRe.Match(raw);
            if (m.Success)
            {
                current = new Turn
                {
                    Timestamp = DateTime.Parse(m.Groups["ts"].Value),
                    Model = m.Groups["model"].Value,
                    HistoryCount = int.Parse(m.Groups["hist"].Value),
                    PromptAbbrev = m.Groups["prompt"].Value
                };
                turns.Add(current);
                continue;
            }

            if (current == null) continue; // riga prima del primo "=== Turno" (es. notice orfana a inizio file)

            if (raw == FineTurno) { current.ClosedCleanly = true; continue; }

            var sm = StepRe.Match(raw);
            if (sm.Success)
            {
                current.Steps.Add((
                    int.Parse(sm.Groups["step"].Value),
                    int.Parse(sm.Groups["max"].Value),
                    int.Parse(sm.Groups["tc"].Value),
                    int.Parse(sm.Groups["pt"].Value),
                    sm.Groups["text"].Value));
                continue;
            }

            var cm = ToolCallRe.Match(raw);
            if (cm.Success)
            {
                current.ToolCalls.Add(new ToolCallRecord
                {
                    Tool = cm.Groups["tool"].Value.Trim(),
                    ArgsAbbrev = cm.Groups["args"].Value
                });
                continue;
            }

            var rm = ToolResultRe.Match(raw);
            if (rm.Success && current.ToolCalls.Count > 0)
            {
                // Associato all'ultima tool call aperta: il log è sequenziale per turno
                // (-> immediatamente seguita da <- per lo stesso tool), ma non c'è un id
                // di correlazione esplicito — se un turno finisse troncato a metà di una
                // tool call senza il suo risultato, questa associazione sarebbe scorretta.
                var last = current.ToolCalls[^1];
                if (last.ResultAbbrev == null && last.Tool == rm.Groups["tool"].Value.Trim())
                    last.ResultAbbrev = rm.Groups["result"].Value;
                continue;
            }

            var nm = NoticeRe.Match(raw);
            if (nm.Success) { current.Notices.Add(nm.Groups["text"].Value); continue; }

            // Riga non riconosciuta (es. continuazione multi-riga di un risultato molto
            // lungo, improbabile visto OneLine() in Program.cs ma non impossibile se il
            // log è stato modificato a mano): ignorata, non aggregata a nessun campo.
        }

        return turns;
    }

    // Raggruppa i turni in sessioni. Euristica (non verificata): una nuova sessione
    // inizia al primo turno del file, o quando "history=" scende rispetto al turno
    // precedente — il segnale che pm-code usa per capire che l'utente ha aperto una
    // nuova chat (vedi Program.cs, commento su "Context vuoto ma il processo ha già
    // una history"). Non copre il caso di un riavvio del PROCESSO (nuova sessione reale
    // ma history riparte comunque da un valore basso: indistinguibile nel log da un
    // /reset dentro lo stesso processo).
    public static List<Session> GroupIntoSessions(List<Turn> turns, string sourceFile)
    {
        var sessions = new List<Session>();
        Session? current = null;
        int? prevHistory = null;
        var n = 0;

        foreach (var turn in turns)
        {
            var isNewSession = current == null || (prevHistory.HasValue && turn.HistoryCount < prevHistory.Value);
            if (isNewSession)
            {
                n++;
                current = new Session { Id = $"S{n}", SourceFile = sourceFile };
                sessions.Add(current);
            }
            current!.Turns.Add(turn);
            prevHistory = turn.HistoryCount;
        }

        return sessions;
    }
}
