using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Finto server OpenAI-compatible per pilotare pm-code senza un llama-server reale.
// Implementa SOLO quello che agent/pm-code/LlamaClient.cs usa davvero (verificato leggendo
// quel file, non supposto): GET /v1/models (per HealthCheckAsync) e POST /v1/chat/completions
// con stream=true, risposta Server-Sent Events nello stesso formato OpenAI che StreamChatAsync
// deserializza (campi snake_case: choices[].delta.content, choices[].delta.tool_calls[].function,
// choices[].finish_reason, usage.prompt_tokens/completion_tokens, terminata da "data: [DONE]").
//
// Uso: fake-llm <scenario.json> [porta] [--log <file>]
// Risponde alla N-esima POST /v1/chat/completions (0-based) con responses[N] dello scenario;
// oltre la fine dell'elenco ripete una risposta di testo neutra, per non far bloccare un test
// che genera più richieste di quelle scriptate (es. gate di auto-build).

if (args.Length < 1)
{
    Console.Error.WriteLine("Uso: fake-llm <scenario.json> [porta] [--log <file>]");
    return 1;
}

var scenarioPath = args[0];
var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8999;
var logPath = Array.IndexOf(args, "--log") is int li and >= 0 && li + 1 < args.Length ? args[li + 1] : null;
var logRequests = args.Contains("--log-requests");

void Log(string msg)
{
    var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
    Console.WriteLine(line);
    if (logPath != null) { try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { } }
}

var scenarioJson = File.ReadAllText(scenarioPath);
var scenario = JsonSerializer.Deserialize<Scenario>(scenarioJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException($"Scenario vuoto o non valido: {scenarioPath}");

var counter = 0;
var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
Log($"fake-llm in ascolto su http://127.0.0.1:{port}/ — scenario '{scenarioPath}' ({scenario.Responses.Count} risposte scriptate)");

// CancelKeyPress/chiusura pulita: il test harness termina questo processo a comando (non via
// Ctrl+C simulato, quello è riservato a pm-code nei test) — un semplice loop basta.
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

_ = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); }
        catch { break; }
        _ = HandleAsync(ctx);
    }
});

// Il processo resta vivo finché il test harness non lo termina (Ctrl+C o Kill).
cts.Token.WaitHandle.WaitOne();
listener.Stop();
return 0;

async Task HandleAsync(HttpListenerContext ctx)
{
    var req = ctx.Request;
    var resp = ctx.Response;
    try
    {
        // Drena sempre il body della richiesta (anche se lo ignoriamo): con connessioni
        // riusate (SocketsHttpHandler lato pm-code, MaxConnectionsPerServer=1) un body non
        // letto blocca la richiesta successiva sulla stessa connessione.
        using var _ = req.InputStream;
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        // --log-requests: oltre al log normale, scrive i ruoli/contenuti abbreviati dei
        // messages della richiesta — serve a verificare che un frammento troncato non sia
        // mai finito nella history rimandata al server nel tentativo successivo.
        if (logRequests && req.Url?.AbsolutePath == "/v1/chat/completions")
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
                {
                    var roles = string.Join(" | ", msgs.EnumerateArray().Select(m =>
                    {
                        var role = m.TryGetProperty("role", out var r) ? r.GetString() : "?";
                        var content = m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                        return $"{role}:{(content.Length > 60 ? content[..60] + "…" : content)}";
                    }));
                    Log($"  messages[{msgs.GetArrayLength()}]: {roles}");
                }
            }
            catch { }
        }

        if (req.Url?.AbsolutePath == "/v1/models" && req.HttpMethod == "GET")
        {
            Log("GET /v1/models -> 200");
            await WriteJsonAsync(resp, 200, new { data = Array.Empty<object>() });
            return;
        }

        if (req.Url?.AbsolutePath == "/v1/chat/completions" && req.HttpMethod == "POST")
        {
            var n = counter++;
            var step = n < scenario.Responses.Count
                ? scenario.Responses[n]
                : new ScenarioStep { Type = "text", Content = "[fake-llm] fine scenario: nessun'altra risposta scriptata." };
            Log($"POST /v1/chat/completions #{n} -> type={step.Type}");
            await HandleChatCompletionAsync(resp, step, n);
            return;
        }

        resp.StatusCode = 404;
        resp.Close();
    }
    catch (Exception ex)
    {
        Log($"errore gestendo {req.Url}: {ex.Message}");
        try { resp.Abort(); } catch { }
    }
}

