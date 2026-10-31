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

    /// <summary>
    /// true se il tool non porta codice (manage_todo, run_command, ricerche...): argomenti così
    /// lunghi sono una generazione degenerata (testo ripetuto), non un file troppo grande.
    /// </summary>
    public bool Degenerate { get; } = !LlamaClient.IsCodeTool(toolName);
}

/// <summary>
/// Sollevata quando lo stream SSE finisce senza che la risposta sia davvero completa:
/// <list type="bullet">
/// <item><c>stream_truncated</c>: la connessione si è chiusa prima di "data: [DONE]" e senza
/// che nessun chunk portasse un <c>finish_reason</c> — la firma di una disconnessione a metà
/// streaming (crash/riavvio del server, rete), non di una risposta conclusa normalmente.</item>
/// <item><c>length</c>: il server ha chiuso la generazione con <c>finish_reason="length"</c>
/// (limite di token raggiunto). Trattato SEMPRE come risposta incompleta, anche quando il JSON
/// di un'eventuale tool call risulta per caso sintatticamente valido: il limite di lunghezza
/// può aver tagliato testo o argomenti in un punto qualunque, e non c'è modo di distinguere da
/// qui una troncatura "innocua" da una che ha mozzato contenuto significativo. Per questo il
/// chiamante non esegue MAI una tool call proveniente da una risposta con questo esito.</item>
/// </list>
/// In entrambi i casi il chiamante (Program.cs) non deve eseguire nessuna tool call parziale:
/// questa eccezione viene sollevata PRIMA di ricostruire il ChatMessage, quindi non ne esiste
/// nessuna istanza parziale da poter usare per errore.
/// </summary>
public sealed class IncompleteLlmResponseException(string kind, string message, Usage? usage) : Exception(message)
{
    /// <summary>"stream_truncated" oppure "length" — usato come errorKind nel trace strutturato.</summary>
    public string Kind { get; } = kind;

    /// <summary>
    /// Usage dell'ultimo chunk ricevuto PRIMA della troncatura, se il server l'ha mandato (non
    /// garantito: per "stream_truncated" la connessione può cadere prima che arrivi). Serve al
    /// chiamante (Program.cs) per capire, con finish_reason="length", se il contesto era quasi
    /// pieno (PromptTokens vicino al massimo configurato) o se è solo il budget di risposta ad
    /// essere stato raggiunto — null se non noto, mai supposto.
    /// </summary>
    public Usage? Usage { get; } = usage;
}

public class LlamaClient
{
    // Soglia di sicurezza per interrompere in anticipo una tool call che sta accumulando
    // troppi caratteri (vedi ToolCallTooLargeException) — leggermente sopra il limite di 32000
    // caratteri imposto da FileSystemTools per assorbire l'overhead di escaping JSON. Con
    // Qwen3.6-35B / Qwen3-Coder-Next in locale a 102-128K di contesto e max_tokens = 32768
    // per turno, c'è ampio margine sotto il ceiling reale del server.
    private const int MaxToolCallArgsLength = 40000;

    // Per i tool che non portano codice basta molto meno. Osservato: un manage_todo arrivato a
    // 22K caratteri e 6K+ token (nota ripetuta in loop), minuti di generazione per un aggiornamento
    // del piano che normalmente sta in poche centinaia di caratteri.
    private const int MaxNonCodeToolArgsLength = 8000;

    public static bool IsCodeTool(string name) => name is "write_file" or "edit_file";

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

