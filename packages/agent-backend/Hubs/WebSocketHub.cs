using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentBackend.Models.Dto;
using AgentBackend.Services;
using LocalCodeAgent.Models;

namespace AgentBackend.Hubs;

/// <summary>
/// WebSocket raw hub — sostituisce SignalR con un gestore WebSocket semplice.
/// Protocollo JSON: ogni messaggio è un singolo JSON con campo "type".
/// </summary>
public class WebSocketHub
{
    private readonly AgentSessionService _sessionService;
    private readonly AgentConfig _config;
    private readonly string? _braveApiKey;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public WebSocketHub(AgentSessionService sessionService, AgentConfig config, string? braveApiKey = null)
    {
        _sessionService = sessionService;
        _config = config;
        _braveApiKey = braveApiKey;
    }

    /// <summary>
    /// Middleware handler per le connessioni WebSocket.
    /// </summary>
    public async Task ProcessWebSocketAsync(HttpContext context, Func<Task> next)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("WebSocket request expected");
            return;
        }

        var ws = await context.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid().ToString("N");
        _clients[clientId] = ws;

        try
        {
            await ReceiveLoop(ws, clientId);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None); } catch { }
        }
    }

    private async Task ReceiveLoop(WebSocket ws, string clientId)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType == WebSocketMessageType.Text && result.EndOfMessage)
            {
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                try
                {
                    await HandleMessage(clientId, text);
                }
                catch (Exception ex)
                {
                    await SendAll("error", new { code = "bad_request", message = ex.Message });
                }
            }
        }
    }

    private async Task HandleMessage(string clientId, string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        // Protocollo v1: {"v":1,"id":"...","ts":"...","sessionId":"...","type":"...","payload":{...}}
        var v = root.TryGetProperty("v", out var vProp) ? vProp.GetInt32() : 1;
        var type = root.GetProperty("type").GetString() ?? "";
        JsonElement? payload = null;
        if (root.TryGetProperty("payload", out var payloadProp))
        {
            payload = payloadProp;
        }

        switch (type)
        {
            case "hello":
                var token = payload?.GetProperty("token").GetString() ?? "";
                await SendAll("hello_ok", new { serverVersion = "2.0.0", protocol = 1 });
                break;

            case "list_sessions":
                {
                    var sessions = _sessionService.ListSessions();
                    await SendAll("sessions_list", new { sessions });
                    break;
                }

            case "new_session":
                {
                    var workspace = payload?.GetProperty("workspace").GetString() ?? ".";
                    var model = payload?.TryGetProperty("model", out var mProp) == true ? mProp.GetString() : null;
                    var session = _sessionService.CreateSession(workspace, model);
                    await SendAll("session_state", new
                    {
                        session = new
                        {
                            Id = session.SessionId,
                            Title = session.Title,
                            Workspace = session.WorkspaceRoot,
                            UpdatedAt = session.UpdatedAt.ToString("o"),
                            Status = session.Status,
                            Model = session.Model
                        },
                        messages = Array.Empty<object>(),
                        model = session.Model,
                        permission = "readonly"
                    });
                    break;
                }

            case "open_session":
                {
                    var sid = payload?.GetProperty("sessionId").GetString() ?? "";
                    var session = _sessionService.GetSession(sid);
                    if (session == null)
                    {
                        await SendAll("error", new { code = "unknown_session", message = "Session not found" });
                        return;
                    }
                    await SendAll("session_state", new
                    {
                        session = new
                        {
                            Id = session.SessionId,
                            Title = session.Title,
                            Workspace = session.WorkspaceRoot,
                            UpdatedAt = session.UpdatedAt.ToString("o"),
                            Status = session.Status,
                            Model = session.Model
                        },
                        messages = Array.Empty<object>(),
                        model = session.Model,
                        permission = "readonly"
                    });
                    break;
                }

            case "send_message":
                {
                    var msgSessionId = root.TryGetProperty("sessionId", out var sidProp) ? sidProp.GetString() ?? "" : "";
                    var msgText = payload?.GetProperty("text").GetString() ?? "";

                    if (string.IsNullOrEmpty(msgSessionId))
                    {
                        await SendAll("error", new { code = "bad_request", message = "sessionId required" });
                        return;
                    }

                    var session = _sessionService.GetSession(msgSessionId);
                    if (session == null)
                    {
                        await SendAll("error", new { code = "unknown_session", message = "Session not found" });
                        return;
                    }

                    if (!_sessionService.TryStartTurn(msgSessionId))
                    {
                        await SendAll("error", new { code = "bad_request", message = "Turn already in progress" });
                        return;
                    }

                    try
                    {
                        var executionService = new ToolExecutionService(session, EmitEvent, _config);
                        await executionService.ExecuteTurnAsync(msgText);
                    }
                    catch (Exception ex)
                    {
                        await SendAll("error", new { code = "error", message = ex.Message });
                        await SendAll("turn_finished", new { turnId = (string?)null, reason = "error" });
                    }
                    finally
                    {
                        _sessionService.EndTurn(msgSessionId, "completed");
                    }
                    break;
                }

            case "cancel":
                {
                    var cancelId = root.TryGetProperty("sessionId", out var ci) ? ci.GetString() ?? "" : "";
                    _sessionService.EndTurn(cancelId, "cancelled");
                    await SendAll("turn_finished", new { turnId = (string?)null, reason = "cancelled" });
                    break;
                }

            case "approve":
                {
                    var approvalId = payload?.GetProperty("approvalId").GetString() ?? "";
                    await SendAll("approval_resolved", new { approvalId, outcome = "approved" });
                    break;
                }

            case "reject":
                {
                    var approvalId = payload?.GetProperty("approvalId").GetString() ?? "";
                    var reason = payload?.TryGetProperty("reason", out var rProp) == true ? rProp.GetString() : null;
                    await SendAll("approval_resolved", new { approvalId, outcome = "rejected" });
                    break;
                }

            default:
                await SendAll("error", new { code = "bad_request", message = $"Unknown message type: {type}" });
                break;
        }
    }

    /// <summary>
    /// Emette un evento dell'agente a tutti i client connessi.
    /// </summary>
    private void EmitEvent(AgentEventDto evt)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var timestamp = DateTime.UtcNow.ToString("o");
        var payloadJson = JsonSerializer.Serialize(evt, _jsonOpts);
        var json = $"{{\"v\":1,\"id\":\"{messageId}\",\"ts\":\"{timestamp}\",\"sessionId\":null,\"type\":\"{evt.Type}\",\"payload\":{payloadJson}}}";
        foreach (var (_, ws) in _clients)
        {
            SendAsync(ws, json); // fire-and-forget
        }
    }

    /// <summary>
    /// Invia un messaggio a tutti i client connessi usando il formato protocollo v1.
    /// Formato: {"v":1,"id":"uuid","ts":"ISO8601","sessionId":null,"type":"eventType","payload":{...}}
    /// </summary>
    private async Task SendAll(string eventType, object payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, _jsonOpts);
        var messageId = Guid.NewGuid().ToString("N");
        var timestamp = DateTime.UtcNow.ToString("o");

        // Build protocol v1 envelope

        var json = $"{{\"v\":1,\"id\":\"{messageId}\",\"ts\":\"{timestamp}\",\"sessionId\":null,\"type\":\"{eventType}\",\"payload\":{payloadJson}}}";

        foreach (var (_, ws) in _clients)
        {
            await SendAsync(ws, json);
        }
    }

    private async Task SendAsync(WebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { /* client disconnected */ }
    }
}