async Task HandleChatCompletionAsync(HttpListenerResponse resp, ScenarioStep step, int n)
{
    var promptTokens = step.PromptTokens ?? 1000 + n * 50;
    var completionTokens = step.CompletionTokens ?? 20;

    if (step.Type == "http_error")
    {
        resp.StatusCode = step.Status ?? 500;
        var body = Encoding.UTF8.GetBytes(step.Body ?? "errore scriptato dal fake-llm");
        resp.ContentType = "text/plain";
        resp.ContentLength64 = body.Length;
        await resp.OutputStream.WriteAsync(body);
        resp.Close();
        return;
    }

    resp.StatusCode = 200;
    resp.ContentType = "text/event-stream";
    resp.SendChunked = true; // niente Content-Length: streaming vero, e troncabile a comando (scenario "truncated")

    if (step.DelayMs is int d && d > 0) await Task.Delay(d);

    async Task WriteChunk(object chunk)
    {
        var line = "data: " + JsonSerializer.Serialize(chunk, JsonOpts()) + "\n\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await resp.OutputStream.WriteAsync(bytes);
        await resp.OutputStream.FlushAsync();
    }

    try
    {
        if (step.Type == "text")
        {
            // Qualche delta separato (come un vero streaming a token), non tutto in un chunk:
            // esercita la stessa logica di accumulo di StreamChatAsync di un caso reale.
            var words = (step.Content ?? "").Split(' ');
            foreach (var w in words)
                await WriteChunk(new { choices = new[] { new { delta = new { content = w + " " }, finish_reason = (string?)null } } });

            await WriteChunk(new
            {
                choices = new[] { new { delta = new { }, finish_reason = step.FinishReason ?? "stop" } },
                usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens }
            });
        }
        else if (step.Type == "tool_call")
        {
            // RawArguments (se presente) va VERBATIM, bypassando la serializzazione di
            // Arguments: serve per scriptare un JSON deliberatamente tagliato/non valido
            // (test di IncompleteLlmResponseException con finish_reason="length").
            var argsJson = step.RawArguments ?? JsonSerializer.Serialize(step.Arguments ?? new Dictionary<string, object?>());
            await WriteChunk(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new { index = 0, id = $"call_{n}", function = new { name = step.Tool ?? "read_file", arguments = argsJson } }
                            }
                        },
                        finish_reason = (string?)null
                    }
                }
            });
            await WriteChunk(new
            {
                choices = new[] { new { delta = new { }, finish_reason = step.FinishReason ?? "tool_calls" } },
                usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens }
            });
        }
        else if (step.Type == "truncated")
        {
            // Un contenuto parziale, poi la connessione si chiude SENZA finish_reason né
            // [DONE]: simula un llama-server che muore/si disconnette a metà streaming.
            await WriteChunk(new { choices = new[] { new { delta = new { content = "Sto scrivendo una risposta che non finirà" }, finish_reason = (string?)null } } });
            resp.Close(); // chiusura brusca: niente altro scritto
            return;
        }
        else if (step.Type == "truncated_toolcall")
        {
            // Come "truncated", ma la connessione cade mentre si stava accumulando una tool
            // call (argomenti JSON a metà) invece che testo: verifica che IncompleteLlmResponseException
            // scatti ANCHE in questo caso, prima che un ToolCall parziale/non valido venga costruito.
            var argsJson = step.RawArguments ?? "{\"path\":\"secret.txt\",\"content\":\"testo tagliato a met";
            await WriteChunk(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new { tool_calls = new[] { new { index = 0, id = $"call_{n}", function = new { name = step.Tool ?? "write_file", arguments = argsJson } } } },
                        finish_reason = (string?)null
                    }
                }
            });
            resp.Close(); // chiusura brusca: niente finish_reason, niente [DONE]
            return;
        }
        else
        {
            await WriteChunk(new { choices = new[] { new { delta = new { content = "[fake-llm] tipo di risposta scriptata sconosciuto" }, finish_reason = "stop" } } });
        }

        // "[DONE]" non è JSON (letterale OpenAI): va scritto a mano, non con WriteChunk.
        var doneLine = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
        await resp.OutputStream.WriteAsync(doneLine);
        await resp.OutputStream.FlushAsync();
        resp.Close();
    }
    catch (HttpListenerException)
    {
        // Il client (pm-code) ha chiuso la connessione prima che finissimo di scrivere:
        // normale per lo scenario "truncated" o se pm-code stesso interrompe lo stream
        // (vedi ToolCallTooLargeException in LlamaClient.cs) — non un errore del fake server.
    }
}

async Task WriteJsonAsync(HttpListenerResponse resp, int status, object body)
{
    resp.StatusCode = status;
    resp.ContentType = "application/json";
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
    resp.ContentLength64 = bytes.Length;
    await resp.OutputStream.WriteAsync(bytes);
    resp.Close();
}

JsonSerializerOptions JsonOpts() => new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

sealed class Scenario
{
    public List<ScenarioStep> Responses { get; set; } = [];
}

sealed class ScenarioStep
{
    public string Type { get; set; } = "text"; // text | tool_call | http_error | truncated | truncated_toolcall
    public string? Content { get; set; }
    public string? Tool { get; set; }
    public Dictionary<string, object?>? Arguments { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? Status { get; set; }
    public string? Body { get; set; }
    public int? DelayMs { get; set; } // attesa prima di iniziare a scrivere lo stream (test Ctrl+C a metà turno)
    public string? FinishReason { get; set; } // override (es. "length"); default "stop"/"tool_calls" secondo Type
    public string? RawArguments { get; set; } // arguments della tool call presi VERBATIM (bypassa Arguments): per JSON deliberatamente tagliato/non valido
}
