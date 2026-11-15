using System.Text.Json;
using AgentBackend.Services;
using LocalCodeAgent.Models;

namespace AgentBackend.Tests;

/// <summary>
/// <c>SessionPersistence.Save</c> può essere chiamata mentre il loop sta ancora aggiungendo messaggi
/// alla stessa lista (watchdog: <c>EndTurn("watchdog")</c> mentre il Task di turno è vivo) e
/// da due thread insieme (watchdog + finally di <c>send_message</c>). Non deve né lanciare
/// ("Collection was modified"), né lasciare un file corrotto, né un .tmp orfano.
/// </summary>
public sealed class SessionSaveConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pm-save-conc-" + Guid.NewGuid().ToString("N"));

    public SessionSaveConcurrencyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static AgentSession NewSession(int messages)
    {
        var s = new AgentSession { SessionId = Guid.NewGuid().ToString("N"), Title = "t", WorkspaceRoot = _ws, Model = "m" };
        for (var i = 0; i < messages; i++)
            s.History.Add(i % 2 == 0 ? ChatMessage.User("u" + i + new string('x', 200)) : new ChatMessage { Role = "assistant", Content = "a" + i + new string('y', 200) });
        return s;
    }

    private const string _ws = "C:\\ws";

    private void AssertFileIsValidJson(string sessionId)
    {
        var path = Path.Combine(_dir, sessionId + ".json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); // lancia se corrotto/troncato
        Assert.Equal(sessionId, doc.RootElement.GetProperty("sessionId").GetString());
        Assert.True(doc.RootElement.GetProperty("history").GetArrayLength() >= 100);
    }

    [Fact]
    public void SaveMentreIlLoopAggiungeMessaggi_NonLanciaENonCorrompeIlFile()
    {
        var session = NewSession(100);
        var stop = false;
        var writer = new Thread(() =>
        {
            var i = 0;
            while (!Volatile.Read(ref stop))
            {
                // Come il loop: Add di messaggi alla lista della sessione (nessun lock condiviso).
                session.History.Add(new ChatMessage { Role = "assistant", Content = "nuovo " + i++ + new string('z', 200) });
                if (session.History.Count > 6000) { Thread.Sleep(1); }
            }
        });
        writer.Start();

        var errors = new List<Exception>();
        try
        {
            for (var n = 0; n < 150; n++)
            {
                try { SessionPersistence.Save(_dir, session); }
                catch (Exception ex) { errors.Add(ex); }
                if (File.Exists(Path.Combine(_dir, session.SessionId + ".json")))
                    AssertFileIsValidJson(session.SessionId);
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            writer.Join();
        }

        // La concorrenza c'è stata davvero: lo scrittore ha aggiunto molti messaggi durante i salvataggi.
        Assert.True(session.History.Count > 1000, $"scrittore troppo lento per sovrapporsi ai Save ({session.History.Count} messaggi)");
        Assert.True(errors.Count == 0,
            $"Save ha lanciato {errors.Count} volte mentre la lista veniva modificata: {errors.FirstOrDefault()?.GetType().Name}: {errors.FirstOrDefault()?.Message}");
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void DueSaveInParallelo_WatchdogEFinally_NonCorrompono()
    {
        var session = NewSession(2000);
        var errors = new List<Exception>();
        var gate = new Barrier(2);

        void Saver()
        {
            gate.SignalAndWait();
            for (var n = 0; n < 60; n++)
            {
                try { SessionPersistence.Save(_dir, session); }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            }
        }

        var t1 = new Thread(Saver);
        var t2 = new Thread(Saver);
        t1.Start(); t2.Start();
        t1.Join(); t2.Join();

        Assert.True(errors.Count == 0, $"Save parallelo ha lanciato: {errors.FirstOrDefault()?.GetType().Name}: {errors.FirstOrDefault()?.Message}");
        AssertFileIsValidJson(session.SessionId);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        // Il contenuto finale è quello completo (2000 messaggi), non uno stato parziale.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, session.SessionId + ".json")));
        Assert.Equal(2000, doc.RootElement.GetProperty("history").GetArrayLength());
    }
}
