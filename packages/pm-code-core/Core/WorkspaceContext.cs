namespace LocalCodeAgent.Core;

/// <summary>
/// Mantiene il percorso del workspace corrente e fornisce
/// utility per costruire path sicuri (no path traversal).
/// </summary>
public class WorkspaceContext
{
    public string Root { get; private set; }

    public WorkspaceContext(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root))
            Directory.CreateDirectory(Root);
    }

    /// <summary>Risolve un path relativo dentro il workspace. Blocca path traversal.</summary>
    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return Root;

        var full = Path.GetFullPath(Path.Combine(Root, relativePath));

        // 1. Controllo base del prefisso stringa
        if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path fuori dal workspace: {relativePath}");

        // 2. Fix di sicurezza: Evita il parziale match con cartelle "sorelle" (es. AgentWorkspace vs AgentWorkspaceSibling)
        if (full.Length > Root.Length && Root[^1] != Path.DirectorySeparatorChar && full[Root.Length] != Path.DirectorySeparatorChar)
        {
            throw new UnauthorizedAccessException($"Accesso negato. Il percorso punta a una directory sorella fuori dalla sandbox: {relativePath}");
        }

        return full;
    }

    public void SetRoot(string newRoot)
    {
        var resolved = Path.GetFullPath(newRoot);
        if (!Directory.Exists(resolved))
            throw new DirectoryNotFoundException($"Directory non trovata: {resolved}");
        Root = resolved;
    }

    /// <summary>
    /// Genera un albero testuale della struttura del workspace
    /// per includerlo nel system prompt.
    /// </summary>
    public string GetTreeSnapshot(int maxDepth = 2, int maxFiles = 30)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Root);
        int fileCount = 0;
        AppendTree(Root, "", 0, maxDepth, maxFiles, ref fileCount, sb);
        if (fileCount >= maxFiles)
            sb.AppendLine("  ... (troncato)");
        return sb.ToString();
    }

    private static readonly string[] _ignoreDirs =
        [".git", "bin", "obj", "node_modules", ".vs", "__pycache__"];

    private static readonly string[] _ignoreExts =
        [".exe", ".dll", ".pdb", ".zip", ".rar", ".7z", ".png", ".jpg", ".gif", ".ico"];

    private void AppendTree(string dir, string indent, int depth, int maxDepth,
        int maxFiles, ref int fileCount, System.Text.StringBuilder sb)
    {
        if (depth > maxDepth || fileCount >= maxFiles) return;

        var entries = Directory.GetFileSystemEntries(dir)
            .OrderBy(e => File.Exists(e) ? 1 : 0) // dirs prima
            .ThenBy(e => Path.GetFileName(e));

        foreach (var entry in entries)
        {
            if (fileCount >= maxFiles) break;
            var name = Path.GetFileName(entry);

            if (Directory.Exists(entry))
            {
                if (_ignoreDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                sb.AppendLine($"{indent}📁 {name}/");
                AppendTree(entry, indent + "  ", depth + 1, maxDepth, maxFiles, ref fileCount, sb);
            }
            else
            {
                var ext = Path.GetExtension(entry).ToLowerInvariant();
                if (_ignoreExts.Contains(ext)) continue;
                sb.AppendLine($"{indent}📄 {name}");
                fileCount++;
            }
        }
    }
}