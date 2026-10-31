namespace LocalCodeAgent.Cli;

using LocalCodeAgent.Core;
using LocalCodeAgent.Models;

/// <summary>
/// Stato condiviso tra le modalità (REPL, serve, stdin-protocol) di pm-code.
/// Incapsula le variabili che i vari modi di esecuzione leggono/modificano.
/// </summary>
public class CliContext
{
    public WorkspaceContext Workspace { get; }
    public LlamaClient Llm { get; }
    public ToolDispatcher Dispatcher { get; }
    public SessionManager SessionManager { get; }
    public string ServerUrl { get; }
    public string Model { get; }
    public string CoreVersion { get; }
    public bool IsMachineMode { get; }
    public bool IsStdinProtocol { get; }

    private List<ChatMessage> _history = [];
    public List<ChatMessage> History
    {
        get => _history;
        set => _history = value;
    }

    private bool _autoApprove;
    public bool AutoApprove
    {
        get => _autoApprove;
        set => _autoApprove = value;
    }

    public string AutoApproveStateFile { get; }

    public IAgentSink Sink { get; set; } = new ConsoleSink(false);

    public CliContext(
        WorkspaceContext workspace,
        LlamaClient llm,
        ToolDispatcher dispatcher,
        SessionManager sessionManager,
        string serverUrl,
        string model,
        string coreVersion,
        bool isMachineMode,
        bool isStdinProtocol,
        string autoApproveStateFile,
        bool autoApprove)
    {
        Workspace = workspace;
        Llm = llm;
        Dispatcher = dispatcher;
        SessionManager = sessionManager;
        ServerUrl = serverUrl;
        Model = model;
        CoreVersion = coreVersion;
        IsMachineMode = isMachineMode;
        IsStdinProtocol = isStdinProtocol;
        AutoApproveStateFile = autoApproveStateFile;
        _autoApprove = autoApprove;
    }

    /// <summary>
    /// Resetta la history al system prompt e apre una nuova sessione nel trace strutturato.
    /// </summary>
    public void ResetHistory()
    {
        History = [ChatMessage.System(SystemPromptBuilder.Build(Workspace, Dispatcher))];
        StructuredTrace.StartSession(Workspace.Root, Llm.Model, Llm.EndpointOverride ?? ServerUrl, CoreVersion);
    }

    /// <summary>
    /// Cambia workspace: resetta piano, cache, cwd e history.
    /// </summary>
    public void ChangeWorkspace(string newPath)
    {
        Workspace.SetRoot(newPath);
        Dispatcher.ResetTodo();
        ResetHistory();
        Dispatcher.ClearCache();
        Dispatcher.ResetCwd();
    }

    public bool LoadAutoApproveState()
    {
        try
        {
            if (File.Exists(AutoApproveStateFile))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AutoApproveStateFile));
                if (doc.RootElement.TryGetProperty("autoApprove", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True)
                    return true;
            }
        }
        catch { }
        return false;
    }

    public void SaveAutoApproveState(bool value)
    {
        try
        {
            string existing = "{}";
            if (File.Exists(AutoApproveStateFile))
                existing = File.ReadAllText(AutoApproveStateFile);
            using var doc = System.Text.Json.JsonDocument.Parse(existing);
            var root = doc.RootElement;
            var props = new Dictionary<string, string>();
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                    props[prop.Name] = prop.Value.GetRawText();
            }
            props["autoApprove"] = value ? "true" : "false";
            var pairs = props.Select(p => System.Text.Json.JsonSerializer.Serialize(p.Key) + ":" + p.Value);
            File.WriteAllText(AutoApproveStateFile, "{" + string.Join(",", pairs) + "}");
        }
        catch { }
    }
}
