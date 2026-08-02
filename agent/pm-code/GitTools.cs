using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Tools;

public class GitTools(WorkspaceContext workspace)
{
    public List<ToolDefinition> Definitions =>
    [
        new() { Function = new() {
            Name = "git_status",
            Description = "Stato repository git.",
            Parameters = new { type = "object", properties = new { }, required = Array.Empty<string>() }
        }},
        new() { Function = new() {
            Name = "git_diff",
            Description = "Diff working tree. staged:true per diff staged.",
            Parameters = new { type = "object",
                properties = new {
                    path   = new { type = "string" },
                    staged = new { type = "boolean" }
                },
                required = Array.Empty<string>() }
        }},
        new() { Function = new() {
            Name = "git_log",
            Description = "Cronologia commit.",
            Parameters = new { type = "object",
                properties = new {
                    count = new { type = "integer" }
                },
                required = Array.Empty<string>() }
        }},
        new() { Function = new() {
            Name = "git_add",
            Description = "Staging file. Usa '.' per tutto.",
            Parameters = new { type = "object",
                properties = new {
                    path = new { type = "string" }
                },
                required = new[] { "path" } }
        }},
        new() { Function = new() {
            Name = "git_commit",
            Description = "Crea commit.",
            Parameters = new { type = "object",
                properties = new {
                    message = new { type = "string" }
                },
                required = new[] { "message" } }
        }},
        new() { Function = new() {
            Name = "git_checkout",
            Description = "Cambia branch. create_new:true per branch nuovo.",
            Parameters = new { type = "object",
                properties = new {
                    branch     = new { type = "string" },
                    create_new = new { type = "boolean" }
                },
                required = new[] { "branch" } }
        }}
    ];

    public string Execute(string toolName, string argumentsJson)
    {
        try
        {
            var args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
            return toolName switch
            {
                "git_status"   => GitStatus(),
                "git_diff"     => GitDiff(args),
                "git_log"      => GitLog(args),
                "git_add"      => GitAdd(args),
                "git_commit"   => GitCommit(args),
                "git_checkout" => GitCheckout(args),
                _ => $"Tool '{toolName}' non trovato."
            };
        }
        catch (Exception ex) { return $"ERRORE: {ex.Message}"; }
    }

    private string GitStatus()  => Run("git status --short --branch");
    private string GitDiff(JsonElement args)
    {
        var path   = args.TryGetProperty("path",   out var p) ? p.GetString() : null;
        var staged = args.TryGetProperty("staged", out var s) && s.GetBoolean();
        var flags  = staged ? "--cached" : "";
        var target = string.IsNullOrEmpty(path) ? "" : $"-- \"{path}\"";
        return Run($"git diff {flags} {target}".Trim());
    }

    private string GitLog(JsonElement args)
    {
        var count = args.TryGetProperty("count", out var c) ? c.GetInt32() : 10;
        return Run($"git log --oneline --graph --decorate -n {count}");
    }

    private string GitAdd(JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        // Resolve if not "."
        var target = path == "." ? "." : $"\"{path}\"";
        return Run($"git add {target}");
    }

    private string GitCommit(JsonElement args)
    {
        var message = args.GetProperty("message").GetString()!
            .Replace("\"", "\\\""); // escape quotes
        return Run($"git commit -m \"{message}\"");
    }

    private string GitCheckout(JsonElement args)
    {
        var branch    = args.GetProperty("branch").GetString()!;
        var createNew = args.TryGetProperty("create_new", out var cn) && cn.GetBoolean();
        var flag      = createNew ? "-b" : "";
        return Run($"git checkout {flag} {branch}".Trim());
    }

    private string Run(string gitCommand, int timeoutMs = 30_000)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "git",
                Arguments              = gitCommand[4..], // strip "git "
                WorkingDirectory       = workspace.Root,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            }
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(timeoutMs))
        {
            proc.Kill(entireProcessTree: true);
            return "ERRORE: timeout git (30s).";
        }

        var result = new StringBuilder();
        if (stdout.Length > 0) result.Append(stdout);
        if (stderr.Length > 0) result.AppendLine($"[STDERR] {stderr}");

        var text = result.ToString().Trim();
        if (text.Length > 4000) text = "...(inizio troncato)\n" + text[^4000..];

        return string.IsNullOrEmpty(text) ? "(nessun output)" : text;
    }
}
