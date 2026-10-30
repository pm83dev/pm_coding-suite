using System.Text.Json.Serialization;

namespace AgentBackend.Models.Dto;

/// <summary>
/// Rappresenta un messaggio live inviato via SignalR durante l'esecuzione dell'agente.
/// Mappa gli eventi del protocollo serve (turn_started, assistant_delta, tool_call_*, turn_finished).
/// </summary>
public class AgentEventDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("turnId")]
    public string? TurnId { get; set; }

    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("callId")]
    public string? CallId { get; set; }

    [JsonPropertyName("toolName")]
    public string? ToolName { get; set; }

    [JsonPropertyName("args")]
    public object? Args { get; set; }

    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("usage")]
    public UsageInfoDto? Usage { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";
}

public class UsageInfoDto
{
    [JsonPropertyName("promptTokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completionTokens")]
    public int CompletionTokens { get; set; }
}
