using System.Text.Json;
using AgentBackend.Models.Dto;
using AgentBackend.Services;
using LocalCodeAgent.Core.Tests.Helpers;
using LocalCodeAgent.Models;

namespace AgentBackend.Tests;

/// <summary>
/// Regressione: dopo <c>SessionManager.MaybeRolloverAsync</c> il loop lavora su una lista NUOVA
/// (<c>AgentLoop.History</c>), mentre <c>AgentSession.History</c> restava la vecchia: <c>EndTurn</c>
/// salvava la vecchia e tutti i messaggi assistant/tool del turno andavano persi su disco
/// (sessione 30bfd22c del 15/11: solo i messaggi utente dopo il rollover).
///
/// Setup: backend reale (<see cref="AgentSessionService"/> + <see cref="AgentLoopSession"/>) contro
/// un <see cref="FakeLlmServer"/> in-process. RolloverTokens molto basso e storico seed grande
/// → il rollover scatta a inizio turno. Script del finto LLM: [0] riassunto del rollover,
/// [1] tool call, [2] risposta finale.
/// </summary>
[Collection("ConsoleCapture")] // un test sostituisce Console.Error: niente parallelismo con altri test che lo scrivono
public sealed class RolloverHistoryPersistenceTests : IAsyncDisposable
{
    private const string FinalAnswer = "risposta finale dopo il rollover";
    private const string UserRequest = "elenca la cartella di lavoro";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pm-rollover-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _sessionsDir;
    private readonly string _workspaceDir;
    private readonly List<FakeLlmServer> _servers = [];

    public RolloverHistoryPersistenceTests()
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

    private Env BuildEnv(params Func<FakeResponse>[] script) => BuildEnv(1000, script);

    private Env BuildEnv(int rolloverTokens, params Func<FakeResponse>[] script) => BuildEnv(rolloverTokens, 20, 280, script);

    private Env BuildEnv(int rolloverTokens, int seedPairs, int seedMsgChars, params Func<FakeResponse>[] script)
    {
        var server = new FakeLlmServer(script);
        _servers.Add(server);

        var config = new AgentConfig();
        config.LlmSettings.BaseUrl = server.BaseUrl;
        config.LlmSettings.Model = "fake";
        config.AgentSettings.MaxContextTokens = 128000;
        config.AgentSettings.RolloverTokens = rolloverTokens; // 1000 → soglia a inizio turno ≈ 600 token
        config.AgentSettings.MaxSteps = 10;
        config.PersistenceSettings.SessionsDir = _sessionsDir;

        var service = new AgentSessionService(config);
        var session = service.CreateSession(_workspaceDir, "fake");
        session.PermissionMode = "auto";
        session.History = SeedHistory(seedPairs, seedMsgChars);
        return new Env(server, config, service, session);
    }

    /// <summary>Storico "vecchio" oltre la soglia di rollover (≈ 12.000 caratteri ≈ 3.000 token).</summary>
    private static List<ChatMessage> SeedHistory(int pairs = 20, int msgChars = 280)
    {
        var h = new List<ChatMessage> { ChatMessage.System("system iniziale") };
        for (var i = 0; i < pairs; i++)
        {
            h.Add(ChatMessage.User($"domanda vecchia {i}: " + new string('x', msgChars)));
            h.Add(new ChatMessage { Role = "assistant", Content = $"risposta vecchia {i}: " + new string('y', msgChars) });
        }
        return h;
    }

    private static Func<FakeResponse>[] HappyScript() =>
    [
        () => FakeResponse.Text("RIASSUNTO FINTO DEL ROLLOVER"),
        () => FakeResponse.ToolCall("list_directory", "{\"path\":\".\"}"),
        () => FakeResponse.Text(FinalAnswer),
    ];

