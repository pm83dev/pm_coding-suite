using System.Text.Json;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Tools;

/// <summary>
/// Tool meta-agente: gestione workspace, info di contesto.
/// </summary>
public class AgentTools(WorkspaceContext workspace)
{
    public List<ToolDefinition> Definitions =>
    [
        new() { Function = new() {
            Name = "get_workspace_tree",
            Description = "Albero file/cartelle workspace.",
            Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
        }},
        new() { Function = new() {
            Name = "set_workspace",
            Description = "Cambia directory di lavoro.",
            Parameters = new { type = "object",
                properties = new {
                    path = new { type = "string" }
                },
                required = new[] { "path" } }
        }},
        new() { Function = new() {
            Name = "dir_info",
            Description = "Dettaglio directory (2 livelli, dimensioni file).",
            Parameters = new { type = "object",
                properties = new {
                    path = new { type = "string" }
                },
                required = new[] { "path" } }
        }}
    ];

    public string Execute(string toolName, string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            return toolName switch
            {
                "get_workspace_tree"     => workspace.GetTreeSnapshot(),
                "set_workspace"          => SetWorkspace(args),
                "dir_info"               => GetDirectoryDetails(args),
                _ => $"Tool '{toolName}' non trovato."
            };
        }
        catch (Exception ex) { return $"ERRORE: {ex.Message}"; }
    }

    private string SetWorkspace(JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        workspace.SetRoot(path);
        return $"✓ Workspace impostato su: {workspace.Root}";
    }

    private string GetDirectoryDetails(JsonElement args)
    {
        var rel     = args.TryGetProperty("path", out var p) ? p.GetString() ?? "." : ".";
        var absPath = workspace.Resolve(rel);

        if (!Directory.Exists(absPath))
            return $"Directory non trovata: {rel}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(rel == "." ? workspace.Root : rel);
        AppendDetails(absPath, "", 0, sb);
        return sb.ToString();
    }

    private static readonly string[] _ignoredDirs =
        [".git", "bin", "obj", "node_modules", ".vs", "__pycache__"];

    private static void AppendDetails(string dir, string indent, int depth, System.Text.StringBuilder sb)
    {
        if (depth >= 2) return;

        var entries = Directory.GetFileSystemEntries(dir)
            .OrderBy(e => File.Exists(e) ? 1 : 0)
            .ThenBy(e => Path.GetFileName(e));

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (_ignoredDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                var fileCount = Directory.GetFiles(entry, "*", SearchOption.TopDirectoryOnly).Length;
                sb.AppendLine($"{indent}📁 {name}/ ({fileCount} file)");
                AppendDetails(entry, indent + "  ", depth + 1, sb);
            }
            else
            {
                var size = new FileInfo(entry).Length;
                var sizeStr = size >= 1024 ? $"{size / 1024.0:F1} KB" : $"{size} B";
                sb.AppendLine($"{indent}📄 {name} ({sizeStr})");
            }
        }
    }
}