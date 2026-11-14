using System.Diagnostics;
using LocalCodeAgent.Core;
using LocalCodeAgent.Core.Tests.Helpers;
using LocalCodeAgent.Models;
using Xunit;

namespace LocalCodeAgent.Core.Tests;

/// <summary>
/// Verifica la robustezza di <see cref="ExploreSubAgent"/> (tool "explore") contro un finto
/// server HTTP (<see cref="FakeLlmServer"/>): timeout dedicato con annullamento reale della
/// richiesta HTTP, cancellazione reale del turno, errori di rete. In NESSUN caso RunAsync deve
/// bloccarsi indefinitamente o lanciare un'eccezione non gestita verso il chiamante (AgentLoop).
/// </summary>
public class ExploreSubAgentTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly WorkspaceContext _workspace;
    private readonly ToolDispatcher _dispatcher;

    public ExploreSubAgentTests()
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

    private static ExploreLlmSettings Settings(int timeoutSeconds = 30) => new() { TimeoutSeconds = timeoutSeconds };

    private static LlamaClient ClientFor(FakeLlmServer server) =>
        new(server.BaseUrl, "fake-model") { EndpointOverride = $"{server.BaseUrl}/v1/chat/completions" };

    private static ExploreSubAgent AgentFor(FakeLlmServer server, ToolDispatcher dispatcher, WorkspaceContext workspace, ExploreLlmSettings settings) =>
        new(ClientFor(server), dispatcher, workspace, settings);

    [Fact]
    public async Task RispostaNormale_RestituisceContenutoConPrefisso()
    {
        await using var server = new FakeLlmServer([() => FakeResponse.Text("conclusione: tutto ok")]);
        var agent = AgentFor(server, _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"question":"dove è definito X?"}""");

        Assert.StartsWith("[EXPLORE]", result);
        Assert.Contains("conclusione: tutto ok", result);
    }

    [Fact]
    public async Task RitardoOltreTimeout_NonSiBloccaERestituisceFallback()
    {
        await using var server = new FakeLlmServer([
            () => FakeResponse.Delayed(TimeSpan.FromSeconds(5), FakeResponse.Text("troppo tardi"))
        ]);
        var agent = AgentFor(server, _dispatcher, _workspace, Settings(timeoutSeconds: 1));

        var sw = Stopwatch.StartNew();
        var result = await agent.RunAsync("""{"question":"test"}""", ct: CancellationToken.None);
        sw.Stop();

        Assert.Equal("[EXPLORE] non disponibile (timeout). Usa direttamente search_in_files/read_file_range.", result);
        // Deve fermarsi vicino al timeout configurato (1s), non aspettare il ritardo intero (5s):
        // prova che il CancellationToken sia arrivato davvero alla richiesta HTTP.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Il turno è rimasto bloccato troppo a lungo: {sw.Elapsed}");
    }

    [Fact]
    public async Task ErroreHttp500_RestituisceMessaggioChiaroSenzaEccezione()
    {
        await using var server = new FakeLlmServer([() => FakeResponse.HttpError(500)]);
        var agent = AgentFor(server, _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"question":"test"}""");

        Assert.StartsWith("[EXPLORE] non disponibile (errore:", result);
    }

    [Fact]
    public async Task ConnessioneRifiutata_RestituisceMessaggioChiaroSenzaEccezione()
    {
        var deadPort = new TcpListenerPortProbe().FindFreeButUnusedPort();
        var client = new LlamaClient($"http://127.0.0.1:{deadPort}", "fake-model")
            { EndpointOverride = $"http://127.0.0.1:{deadPort}/v1/chat/completions" };
        var agent = new ExploreSubAgent(client, _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"question":"test"}""");

        Assert.StartsWith("[EXPLORE] non disponibile (errore:", result);
    }

    [Fact]
    public async Task QuestionVuota_NonChiamaIlServerERestituisceErrore()
    {
        await using var server = new FakeLlmServer([() => FakeResponse.Text("non dovrebbe arrivare qui")]);
        var agent = AgentFor(server, _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"question":""}""");

        Assert.Equal("ERRORE: 'question' vuota.", result);
        Assert.Equal(0, server.CallCount);
    }

    [Fact]
    public async Task ReadFileRangeOltreLimite_RestituisceSoloLePrimeNRigheConAvviso()
    {
        var path = Path.Combine(_workspaceDir, "big.txt");
        File.WriteAllLines(path, Enumerable.Range(1, 500).Select(i => $"line {i}"));

        await using var server = new FakeLlmServer([
            () => FakeResponse.ToolCall("read_file_range", """{"path":"big.txt","start_line":1,"end_line":500}"""),
            () => FakeResponse.Text("fatto"),
        ]);
        var settings = Settings();
        settings.MaxLinesPerReadCall = 10;
        var agent = AgentFor(server, _dispatcher, _workspace, settings);

        var result = await agent.RunAsync("""{"question":"leggi big.txt"}""");

        Assert.StartsWith("[EXPLORE]", result);
        // La 2ª richiesta al modello (LastRequestBody) include nella history il tool_result
        // della 1ª chiamata: verifica che la clamp sia stata applicata DAVVERO (solo le prime
        // 10 righe, con l'avviso) e non solo che il turno sia finito senza errori.
        Assert.Contains("line 10", server.LastRequestBody);
        Assert.DoesNotContain("line 11\\n", server.LastRequestBody);
        Assert.Contains("[LIMITE RIGHE]", server.LastRequestBody);
        Assert.Contains("Richieste 500 righe, restituite solo 10", server.LastRequestBody);
    }

    [Fact]
    public void ReadFileRangeOltreLimite_NonCambiaIlComportamentoDelLoopPrincipale()
    {
        // Lo stesso ToolDispatcher passato a explore, usato DIRETTAMENTE (come farebbe il loop
        // principale): read_file_range deve restare SENZA limite — la clamp vive solo dentro
        // ExploreSubAgent.Execute, non nel ToolDispatcher/FileSystemTools condiviso.
        var path = Path.Combine(_workspaceDir, "big.txt");
        File.WriteAllLines(path, Enumerable.Range(1, 500).Select(i => $"line {i}"));

        var result = _dispatcher.Execute("read_file_range", """{"path":"big.txt","start_line":1,"end_line":500}""");

        Assert.Contains("line 1", result);
        Assert.Contains("line 500", result);
        Assert.DoesNotContain("[LIMITE RIGHE]", result);
    }

    [Fact]
    public async Task ContestoOltreSoglia_FermaLEsplorazioneEAvvisa()
    {
        var path = Path.Combine(_workspaceDir, "big.txt");
        File.WriteAllLines(path, Enumerable.Range(1, 500).Select(i => $"line {i}"));

        // 1° step: una read_file_range (righe 1-300) fa crescere la history di alcune migliaia
        // di caratteri. La soglia (500 token stimati ~= 2000 caratteri) è più del prompt di
        // sistema+domanda iniziali (passa il 1° controllo, ~450 token stimati) ma meno della
        // history dopo il 1° risultato: il 2° controllo (prima del 2° step) deve fermare
        // l'esplorazione.
        await using var server = new FakeLlmServer([
            () => FakeResponse.ToolCall("read_file_range", """{"path":"big.txt","start_line":1,"end_line":300}"""),
            () => FakeResponse.Text("conclusione forzata dal limite di contesto"),
        ]);
        var settings = Settings();
        settings.MaxLinesPerReadCall = 10_000; // non deve interferire con la guardia di contesto
        settings.MaxHistoryTokensEstimate = 500;
        var agent = AgentFor(server, _dispatcher, _workspace, settings);

        var result = await agent.RunAsync("""{"question":"leggi big.txt"}""");

        Assert.StartsWith("[EXPLORE]", result);
        Assert.Contains("conclusione forzata dal limite di contesto", result);
        // Il modello non deve arrivare a fare tutti e 12 gli step: la guardia di contesto
        // interrompe l'esplorazione dopo il 1° tool result (2 chiamate totali: 1 tool call + 1
        // richiesta di conclusione forzata).
        Assert.Equal(2, server.CallCount);
        Assert.Contains("ha superato la soglia configurata", server.LastRequestBody);
    }

    [Fact]
    public async Task SamplingDedicato_InviatoSoloSeConfigurato()
    {
        await using var server = new FakeLlmServer([() => FakeResponse.Text("ok")]);
        var settings = Settings();
        settings.Temperature = 0.3f;
        settings.PresencePenalty = 0f;
        var agent = AgentFor(server, _dispatcher, _workspace, settings);

        await agent.RunAsync("""{"question":"test"}""");

        Assert.Contains("\"temperature\":0.3", server.LastRequestBody);
        Assert.Contains("\"presence_penalty\":0", server.LastRequestBody);
        // TopP/TopK/MinP/RepeatPenalty/FrequencyPenalty non impostati: assenti dalla richiesta
        // (nessun valore ereditato dal coordinatore — il server usa i propri default di avvio).
        Assert.DoesNotContain("top_p", server.LastRequestBody);
        Assert.DoesNotContain("top_k", server.LastRequestBody);
        Assert.DoesNotContain("min_p", server.LastRequestBody);
        Assert.DoesNotContain("repeat_penalty", server.LastRequestBody);
        Assert.DoesNotContain("frequency_penalty", server.LastRequestBody);
    }

    [Fact]
    public async Task SystemPromptPath_SovrascriveIlPromptDiDefault()
    {
        var promptFile = Path.Combine(_workspaceDir, "custom-prompt.txt");
        File.WriteAllText(promptFile, "SEI UN LETTORE PERSONALIZZATO DI TEST.");
        await using var server = new FakeLlmServer([() => FakeResponse.Text("ok")]);
        var settings = Settings();
        settings.SystemPromptPath = promptFile;
        var agent = AgentFor(server, _dispatcher, _workspace, settings);

        await agent.RunAsync("""{"question":"test"}""");

        Assert.Contains("SEI UN LETTORE PERSONALIZZATO DI TEST.", server.LastRequestBody);
    }

    [Fact]
    public async Task SoloReasoningContent_RestituisceErroreChiaroNonStringaVuota()
    {
        await using var server = new FakeLlmServer([() => FakeResponse.ReasoningOnly("sto pensando...")]);
        var agent = AgentFor(server, _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"question":"test"}""");

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("reasoning_content", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ERRORE", result);
    }

    [Fact]
    public async Task ContentVuotoSenzaReasoning_UsaFallbackGenerico()
    {
        await using var server = new FakeLlmServer([() => FakeResponse.Empty()]);
        var agent = AgentFor(server, _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"question":"test"}""");

        Assert.Contains("(nessuna risposta dal modello)", result);
    }

    [Fact]
    public async Task FinishReasonLength_ConContentNonVuoto_AggiungeAvvisoTroncamento()
    {
        await using var server = new FakeLlmServer([
            () => new FakeResponse { Content = "conclusione parziale", FinishReason = "length" }
        ]);
        var agent = AgentFor(server, _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"question":"test"}""");

        Assert.Contains("conclusione parziale", result);
        Assert.Contains("[EXPLORE TRONCATO: limite di token raggiunto]", result);
    }

    [Fact]
    public async Task FinishReasonLength_ConContentVuoto_RestituisceErroreChiaro()
    {
        await using var server = new FakeLlmServer([
            () => new FakeResponse { Content = "", FinishReason = "length" }
        ]);
        var agent = AgentFor(server, _dispatcher, _workspace, Settings());

        var result = await agent.RunAsync("""{"question":"test"}""");

        Assert.Contains("ERRORE", result);
        Assert.Contains("limite di token", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRONCATO", result);
    }

    [Fact]
    public async Task CancellazioneUtenteReale_PropagaSenzaEssereInghiottita()
    {
        await using var server = new FakeLlmServer([
            () => FakeResponse.Delayed(TimeSpan.FromSeconds(5), FakeResponse.Text("non dovrebbe arrivare"))
        ]);
        var agent = AgentFor(server, _dispatcher, _workspace, Settings(timeoutSeconds: 60));
        using var userCts = new CancellationTokenSource();
        userCts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.RunAsync("""{"question":"test"}""", ct: userCts.Token));
    }
}
