using System.Net;
using System.Net.Sockets;
using System.Text;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using Xunit;

namespace LocalCodeAgent.Core.Tests;

/// <summary>
/// Un server che accetta la richiesta e poi tace non deve tenere appesa StreamChatAsync fino al
/// watchdog del turno: i timeout di inattività la fanno fallire con le eccezioni che AgentLoop ritenta.
/// </summary>
public class LlamaClientIdleTimeoutTests
{
    private static (TcpListener listener, string url) StartSilentServer(string? initialResponse)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var buf = new byte[65536];
                await stream.ReadAsync(buf); // consuma (parte de) la richiesta
                if (initialResponse != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(initialResponse);
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
                await Task.Delay(TimeSpan.FromSeconds(30)); // poi silenzio
            }
            catch { /* test terminato */ }
        });
        return (listener, url);
    }

    private static ChatRequest NewRequest() => new() { Messages = [ChatMessage.User("ciao")] };

    [Fact]
    public async Task NoResponseAtAll_ThrowsHttpRequestException_AfterFirstByteTimeout()
    {
        var (listener, url) = StartSilentServer(initialResponse: null);
        try
        {
            var llm = new LlamaClient(url, "fake") { FirstByteTimeout = TimeSpan.FromSeconds(1) };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
                llm.StreamChatAsync(NewRequest(), _ => { }));

            Assert.Null(ex.StatusCode); // senza StatusCode il loop lo tratta come guasto di trasporto e ritenta
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"troppo lento: {sw.Elapsed}");
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task StreamGoesSilentMidResponse_ThrowsIncompleteResponse_AfterIdleTimeout()
    {
        const string head =
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n";
        const string line = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ciao\"}}]}\n\n";
        var chunk = $"{Encoding.UTF8.GetByteCount(line):x}\r\n{line}\r\n";
        var (listener, url) = StartSilentServer(head + chunk);
        try
        {
            var llm = new LlamaClient(url, "fake")
            {
                FirstByteTimeout = TimeSpan.FromSeconds(5),
                IdleTimeout = TimeSpan.FromSeconds(1)
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var ex = await Assert.ThrowsAsync<IncompleteLlmResponseException>(() =>
                llm.StreamChatAsync(NewRequest(), _ => { }));

            Assert.Equal("stream_truncated", ex.Kind);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"troppo lento: {sw.Elapsed}");
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task CallerCancellation_StillThrowsOperationCanceled_NotTimeoutException()
    {
        var (listener, url) = StartSilentServer(initialResponse: null);
        try
        {
            var llm = new LlamaClient(url, "fake") { FirstByteTimeout = TimeSpan.FromSeconds(30) };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                llm.StreamChatAsync(NewRequest(), _ => { }, cts.Token));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task SilentWait_EmitsHeartbeats_SoCallerSeesActivity()
    {
        var (listener, url) = StartSilentServer(initialResponse: null);
        try
        {
            var llm = new LlamaClient(url, "fake") { FirstByteTimeout = TimeSpan.FromSeconds(5) };
            var heartbeats = new List<string>();

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                llm.StreamChatAsync(NewRequest(), _ => { }, onHeartbeat: t => { lock (heartbeats) heartbeats.Add(t); }));

            lock (heartbeats)
            {
                Assert.NotEmpty(heartbeats);
                Assert.Contains("In attesa del server LLM", heartbeats[0]);
            }
        }
        finally { listener.Stop(); }
    }
}
