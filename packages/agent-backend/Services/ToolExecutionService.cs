using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using AgentBackend.Models.Dto;

namespace AgentBackend.Services;

public class ToolExecutionService
{
    private readonly AgentSession _session;
    private readonly Action<AgentEventDto> _onEvent;
    private readonly AgentConfig _config;

    public ToolExecutionService(AgentSession session, Action<AgentEventDto> onEvent, AgentConfig config)
    {
        _session = session;
        _onEvent = onEvent;
        _config = config;
    }

    public async Task<bool> ExecuteTurnAsync(string userMessage)
    {
        var turnId = Guid.NewGuid().ToString("N");
        var llm = _session.LlmClient;
        var history = _session.History;
        var dispatcher = _session.Dispatcher;
        var sessionManager = _session.SessionManager;
        var maxSteps = _config.AgentSettings.MaxSteps;

        if (llm == null || dispatcher == null || sessionManager == null) return false;

        history.Add(new ChatMessage { Role = "user", Content = userMessage });
        _onEvent(new AgentEventDto { Type = "turn_started", TurnId = turnId });

        for (int step = 1; step <= maxSteps; step++)
        {
            var request = new ChatRequest
            {
                Messages = history.ToList(),
                Temperature = _config.LlmSettings.Temperature,
                FrequencyPenalty = _config.LlmSettings.FrequencyPenalty,
                MaxTokens = 4096
            };
            if (_config.LlmSettings.PresencePenalty.HasValue) request.PresencePenalty = _config.LlmSettings.PresencePenalty.Value;
            if (_config.LlmSettings.TopP.HasValue) request.TopP = _config.LlmSettings.TopP.Value;
            if (_config.LlmSettings.TopK.HasValue) request.TopK = _config.LlmSettings.TopK.Value;
            if (_config.LlmSettings.MinP.HasValue) request.MinP = _config.LlmSettings.MinP.Value;
            if (_config.LlmSettings.RepeatPenalty.HasValue) request.RepeatPenalty = _config.LlmSettings.RepeatPenalty.Value;
            if (_config.LlmSettings.RepeatLastN.HasValue) request.RepeatLastN = _config.LlmSettings.RepeatLastN.Value;

            var llmAttempt = 0;
            while (llmAttempt < 3)
            {
                llmAttempt++;
                try
                {
                    var response = await llm.ChatAsync(request);
                    if (response?.Choices.Count == 0) break;
                    var message = response.Choices[0].Message;
                    var usage = response.Usage;

                    if (!string.IsNullOrEmpty(message.Content))
                        _onEvent(new AgentEventDto { Type = "assistant_delta", TurnId = turnId, Text = message.Content });

                    sessionManager.RecordRealUsage(usage?.PromptTokens ?? 0, history);
                    history.Add(new ChatMessage { Role = message.Role, Content = message.Content, ToolCalls = message.ToolCalls });

                    if (message.ToolCalls?.Count > 0)
                    {
                        foreach (var tc in message.ToolCalls)
                        {
                            _onEvent(new AgentEventDto { Type = "tool_call_started", TurnId = turnId, ToolName = tc.Function.Name, Args = tc.Function.Arguments });
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var result = dispatcher.Execute(tc.Function.Name, tc.Function.Arguments, default);
                            var dur = sw.ElapsedMilliseconds;
                            var success = !result.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase);
                            _onEvent(new AgentEventDto { Type = "tool_call_finished", TurnId = turnId, Success = success, Summary = result, DurationMs = dur });
                            history.Add(new ChatMessage { Role = "tool", Content = result });
                        }
                        continue;
                    }

                    _onEvent(new AgentEventDto { Type = "assistant_done", TurnId = turnId, MessageId = Guid.NewGuid().ToString("N"),
                        Usage = new UsageInfoDto { PromptTokens = usage?.PromptTokens ?? 0, CompletionTokens = usage?.CompletionTokens ?? 0 } });
                    _onEvent(new AgentEventDto { Type = "turn_finished", TurnId = turnId, Reason = "completed" });
                    return true;
                }
                catch (Exception ex) when (llmAttempt < 3)
                {
                    var delayMs = 1000 * (int)Math.Pow(2, llmAttempt - 1);
                    _onEvent(new AgentEventDto { Type = "assistant_delta", TurnId = turnId, Text = $"[Retry {llmAttempt}/3 in {delayMs/1000}s]" });
                    await Task.Delay(delayMs);
                }
                catch
                {
                    _onEvent(new AgentEventDto { Type = "turn_finished", TurnId = turnId, Reason = "error" });
                    return false;
                }
            }
        }
        _onEvent(new AgentEventDto { Type = "turn_finished", TurnId = turnId, Reason = "completed" });
        return true;
    }
}
