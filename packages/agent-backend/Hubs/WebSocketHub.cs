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
    private string? _activeTurnId;
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

                // Determina il type del messaggio per decidere se fare await o no.
                // I messaggi "lunghi" (send_message) avviano il loop ReAct che può
                // durare a lungo e bloccarsi in AwaitApprovalAsync. Se facciamo
                // await su HandleMessage, il ReceiveLoop si blocca e non può leggere
                // altri messaggi (approve/reject/cancel) finché il turno non termina.
                // Quindi per send_message NON facciamo await: lo eseguiamo in
                // background così il ReceiveLoop resta libero di leggere approve/cancel.
                //
                // Attenzione: il Task.Run NON va cancellato quando la connessione muore.
                // Se il turno è già in attesa di approvazione, il ReceiveLoop è già
                // terminato e il Task non può più essere cancellato dal connCt di
                // ProcessWebSocketAsync; cancellarlo lo lascerebbe in sospeso per i
                // 300 s di AwaitApprovalAsync. La Task vive per il suo ciclo di vita
                // naturale (il turno, non la connessione): i clienti disconnessi
                // non ricevono più eventi, ma il turno va comunque a buon fine e
                // EndTurn viene chiamato nel finally di HandleMessage.
                var isLongRunning = false;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    isLongRunning = doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "send_message";
                }
                catch { }

                if (isLongRunning)
                {
                    // Esegui in background (fire-and-forget con gestione errori).
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await HandleMessage(clientId, text);
                        }
                        catch (Exception ex)
                        {
                            await SendAll("error", new { code = "error", message = ex.Message });
                        }
                    });
                }
                else
                {
                    // Messaggi brevi: facciamo await (sono rapidi e non bloccano).
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

        // Log di debug: traccia ogni messaggio ricevuto (utile per capire se
        // approve/reject/cancel arrivano al backend). Include il contenuto grezzo
        // per diagnosticare eventuali problemi di formato.
        DebugLog($"received type={type} (client={clientId[..8]}) raw={text.Substring(0, Math.Min(200, text.Length))}");

        switch (type)
        {
            case "hello":
                // Token field is optional — no authentication for local app
                if (payload?.TryGetProperty("token", out var _tokenProp) == true)
                {
                    // Token present but not validated (local-only app)
                }
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
                        permission = session.PermissionMode
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
                        messages = MapHistory(session.History),
                        model = session.Model,
                        permission = session.PermissionMode
                    });
                    break;
                }

            case "delete_session":
                {
                    var delId = payload?.GetProperty("sessionId").GetString() ?? "";
                    var delSession = _sessionService.GetSession(delId);
                    if (delSession == null)
                    {
                        await SendAll("error", new { code = "unknown_session", message = "Session not found" });
                        return;
                    }
                    _sessionService.DeleteSession(delId);
                    await SendAll("session_deleted", new { sessionId = delId });
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
                        // Log per diagnosticare: se la sessione esiste ma il flag TurnInProgress
                        // è ancora true, il turno precedente non ha chiamato EndTurn (es. il
                        // Task è ancora appeso in RunTurnAsync, o il backend è stato riavviato
                        // lasciando lo stato in memoria incoerente).
                        DebugLog($"send_message RIFIUTATO (già in corso) sessione={msgSessionId[..Math.Min(8, msgSessionId.Length)]}");
                        await SendAll("error", new { code = "bad_request", message = "Turn already in progress" });
                        return;
                    }

                    // Avvia il tracking del turno: crea il CTS che il loop ReAct
                    // controlla per fermarsi su "cancel".
                    session.BeginTurn();

                    // Il flag TurnInProgress è la SENTINELLA di fine turno: resta true
                    // per tutta la durata del turno (Tutti gli step, approvazioni incluse)
                    // e viene azzerato SOLO dal finally qui sotto. Se il Task.Run
                    // fallisse con un'eccezione non catturata (es. un bug nel loop) il
                    // finally non girerebbe e la sessione resterebbe "running" per
                    // sempre, rifiutando ogni send_message successivo con
                    // "Turn already in progress" — esattamente il bug che si è visto
                    // (sessione bloccata dopo un turno interrotto). La Task.Run non
                    // può essere cancellata (vedi commento in ReceiveLoop), quindi
                    // l'unico modo per garantire il cleanup è un watchdog che forza
                    // EndTurn se il Task non è ancora terminato dopo un tempo
                    // ragionevole.
                    var turnTask = Task.Run(async () =>
                    {
                        try
                        {
                            var executionService = new AgentLoopSession(session, EmitEvent, _config);
                            var (success, activeTurnId) = await executionService.RunTurnAsync(msgText);
                            _activeTurnId = activeTurnId;
                        }
                        catch (Exception ex)
                        {
                            await SendAll("error", new { code = "error", message = ex.Message });
                            await SendAll("turn_finished", new { turnId = _activeTurnId, reason = "error" });
                        }
                    });

                    // Watchdog: se il Task non è terminato dopo 5 minuti (molto più
                    // dei 300 s di AwaitApprovalAsync + margine per gli step, ma molto
                    // meno dei 10 min precedenti), forza EndTurn. Il Task in sé continua
                    // a girare (non lo cancelliamo: l'approvazione potrebbe ancora essere
                    // in attesa), ma la sessione torna "idle" e un nuovo send_message può
                    // partire. 5 min è un compromesso: abbastanza per un turno lungo con
                    // molte approvazioni, poco abbastanza da non bloccare l'utente a lungo
                    // se un turno resta appeso (es. bug nel loop).
                    _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(t =>
                    {
                        if (!turnTask.IsCompleted)
                        {
                            DebugLog($"watchdog: send_message per sessione {msgSessionId[..8]} non terminato dopo 5 min — forza EndTurn");
                            _sessionService.EndTurn(msgSessionId, "watchdog");
                        }
                    }, TaskScheduler.Default);

                    try
                    {
                        await turnTask;
                    }
                    finally
                    {
                        // Pulisce il CTS del turno (idempotente: va bene chiamarlo
                        // anche se il turno è già terminato normalmente).
                        session.EndTurnCts();
                        _sessionService.EndTurn(msgSessionId, "completed");
                    }
                    break;
                }

            case "cancel":
                {
                    var cancelId = root.TryGetProperty("sessionId", out var ci) ? ci.GetString() ?? "" : "";
                    var cancelSession = string.IsNullOrEmpty(cancelId) ? null : _sessionService.GetSession(cancelId);
                    var cancelled = cancelSession != null && cancelSession.CancelTurn();
                    var cancelTurnId = _activeTurnId ?? Guid.NewGuid().ToString("N");
                    if (cancelled)
                    {
                        // Il loop ReAct vedrà il token cancellato e emetterà
                        // turn_finished da solo. Qui emettiamo anche un evento
                        // immediato per feedback UI istantaneo (il loop lo
                        // sovrascriverà con lo stesso reason).
                        await SendAll("turn_finished", new { turnId = cancelTurnId, reason = "cancelled" });
                        _sessionService.EndTurn(cancelId, "cancelled");
                        _activeTurnId = null;
                    }
                    else
                    {
                        await SendAll("error", new { code = "bad_request", message = "Nessun turno in corso da annullare" });
                    }
                    break;
                }

            case "approve":
                {
                    var approvalId = payload?.GetProperty("approvalId").GetString() ?? "";
                    var approveSid = root.TryGetProperty("sessionId", out var apSid) ? apSid.GetString() ?? "" : "";
                    var approveSession = string.IsNullOrEmpty(approveSid) ? null : _sessionService.GetSession(approveSid);
                    var hasPending = approveSession?.HasPendingApproval ?? false;
                    DebugLog($"approve: approvalId={approvalId}, sessionId={approveSid}, sessionFound={approveSession != null}, hasPending={hasPending}");
                    var approved = approveSession != null && approveSession.TryResolveApproval(true);
                    DebugLog($"approve: resolved={approved}");
                    if (approved)
                        await SendAll("approval_resolved", new { approvalId, outcome = "approved" });
                    else
                        await SendAll("error", new { code = "bad_request", message = "Nessuna approvazione pendente da approvare" });
                    break;
                }

            case "reject":
                {
                    var approvalId = payload?.GetProperty("approvalId").GetString() ?? "";
                    var rejectSid = root.TryGetProperty("sessionId", out var rjSid) ? rjSid.GetString() ?? "" : "";
                    var rejectSession = string.IsNullOrEmpty(rejectSid) ? null : _sessionService.GetSession(rejectSid);
                    var hasPending = rejectSession?.HasPendingApproval ?? false;
                    DebugLog($"reject: approvalId={approvalId}, sessionId={rejectSid}, sessionFound={rejectSession != null}, hasPending={hasPending}");
                    var rejected = rejectSession != null && rejectSession.TryResolveApproval(false);
                    DebugLog($"reject: resolved={rejected}");
                    if (rejected)
                        await SendAll("approval_resolved", new { approvalId, outcome = "rejected" });
                    else
                        await SendAll("error", new { code = "bad_request", message = "Nessuna approvazione pendente da rifiutare" });
                    break;
                }

            case "set_permission":
                {
                    var mode = payload?.GetProperty("mode").GetString() ?? "";
                    if (mode is not ("readonly" or "ask" or "auto"))
                    {
                        await SendAll("error", new { code = "bad_request", message = "Modalità permessi non valida (attesi: readonly, ask, auto)" });
                        return;
                    }
                    var permSid = root.TryGetProperty("sessionId", out var pSid) ? pSid.GetString() ?? "" : "";
                    var permSession = string.IsNullOrEmpty(permSid) ? null : _sessionService.GetSession(permSid);
                    if (permSession != null)
                    {
                        permSession.PermissionMode = mode;
                        // Se il turno è in attesa di conferma, sbloccalo per applicare subito il nuovo modo.
                        if (permSession.HasPendingApproval)
                            permSession.TryResolveApproval(mode == "auto");
                        await SendAll("permission_state", new { mode, rules = Array.Empty<object>() });
                    }
                    break;
                }

            default:
                await SendAll("error", new { code = "bad_request", message = $"Unknown message type: {type}" });
                break;
        }
    }

    /// <summary>
    /// Log di debug su file (C:\pm-code\agent-backend\debug.log) — non viene
    /// ottimizzato via dal compiler, a differenza di Console.WriteLine in release.
    /// </summary>
    private static readonly object _logLock = new();
    private static void DebugLog(string message)
    {
        try
        {
            lock (_logLock)
            {
                var line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}";
                File.AppendAllText(@"C:\pm-code\agent-backend\debug.log", line + Environment.NewLine);
            }
        }
        catch { /* il log non deve mai rompere il flusso */ }
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

    /// <summary>
    /// Mappa la storia LLM (solo ruoli user/assistant con testo) nel formato ChatMessage
    /// atteso dal frontend: { id, role, text, ts }.
    /// </summary>
    private static List<object> MapHistory(List<ChatMessage> history)
    {
        var result = new List<object>();
        var i = 0;
        foreach (var m in history)
        {
            if (m.Role != "user" && m.Role != "assistant") continue;
            var content = m.Content;
            if (string.IsNullOrWhiteSpace(content)) continue;
            result.Add(new
            {
                id = $"hist-{i++}",
                role = m.Role,
                text = content,
                ts = DateTime.UtcNow.ToString("o")
            });
        }
        return result;
    }
}
