using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocalCodeAgent.Models;

public class ChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "local";

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolDefinition>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolChoice { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 4096;

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 0.0f;

    // Con temperature 0 (greedy) un modello locale quantizzato può entrare in un loop di
    // ripetizione (token più probabile = continuare la frase già detta). Una penalità sulla
    // frequenza rompe il loop senza introdurre randomicità nelle scelte di tool call.
    [JsonPropertyName("frequency_penalty")]
    public float FrequencyPenalty { get; set; } = 0.3f;

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stream { get; set; }
}

public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    public static ChatMessage System(string content) => new() { Role = "system", Content = content };
    public static ChatMessage User(string content) => new() { Role = "user", Content = content };
    public static ChatMessage ToolResult(string id, string content) =>
        new() { Role = "tool", ToolCallId = id, Content = content };

    // Kimi (e derivati come Kimi-Linear) emettono le tool call nel formato nativo
    // "functions.<nome>:<indice>{<json>}" invece del campo tool_calls OpenAI-standard,
    // quando llama-server non riconosce il chat_format del modello e non le traduce.
    // Estraendole qui a valle, il dispatcher continua a vedere ToolCalls popolati come
    // per qualunque altro modello, senza dover distinguere i due formati altrove.
    private static readonly Regex KimiToolCallPattern =
        new(@"functions\.(?<name>[A-Za-z0-9_]+):(?<idx>\d+)\s*", RegexOptions.Compiled);

    public bool TryExtractFallbackToolCalls()
    {
        if (ToolCalls is { Count: > 0 } || string.IsNullOrEmpty(Content))
            return false;

        var matches = KimiToolCallPattern.Matches(Content);
        if (matches.Count == 0)
            return false;

        var extracted = new List<ToolCall>();
        var firstMatchStart = -1;

        foreach (Match m in matches)
        {
            var braceStart = m.Index + m.Length;
            if (braceStart >= Content.Length || Content[braceStart] != '{')
                continue;

            var braceEnd = FindMatchingBrace(Content, braceStart);
            if (braceEnd < 0)
                continue;

            extracted.Add(new ToolCall
            {
                Id = $"kimi-{m.Groups["idx"].Value}",
                Function = new FunctionCall
                {
                    Name = m.Groups["name"].Value,
                    Arguments = Content[braceStart..(braceEnd + 1)]
                }
            });

            if (firstMatchStart < 0) firstMatchStart = m.Index;
        }

        if (extracted.Count == 0)
            return false;

        ToolCalls = extracted;
        Content = firstMatchStart > 0 ? Content[..firstMatchStart].TrimEnd() : null;
        return true;
    }

    // Scansione manuale invece di una regex "\{.*\}": gli argomenti sono JSON e possono
    // contenere graffe annidate o dentro stringhe — serve contare la profondità reale
    // ignorando quelle tra virgolette, non un semplice bilanciamento carattere per carattere.
    private static int FindMatchingBrace(string s, int openIndex)
    {
        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = openIndex; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }
}

public class ToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public FunctionDefinition Function { get; set; } = new();
}

public class FunctionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("parameters")]
    public object Parameters { get; set; } = new();
}

public class ChatResponse
{
    [JsonPropertyName("choices")]
    public List<Choice> Choices { get; set; } = [];

    [JsonPropertyName("usage")]
    public Usage? Usage { get; set; }
}

public class Choice
{
    [JsonPropertyName("message")]
    public ChatMessage Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public class ToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public FunctionCall Function { get; set; } = new();
}

public class FunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";
}

public class Usage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }
}

// ── Streaming models ──────────────────────────────────────────────────────────

public class StreamChunk
{
    [JsonPropertyName("choices")]
    public List<StreamChoice> Choices { get; set; } = [];

    [JsonPropertyName("usage")]
    public Usage? Usage { get; set; }
}

public class StreamChoice
{
    [JsonPropertyName("delta")]
    public StreamDelta Delta { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public class StreamDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<ToolCallDelta>? ToolCalls { get; set; }
}

public class ToolCallDelta
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("function")]
    public FunctionDelta? Function { get; set; }
}

public class FunctionDelta
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}
