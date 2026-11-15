using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using Xunit;

namespace LocalCodeAgent.Core.Tests;

/// <summary>
/// Un risultato di tool enorme (es. read_file di un SVG da 2 MB su una riga) non deve entrare per intero
/// nella history, né sopravvivere ai rollover.
/// </summary>
public class ToolResultLimiterTests
{
    [Fact]
    public void Clip_ShortText_IsUnchanged()
    {
        Assert.Equal("ciao", ToolResultLimiter.Clip("ciao", 100));
    }

    [Fact]
    public void Clip_HugeText_KeepsHeadAndTail_AndSaysWhatToDo()
    {
        var text = "INIZIO" + new string('x', 2_000_000) + "FINE";

        var clipped = ToolResultLimiter.Clip(text, ToolResultLimiter.MaxToolResultChars);

        Assert.True(clipped.Length < ToolResultLimiter.MaxToolResultChars + 500, $"len={clipped.Length}");
        Assert.StartsWith("INIZIO", clipped);
        Assert.EndsWith("FINE", clipped);
        Assert.Contains("OUTPUT TRONCATO", clipped);
        Assert.Contains("search_in_files", clipped);
    }

    [Fact]
    public void ShrinkOversizedToolMessages_OnlyTouchesLargeToolMessages()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.User(new string('u', 50_000)),                  // user: mai toccato
            ChatMessage.ToolResult("a", "piccolo"),
            ChatMessage.ToolResult("b", new string('t', 2_000_000)),   // da accorciare
        };

        var n = ToolResultLimiter.ShrinkOversizedToolMessages(history, ToolResultLimiter.MaxKeptToolResultChars);

        Assert.Equal(1, n);
        Assert.Equal(50_000, history[1].Content!.Length);
        Assert.Equal("piccolo", history[2].Content);
        Assert.True(history[3].Content!.Length < ToolResultLimiter.MaxKeptToolResultChars + 500);
    }

    [Fact]
    public async Task Rollover_DoesNotKeepGiantToolResultInRecentTail()
    {
        // Server LLM inesistente: il rollover ripiega sul riassunto di fallback, senza LLM.
        var llm = new LlamaClient("http://127.0.0.1:1", "fake") { EndpointOverride = "http://127.0.0.1:1/v1/chat/completions" };
        var sm = new SessionManager(llm, maxTokens: 128_000, rolloverTokens: 60_000, () => "sys");

        var history = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.User("leggi il file"),
            new() { Role = "assistant", ToolCalls = [new ToolCall { Id = "1", Type = "function",
                Function = new FunctionCall { Name = "read_file", Arguments = "{}" } }] },
            ChatMessage.ToolResult("1", new string('x', 2_000_000)),
        };

        var fresh = await sm.MaybeRolloverAsync(history, atTurnStart: false, hasPendingPlan: true,
            currentRequest: "prosegui", planSummary: null, workspaceRoot: Path.GetTempPath());

        Assert.All(fresh, m => Assert.True((m.Content?.Length ?? 0) < 100_000, $"{m.Role} len={m.Content?.Length}"));
        Assert.True(sm.EstimateHistoryTokens(fresh) < 60_000);
    }
}
