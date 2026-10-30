using System.Text.Json.Serialization;

namespace AgentBackend.Models.Dto;

public class SessionDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("workspace")]
    public string Workspace { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";
}

public class MessageDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("ts")]
    public string Ts { get; set; } = "";

    [JsonPropertyName("toolCalls")]
    public List<ToolCallDto>? ToolCalls { get; set; }

    [JsonPropertyName("toolResult")]
    public ToolResultDto? ToolResult { get; set; }
}

public class ToolCallDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "{}";
}

public class ToolResultDto
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("durationMs")]
    public double DurationMs { get; set; }
}

public class WorkspaceNodeDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("isDirectory")]
    public bool IsDirectory { get; set; }

    [JsonPropertyName("children")]
    public List<WorkspaceNodeDto>? Children { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }

    [JsonPropertyName("fileCount")]
    public int? FileCount { get; set; }
}

public class ConfigDto
{
    [JsonPropertyName("llm")]
    public LlmConfigDto Llm { get; set; } = new();

    [JsonPropertyName("agent")]
    public AgentConfigDto Agent { get; set; } = new();

    [JsonPropertyName("webSearch")]
    public WebSearchConfigDto WebSearch { get; set; } = new();
}

public class LlmConfigDto
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("temperature")]
    public float Temperature { get; set; }

    [JsonPropertyName("frequencyPenalty")]
    public float FrequencyPenalty { get; set; }

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";
}

public class AgentConfigDto
{
    [JsonPropertyName("maxSteps")]
    public int MaxSteps { get; set; }

    [JsonPropertyName("maxContextTokens")]
    public int MaxContextTokens { get; set; }

    [JsonPropertyName("rolloverTokens")]
    public int RolloverTokens { get; set; }

    [JsonPropertyName("maxLinesPerFile")]
    public int MaxLinesPerFile { get; set; }
}

public class WebSearchConfigDto
{
    [JsonPropertyName("braveApiKey")]
    public string BraveApiKey { get; set; } = "";
}

public class CreateSessionRequest
{
    [JsonPropertyName("workspace")]
    public string Workspace { get; set; } = ".";

    [JsonPropertyName("model")]
    public string? Model { get; set; }
}

public class SendMessageRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

public class ErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = "";
}
