using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LocalCodeAgent.Core.Tests.Helpers;

/// <summary>
/// Finto server OpenAI-compatible (stile llama-server) per i test di <c>ConsultSubAgent</c> e
/// <c>AgentLoop</c>, senza dipendere da un vero llama-server né dal tool esterno
/// <c>tools/fake-llm</c> (pensato per pilotare il CLI pm-code, non per test xUnit in-process).
/// Risponde sia in modalità non-streaming (usata da <see cref="LlamaClient.ChatAsync"/>, cioè
/// ConsultSubAgent) sia in streaming SSE (usata da <see cref="LlamaClient.StreamChatAsync"/>,
/// cioè il loop principale di AgentLoop) in base al campo "stream" della richiesta — un solo
/// finto server copre entrambi gli usi nei test.
/// </summary>
public sealed class FakeLlmServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly List<Func<FakeResponse>> _script;
    private int _callIndex = -1;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public int CallCount => Volatile.Read(ref _callIndex) + 1;

    public FakeLlmServer(IEnumerable<Func<FakeResponse>> script)
    {
        _script = script.ToList();
        Port = FindFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    private static int FindFreePort()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token); }
            catch { return; } // listener stopped/disposed o token cancellato
            _ = HandleAsync(ctx); // non blocca il loop: richieste concorrenti/lente gestite in parallelo
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.Url?.AbsolutePath == "/v1/models")
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.Close();
                return;
            }

            using var bodyReader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await bodyReader.ReadToEndAsync();
            var isStreaming = false;
            try
            {
                var doc = JsonDocument.Parse(body);
                isStreaming = doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
            }
            catch { /* corpo non-JSON: trattato come non-streaming */ }

            var idx = Interlocked.Increment(ref _callIndex);
            var builder = idx < _script.Count ? _script[idx] : _script[^1];
            var fake = builder();

            if (fake.DelayBeforeResponding is { } delay)
                await Task.Delay(delay, _cts.Token);

            if (fake.HttpStatus is { } status)
            {
                ctx.Response.StatusCode = status;
                var errBytes = Encoding.UTF8.GetBytes(fake.Body ?? "{\"error\":\"fake error\"}");
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(errBytes);
                ctx.Response.Close();
                return;
            }

            if (isStreaming)
                await WriteStreamingAsync(ctx, fake);
            else
                await WriteNonStreamingAsync(ctx, fake);
        }
        catch
        {
            // Connessione interrotta dal client (es. timeout/cancellazione lato test): niente da
            // fare, il contesto HTTP è già morto — evita che un'eccezione qui abbatta il loop.
            try { ctx.Response.Close(); } catch { /* già chiusa */ }
        }
    }

    private static async Task WriteNonStreamingAsync(HttpListenerContext ctx, FakeResponse fake)
    {
        var messageObj = new Dictionary<string, object?> { ["role"] = "assistant" };
        if (fake.Content != null) messageObj["content"] = fake.Content;
        if (fake.ReasoningContent != null) messageObj["reasoning_content"] = fake.ReasoningContent;
        if (fake.ToolCallName != null)
        {
            messageObj["tool_calls"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "fake-call-1",
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?> { ["name"] = fake.ToolCallName, ["arguments"] = fake.ToolCallArgsJson },
                }
            };
        }

        var payload = new Dictionary<string, object?>
        {
            ["choices"] = new object[] { new Dictionary<string, object?> { ["message"] = messageObj, ["finish_reason"] = fake.FinishReason ?? "stop" } },
            ["usage"] = new Dictionary<string, object?> { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 },
        };
        var json = JsonSerializer.Serialize(payload);
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static async Task WriteStreamingAsync(HttpListenerContext ctx, FakeResponse fake)
    {
        ctx.Response.ContentType = "text/event-stream";
        await using var writer = new StreamWriter(ctx.Response.OutputStream, Encoding.UTF8) { AutoFlush = true };

        if (fake.ToolCallName != null)
        {
            var toolChunk = new Dictionary<string, object?>
            {
                ["choices"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["delta"] = new Dictionary<string, object?>
                        {
                            ["tool_calls"] = new object[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["index"] = 0,
                                    ["id"] = "fake-call-1",
                                    ["type"] = "function",
                                    ["function"] = new Dictionary<string, object?> { ["name"] = fake.ToolCallName, ["arguments"] = fake.ToolCallArgsJson },
                                }
                            }
                        },
                        ["finish_reason"] = null,
                    }
                }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(toolChunk)}\n\n");

            var doneChunk = new Dictionary<string, object?>
            {
                ["choices"] = new object[] { new Dictionary<string, object?> { ["delta"] = new Dictionary<string, object?>(), ["finish_reason"] = "tool_calls" } },
                ["usage"] = new Dictionary<string, object?> { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 },
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(doneChunk)}\n\n");
        }
        else
        {
            var textChunk = new Dictionary<string, object?>
            {
                ["choices"] = new object[] { new Dictionary<string, object?> { ["delta"] = new Dictionary<string, object?> { ["content"] = fake.Content ?? "" }, ["finish_reason"] = null } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(textChunk)}\n\n");

            var doneChunk = new Dictionary<string, object?>
            {
                ["choices"] = new object[] { new Dictionary<string, object?> { ["delta"] = new Dictionary<string, object?>(), ["finish_reason"] = fake.FinishReason ?? "stop" } },
                ["usage"] = new Dictionary<string, object?> { ["prompt_tokens"] = 10, ["completion_tokens"] = 10 },
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(doneChunk)}\n\n");
        }
        await writer.WriteAsync("data: [DONE]\n\n");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* già fermo */ }
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _listener.Close();
        _cts.Dispose();
    }
}

/// <summary>Una risposta scriptata per una singola chiamata al finto server.</summary>
public sealed record FakeResponse
{
    public string? Content { get; init; }
    public string? ReasoningContent { get; init; }
    public string? ToolCallName { get; init; }
    public string? ToolCallArgsJson { get; init; }
    public string? FinishReason { get; init; }
    public int? HttpStatus { get; init; }
    public string? Body { get; init; }
    public TimeSpan? DelayBeforeResponding { get; init; }

    public static FakeResponse Text(string content) => new() { Content = content };
    public static FakeResponse ReasoningOnly(string reasoning) => new() { ReasoningContent = reasoning };
    public static FakeResponse Empty() => new() { Content = "" };
    public static FakeResponse ToolCall(string name, string argsJson) => new() { ToolCallName = name, ToolCallArgsJson = argsJson, FinishReason = "tool_calls" };
    public static FakeResponse HttpError(int status, string body = "{\"error\":\"fake 500\"}") => new() { HttpStatus = status, Body = body };
    public static FakeResponse Delayed(TimeSpan delay, FakeResponse then) => then with { DelayBeforeResponding = delay };
}
