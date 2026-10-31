namespace LocalCodeAgent.Cli;

using System.Text.Json;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Modalità --serve: server WebSocket che parla il protocollo v1 di ui/protocol.
/// Un solo processo = un solo workspace/history/piano alla volta.
/// </summary>
public static class ServeMode
{
    private static readonly HashSet<string> ServeBlockedTools = new(StringComparer.Ordinal)
    {
        "write_file", "edit_file", "delete_file", "move_file", "create_directory",
        "run_command", "run_dotnet", "sol_analyze", "stop_background_job",
        "git_add", "git_commit", "git_checkout",
    };

    public static async Task RunAsync(
        CliContext ctx,
        IConfiguration config,
        int servePort,
        Func<CancellationToken, Task<bool>> runTurn,
        Action<string> trace)
    {
        var token = Environment.GetEnvironmentVariable("PM_SERVE_TOKEN");
        if (string.IsNullOrEmpty(token)) token = config["ServeSettings:Token"];
        if (string.IsNullOrEmpty(token))
        {
            UI.Error("PM_SERVE_TOKEN non impostato e 'ServeSettings:Token' assente/vuoto in appsettings.json: " +
                     "--serve richiede un token (altrimenti chiunque raggiunga 127.0.0.1 potrebbe pilotare l'agente). " +
                     "Imposta la variabile d'ambiente PM_SERVE_TOKEN, oppure 'ServeSettings:Token' in appsettings.json.");
            return;
        }

        var allowedRoots = config.GetSection("AgentSettings:AllowedWorkspaceRoots").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(p => Path.GetFullPath(p!).TrimEnd(Path.DirectorySeparatorChar))
            .ToList();
        if (allowedRoots.Count == 0)
            UI.Dim("[serve] AgentSettings:AllowedWorkspaceRoots è vuoto in appsettings.json: new_session rifiuterà qualunque workspace finché non lo configuri.");

        string? currentSessionId = null;
        var turnInProgress = false;
        var turnLock = new object();
        var sessions = new Dictionary<string, (string title, string workspace, string updatedAt, string model)>();

        var state = new ServeState(ref currentSessionId, ref turnInProgress, turnLock, sessions);

        var serveBindAddress = config["ServeSettings:BindAddress"];
        if (string.IsNullOrWhiteSpace(serveBindAddress)) serveBindAddress = "127.0.0.1";

        var server = new ServeServer(servePort, serveBindAddress);
        UI.Ok($"[serve] in ascolto su ws://{serveBindAddress}:{servePort}/ (token richiesto in 'hello')");

        try
        {
            await server.RunAsync(async (conn, connCt) =>
            {
                await HandleConnectionAsync(conn, connCt, token, ctx, runTurn, trace,
                    allowedRoots, state);
            }, CancellationToken.None);
        }
        catch (System.Net.HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            UI.Error($"Impossibile ascoltare su '{server.Prefix}': accesso negato da Windows " +
                     "(normale per un indirizzo diverso da 127.0.0.1/localhost senza i permessi giusti).");
            UI.Dim("  Da un prompt come Amministratore, UNA volta:");
            UI.Dim($"    netsh http add urlacl url={server.Prefix} user={Environment.UserDomainName}\\{Environment.UserName}");
            UI.Dim("  (per rimuoverlo in futuro: netsh http delete urlacl url={server.Prefix})");
        }
    }

    private sealed class ServeState
    {
        public string? CurrentSessionId { get; set; }
        public bool TurnInProgress { get; set; }
        public object TurnLock { get; }
        public Dictionary<string, (string title, string workspace, string updatedAt, string model)> Sessions { get; }

        public ServeState(ref string? currentSessionId, ref bool turnInProgress, object turnLock,
            Dictionary<string, (string title, string workspace, string updatedAt, string model)> sessions)
        {
            CurrentSessionId = currentSessionId;
            TurnInProgress = turnInProgress;
            TurnLock = turnLock;
            Sessions = sessions;
        }
    }

