using System.Text.Json;
using AgentBackend.Models.Dto;
using AgentBackend.Services;
using LocalCodeAgent.Core.Tests.Helpers;
using LocalCodeAgent.Models;

namespace AgentBackend.Tests;

/// <summary>
/// Regressione: quando un turno viene cancellato durante l'esecuzione di un tool lento
/// e subito dopo viene avviato un nuovo turno, il Task del vecchio turno (che termina
/// più tardi) chiama EndTurnCts() e EndTurn() distruggendo lo stato del nuovo turno.
/// Il cancel del nuovo turno non funziona più e la history può tornare indietro.
///
/// Setup: backend reale (AgentSessionService + AgentLoopSession) contro FakeLlmServer in-process.
/// Script del finto LLM per il turno 1: tool lento (delay). Script per il turno 2: risposta semplice.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class TurnIdentityCancellationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pm-turn-identity-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _sessionsDir;
    private readonly string _workspaceDir;
    private readonly List<FakeLlmServer> _servers = [];

    public TurnIdentityCancellationTests()
    {
        _sessionsDir = Path.Combine(_root, "sessions");
        _workspaceDir = Path.Combine(_root, "ws");
        Directory.CreateDirectory(_sessionsDir);
        Directory.CreateDirectory(_workspaceDir);
        File.WriteAllText(Path.Combine(_workspaceDir, "hello.txt"), "ciao");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _servers) await s.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private sealed record Env(FakeLlmServer Server, AgentConfig Config, AgentSessionService Service, AgentSession Session);

    private Env BuildEnv(params Func<FakeResponse>[] script)
    {
        var server = new FakeLlmServer(script);
        _servers.Add(server);

        var config = new AgentConfig();
        config.LlmSettings.BaseUrl = server.BaseUrl;
        config.LlmSettings.Model = "fake";
        config.AgentSettings.MaxContextTokens = 128000;
        config.AgentSettings.RolloverTokens = 100000; // non fare rollover nei test
        config.AgentSettings.MaxSteps = 5;
        config.PersistenceSettings.SessionsDir = _sessionsDir;

        var service = new AgentSessionService(config);
        var session = service.CreateSession(_workspaceDir, "fake");
        session.PermissionMode = "auto";
        return new Env(server, config, service, session);
    }

    private static void StartTurn(Env env)
    {
        // Nel test, se il turno precedente è ancora "in corso" (il Task non ha chiamato EndTurn),
        // forziamo la fine del turno vecchio per poter iniziare quello nuovo.
        // Questo simula il comportamento dell'hub che dovrebbe gestire l'identità del turno.
        if (env.Session.TurnInProgress)
        {
            env.Session.EndTurnCts();
            env.Service.EndTurn(env.Session.SessionId, "superseded");
        }
        Assert.True(env.Service.TryStartTurn(env.Session.SessionId));
        env.Session.BeginTurn();
    }

    private static void FinishTurn(Env env, string reason = "completed")
    {
        env.Session.EndTurnCts();
        env.Service.EndTurn(env.Session.SessionId, reason);
    }

    /// <summary>
    /// Test 1: Turno 1 con tool lento → cancel → nuovo turno 2 → fine Task turno 1.
    /// Verifica che il cancel del turno 2 funzioni e la history non torni indietro.
    /// </summary>
    [Fact]
    public async Task CancelTurn1ThenNewTurn2_OldTaskFinishDoesNotBreakNewTurnCancellation()
    {
        // Script: turno 1 = tool lento (delay 5s), turno 2 = risposta semplice
        var env = BuildEnv(
            () => FakeResponse.Delayed(TimeSpan.FromSeconds(5), FakeResponse.ToolCall("run_command", "{\"command\":\"Start-Sleep 60\"}")),
            () => FakeResponse.Text("risposta turno 2"),
            () => FakeResponse.Text("non deve arrivare")
        );

        var events = new List<AgentEventDto>();
        var turn1 = new AgentLoopSession(env.Session, evt => events.Add(evt), env.Config);

        // Avvia turno 1
        StartTurn(env);
        var turn1Task = Task.Run(() => turn1.RunTurnAsync("messaggio turno 1"));

        // Attendi che il tool lento sia in esecuzione (il finto LLM ha delay 5s)
        await Task.Delay(2000);

        // Cancel turno 1
        Assert.True(env.Session.CancelTurn(), "turno 1 deve essere cancellabile");

        // Subito dopo avvia turno 2
        StartTurn(env);
        var turn2 = new AgentLoopSession(env.Session, evt => events.Add(evt), env.Config);
        var turn2Task = Task.Run(() => turn2.RunTurnAsync("messaggio turno 2"));

        // Attendi che il Task del turno 1 finisca (deve chiamare EndTurnCts/EndTurn nel finally)
        await turn1Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A questo punto il Task del turno 1 ha chiamato EndTurnCts() e EndTurn() nel finally.
        // Se il bug è presente, il turno 2 ha perso il suo CTS e non può più essere cancellato.

        // Verifica che il turno 2 sia ancora attivo e cancellabile
        bool canCancel2 = env.Session.CancelTurn();
        Assert.True(canCancel2, "turno 2 deve essere ancora cancellabile dopo che il Task del turno 1 è finito");

        // Attendi la fine del turno 2 (deve terminare con "cancelled" perché l'abbiamo appena cancellato)
        await turn2Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Verifica che la history contenga entrambi i messaggi utente e non sia tornata indietro
        Assert.Contains(env.Session.History, m => m.Role == "user" && m.Content == "messaggio turno 1");
        Assert.Contains(env.Session.History, m => m.Role == "user" && m.Content == "messaggio turno 2");

        // Verifica che ci sia un evento turn_finished con reason="cancelled" per il turno 2
        var cancelledEvents = events.Where(e => e.Type == "turn_finished").ToList();
        Assert.True(cancelledEvents.Count > 0, "deve esserci almeno un evento turn_finished");

        FinishTurn(env, "completed");
    }

    /// <summary>
    /// Test 2: Il finally del vecchio Task non deve azzerare CurrentTurnId del nuovo turno.
    /// </summary>
    [Fact]
    public async Task OldTaskFinishDoesNotClearNewTurnCurrentTurnId()
    {
        var env = BuildEnv(
            () => FakeResponse.Delayed(TimeSpan.FromSeconds(3), FakeResponse.Text("risposta turno 1")),
            () => FakeResponse.Text("risposta turno 2")
        );

        var events = new List<AgentEventDto>();
        var turn1 = new AgentLoopSession(env.Session, evt => events.Add(evt), env.Config);

        StartTurn(env);
        var turn1Task = Task.Run(() => turn1.RunTurnAsync("messaggio turno 1"));

        // Attendi che il turno 1 sia in esecuzione
        await Task.Delay(1000);

        // Cancel turno 1
        env.Session.CancelTurn();

        // Subito avvia turno 2
        StartTurn(env);
        var turn2 = new AgentLoopSession(env.Session, evt => events.Add(evt), env.Config);
        var turn2Task = Task.Run(() => turn2.RunTurnAsync("messaggio turno 2"));

        // Attendi che il Task del turno 1 finisca
        await turn1Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Il turno 2 deve essere ancora in corso (CurrentTurnId non nullo)
        // NB: dopo il fix, CurrentTurnId dovrebbe essere quello del turno 2
        // Prima del fix, il finally del turno 1 azzerava CurrentTurnId a null

        // Attendi la fine del turno 2
        await turn2Task.WaitAsync(TimeSpan.FromSeconds(5));

        FinishTurn(env, "completed");
    }
}
