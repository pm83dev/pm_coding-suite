using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace LocalCodeAgent.Core;

/// <summary>
/// Trasporto WebSocket per la modalità --serve. Non sa nulla del protocollo applicativo
/// (hello/new_session/send_message/...): dà solo un modo per accettare connessioni e
/// inviare/ricevere stringhe JSON grezze, un frame per messaggio. La logica del protocollo vive
/// in RunServeModeAsync (Program.cs), che riceve una <see cref="ServeConnection"/> per ogni
/// connessione accettata.
/// </summary>
public sealed class ServeServer
{
    private readonly HttpListener _listener = new();

    public ServeServer(int port)
    {
        // SOLO loopback: mai un prefisso che esponga il server su un'interfaccia diversa da
        // 127.0.0.1 — l'unica autenticazione è il token applicativo (hello), non ce n'è
        // nessun'altra a livello di rete.
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    /// <summary>
    /// Accetta connessioni finché <paramref name="ct"/> non viene annullato. Ogni connessione
    /// viene gestita in un Task separato (fire-and-forget): l'accept loop non aspetta che una
    /// connessione finisca prima di accettare la prossima, così più client (o più messaggi sulla
    /// stessa connessione, se il chiamante legge in un ciclo non bloccante) possono essere in
    /// volo insieme — necessario perché "un turno alla volta" sia un vincolo del PROTOCOLLO
    /// (vedi turnInProgress in RunServeModeAsync), non un limite accidentale del trasporto.
    /// </summary>
    public async Task RunAsync(Func<ServeConnection, CancellationToken, Task> onConnection, CancellationToken ct)
    {
        _listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext httpCtx;
                try { httpCtx = await _listener.GetContextAsync().WaitAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }

                if (!httpCtx.Request.IsWebSocketRequest)
                {
                    httpCtx.Response.StatusCode = 400;
                    httpCtx.Response.Close();
                    continue;
                }

                WebSocketContext wsCtx;
                try { wsCtx = await httpCtx.AcceptWebSocketAsync(subProtocol: null); }
                catch { continue; } // handshake fallito: non deve fermare il server

                var conn = new ServeConnection(wsCtx.WebSocket);
                _ = Task.Run(() => onConnection(conn, ct));
            }
        }
        finally { _listener.Stop(); }
    }
}

/// <summary>
/// Una connessione WebSocket accettata. Invio serializzato tramite una coda interna (un solo
/// WebSocket non supporta più SendAsync concorrenti): <see cref="Send"/> non blocca mai chi la
/// chiama (IAgentSink ha metodi void sincroni — vedi ServeSink), l'invio reale avviene su un Task
/// dedicato che scrive in ordine di arrivo.
/// </summary>
public sealed class ServeConnection
{
    private readonly WebSocket _ws;
    private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>();
    private readonly Task _writerTask;

    public ServeConnection(WebSocket ws)
    {
        _ws = ws;
        _writerTask = Task.Run(WriterLoopAsync);
    }

    /// <summary>Accoda un oggetto da serializzare (camelCase, vedi ServeProtocol.JsonOpts) e inviare.</summary>
    public void Send(object envelope) =>
        _outbox.Writer.TryWrite(System.Text.Json.JsonSerializer.Serialize(envelope, ServeProtocol.JsonOpts));

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var json in _outbox.Reader.ReadAllAsync())
            {
                if (_ws.State != WebSocketState.Open) break;
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
        }
        catch { /* connessione chiusa a metà invio: niente da fare qui, ReceiveAsync lo scoprirà */ }
    }

    /// <summary>Riceve il prossimo frame testuale COMPLETO (riassembla frammenti), o null se la connessione è chiusa.</summary>
    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try { result = await _ws.ReceiveAsync(buffer, ct); }
            catch { return null; }

            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async Task CloseAsync()
    {
        _outbox.Writer.TryComplete();
        try { await _writerTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch { /* già chiusa dall'altro lato o a metà di un errore precedente */ }
    }
}
