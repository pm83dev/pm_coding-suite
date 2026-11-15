using LocalCodeAgent.Core.Tests.Helpers;
using LocalCodeAgent.Models;
using LocalCodeAgent.Tools;
using Xunit;

namespace LocalCodeAgent.Core.Tests;

/// <summary>
/// Prima di fermarsi per "stesso comando rilanciato cambiando solo dei numeri", il loop deve concedere un
/// passo in cui l'agente chiede aiuto (ask_revision o ask_second_opinion) invece di arrendersi subito.
/// </summary>
public class AgentLoopEscalationTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly WorkspaceContext _workspace;

    public AgentLoopEscalationTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "pm-code-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceDir);
        _workspace = new WorkspaceContext(_workspaceDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceDir, recursive: true); } catch { /* best-effort */ }
    }

    // Comando bloccato dalla blocklist di sicurezza: nessun processo reale, risultato deterministico.
    private static string BlockedCommand(int n) => $$"""{"command":"rm -rf /","note":"tentativo {{n}}"}""";

    [Fact]
    public async Task ComandoRilanciatoSoloCambiandoNumeri_ChiedeAiutoPrimaDiFermarsi()
    {
        await using var mainServer = new FakeLlmServer([
            () => FakeResponse.ToolCall("run_command", BlockedCommand(1)),
            () => FakeResponse.ToolCall("run_command", BlockedCommand(2)),
            () => FakeResponse.ToolCall("run_command", BlockedCommand(3)),
            () => FakeResponse.ToolCall("run_command", BlockedCommand(4)),
            () => FakeResponse.ToolCall("run_command", BlockedCommand(5)),
            () => FakeResponse.ToolCall("run_command", BlockedCommand(6)),   // la 6ª scatta l'escalation
            () => FakeResponse.ToolCall(ConsultSubAgent.ToolName, """{"problem":"comando bloccato, non so come procedere"}"""),
            () => FakeResponse.Text("ho seguito il parere, provo un approccio diverso."),
        ]);
        await using var consultServer = new FakeLlmServer([() => FakeResponse.Text("DIAGNOSIS: usa un altro comando. CONFIDENCE: high")]);

        var mainLlm = new LlamaClient(mainServer.BaseUrl, "fake-main") { EndpointOverride = $"{mainServer.BaseUrl}/v1/chat/completions" };
        var consultLlm = new LlamaClient(consultServer.BaseUrl, "fake-consult") { EndpointOverride = $"{consultServer.BaseUrl}/v1/chat/completions" };
        var dispatcher = new ToolDispatcher(_workspace);
        var sessionManager = new SessionManager(mainLlm, maxTokens: 128000, rolloverTokens: 64000, () => "test system prompt");
        var consultAgent = new ConsultSubAgent(consultLlm, new ToolDispatcher(_workspace), _workspace,
            new SecondOpinionSettings { MaxStepsSubAgent = 2, TimeoutSeconds = 30 });

        var sink = new RecordingSink();
        var options = new AgentLoopOptions { IsMachineMode = true, MaxSteps = 20, MaxContextTokens = 128000 };
        var loop = new AgentLoop(
            mainLlm, dispatcher, sessionManager, sink, _workspace,
            exploreAgent: null, reviewAgent: null, consultAgent,
            options,
            ui: _ => { }, trace: _ => { }, notice: _ => { }, onStatus: _ => { });

        var history = new List<ChatMessage> { ChatMessage.System("test"), ChatMessage.User("esegui il comando") };
        var success = await loop.RunTurnAsync(history, CancellationToken.None, maxTokens: 4096, turnId: "test-turn-escalation");

        Assert.True(success);

        var runCommandResults = sink.ToolCallsFinished
            .Where(t => t.Name == "run_command")
            .Select(t => t.Result?.ToString() ?? "")
            .ToList();
        // La 6ª chiamata NON ferma il turno: restituisce l'ordine di chiedere aiuto.
        Assert.Single(runCommandResults, r => r.Contains("[LOOP-BLOCK]") && r.Contains("Chiama ORA il tool ask_second_opinion"));

        // L'agente ha davvero chiesto il parere e il turno è proseguito fino alla risposta finale.
        Assert.Equal(1, consultServer.CallCount);
        Assert.Contains(sink.ToolCallsFinished, t => t.Name == ConsultSubAgent.ToolName);
        Assert.Contains(history, m => m.Role == "assistant" && (m.Content ?? "").Contains("approccio diverso"));
        Assert.DoesNotContain(history, m => (m.Content ?? "").Contains("Mi fermo: ho rilanciato"));
    }

    [Fact]
    public async Task SeIgnoraLEscalation_AlTentativoSuccessivoSiFerma()
    {
        // Dopo l'ordine di chiedere aiuto il modello rilancia comunque il comando: a quel punto si ferma davvero.
        await using var mainServer = new FakeLlmServer([
            () => FakeResponse.ToolCall("run_command", BlockedCommand(1)),
            () => FakeResponse.ToolCall("run_command", BlockedCommand(2)),
            () => FakeResponse.ToolCall("run_command", BlockedCommand(3)),
            () => FakeResponse.ToolCall("run_command", BlockedCommand(4)),
            () => FakeResponse.ToolCall("run_command", BlockedCommand(5)),
            () => FakeResponse.ToolCall("run_command", BlockedCommand(6)),   // escalation
            () => FakeResponse.ToolCall("run_command", BlockedCommand(7)),   // ignorata → stop
            () => FakeResponse.Text("non dovrebbe arrivare qui."),
        ]);
        var mainLlm = new LlamaClient(mainServer.BaseUrl, "fake-main") { EndpointOverride = $"{mainServer.BaseUrl}/v1/chat/completions" };
        var dispatcher = new ToolDispatcher(_workspace);
        var sessionManager = new SessionManager(mainLlm, maxTokens: 128000, rolloverTokens: 64000, () => "test system prompt");
        var consultAgent = new ConsultSubAgent(mainLlm, new ToolDispatcher(_workspace), _workspace, new SecondOpinionSettings());

        var sink = new RecordingSink();
        var options = new AgentLoopOptions { IsMachineMode = true, MaxSteps = 20, MaxContextTokens = 128000 };
        var loop = new AgentLoop(
            mainLlm, dispatcher, sessionManager, sink, _workspace,
            exploreAgent: null, reviewAgent: null, consultAgent,
            options,
            ui: _ => { }, trace: _ => { }, notice: _ => { }, onStatus: _ => { });

        var history = new List<ChatMessage> { ChatMessage.System("test"), ChatMessage.User("esegui il comando") };
        await loop.RunTurnAsync(history, CancellationToken.None, maxTokens: 4096, turnId: "test-turn-escalation-2");

        var runCommandResults = sink.ToolCallsFinished
            .Where(t => t.Name == "run_command")
            .Select(t => t.Result?.ToString() ?? "")
            .ToList();
        Assert.Contains(runCommandResults, r => r.Contains("Chiama ORA il tool"));
        Assert.Contains(runCommandResults, r => r.Contains("[LOOP-BLOCK]") && r.Contains("Interrotto."));
    }
}