    public async Task<ChatResponse?> ChatAsync(ChatRequest request, CancellationToken ct = default)
    {
        request.Model  = Model;
        request.Stream = false;
        var resp = await _http.PostAsJsonAsync(EndpointOverride ?? "/v1/chat/completions", request, _json, ct);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<ChatResponse>(
            await resp.Content.ReadAsStringAsync(ct), _json);
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
    // ct: CancellationToken del turno corrente (vedi Program.cs/RunAgentLoopAsync). Oggi nessun
    // chiamante lo annulla mai (CancellationToken.None/default a ogni call site): è plumbing per
    // un futuro comando "cancel" lato UI/estensione, non cambia alcun comportamento osservabile.
    public async Task<(ChatMessage Message, Usage? Usage, string? FinishReason)> StreamChatAsync(
        ChatRequest request,
        Action<string> onToken,
        CancellationToken ct = default,
        Action<string>? onHeartbeat = null)
    {
        request.Model  = Model;
        request.Stream = true;
        request.StreamOptions = new StreamOptions();

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, EndpointOverride ?? "/v1/chat/completions");
        httpReq.Content = JsonContent.Create(request, options: _json);
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Legge il body per includere il dettaglio dell'errore (es. "Failed to parse tool call arguments as JSON")
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {errBody}", null, resp.StatusCode);
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var contentSb = new StringBuilder();
        var toolBuilders = new Dictionary<int, ToolCallBuilder>();
        Usage? usage = null;
        string? finishReason = null;
        var sawDone = false; // vero solo se "data: [DONE]" è arrivato prima che lo stream finisse
        var lastHeartbeat = DateTime.UtcNow;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var data = line[6..];
            if (data == "[DONE]") { sawDone = true; break; }

            StreamChunk? chunk;
            try { chunk = JsonSerializer.Deserialize<StreamChunk>(data, _json); }
            catch { continue; }

            if (chunk?.Choices is not { Count: > 0 }) { usage ??= chunk?.Usage; continue; }
            if (chunk.Usage != null) usage = chunk.Usage;
            if (chunk.Choices[0].FinishReason != null) finishReason = chunk.Choices[0].FinishReason;

            var delta = chunk.Choices[0].Delta;

            // Content token → display immediately
            if (!string.IsNullOrEmpty(delta.Content))
            {
                contentSb.Append(delta.Content);
                onToken(delta.Content);
                // Heartbeat anche durante la generazione di testo: l'utente vede
                // "Generazione in corso… (N caratteri)" ogni 3s anche quando il
                // LLM sta producendo testo (non solo tool call). Senza questo,
                // durante il time-to-first-token o una generazione lunga di testo
                // l'utente non vedrebbe nessun feedback.
                if (onHeartbeat != null && DateTime.UtcNow - lastHeartbeat >= HeartbeatInterval)
                {
                    lastHeartbeat = DateTime.UtcNow;
                    onHeartbeat($"Generazione in corso… ({contentSb.Length} caratteri)");
                }
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
                    var maxArgs = string.IsNullOrEmpty(builder.Name) || IsCodeTool(builder.Name)
                        ? MaxToolCallArgsLength
                        : MaxNonCodeToolArgsLength;
                    if (builder.Args.Length > maxArgs)
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

        // finish_reason="length" è sempre trattato come risposta incompleta (vedi
        // IncompleteLlmResponseException): controlliamo comunque se c'era una tool call con
        // JSON non valido solo per rendere il messaggio d'errore più preciso, non per decidere
        // SE è un errore — lo è in ogni caso.
        if (finishReason == "length")
        {
            var brokenToolCall = toolBuilders.Values.FirstOrDefault(b =>
            {
                try { JsonDocument.Parse(b.Args.Length == 0 ? "{}" : b.Args.ToString()); return false; }
                catch { return true; }
            });
            var detail = brokenToolCall != null
                ? $" (tool call '{brokenToolCall.Name}' con argomenti JSON incompleti/non validi)"
                : "";
            throw new IncompleteLlmResponseException("length",
                $"Generazione interrotta dal server per limite di lunghezza (finish_reason=\"length\"){detail}: " +
                "risposta incompleta, nessuna tool call verrà eseguita.", usage);
        }

        // Nessun finish_reason e connessione finita senza [DONE]: disconnessione a metà
        // streaming, non una risposta conclusa (vedi IncompleteLlmResponseException).
        if (!sawDone && finishReason == null)
            throw new IncompleteLlmResponseException("stream_truncated",
                "Lo stream si è interrotto prima di \"data: [DONE]\" e senza alcun finish_reason: " +
                "connessione terminata in anticipo (probabile crash/riavvio del server o rete).", usage);

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

        return (message, usage, finishReason);
    }

    private sealed class ToolCallBuilder
    {
        public string Id   { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Args { get; } = new();
    }
}
