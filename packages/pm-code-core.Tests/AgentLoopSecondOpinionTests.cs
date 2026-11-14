using LocalCodeAgent.Core.Tests.Helpers;
using LocalCodeAgent.Models;
using Xunit;

namespace LocalCodeAgent.Core.Tests;

/// <summary>
/// Test di integrazione (vero <see cref="AgentLoop"/>, modello principale e modello di supporto
/// entrambi contro un <see cref="FakeLlmServer"/>) per i guard anti-loop legati ad
/// ask_second_opinion: contatore dedicato per turno e suggerimento iniettato una sola volta.
/// </summary>
public class AgentLoopSecondOpinionTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly WorkspaceContext _workspace;

    public AgentLoopSecondOpinionTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "pm-code-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceDir);
        _workspace = new WorkspaceContext(_workspaceDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceDir, recursive: true); } catch { /* best-effort */ }
    }

    private static string AskSecondOpinionArgs(int i) => $"{{\"problem\":\"call-{i}\"}}";

    [Fact]
    public async Task QuartaChiamataAskSecondOpinionNelloStessoTurno_VieneRifiutata()
    {
        // Modello principale (streaming): chiama ask_second_opinion 4 volte con argomenti sempre
        // diversi (evita il guard generico di ripetizione esatta, che altrimenti bloccherebbe già
        // alla 3ª chiamata per un motivo diverso), poi risponde con testo semplice.
        await using var mainServer = new FakeLlmServer([
            () => FakeResponse.ToolCall(ConsultSubAgent.ToolName, AskSecondOpinionArgs(0)),
            () => FakeResponse.ToolCall(ConsultSubAgent.ToolName, AskSecondOpinionArgs(1)),
            () => FakeResponse.ToolCall(ConsultSubAgent.ToolName, AskSecondOpinionArgs(2)),
            () => FakeResponse.ToolCall(ConsultSubAgent.ToolName, AskSecondOpinionArgs(3)),
            () => FakeResponse.Text("fatto."),
        ]);
        // Modello di supporto (non-streaming): risposta semplice, sempre uguale — chiamato solo
        // per le prime 3 richieste (la 4ª deve essere bloccata PRIMA di arrivare qui).
        await using var consultServer = new FakeLlmServer([() => FakeResponse.Text("DIAGNOSIS: ok. CONFIDENCE: low")]);

        var mainLlm = new LlamaClient(mainServer.BaseUrl, "fake-main") { EndpointOverride = $"{mainServer.BaseUrl}/v1/chat/completions" };
        var consultLlm = new LlamaClient(consultServer.BaseUrl, "fake-consult") { EndpointOverride = $"{consultServer.BaseUrl}/v1/chat/completions" };

        var dispatcher = new ToolDispatcher(_workspace);
        var sessionManager = new SessionManager(mainLlm, maxTokens: 128000, rolloverTokens: 64000, () => "test system prompt");
        var consultDispatcher = new ToolDispatcher(_workspace);
        var consultAgent = new ConsultSubAgent(consultLlm, consultDispatcher, _workspace, new SecondOpinionSettings
        {
            MaxStepsSubAgent = 2,
            TimeoutSeconds = 30,
        });

        var sink = new RecordingSink();
        var options = new AgentLoopOptions
        {
            IsMachineMode = true,
            MaxSteps = 10,
            MaxContextTokens = 128000,
            SecondOpinionMaxCallsPerTurn = 3, // la 4ª chiamata deve essere rifiutata
        };

        var loop = new AgentLoop(
            mainLlm, dispatcher, sessionManager, sink, _workspace,
            exploreAgent: null, reviewAgent: null, consultAgent,
            options,
            ui: _ => { }, trace: _ => { }, notice: _ => { }, onStatus: _ => { });

        var history = new List<ChatMessage> { ChatMessage.System("test"), ChatMessage.User("sei bloccato, chiedi un parere") };
        var success = await loop.RunTurnAsync(history, CancellationToken.None, maxTokens: 4096, turnId: "test-turn");

        Assert.True(success);
        // Il modello di supporto è stato chiamato solo 3 volte: la 4ª richiesta è stata bloccata
        // PRIMA di arrivare al sotto-agente (quindi prima di contattare il suo server).
        Assert.Equal(3, consultServer.CallCount);

        var secondOpinionResults = sink.ToolCallsFinished
            .Where(t => t.Name == ConsultSubAgent.ToolName)
            .Select(t => t.Result?.ToString() ?? "")
            .ToList();
        Assert.Equal(4, secondOpinionResults.Count); // 3 eseguite + 1 rifiutata = 4 tool_call_finished
        Assert.Equal(3, secondOpinionResults.Count(r => r.StartsWith("[SECOND OPINION]")));
        Assert.Single(secondOpinionResults, r => r.Contains("[LOOP-BLOCK]") && r.Contains("Hai già chiesto un secondo parere"));
    }

    [Fact]
    public async Task StessoErroreRipetuto_SuggerisceAskSecondOpinionUnaSolaVolta()
    {
        // run_command con un comando bloccato dalla blocklist di sicurezza (vedi TerminalTools):
        // errore DETERMINISTICO e indipendente dall'ambiente (niente processi esterni reali), con
        // lo stesso testo esatto ("ERRORE: comando bloccato per sicurezza: ...") a ogni chiamata.
        // Il campo extra "note" varia con una LETTERA (non un numero): evita sia il guard generico
        // di ripetizione ESATTA (stessi argomenti) sia quello di "ripetizione QUASI identica" di
        // run_command (che normalizza solo i numeri, vedi AgentLoop) — quest'ultimo, se scattasse,
        // aggiungerebbe un testo diverso a ogni chiamata e invaliderebbe la firma usata dal guard
        // "stesso errore" qui sotto test, che è un guard DIVERSO e indipendente da quello.
        const string blockedCommand = "rm -rf /";
        await using var mainServer = new FakeLlmServer([
            () => FakeResponse.ToolCall("run_command", $$"""{"command":"{{blockedCommand}}","note":"a"}"""),
            () => FakeResponse.ToolCall("run_command", $$"""{"command":"{{blockedCommand}}","note":"b"}"""),
            () => FakeResponse.ToolCall("run_command", $$"""{"command":"{{blockedCommand}}","note":"c"}"""),
            () => FakeResponse.ToolCall("run_command", $$"""{"command":"{{blockedCommand}}","note":"d"}"""),
            () => FakeResponse.Text("mi arrendo."),
        ]);
        var mainLlm = new LlamaClient(mainServer.BaseUrl, "fake-main") { EndpointOverride = $"{mainServer.BaseUrl}/v1/chat/completions" };
        var dispatcher = new ToolDispatcher(_workspace);
        var sessionManager = new SessionManager(mainLlm, maxTokens: 128000, rolloverTokens: 64000, () => "test system prompt");
        var consultAgent = new ConsultSubAgent(mainLlm, new ToolDispatcher(_workspace), _workspace, new SecondOpinionSettings());

        var sink = new RecordingSink();
        var options = new AgentLoopOptions
        {
            IsMachineMode = true,
            MaxSteps = 10,
            MaxContextTokens = 128000,
            SecondOpinionSameErrorThreshold = 3, // sospetta "stesso errore" dalla 3ª volta
        };
        var loop = new AgentLoop(
            mainLlm, dispatcher, sessionManager, sink, _workspace,
            exploreAgent: null, reviewAgent: null, consultAgent,
            options,
            ui: _ => { }, trace: _ => { }, notice: _ => { }, onStatus: _ => { });

        var history = new List<ChatMessage> { ChatMessage.System("test"), ChatMessage.User("esegui questo comando") };
        await loop.RunTurnAsync(history, CancellationToken.None, maxTokens: 4096, turnId: "test-turn-2");

        var runCommandResults = sink.ToolCallsFinished
            .Where(t => t.Name == "run_command")
            .Select(t => t.Result?.ToString() ?? "")
            .ToList();
        var withHint = runCommandResults.Count(r => r.Contains("Sei in loop: sintetizza i tentativi falliti e chiama ask_second_opinion"));
        Assert.Equal(1, withHint); // iniettato una sola volta, non a ogni step sopra soglia
        Assert.True(runCommandResults.Count(r => r.Contains("[STESSO ERRORE")) >= 2); // il guard base continua a scattare più volte
    }
}
