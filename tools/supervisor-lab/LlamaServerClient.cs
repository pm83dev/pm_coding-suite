using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupervisorLab;

// Client HTTP minimale verso un endpoint llama-server compatibile OpenAI
// (POST {endpoint}/v1/chat/completions), copiato nello stile da agent/pm-code/LlamaClient.cs
// ma riscritto qui (non referenziato come libreria: pm-code resta sola lettura) e ridotto a
// quanto serve per gli esperimenti 5 e 6: nessuno streaming, nessuna gestione tool_calls.
//
// NON VERIFICATO (da verificare dall'utente contro il/i server reali):
// - se il server 9B su llama-server supporta "response_format": {"type":"json_schema", ...}
//   nello stile OpenAI (llama.cpp lo supporta da versioni recenti tramite grammar GBNF
//   generata dallo schema, ma la versione in uso qui non è nota a questo strumento);
// - come disattivare il "ragionamento" per un modello Qwen3 su llama-server: qui si tenta
//   "chat_template_kwargs": {"enable_thinking": false} (pattern noto per vLLM/Qwen3 lato
//   client), ma se il server non lo riconosce il campo viene semplicemente ignorato dai
//   server OpenAI-compatibili permissivi, O rifiutato con 400 da quelli strict — l'opzione
//   --disable-thinking va quindi provata a mano la prima volta.
public sealed class LlamaServerClient(string endpoint, string model)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<string> ChatRawAsync(string systemPrompt, string userPrompt,
        JsonElement? jsonSchema = null, bool disableThinking = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = 0,
            ["messages"] = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        if (jsonSchema is { } schema)
        {
            body["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = "classification", strict = true, schema }
            };
        }

        if (disableThinking)
            body["chat_template_kwargs"] = new { enable_thinking = false };

        using var resp = await Http.PostAsJsonAsync($"{endpoint.TrimEnd('/')}/v1/chat/completions", body);
        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} da {endpoint}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}

public sealed class ClassificationResult
{
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("suggestion")] public string Suggestion { get; set; } = "";
}

public static class ClassificationSchema
{
    // Schema JSON dato dall'utente: state enum, reason/suggestion stringhe max 200 caratteri
    // (il max-length è un vincolo descrittivo nello schema: molti motori di grammar GBNF
    // generati da llama.cpp NON impongono maxLength in modo rigido — da verificare).
    public static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            state = new { type = "string", @enum = new[] { "on_track", "looping", "off_task", "stuck" } },
            reason = new { type = "string", maxLength = 200 },
            suggestion = new { type = "string", maxLength = 200 }
        },
        required = new[] { "state", "reason", "suggestion" },
        additionalProperties = false
    });
}
