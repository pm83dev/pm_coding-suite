namespace SupervisorLab;

// Una singola chiamata a uno strumento dentro un turno: riga "  -> tool(args)" seguita
// (quasi sempre, salvo troncamento del log a metà turno) dalla riga "  <- tool: result".
public sealed class ToolCallRecord
{
    public required string Tool { get; init; }
    public required string ArgsAbbrev { get; init; }
    public string? ResultAbbrev { get; set; }

    // Eredità osservata nel codice (FileSystemTools.cs, GitTools.cs, AgentTools.cs,
    // TerminalTools.cs, WebTools.cs): tutti i rami catch restituiscono una stringa che
    // inizia con "ERRORE" (o "ERRORE sicurezza"). Non esiste un campo ok/errore esplicito
    // nel log: questo è un controllo testuale su quella convenzione, non un dato certo
    // per ogni tool (un tool potrebbe in teoria restituire un errore con un altro testo).
    public bool LooksLikeError =>
        ResultAbbrev != null && ResultAbbrev.TrimStart().StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase);
}

// Un turno: dalla riga "=== Turno ..." alla riga "=== Fine turno" (se presente: un turno
// interrotto a metà log/processo non ha la riga di chiusura).
public sealed class Turn
{
    public required DateTime Timestamp { get; init; }
    public required string Model { get; init; }
    public required int HistoryCount { get; init; }
    // Non "required": nel formato NDJSON (vedi NdjsonLogParser) il Turn viene creato
    // all'evento turn_start e il testo arriva solo dopo, con l'evento user_message separato.
    public string PromptAbbrev { get; set; } = "";
    public List<(int Step, int MaxSteps, int ToolCalls, int PromptTokens, string TextAbbrev)> Steps { get; } = [];
    public List<ToolCallRecord> ToolCalls { get; } = [];
    public List<string> Notices { get; } = [];
    public bool ClosedCleanly { get; set; }

    // Popolato SOLO dai turni letti da NDJSON (l'evento turn_end porta il motivo esplicito:
    // completed|cancelled|error|max_turns). null per i turni letti dal vecchio formato testo,
    // dove quel dato non esiste e va dedotto con l'euristica in ApparentOutcome/AnyTurnHitStepLimit.
    public string? EndReason { get; set; }

    // Un evento llm_request per riga NDJSON (ok/errorKind/contextPercent — vedi
    // StructuredTrace.LlmRequest in agent/pm-code). Popolato SOLO dai turni letti da NDJSON:
    // il vecchio formato testo non porta questi campi (ok, errorKind, contextPercent non
    // esistono in quel formato), quindi resta sempre vuoto per quei turni — i due segnali che
    // lo usano (SignalDetector: "llm_errors", "length_context_full") semplicemente non
    // scattano mai sul formato vecchio, non è un'approssimazione silenziosa.
    public List<(bool Ok, string? ErrorKind, double? ContextPercent)> LlmRequests { get; } = [];

    // Ultimo prompt_tokens osservato nei "[step ...]" di questo turno: unica misura di
    // dimensione del contesto presente nel log (non c'è una percentuale sul massimo
    // configurato — quella va ricalcolata qui da MaxContextTokens di appsettings.json,
    // che questo turno NON porta con sé).
    public int? LastPromptTokens => Steps.Count > 0 ? Steps[^1].PromptTokens : null;
}

// Una sessione = una sequenza di turni consecutivi nello stesso processo con la STESSA
// conversazione (history che non si azzera). Il log di pm-code non contiene un id di
// sessione o di processo: questo raggruppamento è un'EURISTICA (vedi LogParser), non un
// dato verificato. Una nuova sessione viene inferita quando "history=" in un nuovo turno
// scende rispetto al turno precedente (il reset che pm-code fa su /reset o nuova chat).
public sealed class Session
{
    public required string Id { get; init; } // "S1", "S2", ... assegnato in ordine di apparizione
    public required string SourceFile { get; init; }
    public List<Turn> Turns { get; } = [];

    public DateTime Start => Turns[0].Timestamp;
    public DateTime End => Turns[^1].Timestamp;
    public TimeSpan Duration => End - Start;
    public int TurnCount => Turns.Count;
    public IEnumerable<ToolCallRecord> AllToolCalls => Turns.SelectMany(t => t.ToolCalls);
    public bool HasErrors => AllToolCalls.Any(tc => tc.LooksLikeError);
    // Per i turni letti da NDJSON EndReason è il dato VERO (vedi StructuredTrace.EndTurn in
    // pm-code): "max_turns" lo dice esplicitamente, niente da dedurre dagli Steps. Per i turni
    // letti dal vecchio formato testo (EndReason sempre null) resta l'euristica sugli Steps.
    public bool AnyTurnHitStepLimit => Turns.Any(t => t.EndReason != null
        ? t.EndReason == "max_turns"
        : t.Steps.Count > 0 && t.Steps[^1].Step >= t.Steps[^1].MaxSteps);

    // "Esito apparente" per la colonna sessions.csv: non è una verità osservata ma una
    // classificazione grezza basata solo su segnali deterministici visibili nel log
    // (vedi README §"Esito apparente"). Le etichette vere (on_track/looping/...) le
    // assegna l'utente in labels.csv.
    public string ApparentOutcome
    {
        get
        {
            if (AnyTurnHitStepLimit) return "interrotta (limite step)";
            if (Turns.Any(t => t.EndReason == "error")) return "interrotta (errore)";
            if (Turns.Any(t => t.EndReason == "cancelled")) return "interrotta (cancellata)";
            if (!Turns[^1].ClosedCleanly) return "interrotta (log troncato)";
            var repeated = ToolRepeatCounts().Values.DefaultIfEmpty(0).Max();
            if (repeated >= 3) return "possibile loop";
            return "completata";
        }
    }

    public Dictionary<string, int> ToolRepeatCounts()
    {
        var counts = new Dictionary<string, int>();
        string? prevKey = null;
        var run = 0;
        var max = new Dictionary<string, int>();
        foreach (var tc in AllToolCalls)
        {
            var key = tc.Tool + "|" + tc.ArgsAbbrev;
            if (key == prevKey) run++; else run = 1;
            prevKey = key;
            if (!max.TryGetValue(tc.Tool, out var m) || run > m) max[tc.Tool] = run;
        }
        return max;
    }
}

public sealed class Thresholds
{
    public int RepeatCallMin { get; set; } = 3;      // stessa chiamata ripetuta >= N
    public int RepeatErrorMin { get; set; } = 2;      // stesso errore ripetuto >= N
    public int NoFileChangeTurns { get; set; } = 5;   // nessuna modifica file dopo M turni
    public double ContextPctMax { get; set; } = 0.9;  // contesto oltre X% del massimo configurato
    public int MaxContextTokens { get; set; } = 81920; // da agent/pm-code/appsettings.json (AgentSettings:MaxContextTokens)
    public double DurationMinutesMax { get; set; } = 20.0; // durata oltre T minuti

    // Stessa soglia (0.85) usata in agent/pm-code/Program.cs (LengthContextFullThreshold) per
    // decidere se un finish_reason="length" è probabilmente il contesto quasi pieno: qui serve
    // al segnale "length_context_full" (solo sessioni lette da NDJSON — vedi Turn.LlmRequests).
    public double LengthContextFullPct { get; set; } = 0.85;
}

public sealed class SignalHit
{
    public required string Signal { get; init; }
    public required int TurnIndex { get; init; } // indice 1-based del turno che fa scattare il segnale
    public required string Detail { get; init; }
}
