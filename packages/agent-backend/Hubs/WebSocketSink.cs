using System.Text.Json;
using LocalCodeAgent.Core;
using AgentBackend.Models.Dto;

namespace AgentBackend.Hubs;

/// <summary>
/// Implementa <see cref="IAgentSink"/> per il backend: traduce gli eventi del loop
/// ReAct in <see cref="AgentEventDto"/> che il <see cref="WebSocketHub"/> invia via WS
/// a tutti i client connessi. Segue il pattern di <c>ServeSink</c> (agent/pm-code):
/// genera <c>messageId</c>/<c>callId</c>, mappa i reason di <c>turn_finished</c>,
/// e deserializza gli argomenti dei tool call in oggetti JSON.
/// </summary>
public class WebSocketSink : IAgentSink
{
    private readonly Action<AgentEventDto> _emit;

    // Stato per generare ID coerenti: un messageId per messaggio assistant,
    // un callId per tool call. Start/finish di una tool call sono sempre
    // sequenziali nel loop, quindi un singolo _currentCallId basta.
    private string? _currentMessageId;
    private string? _currentCallId;

    public WebSocketSink(Action<AgentEventDto> emit)
    {
        _emit = emit;
    }

    public void TurnStarted(string turnId, string sessionId)
    {
        // Reset dello stato per il nuovo turno (stesso pattern di ServeSink).
        _currentMessageId = null;
        _currentCallId = null;
        _emit(new AgentEventDto { Type = "turn_started", TurnId = turnId });
    }

    public void AssistantDelta(string turnId, string text)
    {
        _currentMessageId ??= Guid.NewGuid().ToString("N");
        _emit(new AgentEventDto
        {
            Type = "assistant_delta",
            TurnId = turnId,
            MessageId = _currentMessageId,
            Text = text
        });
    }

    public void AssistantDone(string turnId, int promptTokens, int completionTokens)
    {
        _currentMessageId ??= Guid.NewGuid().ToString("N");
        _emit(new AgentEventDto
        {
            Type = "assistant_done",
            TurnId = turnId,
            MessageId = _currentMessageId,
            Usage = new UsageInfoDto { PromptTokens = promptTokens, CompletionTokens = completionTokens }
        });
        _currentMessageId = null;
    }

    public void ToolCallStarted(string turnId, string toolName, object? arguments)
    {
        _currentCallId = Guid.NewGuid().ToString("N");
        _emit(new AgentEventDto
        {
            Type = "tool_call_started",
            TurnId = turnId,
            CallId = _currentCallId,
            ToolName = toolName,
            Args = ToJsonObject(arguments)
        });
    }

    public void ToolCallFinished(string turnId, string toolName, bool success, object? result, double durationMs)
    {
        var callId = _currentCallId ?? Guid.NewGuid().ToString("N");
        var resultText = result as string ?? result?.ToString() ?? "";
        _emit(new AgentEventDto
        {
            Type = "tool_call_finished",
            TurnId = turnId,
            CallId = callId,
            ToolName = toolName,
            Success = success,
            Summary = Summarize(resultText),
            DurationMs = durationMs
        });
        _currentCallId = null;
    }

    public void TurnFinished(string turnId, string reason)
    {
        // L'UI valida reason ∈ {completed, cancelled, error, max_steps, timeout, aborted}.
        // Il core emette anche "max_turns" (limite di step): lo mappiamo su "completed"
        // (stesso pattern di ServeSink).
        var mapped = reason is "completed" or "cancelled" or "error" or "max_steps" or "timeout" or "aborted"
            ? reason
            : "completed";
        _emit(new AgentEventDto { Type = "turn_finished", TurnId = turnId, Reason = mapped });
    }

    public void Error(string message)
    {
        // L'UI valida error.payload = { code, message }. Usiamo "bad_request" come code
        // generico (stesso pattern di ServeSink).
        _emit(new AgentEventDto { Type = "error", Code = "bad_request", Message = message });
    }

    // Nessun tipo "notice" nel protocollo v1 (vedi ui/protocol/src/types.ts):
    // i notice del motore (anti-loop, rollover, ...) restano solo nel trace server.
    public void Notice(string text) { }

    /// <summary>
    /// Deserializza gli argomenti del tool call in un oggetto JSON (l'UI richiede
    /// <c>tool_call_started.payload.args</c> come oggetto). Se la stringa non è JSON
    /// valido, la avvolgiamo in <c>{ raw: "..." }</c> per non violare il contratto.
    /// </summary>
    private static object? ToJsonObject(object? arguments)
    {
        if (arguments is string s)
        {
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(s);
                if (el.ValueKind == JsonValueKind.Object) return el;
            }
            catch { /* non JSON valido */ }
            return new { raw = s };
        }
        return arguments ?? new { };
    }

    /// <summary>
    /// Riassunto breve del risultato di un tool per <c>tool_call_finished.payload.summary</c>.
    /// Prende la prima riga, rimuove i prefissi "ERRORE:"/"⚠️ " (l'UI sa già da <c>ok</c>
    /// se è un errore), e tronca a 200 caratteri.
    /// </summary>
    private static string Summarize(string result, int max = 200)
    {
        if (string.IsNullOrEmpty(result)) return "";
        var firstLine = result.Split('\n')[0].Trim();
        foreach (var prefix in new[] { "ERRORE: ", "ERRORE ", "⚠️ " })
        {
            if (firstLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                firstLine = firstLine[prefix.Length..];
                break;
            }
        }
        return firstLine.Length <= max ? firstLine : firstLine[..max] + "…";
    }
}