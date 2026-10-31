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
    private readonly string _sessionsDir;

    public AgentSessionService(AgentConfig config, string? braveApiKey = null)
    {
        _config = config;
        _braveApiKey = braveApiKey;
        _sessionsDir = SessionPersistence.ResolveDir(config.PersistenceSettings?.SessionsDir);
        LoadPersistedSessions();
    }

    /// <summary>
    /// Costruisce gli oggetti di runtime (LlmClient, Workspace, Dispatcher, SessionManager)
    /// a partire da workspace + model. Condiviso tra CreateSession e il load da disco.
    /// </summary>
    private (LlamaClient llm, WorkspaceContext workspace, ToolDispatcher dispatcher, SessionManager sessionManager)
        BuildRuntime(string workspaceRoot, string model)
    {
        var workspace = new WorkspaceContext(workspaceRoot);
        var llm = new LlamaClient(
            $"{_config.LlmSettings.BaseUrl}/v1/chat/completions",
            model
        );
        llm.EndpointOverride = $"{_config.LlmSettings.BaseUrl}/v1/chat/completions";
        llm.Model = model;

        var dispatcher = new ToolDispatcher(workspace, _braveApiKey);
        AgentStateDir.Ensure(workspaceRoot);

        var sessionManager = new SessionManager(
            llm,
            _config.AgentSettings.MaxContextTokens,
            _config.AgentSettings.RolloverTokens,
            () => $"Sei un agente AI in {workspaceRoot}."
        );
        return (llm, workspace, dispatcher, sessionManager);
    }

    /// <summary>
    /// Al via del backend ricrea in memoria le sessioni persistite su disco. Gli oggetti
    /// di runtime vengono ricostruiti; un turno in corso al momento del crash è morto,
    /// quindi si forza sempre Status="idle" e TurnInProgress=false.
    /// </summary>
    private void LoadPersistedSessions()
    {
        var loaded = SessionPersistence.LoadAll(_sessionsDir);
        lock (_lock)
        {
            foreach (var dto in loaded)
            {
                if (_sessions.ContainsKey(dto.SessionId)) continue; // già presente
                try
                {
                    var (llm, workspace, dispatcher, sessionManager) =
                        BuildRuntime(dto.WorkspaceRoot, dto.Model);
                    var session = new AgentSession
                    {
                        SessionId = dto.SessionId,
                        Title = dto.Title,
                        WorkspaceRoot = dto.WorkspaceRoot,
                        Model = dto.Model,
                        PermissionMode = string.IsNullOrEmpty(dto.PermissionMode) ? "ask" : dto.PermissionMode,
                        LlmClient = llm,
                        Workspace = workspace,
                        Dispatcher = dispatcher,
                        SessionManager = sessionManager,
                        History = dto.History ?? [],
                        UpdatedAt = dto.UpdatedAt,
                        // Un turno in corso al riavvio è morto: ripartiamo sempre idle.
                        Status = "idle",
                        TurnInProgress = false,
                    };
                    _sessions[dto.SessionId] = session;
                }
                catch
                {
                    // Sessione non ricostruibile (es. workspace inesistente): la salto.
                    // Il file resta su disco; verrà riprovato al prossimo avvio.
                }
            }
        }
    }

    public AgentSession CreateSession(string workspaceRoot, string? model = null)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var title = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
        var actualModel = model ?? _config.LlmSettings.Model;

        var (llm, workspace, dispatcher, sessionManager) = BuildRuntime(workspaceRoot, actualModel);

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
        SessionPersistence.Save(_sessionsDir, session);
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
        SessionPersistence.Delete(_sessionsDir, sessionId);
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
        AgentSession? session = null;
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var s))
            {
                s.TurnInProgress = false;
                s.Status = "idle";
                s.UpdatedAt = DateTime.UtcNow;
                session = s;
            }
        }
        // Salvo lo storico aggiornato a fine turno: è il momento in cui la history
        // è completa e stabile (il loop ha già aggiunto tutti i messaggi).
        if (session != null) SessionPersistence.Save(_sessionsDir, session);
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
