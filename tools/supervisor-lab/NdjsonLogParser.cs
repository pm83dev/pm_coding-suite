using System.Text.Json;

namespace SupervisorLab;

// Legge il trace STRUTTURATO additivo introdotto in agent/pm-code/StructuredTrace.cs: un file
// NDJSON per sessione (<data>_<ora>_<sessionId>.ndjson), un oggetto JSON per riga, campi
// ts/sessionId/turnId/type + campi specifici per tipo. A differenza del vecchio
// pm-code-trace.log (LogParser.cs), qui i confini di sessione/turno sono DATI ESPLICITI
// (session_start/session_end, turn_start/turn_end con "reason") e non un'euristica.
public static class NdjsonLogParser
{
    // Un intero file .ndjson = esattamente una sessione (per costruzione di StructuredTrace:
    // StartSession ne apre uno nuovo a ogni conversazione). Righe non JSON o senza "type"
    // vengono ignorate (stesso approccio permissivo di LogParser per righe non riconosciute).
    public static Session ParseFile(string path, string id)
    {
        var session = new Session { Id = id, SourceFile = path };
        Turn? current = null;
        var model = "";

        foreach (var raw in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            JsonElement root;
            try { using var doc = JsonDocument.Parse(raw); root = doc.RootElement.Clone(); }
            catch { continue; }

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == null) continue;
            var ts = root.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                && DateTime.TryParse(tsEl.GetString(), out var parsedTs) ? parsedTs : DateTime.MinValue;

            switch (type)
            {
                case "session_start":
                    model = GetString(root, "model") ?? "";
                    break;

                case "turn_start":
                    // HistoryCount non esiste nel formato NDJSON (nessun concetto equivalente
                    // negli eventi strutturati): 0, non un dato osservato — vedi Models.cs.
                    current = new Turn { Timestamp = ts, Model = model, HistoryCount = 0 };
                    session.Turns.Add(current);
                    break;

                case "user_message":
                    if (current != null) current.PromptAbbrev = GetString(root, "text") ?? "";
                    break;

                case "tool_call":
                    if (current != null)
                    {
                        var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                        var errorKind = GetString(root, "errorKind");
                        current.ToolCalls.Add(new ToolCallRecord
                        {
                            Tool = GetString(root, "name") ?? "",
                            ArgsAbbrev = GetString(root, "args") ?? "",
                            // Stessa convenzione "ERRORE..." del vecchio formato (vedi
                            // ToolCallRecord.LooksLikeError): qui però ok/errorKind sono il
                            // dato VERO registrato da pm-code, non un'euristica testuale.
                            ResultAbbrev = ok ? "OK" : $"ERRORE ({errorKind ?? "sconosciuto"})",
                        });
                    }
                    break;

                case "guard_triggered":
                    if (current != null)
                        current.Notices.Add($"[guard:{GetString(root, "guard")}] {GetString(root, "reason")}");
                    break;

                case "llm_request":
                    if (current != null)
                    {
                        var promptTokens = root.TryGetProperty("promptTokens", out var ptEl) && ptEl.ValueKind == JsonValueKind.Number
                            ? ptEl.GetInt32() : 0;
                        // MaxSteps=int.MaxValue apposta: non esiste nel formato NDJSON (nessun
                        // limite di step per singola chiamata LLM) e non deve far scattare
                        // AnyTurnHitStepLimit, che per questi turni usa EndReason (vedi Models.cs).
                        current.Steps.Add((current.Steps.Count + 1, int.MaxValue, current.ToolCalls.Count,
                            promptTokens, GetString(root, "finishReason") ?? ""));

                        var ok = !root.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                        var contextPercent = root.TryGetProperty("contextPercent", out var cpEl) && cpEl.ValueKind == JsonValueKind.Number
                            ? cpEl.GetDouble() : (double?)null;
                        current.LlmRequests.Add((ok, GetString(root, "errorKind"), contextPercent));
                    }
                    break;

                case "turn_end":
                    if (current != null)
                    {
                        current.ClosedCleanly = true;
                        current.EndReason = GetString(root, "reason");
                    }
                    break;

                // session_end: nessun dato da riportare su Session (Models.cs non ha un
                // campo equivalente); il solo fatto che il file finisca qui è già l'informazione.
            }
        }

        return session;
    }

    private static string? GetString(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Un intero file .ndjson, oppure una cartella di file .ndjson (uno per sessione, come li
    // scrive StructuredTrace): restituisce le sessioni non vuote, numerate "S1","S2",... in
    // ordine di nome file (che inizia con yyyyMMdd_HHmmss: ordine lessicografico = cronologico).
    public static List<Session> LoadFromPathOrDirectory(string path)
    {
        var files = Directory.Exists(path)
            ? new DirectoryInfo(path).GetFiles("*.ndjson").OrderBy(f => f.Name).Select(f => f.FullName).ToList()
            : [path];

        var sessions = new List<Session>();
        var n = 0;
        foreach (var file in files)
        {
            var parsed = ParseFile(file, "");
            if (parsed.Turns.Count == 0)
            {
                Console.Error.WriteLine($"Avviso: '{Path.GetFileName(file)}' non contiene nessun turn_start, sessione ignorata.");
                continue;
            }
            n++;
            var session = new Session { Id = $"S{n}", SourceFile = file };
            session.Turns.AddRange(parsed.Turns);
            sessions.Add(session);
        }
        return sessions;
    }
}
