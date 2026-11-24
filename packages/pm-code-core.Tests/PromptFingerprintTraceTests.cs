using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LocalCodeAgent.Models;
using Xunit;

namespace LocalCodeAgent.Core.Tests;

/// <summary>
/// Ogni richiesta a llama-server deve lasciare nel trace un evento "prompt_fingerprint" con
/// l'indice del primo messaggio diverso dalla richiesta precedente allo stesso endpoint.
/// </summary>
public class PromptFingerprintTraceTests
{
    [Fact]
    public async Task EveryRequest_WritesFingerprint_WithFirstDiffIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pm-trace-test-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("PM_TRACE", null);
        StructuredTrace.Init(new TraceSettings { Enabled = true, Dir = dir, DiffSnippets = true });
        StructuredTrace.StartSession("test", "fake", "http://127.0.0.1", "test");

        // Porta chiusa: la richiesta fallisce DOPO che il fingerprint è stato scritto.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
        probe.Stop();
        var llm = new LlamaClient(url, "fake") { EndpointOverride = url + "/v1/chat/completions" };

        List<ToolDefinition> tools = [new() { Function = new() { Name = "t1", Description = "d" } }];
        ChatRequest Req(string system, params string[] more)
        {
            var r = new ChatRequest { Tools = tools, Messages = [ChatMessage.System(system)] };
            foreach (var m in more) r.Messages.Add(ChatMessage.User(m));
            return r;
        }

        async Task Send(ChatRequest r)
        {
            try { await llm.StreamChatAsync(r, _ => { }); } catch (HttpRequestException) { }
        }

        await Send(Req("sys", "a"));                 // prima richiesta: nessun precedente
        await Send(Req("sys", "a", "b"));            // pura estensione
        await Send(Req("sys CAMBIATO", "a", "b"));   // cambia il system prompt
        tools = [.. tools, new() { Function = new() { Name = "t2", Description = "d" } }];
        await Send(Req("sys CAMBIATO", "a", "b"));   // cambia solo il blocco tool

        var file = Directory.GetFiles(dir, "*.ndjson").Single();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var events = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            // Il trace è globale al processo: altri test in parallelo scrivono sullo stesso file,
            // quindi si filtra per l'endpoint (porta univoca) di questo test.
            .Where(e => e.GetProperty("type").GetString() == "prompt_fingerprint"
                        && e.GetProperty("endpoint").GetString()!.StartsWith(url))
            .ToList();

        Assert.Equal(4, events.Count);
        Assert.Equal(JsonValueKind.Null, events[0].GetProperty("firstDiffIndex").ValueKind);
        Assert.Equal(-1, events[1].GetProperty("firstDiffIndex").GetInt32());
        Assert.False(events[1].GetProperty("toolsChanged").GetBoolean());
        Assert.Equal(0, events[2].GetProperty("firstDiffIndex").GetInt32());
        Assert.True(events[2].GetProperty("messagesChanged").GetBoolean());
        Assert.True(events[3].GetProperty("toolsChanged").GetBoolean());
        Assert.Equal(-1, events[3].GetProperty("firstDiffIndex").GetInt32());
        Assert.Equal(2, events[3].GetProperty("toolsCount").GetInt32());
        Assert.Equal(64, events[0].GetProperty("promptHead2000Sha256").GetString()!.Length);

        // Diagnosi di COSA è cambiato: regione, quota di prefisso condiviso, estratto (opt-in).
        Assert.Equal("first", events[0].GetProperty("diffRegion").GetString());
        Assert.Equal("none", events[1].GetProperty("diffRegion").GetString());
        Assert.Equal("system", events[2].GetProperty("diffRegion").GetString());
        Assert.Equal("tools", events[3].GetProperty("diffRegion").GetString());
        Assert.Equal(2, events[1].GetProperty("endpointSeq").GetInt32());
        Assert.True(events[1].GetProperty("sharedPrefixPct").GetDouble() > 90);
        Assert.True(events[2].GetProperty("sharedPrefixPct").GetDouble() < events[1].GetProperty("sharedPrefixPct").GetDouble());
        Assert.Contains("CAMBIATO", events[2].GetProperty("newSnippet").GetString());
        Assert.False(events[1].TryGetProperty("newSnippet", out _)); // pura estensione: nessuna divergenza da mostrare
    }

    [Fact]
    public async Task ChatSessions_HaveIndependentTurnIds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pm-trace-test-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("PM_TRACE", null);
        StructuredTrace.Init(new TraceSettings { Enabled = true, Dir = dir });
        StructuredTrace.StartSession("test", "fake", "http://127.0.0.1", "test");

        var a = "chatAAAA" + Guid.NewGuid().ToString("N")[..4];
        var b = "chatBBBB" + Guid.NewGuid().ToString("N")[..4];
        // Task.Run: ogni chat ha il proprio ExecutionContext, come i turni del backend.
        await Task.WhenAll(
            Task.Run(async () => { StructuredTrace.SetChat(a); StructuredTrace.BeginTurn(); await Task.Delay(20); StructuredTrace.BeginTurn(); }),
            Task.Run(async () => { StructuredTrace.SetChat(b); await Task.Delay(10); StructuredTrace.BeginTurn(); }));

        var file = Directory.GetFiles(dir, "*.ndjson").Single();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var starts = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("type").GetString() == "turn_start" && e.TryGetProperty("chatSessionId", out _))
            .ToList();

        int[] TurnsOf(string chat) => starts.Where(e => e.GetProperty("chatSessionId").GetString() == chat[..8])
            .Select(e => e.GetProperty("turnId").GetInt32()).ToArray();
        // I due prefissi di 8 caratteri sono distinti ("chatAAAA"/"chatBBBB"): contatori indipendenti.
        Assert.Equal([1, 2], TurnsOf(a));
        Assert.Equal([1], TurnsOf(b));
    }
}
