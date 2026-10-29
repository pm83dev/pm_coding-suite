namespace SupervisorLab;

public static class SignalDetector
{
    private static readonly HashSet<string> FileWriteTools =
        new(StringComparer.OrdinalIgnoreCase) { "write_file", "edit_file", "delete_file", "move_file", "create_directory" };

    // Valuta i segnali deterministici turno per turno (1-based) e riporta, per ciascun
    // segnale che scatta, il PRIMO turno in cui la condizione è soddisfatta. Le soglie sono
    // quelle di Thresholds (data/thresholds.json), tutte configurabili dall'utente.
    public static List<SignalHit> Evaluate(Session session, Thresholds th)
    {
        var hits = new List<SignalHit>();
        var callKeyRun = 0;
        string? lastCallKey = null;
        var errorKeyRun = 0;
        string? lastErrorKey = null;
        var turnsSinceWrite = 0;
        var anyWriteYet = false;
        var repeatCallHit = false;
        var repeatErrorHit = false;
        var noChangeHit = false;
        var contextHit = false;
        var durationHit = false;
        var llmErrorCount = 0;
        int? llmErrorFirstTurn = null;
        var lengthContextFullHit = false;

        for (var i = 0; i < session.Turns.Count; i++)
        {
            var turn = session.Turns[i];
            var turnIndex = i + 1;

            foreach (var call in turn.ToolCalls)
            {
                var key = call.Tool + "|" + call.ArgsAbbrev;
                callKeyRun = key == lastCallKey ? callKeyRun + 1 : 1;
                lastCallKey = key;
                if (!repeatCallHit && callKeyRun >= th.RepeatCallMin)
                {
                    repeatCallHit = true;
                    hits.Add(new SignalHit { Signal = "repeat_call", TurnIndex = turnIndex,
                        Detail = $"{call.Tool} ripetuta {callKeyRun} volte con argomenti uguali" });
                }

                if (call.LooksLikeError)
                {
                    var ekey = call.Tool + "|" + call.ResultAbbrev;
                    errorKeyRun = ekey == lastErrorKey ? errorKeyRun + 1 : 1;
                    lastErrorKey = ekey;
                    if (!repeatErrorHit && errorKeyRun >= th.RepeatErrorMin)
                    {
                        repeatErrorHit = true;
                        hits.Add(new SignalHit { Signal = "repeat_error", TurnIndex = turnIndex,
                            Detail = $"{call.Tool} stesso errore {errorKeyRun} volte" });
                    }
                }
                else
                {
                    lastErrorKey = null;
                    errorKeyRun = 0;
                }

                if (FileWriteTools.Contains(call.Tool)) { anyWriteYet = true; turnsSinceWrite = 0; }
            }

            if (anyWriteYet && turn.ToolCalls.All(c => !FileWriteTools.Contains(c.Tool))) turnsSinceWrite++;

            if (!noChangeHit && anyWriteYet && turnsSinceWrite >= th.NoFileChangeTurns)
            {
                noChangeHit = true;
                hits.Add(new SignalHit { Signal = "no_file_change", TurnIndex = turnIndex,
                    Detail = $"nessuna modifica a file da {turnsSinceWrite} turni" });
            }
            // Nota: se la sessione non ha MAI scritto un file, questo segnale non scatta mai
            // (non distingue "non ha ancora avuto bisogno di scrivere" da "è bloccata prima
            // di scrivere" — serve il digest/giudizio umano per quella distinzione).

            if (!contextHit && turn.LastPromptTokens is int pt && th.MaxContextTokens > 0)
            {
                var pct = (double)pt / th.MaxContextTokens;
                if (pct >= th.ContextPctMax)
                {
                    contextHit = true;
                    hits.Add(new SignalHit { Signal = "context_pct", TurnIndex = turnIndex,
                        Detail = $"prompt_tokens={pt} ({pct:P0} di MaxContextTokens={th.MaxContextTokens})" });
                }
            }

            if (!durationHit)
            {
                var elapsed = turn.Timestamp - session.Turns[0].Timestamp;
                if (elapsed.TotalMinutes >= th.DurationMinutesMax)
                {
                    durationHit = true;
                    hits.Add(new SignalHit { Signal = "duration", TurnIndex = turnIndex,
                        Detail = $"{elapsed} dall'inizio sessione (soglia {th.DurationMinutesMax} min)" });
                }
            }

            // I due segnali sotto leggono Turn.LlmRequests, popolato SOLO dai turni letti da
            // NDJSON (vedi Models.cs/NdjsonLogParser.cs): su log in formato testo questo elenco
            // è sempre vuoto e nessuno dei due scatta mai — non un'approssimazione silenziosa.
            foreach (var req in turn.LlmRequests)
            {
                if (req.ErrorKind != null)
                {
                    llmErrorCount++;
                    llmErrorFirstTurn ??= turnIndex;
                }

                if (!lengthContextFullHit && req.ErrorKind == "length"
                    && req.ContextPercent is double cp && cp / 100.0 > th.LengthContextFullPct)
                {
                    lengthContextFullHit = true;
                    hits.Add(new SignalHit { Signal = "length_context_full", TurnIndex = turnIndex,
                        Detail = $"finish_reason=\"length\" con contesto al {cp:F0}% (soglia {th.LengthContextFullPct:P0})" });
                }
            }
        }

        // Un solo hit aggregato per la sessione (non per-turno come gli altri): il conteggio
        // ha senso solo a livello di sessione, "scatta al primo turno" ne indica solo l'inizio.
        if (llmErrorCount > 0 && llmErrorFirstTurn is int firstTurn)
            hits.Add(new SignalHit { Signal = "llm_errors", TurnIndex = firstTurn,
                Detail = $"{llmErrorCount} llm_request con errorKind nella sessione (il primo al turno {firstTurn})" });

        return hits;
    }
}
