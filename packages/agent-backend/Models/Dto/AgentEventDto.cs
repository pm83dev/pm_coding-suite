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

    [JsonPropertyName("name")]
    public string? ToolName { get; set; }

    [JsonPropertyName("args")]
    public object? Args { get; set; }

    [JsonPropertyName("ok")]
    public bool? Success { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    [JsonPropertyName("usage")]
    public UsageInfoDto? Usage { get; set; }

    // --- error ---
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    // --- approval_request ---
    [JsonPropertyName("approvalId")]
    public string? ApprovalId { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("unifiedDiff")]
    public string? UnifiedDiff { get; set; }

    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("expiresAt")]
    public string? ExpiresAt { get; set; }

    // --- permission_state ---
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("rules")]
    public object? Rules { get; set; }
}

public class UsageInfoDto
{
    [JsonPropertyName("promptTokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completionTokens")]
    public int CompletionTokens { get; set; }
}
