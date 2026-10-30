using System.Text.Json.Serialization;

namespace LocalCodeAgent.Models;

/// <summary>
/// Configurazione LLM (base URL, modello, parametri di campionamento).
/// Mappa il blocco "LlmSettings" di appsettings.json.
/// </summary>
public class LlmSettings
{
    [JsonPropertyName("BaseUrl")]
    public string BaseUrl { get; set; } = "http://localhost:8080";

    [JsonPropertyName("Model")]
    public string Model { get; set; } = "local";

    [JsonPropertyName("Temperature")]
    public float Temperature { get; set; } = 0.0f;

    [JsonPropertyName("FrequencyPenalty")]
    public float FrequencyPenalty { get; set; } = 0.3f;

    [JsonPropertyName("PresencePenalty")]
    public float? PresencePenalty { get; set; }

    [JsonPropertyName("TopP")]
    public float? TopP { get; set; }

    [JsonPropertyName("TopK")]
    public int? TopK { get; set; }

    [JsonPropertyName("MinP")]
    public float? MinP { get; set; }

    [JsonPropertyName("RepeatPenalty")]
    public float? RepeatPenalty { get; set; }

    [JsonPropertyName("RepeatLastN")]
    public int? RepeatLastN { get; set; }

    [JsonPropertyName("ApiKey")]
    public string ApiKey { get; set; } = "";
}

/// <summary>
/// Configurazione agente (workspace, limiti di contesto, rollover).
/// Mappa il blocco "AgentSettings" di appsettings.json.
/// </summary>
public class AgentSettings
{
    [JsonPropertyName("Workspace")]
    public string Workspace { get; set; } = ".";

    [JsonPropertyName("MaxSteps")]
    public int MaxSteps { get; set; } = 30;

    [JsonPropertyName("MaxContextTokens")]
    public int MaxContextTokens { get; set; } = 81920;

    [JsonPropertyName("RolloverTokens")]
    public int RolloverTokens { get; set; } = 40000;

    [JsonPropertyName("MaxRepeats")]
    public int MaxRepeats { get; set; } = 3;

    [JsonPropertyName("MaxConsecutiveReadOnly")]
    public int MaxConsecutiveReadOnly { get; set; } = 6;

    [JsonPropertyName("MaxLinesPerFile")]
    public int MaxLinesPerFile { get; set; } = 400;

    [JsonPropertyName("AllowedWorkspaceRoots")]
    public List<string> AllowedWorkspaceRoots { get; set; } = [];
}

/// <summary>
/// Configurazione ricerca web (Brave API key).
/// Mappa il blocco "WebSearchSettings" di appsettings.json.
/// </summary>
public class WebSearchSettings
{
    [JsonPropertyName("BraveApiKey")]
    public string BraveApiKey { get; set; } = "";
}

/// <summary>
/// Configurazione server WebSocket (--serve mode).
/// Mappa il blocco "ServeSettings" di appsettings.json.
/// </summary>
public class ServeSettings
{
    [JsonPropertyName("Port")]
    public int Port { get; set; } = 8765;

    [JsonPropertyName("BindAddress")]
    public string BindAddress { get; set; } = "127.0.0.1";

    [JsonPropertyName("Token")]
    public string Token { get; set; } = "";
}

/// <summary>
/// Configurazione completa dell'agente, caricata da appsettings.json.
/// </summary>
public class AgentConfig
{
    [JsonPropertyName("LlmSettings")]
    public LlmSettings LlmSettings { get; set; } = new();

    [JsonPropertyName("AgentSettings")]
    public AgentSettings AgentSettings { get; set; } = new();

    [JsonPropertyName("WebSearchSettings")]
    public WebSearchSettings WebSearchSettings { get; set; } = new();

    [JsonPropertyName("ServeSettings")]
    public ServeSettings ServeSettings { get; set; } = new();
}
