using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using AgentBackend.Models.Dto;

namespace AgentBackend.Services;

public class AgentSessionService
{
    private readonly Dictionary<string, AgentSession> _sessions = new();
    private readonly AgentConfig _config;
    private readonly string? _braveApiKey;
    private readonly object _lock = new();

    public AgentSessionService(AgentConfig config, string? braveApiKey = null)
    {
        _config = config;
        _braveApiKey = braveApiKey;
    }

    public AgentSession CreateSession(string workspaceRoot, string? model = null)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var title = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        var actualModel = model ?? _config.LlmSettings.Model;

        var workspace = new WorkspaceContext(workspaceRoot);
        var llm = new LlamaClient(
            $"{_config.LlmSettings.BaseUrl}/v1/chat/completions",
            actualModel
        );
        llm.EndpointOverride = $"{_config.LlmSettings.BaseUrl}/v1/chat/completions";
        llm.Model = actualModel;

        var dispatcher = new ToolDispatcher(workspace, _braveApiKey);
        var stateDir = AgentStateDir.Ensure(workspaceRoot);

        var sessionManager = new SessionManager(
            llm,
            _config.AgentSettings.MaxContextTokens,
            _config.AgentSettings.RolloverTokens,
            () => $"Sei un agente AI in {workspaceRoot}."
        );

        var session = new AgentSession
        {
            SessionId = sessionId,
            Title = title,
            WorkspaceRoot = workspaceRoot,
            Model = actualModel,
            LlmClient = llm,
            Workspace = workspace,
            Dispatcher = dispatcher,
            SessionManager = sessionManager,
            UpdatedAt = DateTime.UtcNow
        };

        lock (_lock) _sessions[sessionId] = session;
        return session;
    }

    public AgentSession? GetSession(string sessionId)
    {
        lock (_lock) return _sessions.GetValueOrDefault(sessionId);
    }

    public List<SessionDto> ListSessions()
    {
        lock (_lock)
            return _sessions.Values.Select(s => new SessionDto
            {
                Id = s.SessionId, Title = s.Title,
                Workspace = s.WorkspaceRoot,
                UpdatedAt = s.UpdatedAt.ToString("o"),
                Status = s.Status, Model = s.Model
            }).ToList();
    }

    public void DeleteSession(string sessionId)
    {
        lock (_lock) _sessions.Remove(sessionId);
    }

    public bool TryStartTurn(string sessionId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s)) return false;
            if (s.TurnInProgress) return false;
            s.TurnInProgress = true;
            s.Status = "running";
            s.UpdatedAt = DateTime.UtcNow;
            return true;
        }
    }

    public void EndTurn(string sessionId, string reason)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var s))
            {
                s.TurnInProgress = false;
                s.Status = "idle";
                s.UpdatedAt = DateTime.UtcNow;
            }
        }
    }

    public void UpdateSessionStatus(string sessionId, string status)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var s))
            {
                s.Status = status;
                s.UpdatedAt = DateTime.UtcNow;
            }
        }
    }
}
