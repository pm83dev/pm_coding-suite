namespace LocalCodeAgent.Cli;

using System.Text.Json;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;

/// <summary>
/// Modalità --stdin-protocol: legge richieste NDJSON da stdin, scrive eventi NDJSON
/// su stdout. Mai un banner/prompt interattivo. Usato dall'extension VS Code.
/// </summary>
public static class StdinProtocolMode
{
    public static async Task RunAsync(
        CliContext ctx,
        Func<CancellationToken, Task<bool>> runTurn,
        Action<object> emitEvent,
        Action<string> trace,
        string defaultModel)
    {
        while (true)
        {
            var line = await Console.In.ReadLineAsync();
            if (line == null) break; // stdin chiuso: l'extension ha terminato il processo
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[stdin-protocol] Riga non valida, ignorata: {ex.Message}");
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type != "request") continue;

                var prompt = root.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";

                if (root.TryGetProperty("autoApprove", out var aa))
                {
                    if (aa.ValueKind == JsonValueKind.True) ctx.AutoApprove = true;
                    else if (aa.ValueKind == JsonValueKind.False) ctx.AutoApprove = false;
                }

                var requestedModel = root.TryGetProperty("model", out var m) ? m.GetString() : null;
                ctx.Llm.Model = string.IsNullOrWhiteSpace(requestedModel) ? defaultModel : requestedModel;

                var requestedEndpoint = root.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
                ctx.Llm.EndpointOverride = string.IsNullOrWhiteSpace(requestedEndpoint) ? null : requestedEndpoint;

                Console.Error.WriteLine($"[stdin-protocol] Turno → modello '{ctx.Llm.Model}', endpoint '{ctx.Llm.EndpointOverride ?? ctx.ServerUrl}'");

                var contextMessages = new List<ChatMessage>();
                if (root.TryGetProperty("context", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in ctxEl.EnumerateArray())
                    {
                        var role = item.TryGetProperty("role", out var r) ? r.GetString() : null;
                        var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;
                        if (string.IsNullOrEmpty(role) || content == null) continue;
                        contextMessages.Add(new ChatMessage { Role = role, Content = content });
                    }
                }

                if (ctx.History.Count <= 1)
                {
                    ctx.History = [ChatMessage.System(SystemPromptBuilder.Build(ctx.Workspace, ctx.Dispatcher)), .. contextMessages, ChatMessage.User(prompt)];
                }
                else if (contextMessages.Count == 0)
                {
                    ctx.Dispatcher.ResetTodo();
                    ctx.History = [ChatMessage.System(SystemPromptBuilder.Build(ctx.Workspace, ctx.Dispatcher)), ChatMessage.User(prompt)];
                    ctx.Dispatcher.ClearCache();
                    ctx.Dispatcher.ResetCwd();
                    StructuredTrace.StartSession(ctx.Workspace.Root, ctx.Llm.Model, ctx.Llm.EndpointOverride ?? ctx.ServerUrl, ctx.CoreVersion);
                }
                else
                {
                    ctx.History.Add(ChatMessage.User(prompt));
                }

                trace($"=== Turno {DateTime.Now:yyyy-MM-dd HH:mm:ss} | modello '{ctx.Llm.Model}' | history={ctx.History.Count} msg | prompt: {OneLine(prompt, 200)}");

                await runTurn(CancellationToken.None);

                trace("=== Fine turno");
                emitEvent(new { type = "done" });
            }
        }
    }

    internal static string OneLine(string s, int max)
    {
        var flat = s.Replace("\r", "").Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..max] + $"…(+{flat.Length - max})";
    }
}