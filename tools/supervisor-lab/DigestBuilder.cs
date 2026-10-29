using System.Text;

namespace SupervisorLab;

public static class DigestBuilder
{
    private const int MaxChars = 1500;

    // Nomi di tool che modificano il workspace, verificati in agent/pm-code/ToolDispatcher.cs
    // (righe che condizionano su questi nomi per cache dei file letti / conferma edit).
    private static readonly HashSet<string> FileWriteTools =
        new(StringComparer.OrdinalIgnoreCase) { "write_file", "edit_file", "delete_file", "move_file", "create_directory" };

    // Digest di una sessione, troncato all'incirca a "turnoFino" turni (1-based, inclusivo).
    // Se turnoFino è null usa tutta la sessione. Non include mai testo integrale di file o
    // log grezzo: solo i campi richiesti, già troncati dal log stesso (OneLine in Program.cs).
    public static string Build(Session session, int? turnoFino, string? gitDiffStat)
    {
        var turns = turnoFino is int n ? session.Turns.Take(n).ToList() : session.Turns;
        if (turns.Count == 0) return "(nessun turno)";

        var sb = new StringBuilder();
        sb.AppendLine($"Sessione {session.Id} — turno {turns.Count}/{session.TurnCount}");
        sb.AppendLine($"Obiettivo iniziale: {Truncate(turns[0].PromptAbbrev, 200)}");

        var allCalls = turns.SelectMany(t => t.ToolCalls).ToList();
        var last10 = allCalls.Skip(Math.Max(0, allCalls.Count - 10)).ToList();
        sb.AppendLine($"Ultime {last10.Count} chiamate a strumenti:");
        foreach (var c in last10)
        {
            var status = c.ResultAbbrev == null ? "?" : c.LooksLikeError ? "errore" : "ok";
            sb.AppendLine($"  - {c.Tool}({Truncate(c.ArgsAbbrev, 60)}) -> {status}");
        }

        var repeats = session.ToolRepeatCounts().Where(kv => kv.Value > 1).ToList();
        sb.AppendLine(repeats.Count == 0
            ? "Ripetizioni: nessuna chiamata ripetuta consecutivamente."
            : "Ripetizioni: " + string.Join(", ", repeats.Select(kv => $"{kv.Key} x{kv.Value}")));

        var consecErrors = 0;
        for (var i = allCalls.Count - 1; i >= 0 && allCalls[i].LooksLikeError; i--) consecErrors++;
        sb.AppendLine($"Errori consecutivi (in coda): {consecErrors}");

        var lastWriteTurn = -1;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            if (turns[i].ToolCalls.Any(c => FileWriteTools.Contains(c.Tool))) { lastWriteTurn = i; break; }
        }
        sb.AppendLine(lastWriteTurn < 0
            ? $"Turni dall'ultima modifica a un file: nessuna modifica in questa sessione ({turns.Count} turni)."
            : $"Turni dall'ultima modifica a un file: {turns.Count - 1 - lastWriteTurn}.");

        var lastTokens = turns[^1].LastPromptTokens;
        sb.AppendLine(lastTokens is int pt
            ? $"Crescita contesto: prompt_tokens ultimo step = {pt}."
            : "Crescita contesto: non disponibile (nessun [step] registrato).");

        sb.AppendLine($"Durata (dal primo al turno corrente): {(turns[^1].Timestamp - turns[0].Timestamp)}.");

        sb.AppendLine(gitDiffStat != null
            ? $"git diff --stat: {Truncate(gitDiffStat, 200)}"
            : "git diff --stat: workspace non noto/non fornito.");

        var result = sb.ToString().TrimEnd();
        return result.Length <= MaxChars ? result : result[..MaxChars] + $"…(+{result.Length - MaxChars} troncati)";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
