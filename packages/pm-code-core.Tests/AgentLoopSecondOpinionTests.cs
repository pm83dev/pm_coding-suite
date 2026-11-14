using LocalCodeAgent.Core.Tests.Helpers;
using LocalCodeAgent.Models;
using LocalCodeAgent.Tools;
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

    [Fact]
    public async Task RipetizioneEsatta_SuggerisceAskSecondOpinionPrimaDiBloccareDavvero()
    {
        // Tool inesistente con argomenti identici: risultato deterministico identico a ogni
        // chiamata ("Tool '...' non registrato nel dispatcher."), SENZA passare per il guard di
        // "ripetizione quasi identica" (quello si applica solo a run_command/run_dotnet) — così
        // il guard di ripetizione ESATTA qui sotto test non viene disturbato da un altro guard che
        // farebbe variare il testo (e quindi l'hash) a ogni chiamata.
        const string toolName = "nonexistent_tool_for_test";
        await using var mainServer = new FakeLlmServer([
            () => FakeResponse.ToolCall(toolName, "{}"),
            () => FakeResponse.ToolCall(toolName, "{}"),
            () => FakeResponse.ToolCall(toolName, "{}"),
            () => FakeResponse.ToolCall(toolName, "{}"),
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
            MaxRepeats = 2,
        };
        var loop = new AgentLoop(
            mainLlm, dispatcher, sessionManager, sink, _workspace,
            exploreAgent: null, reviewAgent: null, consultAgent,
            options,
            ui: _ => { }, trace: _ => { }, notice: _ => { }, onStatus: _ => { });

        var history = new List<ChatMessage> { ChatMessage.System("test"), ChatMessage.User("esegui questo comando") };
        var success = await loop.RunTurnAsync(history, CancellationToken.None, maxTokens: 4096, turnId: "test-turn-3");

        Assert.True(success);
        var runCommandResults = sink.ToolCallsFinished
            .Where(t => t.Name == toolName)
            .Select(t => t.Result?.ToString() ?? "")
            .ToList();

        // 3 esecuzioni reali (count 1, 2, 3 — la 3ª porta l'avviso "LOOP IMMINENTE" ma viene
        // comunque eseguita) + 1 blocco vero (4ª, mai eseguita) = 4 eventi tool_call_finished.
        Assert.Equal(4, runCommandResults.Count);
        Assert.Equal(1, runCommandResults.Count(r => r.Contains("[LOOP IMMINENTE]") && r.Contains("ask_second_opinion")));
        Assert.Equal(1, runCommandResults.Count(r => r.StartsWith("[LOOP-BLOCK]")));
        // L'avviso precede il blocco vero (non sono la stessa chiamata).
        var hintIndex = runCommandResults.FindIndex(r => r.Contains("[LOOP IMMINENTE]"));
        var blockIndex = runCommandResults.FindIndex(r => r.StartsWith("[LOOP-BLOCK]"));
        Assert.True(hintIndex >= 0 && blockIndex >= 0 && hintIndex < blockIndex);
    }

    [Fact]
    public async Task ComandoInesistenteRipetuto_RiconosciutoComeErroreESuggerisceAskSecondOpinion()
    {
        // run_command con un comando che NON esiste: PowerShell non lancia un'eccezione
        // terminante né imposta sempre $LASTEXITCODE in modo ovvio per questo caso — prima della
        // correzione il wrapper PowerShell restituiva comunque [ESITO: SUCCESSO] e looksLikeError
        // non riconosceva il testo "is not recognized as" come errore, quindi il guard "stesso
        // errore" non scattava mai per questo caso reale. argsJson varia con una LETTERA (non un
        // numero, vedi StessoErroreRipetuto) per non interferire con il guard "quasi identica".
        const string badCommand = "thiscommanddoesnotexist987xyz";
        await using var mainServer = new FakeLlmServer([
            () => FakeResponse.ToolCall("run_command", $$"""{"command":"{{badCommand}}","note":"a"}"""),
            () => FakeResponse.ToolCall("run_command", $$"""{"command":"{{badCommand}}","note":"b"}"""),
            () => FakeResponse.ToolCall("run_command", $$"""{"command":"{{badCommand}}","note":"c"}"""),
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
            SecondOpinionSameErrorThreshold = 3,
        };
        var loop = new AgentLoop(
            mainLlm, dispatcher, sessionManager, sink, _workspace,
            exploreAgent: null, reviewAgent: null, consultAgent,
            options,
            ui: _ => { }, trace: _ => { }, notice: _ => { }, onStatus: _ => { });

        var history = new List<ChatMessage> { ChatMessage.System("test"), ChatMessage.User("esegui questo comando") };
        await loop.RunTurnAsync(history, CancellationToken.None, maxTokens: 4096, turnId: "test-turn-4");

        var runCommandResults = sink.ToolCallsFinished
            .Where(t => t.Name == "run_command")
            .Select(t => t.Result?.ToString() ?? "")
            .ToList();

        Assert.Equal(3, runCommandResults.Count);
        Assert.All(runCommandResults, r => Assert.Contains("[ESITO: FALLITO", r));
        Assert.Equal(1, runCommandResults.Count(r => r.Contains("Sei in loop: sintetizza i tentativi falliti e chiama ask_second_opinion")));
        Assert.True(runCommandResults.Count(r => r.Contains("[STESSO ERRORE")) >= 1);
    }

    [Fact]
    public async Task TreControlliFindstrSenzaMatch_NonScatenaSuggerimentoAskSecondOpinion()
    {
        // findstr su 3 file DIVERSI, nessuno con match: exit code 1 "ambiguo" (nessun output
        // reale) ogni volta — NON deve contare nei guard di errore ripetuto, quindi non deve mai
        // suggerire ask_second_opinion anche se è lo stesso tool fallito 3 volte di fila. I nomi
        // variano con una LETTERA (non un numero) per non far scattare anche il guard "quasi
        // identica" di run_command (che normalizza i numeri e appenderebbe un testo che contiene
        // comunque "ask_second_opinion", confondendo l'assert su quel guard DIVERSO).
        foreach (var letter in new[] { "a", "b", "c" })
            File.WriteAllText(Path.Combine(_workspaceDir, $"haystack{letter}.txt"), "hello world");

        await using var mainServer = new FakeLlmServer([
            () => FakeResponse.ToolCall("run_command", """{"command":"findstr /c:\"needle\" haystacka.txt"}"""),
            () => FakeResponse.ToolCall("run_command", """{"command":"findstr /c:\"needle\" haystackb.txt"}"""),
            () => FakeResponse.ToolCall("run_command", """{"command":"findstr /c:\"needle\" haystackc.txt"}"""),
            () => FakeResponse.Text("nessuna corrispondenza nei 3 file, come previsto."),
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
            SecondOpinionSameErrorThreshold = 3,
        };
        var loop = new AgentLoop(
            mainLlm, dispatcher, sessionManager, sink, _workspace,
            exploreAgent: null, reviewAgent: null, consultAgent,
            options,
            ui: _ => { }, trace: _ => { }, notice: _ => { }, onStatus: _ => { });

        var history = new List<ChatMessage> { ChatMessage.System("test"), ChatMessage.User("controlla se 'needle' è nei 3 file") };
        await loop.RunTurnAsync(history, CancellationToken.None, maxTokens: 4096, turnId: "test-turn-5");

        var runCommandResults = sink.ToolCallsFinished
            .Where(t => t.Name == "run_command")
            .Select(t => t.Result?.ToString() ?? "")
            .ToList();

        Assert.Equal(3, runCommandResults.Count);
        Assert.All(runCommandResults, r => Assert.Contains(TerminalTools.AmbiguousExitOneMarker, r));
        Assert.DoesNotContain(runCommandResults, r => r.Contains("[STESSO ERRORE"));
        Assert.DoesNotContain(runCommandResults, r => r.Contains("ask_second_opinion"));
    }

    [Fact]
    public async Task ExploreAgentNull_NonOffreIlToolExploreAlModello()
    {
        // Se exploreAgent è null (nessun caso reale oggi: entrambi i runtime lo costruiscono
        // sempre), il tool "explore" non deve nemmeno essere offerto al modello — altrimenti,
        // se il modello lo chiamasse comunque, AgentLoop farebbe _exploreAgent!.RunAsync su un
        // riferimento null.
        await using var mainServer = new FakeLlmServer([() => FakeResponse.Text("fatto.")]);
        var mainLlm = new LlamaClient(mainServer.BaseUrl, "fake-main") { EndpointOverride = $"{mainServer.BaseUrl}/v1/chat/completions" };
        var dispatcher = new ToolDispatcher(_workspace);
        var sessionManager = new SessionManager(mainLlm, maxTokens: 128000, rolloverTokens: 64000, () => "test system prompt");
        var consultAgent = new ConsultSubAgent(mainLlm, new ToolDispatcher(_workspace), _workspace, new SecondOpinionSettings());

        var sink = new RecordingSink();
        var options = new AgentLoopOptions { IsMachineMode = true, MaxSteps = 10, MaxContextTokens = 128000 };
        var loop = new AgentLoop(
            mainLlm, dispatcher, sessionManager, sink, _workspace,
            exploreAgent: null, reviewAgent: null, consultAgent,
            options,
            ui: _ => { }, trace: _ => { }, notice: _ => { }, onStatus: _ => { });

        var history = new List<ChatMessage> { ChatMessage.System("test"), ChatMessage.User("qualunque cosa") };
        var success = await loop.RunTurnAsync(history, CancellationToken.None, maxTokens: 4096, turnId: "test-turn-6");

        Assert.True(success);
        Assert.DoesNotContain("\"explore\"", mainServer.LastRequestBody);
    }
}
