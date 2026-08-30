using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Core;

/// <summary>
/// Sollevata quando gli argomenti di una tool call in streaming superano
/// <see cref="LlamaClient.MaxToolCallArgsLength"/> prima che la generazione finisca — segnale che
/// il modello sta scrivendo un intero file/blocco di codice in una sola tool call invece di usare
/// blocchi piccoli, destinato comunque a fallire con un errore 500 di JSON troncato lato server.
/// </summary>
public sealed class ToolCallTooLargeException(string toolName, int length) : Exception(
    $"Tool call '{toolName}' interrotta: argomenti oltre {length} caratteri prima del completamento " +
    "(probabile write_file/edit_file con un intero file/blocco di codice in una sola chiamata).")
{
    public string ToolName { get; } = toolName;
}

public class LlamaClient
{
    // Soglia di sicurezza per interrompere in anticipo una tool call che sta accumulando
    // troppi caratteri (vedi ToolCallTooLargeException) — leggermente sopra il limite di 32000
    // caratteri imposto da FileSystemTools per assorbire l'overhead di escaping JSON. Con
    // Qwen3.6-35B / Qwen3-Coder-Next in locale a 102-128K di contesto e max_tokens = 32768
    // per turno, c'è ampio margine sotto il ceiling reale del server.
    private const int MaxToolCallArgsLength = 40000;

    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Settable (non solo da costruttore): in modalità --stdin-protocol l'extension può
    // inoltrare per-turno il modello scelto dall'utente nel picker nativo di VS Code,
    // sovrascrivendo il default di appsettings.json senza dover ricreare il client.
    public string Model { get; set; }

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

    // Modelli diversi possono essere serviti da server llama-server diversi (setup
    // multi-macchina): l'extension inoltra per-turno l'URL assoluto giusto in base al
    // modello scelto nel picker. NON tocca mai _http.BaseAddress: HttpClient lo blocca
    // (InvalidOperationException) non appena è già partita una richiesta — mutarlo dopo
    // il primo turno crasherebbe il processo con un'eccezione non gestita. Passare l'URL
    // assoluto per-richiesta (vedi ChatAsync/StreamChatAsync) bypassa BaseAddress invece
    // di modificarlo, quindi resta sicuro chiamarlo ad ogni turno.
    public string? EndpointOverride { get; set; }

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
        var resp = await _http.PostAsJsonAsync(EndpointOverride ?? "/v1/chat/completions", request, _json);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<ChatResponse>(
            await resp.Content.ReadAsStringAsync(), _json);
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Invia la richiesta in streaming. I token di testo vengono passati a <paramref name="onToken"/>
    /// man mano che arrivano. Restituisce il messaggio completo accumulato e i token usati.
    /// </summary>
    // Ogni quanti secondi (min) inviare un ping di stato mentre si accumula una tool call —
    // durante quella fase nessun token di testo arriva (vedi commento su onHeartbeat sotto),
    // quindi senza questo la chat resta silenziosa per l'intera durata della generazione.
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Invia la richiesta in streaming. I token di testo vengono passati a <paramref name="onToken"/>
    /// man mano che arrivano. <paramref name="onHeartbeat"/> (opzionale) viene invocato periodicamente
    /// MENTRE si accumula una tool call — quella fase non produce alcun token di testo, quindi senza
    /// un segnale esplicito il chiamante (chat) resta senza feedback per tutta la generazione (osservato:
    /// 20-90+ secondi di silenzio totale dopo aver accettato/rifiutato una proposta, indistinguibile da
    /// un blocco). Restituisce il messaggio completo accumulato e i token usati.
    /// </summary>
    public async Task<(ChatMessage Message, Usage? Usage)> StreamChatAsync(
        ChatRequest request,
        Action<string> onToken,
        Action<string>? onHeartbeat = null)
    {
        request.Model  = Model;
        request.Stream = true;

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, EndpointOverride ?? "/v1/chat/completions");
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
        var lastHeartbeat = DateTime.UtcNow;

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

                    // Un file di codice generato per intero dentro write_file/edit_file supera
                    // sempre il budget di token della risposta a metà stringa JSON: llama-server
                    // risponde 500 solo DOPO aver generato fino al limite (minuti sprecati). Qui
                    // interrompiamo la connessione non appena gli argomenti accumulati superano
                    // la soglia di sicurezza, invece di aspettare il fallimento a fine generazione.
                    if (builder.Args.Length > MaxToolCallArgsLength)
                        throw new ToolCallTooLargeException(builder.Name, builder.Args.Length);

                    if (onHeartbeat != null && DateTime.UtcNow - lastHeartbeat >= HeartbeatInterval)
                    {
                        lastHeartbeat = DateTime.UtcNow;
                        var name = string.IsNullOrEmpty(builder.Name) ? "tool" : builder.Name;
                        onHeartbeat($"Generazione '{name}' in corso… ({builder.Args.Length} caratteri)");
                    }
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
