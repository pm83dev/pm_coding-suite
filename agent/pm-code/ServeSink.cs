using System.Text.Json;

namespace LocalCodeAgent.Core;

/// <summary>
/// IAgentSink per la modalità --serve: ogni evento del loop diventa un messaggio del protocollo
/// v1 (ui/protocol), passato al delegato <paramref name="send"/> (che lo accoda sulla connessione
/// WebSocket corrente — vedi ServeConnection.Send, mai bloccante). Un'istanza per TURNO (creata
/// da RunServeModeAsync subito prima di ogni send_message): lo stato interno (messageId/callId
/// del "messaggio in corso") non deve sopravvivere al turno successivo.
/// </summary>
public sealed class ServeSink(Action<object> send, string sessionId) : IAgentSink
{
    private string? _currentMessageId;
    private string? _currentCallId;

    public void TurnStarted(string turnId, string sessionIdIgnored) =>
        send(ServeProtocol.Envelope("turn_started", new { turnId }, sessionId));

    public void AssistantDelta(string turnId, string text)
    {
        _currentMessageId ??= ServeProtocol.MakeId();
        send(ServeProtocol.Envelope("assistant_delta", new { turnId, messageId = _currentMessageId, text }, sessionId));
    }

    public void AssistantDone(string turnId, int promptTokens, int completionTokens)
    {
        _currentMessageId ??= ServeProtocol.MakeId();
        send(ServeProtocol.Envelope("assistant_done",
            new { turnId, messageId = _currentMessageId, usage = new { promptTokens, completionTokens } }, sessionId));
        _currentMessageId = null;
    }

    public void ToolCallStarted(string turnId, string toolName, object? arguments)
    {
        _currentCallId = ServeProtocol.MakeId();
        send(ServeProtocol.Envelope("tool_call_started",
            new { turnId, callId = _currentCallId, name = toolName, args = ToJsonObject(arguments) }, sessionId));
    }

    public void ToolCallFinished(string turnId, string toolName, bool success, object? result, double durationMs)
    {
        // callId sempre quello dell'ultima ToolCallStarted: nel loop (Program.cs) start/finish di
        // una tool call sono sempre una coppia sequenziale, mai due in corso contemporaneamente.
        var callId = _currentCallId ?? ServeProtocol.MakeId();
        var resultText = result as string ?? result?.ToString() ?? "";
        send(ServeProtocol.Envelope("tool_call_finished",
            new { turnId, callId, ok = success, summary = ServeProtocol.Summarize(resultText), durationMs }, sessionId));
        _currentCallId = null;
    }

    public void TurnFinished(string turnId, string reason)
    {
        // TurnFinishReason (ui/protocol) ammette solo completed|cancelled|error: "max_turns"
        // (limite interno di step, vedi Program.cs/RunAgentLoopAsync) non ha un valore proprio —
        // il turno si è comunque concluso (non è un errore né un annullamento), quindi il più
        // vicino è "completed".
        var mapped = reason is "completed" or "cancelled" or "error" ? reason : "completed";
        send(ServeProtocol.Envelope("turn_finished", new { turnId, reason = mapped }, sessionId));
    }

    public void Error(string message) =>
        send(ServeProtocol.Envelope("error", new { code = "bad_request", message }, sessionId));

    // Nessun tipo "notice" nel protocollo v1 (vedi ui/protocol/src/types.ts, SERVER_MESSAGE_TYPES):
    // un intervento automatico del motore (anti-loop, rollover...) resta solo nel trace lato
    // server (Trace()/StructuredTrace in Program.cs), non diventa un evento sul socket.
    public void Notice(string text) { }

    // tool_call_started.payload.args deve essere un oggetto (ui/protocol/src/parse.ts): gli
    // argomenti arrivano qui come la stringa JSON grezza della tool call (stesso valore passato a
    // ConsoleSink), quindi li riparsiamo. Se per qualche motivo non fossero JSON valido, li
    // avvolgiamo invece di violare il contratto del payload.
    private static object ToJsonObject(object? arguments)
    {
        if (arguments is string s)
        {
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(s);
                if (el.ValueKind == JsonValueKind.Object) return el;
            }
            catch { /* non JSON valido: avvolto sotto */ }
            return new { raw = s };
        }
        return arguments ?? new { };
    }
}
