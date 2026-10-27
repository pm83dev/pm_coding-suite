namespace LocalCodeAgent.Core;

/// <summary>
/// Cartella di stato dell'agente dentro il workspace (".pm-agent/"): piano persistente e
/// output lunghi dei comandi. Sta DENTRO il workspace perché i tool di lettura (read_file_range,
/// search_in_files) sono confinati lì; viene aggiunta a .git/info/exclude così non compare
/// in git status e non finisce mai in un commit.
/// </summary>
public static class AgentStateDir
{
    public const string DirName = ".pm-agent";
    private const int MaxOverflowFiles = 20;

    public static string Ensure(string root)
    {
        var dir = Path.Combine(root, DirName);
        Directory.CreateDirectory(dir);
        EnsureGitExclude(root);
        return dir;
    }

    // .git/info/exclude è locale al clone (non versionato, a differenza di .gitignore):
    // escludere qui non modifica nessun file del progetto dell'utente.
    private static void EnsureGitExclude(string root)
    {
        try
        {
            var dir = new DirectoryInfo(root);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
                dir = dir.Parent;
            if (dir == null) return;

            var excludeFile = Path.Combine(dir.FullName, ".git", "info", "exclude");
            Directory.CreateDirectory(Path.GetDirectoryName(excludeFile)!);
            var existing = File.Exists(excludeFile) ? File.ReadAllLines(excludeFile) : [];
            var missing = new[] { $"{DirName}/", ".pm-agent.json" }
                .Where(p => !existing.Any(l => l.Trim() == p))
                .ToList();
            if (missing.Count > 0)
                File.AppendAllLines(excludeFile, missing);
        }
        catch { /* best effort: non bloccare il tool per un exclude non scrivibile */ }
    }

    /// <summary>
    /// Se <paramref name="text"/> supera <paramref name="maxChars"/>, lo salva per intero in
    /// .pm-agent/output/ e restituisce inizio + fine + il percorso del file da leggere a pezzi.
    /// Prima l'output veniva solo troncato tenendo la coda: su un diff di 1000+ righe il modello
    /// perdeva quasi tutto il contenuto e sprecava decine di step a richiederlo a finestre.
    /// </summary>
    public static string Overflow(string root, string label, string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;

        try
        {
            var outDir = Path.Combine(Ensure(root), "output");
            Directory.CreateDirectory(outDir);
            var file = Path.Combine(outDir, $"{label}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            File.WriteAllText(file, text);

            foreach (var old in new DirectoryInfo(outDir).GetFiles("*.txt")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxOverflowFiles))
            {
                try { old.Delete(); } catch { }
            }

            var lineCount = text.Count(c => c == '\n') + 1;
            var head = text[..(maxChars / 3)];
            var tail = text[^(maxChars * 2 / 3)..];
            return $"[OUTPUT LUNGO: {lineCount} righe, {text.Length} caratteri — salvato per intero in \"{file}\". " +
                   "NON rieseguire il comando per vederne altre parti: leggi SOLO le righe che ti servono con " +
                   "read_file_range su quel file, oppure cercaci dentro con search_in_files.]\n" +
                   $"{head}\n...(parte centrale omessa, è nel file)...\n{tail}";
        }
        catch
        {
            return "...(inizio troncato)\n" + text[^maxChars..];
        }
    }
}
