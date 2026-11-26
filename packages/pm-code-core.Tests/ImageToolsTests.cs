using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using LocalCodeAgent.Tools;
using Xunit;

namespace LocalCodeAgent.Tests;

/// <summary>Tool "analyze_image": esposizione condizionata e validazione degli argomenti (senza server di visione).</summary>
public class ImageToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "imgtools-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceContext _ws;

    public ImageToolsTests()
    {
        Directory.CreateDirectory(_dir);
        _ws = new WorkspaceContext(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static VisionSettings Configured() => new() { BaseUrl = "http://127.0.0.1:1", Model = "vision", TimeoutSeconds = 2 };

    [Fact]
    public void NonConfigurato_ToolNonEsposto() =>
        Assert.DoesNotContain(new ToolDispatcher(_ws).AllDefinitions, t => t.Function.Name == "analyze_image");

    [Fact]
    public void Configurato_ToolEsposto() =>
        Assert.Contains(new ToolDispatcher(_ws, null, null, Configured()).AllDefinitions, t => t.Function.Name == "analyze_image");

    [Fact]
    public void FormatoNonSupportato_Errore()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
        var r = new ToolDispatcher(_ws, null, null, Configured()).Execute("analyze_image", """{"path":"a.txt"}""");
        Assert.Contains("non supportato", r);
    }

    [Fact]
    public void FileMancante_Errore()
    {
        var r = new ToolDispatcher(_ws, null, null, Configured()).Execute("analyze_image", """{"path":"no.png"}""");
        Assert.Contains("non trovato", r);
    }

    [Fact]
    public void PercorsoAssolutoFuoriDaAllowedDirs_Rifiutato()
    {
        var outside = Path.Combine(Path.GetTempPath(), "fuori.png");
        var r = new ToolDispatcher(_ws, null, null, Configured())
            .Execute("analyze_image", System.Text.Json.JsonSerializer.Serialize(new { path = outside }));
        Assert.Contains("fuori dal workspace", r);
    }

    [Fact]
    public void ServerIrraggiungibile_ErroreSenzaEccezione()
    {
        File.WriteAllBytes(Path.Combine(_dir, "s.png"), [0x89, 0x50, 0x4E, 0x47]);
        var r = new ToolDispatcher(_ws, null, null, Configured()).Execute("analyze_image", """{"path":"s.png"}""");
        Assert.StartsWith("ERRORE", r);
    }

    [Fact]
    public void EffectiveVisionSettings_SenzaConfigurazioneRicadeSuLlmSettings()
    {
        var config = new AgentConfig
        {
            LlmSettings = new LlmSettings { BaseUrl = "http://100.102.197.68:9000", Model = "PM LLM", ApiKey = "k" }
        };
        var effective = config.EffectiveVisionSettings();
        Assert.True(effective.IsConfigured);
        Assert.Equal("http://100.102.197.68:9000", effective.BaseUrl);
        Assert.Equal("PM LLM", effective.Model);
        Assert.Equal("k", effective.ApiKey);
    }

    [Fact]
    public void EffectiveVisionSettings_ConfiguratoEsplicitamenteNonRicade()
    {
        var config = new AgentConfig
        {
            LlmSettings = new LlmSettings { BaseUrl = "http://main:9000", Model = "main-model" },
            VisionSettings = new VisionSettings { BaseUrl = "http://vision:9020", Model = "vision-model" }
        };
        var effective = config.EffectiveVisionSettings();
        Assert.Equal("http://vision:9020", effective.BaseUrl);
        Assert.Equal("vision-model", effective.Model);
    }
}
