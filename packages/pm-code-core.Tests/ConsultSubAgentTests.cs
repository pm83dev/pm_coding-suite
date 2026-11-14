using System.Diagnostics;
using LocalCodeAgent.Core;
using LocalCodeAgent.Core.Tests.Helpers;
using LocalCodeAgent.Models;
using Xunit;

namespace LocalCodeAgent.Core.Tests;

/// <summary>
/// Verifica la robustezza di <see cref="ConsultSubAgent"/> (tool "ask_second_opinion") contro un
/// finto server HTTP (<see cref="FakeLlmServer"/>): risposta normale, risposta con solo
/// reasoning_content, content vuoto, ritardo oltre il timeout, errore HTTP 500, connessione
/// rifiutata. In NESSUN caso RunAsync deve bloccarsi indefinitamente o lanciare un'eccezione non
/// gestita verso il chiamante (AgentLoop).
/// </summary>
public class ConsultSubAgentTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly WorkspaceContext _workspace;
    private readonly ToolDispatcher _dispatcher;

    public ConsultSubAgentTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "pm-code-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceDir);
        _workspace = new WorkspaceContext(_workspaceDir);
        _dispatcher = new ToolDispatcher(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceDir, recursive: true); } catch { /* best-effort */ }
    }

    private static SecondOpinionSettings Settings(int timeoutSeconds = 30) => new()
    {
        TimeoutSeconds = timeoutSeconds,
        MaxStepsSubAgent = 4,
        MaxAnswerChars = 6000,
    };

    private static LlamaClient ClientFor(FakeLlmServer server) =>
        new(server.BaseUrl, "fake-model") { EndpointOverride = $"{server.BaseUrl}/v1/chat/completions" };

    [Fact]
    public async Task RispostaNormale_RestituisceContenutoConPrefisso()
    {
        await using var server = new FakeLlmServer([() => FakeResponse.Text("DIAGNOSIS: tutto ok. CONFIDENCE: high")]);
        var agent = new ConsultSubAgent(ClientFor(server), _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"problem":"GOAL: test\nQUESTION: funziona?"}""");

        Assert.StartsWith("[SECOND OPINION]", result);
        Assert.Contains("DIAGNOSIS: tutto ok", result);
    }

    [Fact]
    public async Task SoloReasoningContent_RestituisceErroreChiaroNonStringaVuota()
    {
        await using var server = new FakeLlmServer([() => FakeResponse.ReasoningOnly("sto pensando...")]);
        var agent = new ConsultSubAgent(ClientFor(server), _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"problem":"GOAL: test"}""");

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("reasoning_content", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ERRORE", result);
    }

    [Fact]
    public async Task ContentVuotoSenzaReasoning_UsaFallbackGenerico()
    {
        await using var server = new FakeLlmServer([() => FakeResponse.Empty()]);
        var agent = new ConsultSubAgent(ClientFor(server), _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"problem":"GOAL: test"}""");

        Assert.Equal("[SECOND OPINION] (nessuna risposta dal modello)", result);
    }

    [Fact]
    public async Task RitardoOltreTimeout_NonSiBloccaERestituisceFallback()
    {
        await using var server = new FakeLlmServer([
            () => FakeResponse.Delayed(TimeSpan.FromSeconds(5), FakeResponse.Text("troppo tardi"))
        ]);
        var agent = new ConsultSubAgent(ClientFor(server), _dispatcher, _workspace, Settings(timeoutSeconds: 1));

        var sw = Stopwatch.StartNew();
        var result = await agent.RunAsync("""{"problem":"GOAL: test"}""", ct: CancellationToken.None);
        sw.Stop();

        Assert.Equal("[SECOND OPINION] non disponibile (timeout). Procedi con la tua ipotesi migliore o chiedi all'utente.", result);
        // Deve fermarsi vicino al timeout configurato (1s), non aspettare il ritardo intero (5s) né bloccarsi.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Il turno è rimasto bloccato troppo a lungo: {sw.Elapsed}");
    }

    [Fact]
    public async Task ErroreHttp500_RestituisceMessaggioChiaroSenzaEccezione()
    {
        await using var server = new FakeLlmServer([() => FakeResponse.HttpError(500)]);
        var agent = new ConsultSubAgent(ClientFor(server), _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"problem":"GOAL: test"}""");

        Assert.StartsWith("[SECOND OPINION] non disponibile (errore:", result);
    }

    [Fact]
    public async Task ConnessioneRifiutata_RestituisceMessaggioChiaroSenzaEccezione()
    {
        // Nessun server avviato su questa porta: la connessione viene rifiutata dal sistema.
        var deadPort = new TcpListenerPortProbe().FindFreeButUnusedPort();
        var client = new LlamaClient($"http://127.0.0.1:{deadPort}", "fake-model")
            { EndpointOverride = $"http://127.0.0.1:{deadPort}/v1/chat/completions" };
        var agent = new ConsultSubAgent(client, _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"problem":"GOAL: test"}""");

        Assert.StartsWith("[SECOND OPINION] non disponibile (errore:", result);
    }

    [Fact]
    public async Task ProblemVuoto_NonChiamaIlServerERestituisceErrore()
    {
        await using var server = new FakeLlmServer([() => FakeResponse.Text("non dovrebbe arrivare qui")]);
        var agent = new ConsultSubAgent(ClientFor(server), _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"problem":""}""");

        Assert.Equal("ERRORE: 'problem' vuoto.", result);
        Assert.Equal(0, server.CallCount);
    }

    [Fact]
    public async Task CancellazioneUtenteReale_PropagaSenzaEssereInghiottita()
    {
        await using var server = new FakeLlmServer([
            () => FakeResponse.Delayed(TimeSpan.FromSeconds(5), FakeResponse.Text("non dovrebbe arrivare"))
        ]);
        var agent = new ConsultSubAgent(ClientFor(server), _dispatcher, _workspace, Settings(timeoutSeconds: 60));
        using var userCts = new CancellationTokenSource();
        userCts.CancelAfter(TimeSpan.FromMilliseconds(200));

        // Una cancellazione reale (non il timeout interno, qui molto più lungo) deve propagare come
        // OperationCanceledException, non tornare come una stringa di errore qualunque: altrimenti
        // il turno principale non la riconoscerebbe come "annullato dall'utente".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.RunAsync("""{"problem":"GOAL: test"}""", ct: userCts.Token));
    }
}

/// <summary>Trova una porta libera SENZA avviarci nulla sopra: una connessione successiva a quella porta viene rifiutata dal sistema (nessun listener), utile per simulare "server non raggiungibile".</summary>
public sealed class TcpListenerPortProbe
{
    public int FindFreeButUnusedPort()
    {
        var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }
}
