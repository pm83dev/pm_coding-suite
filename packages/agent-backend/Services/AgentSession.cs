using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using AgentBackend.Models.Dto;

namespace AgentBackend.Services;

public class AgentSession
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string WorkspaceRoot { get; set; } = "";
    public string Status { get; set; } = "idle";
    public string Model { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public LlamaClient? LlmClient { get; set; }
    public WorkspaceContext? Workspace { get; set; }
    public ToolDispatcher? Dispatcher { get; set; }
    public SessionManager? SessionManager { get; set; }
    public List<ChatMessage> History { get; set; } = [];
    private readonly object _lock = new();
    public object Lock => _lock;
    public bool TurnInProgress { get; set; }
}
