using Microsoft.AspNetCore.SignalR;
using LocalCodeAgent.Models;
using AgentBackend.Services;
using AgentBackend.Models.Dto;

namespace AgentBackend.Hubs;

public class AgentHub : Hub
{
    private readonly AgentSessionService _sessionService;
    private readonly AgentConfig _config;
    private readonly string? _braveApiKey;

    public AgentHub(AgentSessionService sessionService, AgentConfig config, string? braveApiKey = null)
    {
        _sessionService = sessionService;
        _config = config;
        _braveApiKey = braveApiKey;
    }

    public async Task Hello(string clientName, string clientVersion)
    {
        await Clients.Caller.SendAsync("hello_ok", new
        {
            serverVersion = "2.0.0",
            protocol = 1
        });
    }

    public async Task ListSessions()
    {
        var sessions = _sessionService.ListSessions();
        await Clients.Caller.SendAsync("sessions_list", new { sessions });
    }

    public async Task NewSession(CreateSessionRequest request)
    {
        var session = _sessionService.CreateSession(request.Workspace, request.Model);
        await Clients.Caller.SendAsync("session_state", new
        {
            session = new SessionDto
            {
                Id = session.SessionId, Title = session.Title,
                Workspace = session.WorkspaceRoot,
                UpdatedAt = session.UpdatedAt.ToString("o"),
                Status = session.Status, Model = session.Model
            },
            messages = Array.Empty<object>(),
            model = session.Model,
            permission = "readonly"
        });
    }

    public async Task SendMessage(string sessionId, string text)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            await Clients.Caller.SendAsync("error", new { code = "unknown_session", message = "Session not found" });
            return;
        }

        if (!_sessionService.TryStartTurn(sessionId))
        {
            await Clients.Caller.SendAsync("error", new { code = "bad_request", message = "Turn already in progress" });
            return;
        }

        try
        {
            var executionService = new ToolExecutionService(session, EmitEvent, _config);
            await executionService.ExecuteTurnAsync(text);
        }
        finally
        {
            _sessionService.EndTurn(sessionId, "completed");
        }
    }

    public async Task Cancel(string sessionId)
    {
        _sessionService.EndTurn(sessionId, "cancelled");
        await Clients.Caller.SendAsync("turn_finished", new { turnId = (string?)null, reason = "cancelled" });
    }

    private void EmitEvent(AgentEventDto evt)
    {
        Clients.Caller.SendAsync(evt.Type, evt);
    }
}
