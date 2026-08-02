using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Core;

public class LlamaClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public string Model { get; }

    public LlamaClient(string baseUrl, string model)
    {
        Model = model;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime    = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer     = 1
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout     = Timeout.InfiniteTimeSpan  // il server locale può impiegare minuti su CPU
        };
    }

    public async Task<bool> HealthCheckAsync()
    {
        try { return (await _http.GetAsync("/v1/models")).IsSuccessStatusCode; }
        catch { return false; }
    }

    // ── Standard (non-streaming) ───────────────────────────────────────────────

    public async Task<ChatResponse?> ChatAsync(ChatRequest request)
    {
        request.Model  = Model;
        request.Stream = false;
        var resp = await _http.PostAsJsonAsync("/v1/chat/completions", request, _json);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<ChatResponse>(
            await resp.Content.ReadAsStringAsync(), _json);
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Invia la richiesta in streaming. I token di testo vengono passati a <paramref name="onToken"/>
    /// man mano che arrivano. Restituisce il messaggio completo accumulato e i token usati.
    /// </summary>
    public async Task<(ChatMessage Message, Usage? Usage)> StreamChatAsync(
        ChatRequest request,
        Action<string> onToken)
    {
        request.Model  = Model;
        request.Stream = true;

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        httpReq.Content = JsonContent.Create(request, options: _json);
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
        {
            // Legge il body per includere il dettaglio dell'errore (es. "Failed to parse tool call arguments as JSON")
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {errBody}", null, resp.StatusCode);
        }

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var contentSb = new StringBuilder();
        var toolBuilders = new Dictionary<int, ToolCallBuilder>();
        Usage? usage = null;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            StreamChunk? chunk;
            try { chunk = JsonSerializer.Deserialize<StreamChunk>(data, _json); }
            catch { continue; }

            if (chunk?.Choices is not { Count: > 0 }) { usage ??= chunk?.Usage; continue; }
            if (chunk.Usage != null) usage = chunk.Usage;

            var delta = chunk.Choices[0].Delta;

            // Content token → display immediately
            if (!string.IsNullOrEmpty(delta.Content))
            {
                contentSb.Append(delta.Content);
                onToken(delta.Content);
            }

            // Accumulate tool call deltas
            if (delta.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in delta.ToolCalls)
                {
                    if (!toolBuilders.TryGetValue(tc.Index, out var builder))
                    {
                        builder = new ToolCallBuilder();
                        toolBuilders[tc.Index] = builder;
                    }
                    if (tc.Id   != null) builder.Id   = tc.Id;
                    if (tc.Function?.Name      != null) builder.Name = tc.Function.Name;
                    if (tc.Function?.Arguments != null) builder.Args.Append(tc.Function.Arguments);
                }
            }
        }

        // Reconstruct full ChatMessage
        var message = new ChatMessage { Role = "assistant" };

        if (contentSb.Length > 0)
            message.Content = contentSb.ToString();

        if (toolBuilders.Count > 0)
        {
            message.ToolCalls = toolBuilders
                .OrderBy(kv => kv.Key)
                .Select(kv => new ToolCall
                {
                    Id   = kv.Value.Id,
                    Type = "function",
                    Function = new FunctionCall
                    {
                        Name      = kv.Value.Name,
                        Arguments = kv.Value.Args.ToString()
                    }
                })
                .ToList();
        }

        return (message, usage);
    }

    private sealed class ToolCallBuilder
    {
        public string Id   { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Args { get; } = new();
    }
}
