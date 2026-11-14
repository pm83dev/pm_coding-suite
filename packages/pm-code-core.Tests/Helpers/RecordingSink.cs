namespace LocalCodeAgent.Core.Tests.Helpers;

/// <summary>Implementazione minimale di <c>IAgentSink</c> che registra gli eventi per le assert dei test, senza side effect (nessuna UI/WebSocket reale).</summary>
public sealed class RecordingSink : LocalCodeAgent.Core.IAgentSink
{
    public List<(string Name, object? Args)> ToolCallsStarted { get; } = [];
    public List<(string Name, bool Ok, object? Result)> ToolCallsFinished { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Notices { get; } = [];
    public string? LastTurnFinishedReason { get; private set; }

    public void TurnStarted(string turnId, string sessionId) { }
    public void AssistantDelta(string turnId, string text) { }
    public void AssistantDone(string turnId, int promptTokens, int completionTokens) { }
    public void ToolCallStarted(string turnId, string toolName, object? arguments) => ToolCallsStarted.Add((toolName, arguments));
    public void ToolCallFinished(string turnId, string toolName, bool success, object? result, double durationMs) =>
        ToolCallsFinished.Add((toolName, success, result));
    public void TurnFinished(string turnId, string reason) => LastTurnFinishedReason = reason;
    public void Error(string message) => Errors.Add(message);
    public void Notice(string text) => Notices.Add(text);
}