    private static List<(string Role, string Content)> ReadSavedHistory(string sessionsDir, string sessionId)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionsDir, sessionId + ".json")));
        return doc.RootElement.GetProperty("history").EnumerateArray()
            .Select(m => (
                m.GetProperty("role").GetString() ?? "",
                m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : ""))
            .ToList();
    }

    private static void StartTurn(Env env)
    {
        Assert.True(env.Service.TryStartTurn(env.Session.SessionId));
        env.Session.BeginTurn();
    }

    private static void FinishTurn(Env env, string reason = "completed")
    {
        env.Session.EndTurnCts();
        env.Service.EndTurn(env.Session.SessionId, reason);
    }

    // ── 1) Percorso normale ─────────────────────────────────────────────────────

    [Fact]
    public async Task DopoRollover_SessionHistoryEFileSalvatoContengonoRispostaETool()
    {
        var env = BuildEnv(HappyScript());
        var turn = new AgentLoopSession(env.Session, _ => { }, env.Config);

        StartTurn(env);
        await turn.RunTurnAsync(UserRequest);
        FinishTurn(env);

        Assert.True(env.Server.CallCount >= 3, $"attese ≥3 chiamate LLM (riassunto, tool, risposta), viste {env.Server.CallCount}");

        // In memoria
        Assert.Contains(env.Session.History, m => m.Role == "assistant" && m.Content == FinalAnswer);
        Assert.Contains(env.Session.History, m => m.Role == "tool");

        // Su disco (file scritto da EndTurn)
        var saved = ReadSavedHistory(_sessionsDir, env.Session.SessionId);
        Assert.Contains(saved, m => m.Role == "assistant" && m.Content == FinalAnswer);
        Assert.Contains(saved, m => m.Role == "tool");
        Assert.Contains(saved, m => m.Role == "user" && m.Content == UserRequest);
        Assert.Contains(saved, m => m.Role == "user" && m.Content.StartsWith("[SESSIONE PRECEDENTE"));
    }

    // ── 2) Cancel a metà turno, dopo il rollover ────────────────────────────────

    [Fact]
    public async Task CancelDopoRollover_ConservaIMessaggiGiaProdotti()
    {
        var env = BuildEnv(
            () => FakeResponse.Text("RIASSUNTO FINTO DEL ROLLOVER"),
            () => FakeResponse.ToolCall("list_directory", "{\"path\":\".\"}"),
            () => FakeResponse.Delayed(TimeSpan.FromSeconds(60), FakeResponse.Text("non deve arrivare")));
        var turn = new AgentLoopSession(env.Session, _ => { }, env.Config);

        StartTurn(env);
        var run = turn.RunTurnAsync(UserRequest);

        // Il turno è a metà: riassunto + tool call già serviti, la 3ª chiamata è in attesa.
        await WaitUntil(() => env.Server.CallCount >= 3);
        Assert.True(env.Session.CancelTurn());
        await run.WaitAsync(TimeSpan.FromSeconds(15));
        FinishTurn(env, "cancelled");

        var saved = ReadSavedHistory(_sessionsDir, env.Session.SessionId);
        Assert.Contains(saved, m => m.Role == "tool");
        Assert.Contains(saved, m => m.Role == "user" && m.Content == UserRequest);
        Assert.Contains(saved, m => m.Role == "user" && m.Content.StartsWith("[SESSIONE PRECEDENTE"));
        Assert.DoesNotContain(saved, m => m.Content == "non deve arrivare");
    }

    // ── 3) Eccezione a metà turno, dopo il rollover ─────────────────────────────

    [Fact]
    public async Task EccezioneDopoRollover_ConservaIMessaggiGiaProdotti()
    {
        var env = BuildEnv(
            () => FakeResponse.Text("RIASSUNTO FINTO DEL ROLLOVER"),
            () => FakeResponse.ToolCall("list_directory", "{\"path\":\".\"}"),
            () => FakeResponse.Text(FinalAnswer));

        // Il callback degli eventi lancia UNA volta quando il tool finisce (dopo il rollover):
        // simula un bug/eccezione non gestita a metà turno. Le eccezioni successive (nel catch
        // del loop che emette error/turn_finished) non devono ripetersi.
        var thrown = 0;
        var turn = new AgentLoopSession(env.Session, e =>
        {
            if (e.Type == "tool_call_finished" && Interlocked.Increment(ref thrown) == 1)
                throw new InvalidOperationException("eccezione simulata a metà turno");
        }, env.Config);

        StartTurn(env);
        try { await turn.RunTurnAsync(UserRequest); }
        catch (InvalidOperationException) { /* nel backend la prende il catch di WebSocketHub */ }
        FinishTurn(env, "error");

        Assert.Equal(1, thrown);
        var saved = ReadSavedHistory(_sessionsDir, env.Session.SessionId);
        Assert.Contains(saved, m => m.Role == "user" && m.Content == UserRequest);
        Assert.Contains(saved, m => m.Role == "user" && m.Content.StartsWith("[SESSIONE PRECEDENTE"));
        // L'eccezione parte sull'evento tool_call_finished, PRIMA che il loop aggiunga il messaggio
        // "tool" alla history: quello che deve esserci è il messaggio assistant con la tool call
        // (prodotto dopo il rollover), dopo la richiesta dell'utente.
        var requestIdx = saved.FindLastIndex(m => m.Role == "user" && m.Content == UserRequest);
        Assert.True(saved.FindLastIndex(m => m.Role == "assistant") > requestIdx,
            "manca il messaggio assistant del turno (tool call) dopo la richiesta utente");
    }

    // ── 4) Watchdog: EndTurn mentre il Task di turno è ancora vivo ──────────────

    [Fact]
    public async Task WatchdogDopoRollover_SalvaLoStatoCorrenteEnonQuelloVecchio()
    {
        var env = BuildEnv(
            () => FakeResponse.Text("RIASSUNTO FINTO DEL ROLLOVER"),
            () => FakeResponse.ToolCall("list_directory", "{\"path\":\".\"}"),
            () => FakeResponse.Delayed(TimeSpan.FromSeconds(60), FakeResponse.Text("non deve arrivare")));
        var turn = new AgentLoopSession(env.Session, _ => { }, env.Config);

        StartTurn(env);
        var run = turn.RunTurnAsync(UserRequest);
        await WaitUntil(() => env.Server.CallCount >= 3);

        // Stessa sequenza del watchdog in WebSocketHub: CancelTurn + EndTurn("watchdog"),
        // SENZA aspettare che il Task di turno termini.
        env.Session.CancelTurn();
        env.Service.EndTurn(env.Session.SessionId, "watchdog");

        var saved = ReadSavedHistory(_sessionsDir, env.Session.SessionId);
        Assert.Contains(saved, m => m.Role == "tool");
        Assert.Contains(saved, m => m.Role == "user" && m.Content.StartsWith("[SESSIONE PRECEDENTE"));

        await run.WaitAsync(TimeSpan.FromSeconds(15)); // il Task orfano si chiude comunque
        env.Session.EndTurnCts();
    }

    // ── 5) Archivio dello storico completo ──────────────────────────────────────

    [Fact]
    public async Task DopoRollover_LoStoricoCompletoPrecedenteEArchiviatoENonCaricatoComeSessione()
    {
        var env = BuildEnv(HappyScript());
        var turn = new AgentLoopSession(env.Session, _ => { }, env.Config);

        StartTurn(env);
        await turn.RunTurnAsync(UserRequest);
        FinishTurn(env);

        var archiveDir = Path.Combine(_sessionsDir, "archive", env.Session.SessionId);
        var files = Directory.GetFiles(archiveDir, "*-pre-rollover.json");
        Assert.Single(files);

        // L'archivio contiene la cronologia VECCHIA completa (tutti i 40 messaggi seed + system)
        // più la richiesta dell'utente, e NON il riassunto né la risposta del turno.
        using var doc = JsonDocument.Parse(File.ReadAllText(files[0]));
        var archived = doc.RootElement.GetProperty("history").EnumerateArray()
            .Select(m => m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "")
            .ToList();
        Assert.Contains(archived, c => c.StartsWith("domanda vecchia 0:"));
        Assert.Contains(archived, c => c.StartsWith("risposta vecchia 19:"));
        Assert.Contains(archived, c => c == UserRequest);
        Assert.DoesNotContain(archived, c => c == FinalAnswer);

        // La sottocartella archive non viene ricaricata come sessione, e la sessione ricaricata
        // da disco ha la history compressa con il lavoro del turno.
        var loaded = SessionPersistence.LoadAll(_sessionsDir);
        Assert.Single(loaded);
        Assert.Equal(env.Session.SessionId, loaded[0].SessionId);
        Assert.Contains(loaded[0].History, m => m.Role == "assistant" && m.Content == FinalAnswer);
        Assert.Contains(loaded[0].History, m => m.Role == "tool");
    }

    [Fact]
    public async Task SenzaRollover_NessunArchivioEHistoryInvariataMaConTurnoSalvato()
    {
        var env = BuildEnv(rolloverTokens: 100000,
            () => FakeResponse.ToolCall("list_directory", "{\"path\":\".\"}"),
            () => FakeResponse.Text(FinalAnswer));
        var before = env.Session.History;
        var turn = new AgentLoopSession(env.Session, _ => { }, env.Config);

        StartTurn(env);
        await turn.RunTurnAsync(UserRequest);
        FinishTurn(env);

        Assert.Same(before, env.Session.History);                       // nessuna sostituzione di lista
        Assert.False(Directory.Exists(Path.Combine(_sessionsDir, "archive")));
        var saved = ReadSavedHistory(_sessionsDir, env.Session.SessionId);
        Assert.Contains(saved, m => m.Role == "assistant" && m.Content == FinalAnswer);
        Assert.Contains(saved, m => m.Role == "tool");
        Assert.Contains(saved, m => m.Role == "user" && m.Content.StartsWith("domanda vecchia 0:")); // storico integro
    }

    // ── 6) Più turni di fila: il rollover non deve ripetersi a ogni turno ───────

    /// <summary>
    /// Riproduce l'evidenza dalla console del backend (15/11):
    /// <c>[sessione: ~42854 token stimati (soglia 36000) — rollover in corso...]</c> seguito da
    /// <c>[rollover a→b, 180 msg compressi]</c>, poi 181, 182, 183, 184 nei turni successivi.
    /// Causa: la sessione non adotta la lista del rollover, quindi ogni turno riparte dalla lista
    /// vecchia (che cresce solo del messaggio utente) e la ricomprime da capo, con un messaggio
    /// compresso in più a ogni turno. Con la correzione il rollover avviene UNA volta (turno 1) e
    /// i turni 2-4 ripartono dalla lista già compressa, sotto soglia.
    /// </summary>
    [Fact]
    public async Task PiuTurniDiFila_IlRolloverNonSiRipeteEIMessaggiCompressiNonCrescono()
    {
        // Rapporto caratteri/token realistico (~3, come in produzione: 42854 token per ~128K caratteri),
        // calcolato sul corpo della richiesta: senza questo il finto server riporta sempre
        // prompt_tokens=10, la ricalibrazione di SessionManager azzera la stima e il rollover non
        // si ripeterebbe nemmeno col bug.
        FakeLlmServer? server = null;
        Func<FakeResponse> reply = () => new FakeResponse
        {
            Content = "ok",
            PromptTokens = (int)(EstimateHistoryChars(server!.LastRequestBody) / 3.0),
        };
        // 25 coppie da 600 caratteri ≈ 30.000 caratteri: sopra la soglia di inizio turno (3000 token),
        // mentre la lista compressa (riassunto + ultimi ~20 messaggi) resta sotto.
        var env = BuildEnv(rolloverTokens: 5000, seedPairs: 25, seedMsgChars: 600, reply);
        server = env.Server;
        var seedCount = env.Session.History.Count;

        var stderr = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(TextWriter.Synchronized(stderr));
        var compressedPerTurn = new List<int?>();
        try
        {
            for (var turnNo = 1; turnNo <= 4; turnNo++)
            {
                var before = stderr.ToString().Length;
                var turn = new AgentLoopSession(env.Session, _ => { }, env.Config);
                StartTurn(env);
                await turn.RunTurnAsync($"richiesta numero {turnNo}");
                FinishTurn(env);

                var log = stderr.ToString()[before..];
                var m = System.Text.RegularExpressions.Regex.Match(log, @"(\d+) msg compressi");
                compressedPerTurn.Add(m.Success ? int.Parse(m.Groups[1].Value) : null);
            }
        }
        finally { Console.SetError(originalErr); }

        var summary = string.Join(", ", compressedPerTurn.Select((c, i) => $"turno {i + 1}: {(c?.ToString() ?? "nessun rollover")}"));

        // Il rollover scatta al turno 1 (storico seed oltre soglia)...
        Assert.True(compressedPerTurn[0] == seedCount, $"turno 1 doveva comprimere {seedCount} messaggi. Osservato → {summary}");
        // ...e NON si ripete nei turni 2-4 (col bug: {seedCount+1}, {seedCount+2}, {seedCount+3}).
        Assert.True(compressedPerTurn.Skip(1).All(c => c is null),
            $"rollover ripetuto a ogni turno con un messaggio compresso in più (sessione non aggiornata). Osservato → {summary}");

        // La history della sessione (e il file) contengono il lavoro di tutti e 4 i turni.
        var saved = ReadSavedHistory(_sessionsDir, env.Session.SessionId);
        for (var turnNo = 1; turnNo <= 4; turnNo++)
            Assert.Contains(saved, x => x.Role == "user" && x.Content == $"richiesta numero {turnNo}");
        Assert.Equal(4, saved.Count(x => x.Role == "assistant" && x.Content == "ok"));
    }

    // ── 7) Rollover A METÀ turno (step > 1) ─────────────────────────────────────

    /// <summary>
    /// Storico seed SOTTO la soglia di inizio turno (≈ 3.000 caratteri + system prompt del turno ≈ 7.000, soglia 3.000 token): al
    /// turno 1 non c'è rollover. Il primo tool (read_file su un file da ~20.000 caratteri) fa
    /// superare la soglia di metà turno (4.000 token) → rollover allo step 2, con il lavoro del
    /// turno (tool call + risultato) già in lista; poi un secondo tool e la risposta finale dopo il
    /// rollover. Script del finto LLM: [0] read_file, [1] riassunto del rollover, [2] list_directory,
    /// [3] risposta finale.
    /// </summary>
    [Fact]
    public async Task RolloverAMetaTurno_SessioneEFileContengonoRiassuntoToolERisposta_EArchivioLaListaPrecedente()
    {
        File.WriteAllText(Path.Combine(_workspaceDir, "big.txt"),
            string.Join("\n", Enumerable.Range(0, 200).Select(i => $"riga {i:000} " + new string('z', 90))));

        FakeLlmServer? server = null;
        int Tokens() => (int)(EstimateHistoryChars(server!.LastRequestBody) / 3.0); // rapporto realistico ≈ 3
        var env = BuildEnv(rolloverTokens: 4000, seedPairs: 4, seedMsgChars: 380,
            () => FakeResponse.ToolCall("read_file", "{\"path\":\"big.txt\"}") with { PromptTokens = Tokens() },
            () => FakeResponse.Text("RIASSUNTO FINTO DEL ROLLOVER A META TURNO"),
            () => FakeResponse.ToolCall("list_directory", "{\"path\":\".\"}") with { PromptTokens = Tokens() },
            () => new FakeResponse { Content = FinalAnswer, PromptTokens = Tokens() });
        server = env.Server;

        var stderr = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(TextWriter.Synchronized(stderr));
        try
        {
            var turn = new AgentLoopSession(env.Session, _ => { }, env.Config);
            StartTurn(env);
            await turn.RunTurnAsync(UserRequest);
            FinishTurn(env);
        }
        finally { Console.SetError(originalErr); }

        // Rollover avvenuto UNA volta e a metà turno (soglia 4000 = HardLimit, non 2400 = inizio turno).
        var log = stderr.ToString();
        Assert.True(System.Text.RegularExpressions.Regex.Matches(log, "rollover in corso").Count == 1,
            "attesi 1 rollover a metà turno. Chiamate LLM=" + env.Server.CallCount +
            ", caratteri history=" + env.Session.History.Sum(m => m.Content?.Length ?? 0) +
            ", lunghezze tool=" + string.Join("|", env.Session.History.Where(m => m.Role == "tool").Select(m => m.Content!.Length)) +
            ". Log: " + log);
        Assert.Contains("soglia 4000", log); // HardLimit (metà turno); a inizio turno sarebbe 2400
        Assert.Equal(4, env.Server.CallCount); // tool 1, riassunto del rollover, tool 2, risposta finale

        // History in memoria e file salvato: riassunto, tool, risposta finale, richiesta.
        Assert.Contains(env.Session.History, m => m.Role == "user" && m.Content!.StartsWith("[SESSIONE PRECEDENTE"));
        Assert.Contains(env.Session.History, m => m.Role == "tool" && m.Content!.Contains("riga 000"));
        Assert.Contains(env.Session.History, m => m.Role == "tool" && m.Content!.Contains("hello.txt")); // tool 2, dopo il rollover
        Assert.Contains(env.Session.History, m => m.Role == "assistant" && m.Content == FinalAnswer);

        var saved = ReadSavedHistory(_sessionsDir, env.Session.SessionId);
        Assert.Contains(saved, m => m.Role == "user" && m.Content.StartsWith("[SESSIONE PRECEDENTE"));
        Assert.Contains(saved, m => m.Role == "tool" && m.Content.Contains("riga 000"));
        Assert.Contains(saved, m => m.Role == "tool" && m.Content.Contains("hello.txt"));
        Assert.Contains(saved, m => m.Role == "assistant" && m.Content == FinalAnswer);
        Assert.Contains(saved, m => m.Content.Contains(UserRequest));

        // Archivio pre-rollover: la lista PRECEDENTE com'era a metà turno (seed + richiesta + tool),
        // senza il riassunto e senza la risposta finale.
        var archiveFiles = Directory.GetFiles(Path.Combine(_sessionsDir, "archive", env.Session.SessionId), "*-pre-rollover.json");
        Assert.Single(archiveFiles);
        using var doc = JsonDocument.Parse(File.ReadAllText(archiveFiles[0]));
        var archived = doc.RootElement.GetProperty("history").EnumerateArray()
            .Select(m => (
                Role: m.GetProperty("role").GetString() ?? "",
                Content: m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : ""))
            .ToList();
        Assert.Contains(archived, m => m.Content.StartsWith("domanda vecchia 0:"));
        Assert.Contains(archived, m => m.Content.StartsWith("risposta vecchia 3:"));
        Assert.Contains(archived, m => m.Role == "user" && m.Content == UserRequest);
        Assert.Contains(archived, m => m.Role == "tool" && m.Content.Contains("riga 000"));
        Assert.DoesNotContain(archived, m => m.Role == "tool" && m.Content.Contains("hello.txt")); // tool 2 è dopo il rollover
        Assert.DoesNotContain(archived, m => m.Content == FinalAnswer);
        Assert.DoesNotContain(archived, m => m.Content.StartsWith("[SESSIONE PRECEDENTE"));
    }

    /// <summary>Somma di content + tool_calls (nome e argomenti) dei messaggi della richiesta: stessa misura di SessionManager.TotalChars.</summary>
    private static int EstimateHistoryChars(string? requestBody)
    {
        if (string.IsNullOrEmpty(requestBody)) return 0;
        using var doc = JsonDocument.Parse(requestBody);
        if (!doc.RootElement.TryGetProperty("messages", out var msgs)) return 0;
        var total = 0;
        foreach (var m in msgs.EnumerateArray())
        {
            if (m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) total += c.GetString()!.Length;
            if (m.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                foreach (var tc in tcs.EnumerateArray())
                    if (tc.TryGetProperty("function", out var f))
                    {
                        if (f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) total += n.GetString()!.Length;
                        if (f.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) total += a.GetString()!.Length;
                    }
        }
        return total;
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condizione non raggiunta in tempo");
            await Task.Delay(25);
        }
    }
}