    private static async Task HandleConnectionAsync(
        ServeConnection conn,
        CancellationToken connCt,
        string token,
        CliContext ctx,
        Func<CancellationToken, Task<bool>> runTurn,
        Action<string> trace,
        List<string> allowedRoots,
        ServeState state)
    {
        var first = await conn.ReceiveAsync(connCt);
        if (first == null) { await conn.CloseAsync(); return; }

        var (helloEnv, helloParseErr) = ServeProtocol.ParseEnvelope(first);
        if (helloEnv == null)
        {
            conn.Send(ServeProtocol.Envelope("error", new { code = "bad_request", message = helloParseErr }, null));
            await conn.CloseAsync();
            return;
        }
        if (helloEnv.Type != "hello")
        {
            conn.Send(ServeProtocol.Envelope("error", new { code = "unauthorized", message = "il primo messaggio della connessione deve essere 'hello'" }, null));
            await conn.CloseAsync();
            return;
        }
        var helloToken = helloEnv.Payload.TryGetProperty("token", out var tokEl) ? tokEl.GetString() : null;
        if (string.IsNullOrEmpty(helloToken) || helloToken != token)
        {
            conn.Send(ServeProtocol.Envelope("error", new { code = "unauthorized", message = "token non valido" }, null));
            await conn.CloseAsync();
            return;
        }
        conn.Send(ServeProtocol.Envelope("hello_ok", new { serverVersion = ctx.CoreVersion, protocol = ServeProtocol.Version }, null));

        while (true)
        {
            var raw = await conn.ReceiveAsync(connCt);
            if (raw == null) break;

            var (msg, parseErr) = ServeProtocol.ParseEnvelope(raw);
            if (msg == null)
            {
                conn.Send(ServeProtocol.Envelope("error", new { code = "bad_request", message = parseErr }, state.CurrentSessionId));
                continue;
            }

            switch (msg.Type)
            {
                case "new_session":
                    await HandleNewSessionAsync(conn, msg, ctx, allowedRoots, state, trace);
                    break;
                case "send_message":
                    await HandleSendMessageAsync(conn, msg, ctx, runTurn, state);
                    break;
                case "hello":
                    conn.Send(ServeProtocol.Envelope("error", new { code = "bad_request", message = "'hello' già ricevuto su questa connessione" }, state.CurrentSessionId));
                    break;
                case "list_sessions":
                    conn.Send(ServeProtocol.Envelope("sessions", new
                    {
                        sessions = state.Sessions.Select(kv => new
                        {
                            id = kv.Key,
                            title = kv.Value.title,
                            workspace = kv.Value.workspace,
                            model = kv.Value.model,
                            updatedAt = kv.Value.updatedAt,
                            status = "idle"
                        }).ToArray()
                    }, null));
                    break;
                case "open_session":
                {
                    var sessionId = msg.Payload.TryGetProperty("sessionId", out var sidEl) ? sidEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(sessionId) || !state.Sessions.TryGetValue(sessionId, out var sessInfo))
                    {
                        conn.Send(ServeProtocol.Envelope("error", new { code = "not_found", message = $"sessione '{sessionId}' non trovata" }, sessionId));
                        break;
                    }
                    state.CurrentSessionId = sessionId;
                    conn.Send(ServeProtocol.Envelope("session_state", new
                    {
                        session = new { id = sessionId, title = sessInfo.title, workspace = sessInfo.workspace, updatedAt = sessInfo.updatedAt, status = "idle" },
                        messages = Array.Empty<object>(),
                        model = sessInfo.model,
                        permission = "readonly"
                    }, sessionId));
                    break;
                }
                default:
                    conn.Send(ServeProtocol.Envelope("error", new { code = "bad_request", message = $"comando '{msg.Type}' non ancora supportato in questa fase" }, state.CurrentSessionId));
                    break;
            }
        }

        await conn.CloseAsync();
    }

    private static async Task HandleNewSessionAsync(
        ServeConnection conn,
        ServeProtocol.ClientEnvelope msg,
        CliContext ctx,
        List<string> allowedRoots,
        ServeState state,
        Action<string> trace)
    {
        var requested = msg.Payload.TryGetProperty("workspace", out var wsEl) ? wsEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(requested))
        {
            conn.Send(ServeProtocol.Envelope("error", new { code = "bad_request", message = "new_session.payload.workspace mancante o vuoto" }, state.CurrentSessionId));
            return;
        }
        trace($"[new_session] workspace richiesto: '{requested}' (len={requested.Length})");

        var resolved = requested == "." ? Directory.GetCurrentDirectory() : requested;
        var fullWs = Path.GetFullPath(resolved);
        var normalized = fullWs.TrimEnd(Path.DirectorySeparatorChar);
        if (!allowedRoots.Any(root =>
            string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            conn.Send(ServeProtocol.Envelope("error",
                new { code = "bad_request", message = $"workspace non consentito: '{requested}' non è sotto nessuna delle AgentSettings:AllowedWorkspaceRoots configurate" },
                state.CurrentSessionId));
            return;
        }
        trace($"[new_session] workspace '{fullWs}' consentito");

        ctx.ChangeWorkspace(fullWs);
        state.CurrentSessionId = ServeProtocol.MakeId();
        state.Sessions[state.CurrentSessionId] = (
            title: Path.GetFileName(fullWs.TrimEnd(Path.DirectorySeparatorChar)),
            workspace: fullWs,
            updatedAt: ServeProtocol.NowIso(),
            model: ctx.Llm.Model
        );
        conn.Send(ServeProtocol.Envelope("session_state", new
        {
            session = new
            {
                id = state.CurrentSessionId,
                title = Path.GetFileName(fullWs.TrimEnd(Path.DirectorySeparatorChar)),
                workspace = fullWs,
                updatedAt = ServeProtocol.NowIso(),
                status = "idle"
            },
            messages = Array.Empty<object>(),
            model = ctx.Llm.Model,
            permission = "readonly"
        }, state.CurrentSessionId));
    }

    private static async Task HandleSendMessageAsync(
        ServeConnection conn,
        ServeProtocol.ClientEnvelope msg,
        CliContext ctx,
        Func<CancellationToken, Task<bool>> runTurn,
        ServeState state)
    {
        if (state.CurrentSessionId == null)
        {
            conn.Send(ServeProtocol.Envelope("error", new { code = "unknown_session", message = "nessuna sessione attiva: manda prima new_session" }, state.CurrentSessionId));
            return;
        }
        var text = msg.Payload.TryGetProperty("text", out var txtEl) ? txtEl.GetString() ?? "" : "";

        bool accepted;
        lock (state.TurnLock)
        {
            accepted = !state.TurnInProgress;
            if (accepted) state.TurnInProgress = true;
        }
        if (!accepted)
        {
            conn.Send(ServeProtocol.Envelope("error", new { code = "bad_request", message = "busy" }, state.CurrentSessionId));
            return;
        }

        var sess = state.CurrentSessionId;
        _ = Task.Run(async () =>
        {
            try
            {
                ctx.Sink = new ServeSink(conn.Send, sess);
                ctx.History.Add(ChatMessage.User(text));
                await runTurn(CancellationToken.None);
            }
            catch (Exception ex)
            {
                conn.Send(ServeProtocol.Envelope("error", new { code = "bad_request", message = $"errore interno del turno: {ex.Message}" }, sess));
            }
            finally
            {
                lock (state.TurnLock) { state.TurnInProgress = false; }
            }
        });
    }
}
