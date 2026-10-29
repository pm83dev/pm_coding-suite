using System.Text.RegularExpressions;

namespace SupervisorLab;

// Oscura chiavi/token/password/variabili d'ambiente sensibili PRIMA di scrivere qualunque
// copia di un log in data/ (vedi README §"Dati e privacy"). Pattern generici, non specifici
// di questo repo: per quanto verificato (vedi Models.cs/LogParser.cs), pm-code-trace.log non
// contiene credenziali (solo testo di prompt/risposte e nomi/argomenti di tool), ma il
// redattore gira comunque su ogni file copiato, nel caso l'utente punti lo strumento su log
// futuri che ne contengono.
public static class Redactor
{
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        (new Regex(@"(?i)(api[_-]?key|token|secret|password|pwd)(['""\s:=]+)([A-Za-z0-9_\-\.\/\+]{8,})",
            RegexOptions.Compiled), "$1$2[REDACTED]"),
        (new Regex(@"Bearer\s+[A-Za-z0-9_\-\.]{10,}", RegexOptions.Compiled), "Bearer [REDACTED]"),
        (new Regex(@"sk-[A-Za-z0-9]{10,}", RegexOptions.Compiled), "[REDACTED]"),
        // Valori di variabili d'ambiente nella forma NOME=valore per nomi tipicamente sensibili.
        (new Regex(@"(?im)^([A-Z0-9_]*(KEY|TOKEN|SECRET|PASSWORD|PWD)[A-Z0-9_]*)=.+$", RegexOptions.Compiled),
            "$1=[REDACTED]"),
    ];

    private const int MaxLineLength = 2000; // troncamento richiesto esplicitamente: "troncare i contenuti dei file letti"

    public static string RedactLine(string line)
    {
        foreach (var (pattern, replacement) in Rules)
            line = pattern.Replace(line, replacement);

        return line.Length > MaxLineLength
            ? line[..MaxLineLength] + $"…(+{line.Length - MaxLineLength} troncati)"
            : line;
    }

    // Copia sourcePath in destPath riga per riga, oscurando e troncando. Non apre mai
    // sourcePath in scrittura (File.OpenRead, FileShare.Read).
    public static void RedactFile(string sourcePath, string destPath)
    {
        using var reader = new StreamReader(File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read));
        using var writer = new StreamWriter(destPath, append: false);
        writer.WriteLine($"# Copia redatta di: {sourcePath}");
        writer.WriteLine($"# Generata da tools/supervisor-lab il: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        string? line;
        while ((line = reader.ReadLine()) != null)
            writer.WriteLine(RedactLine(line));
    }
}
