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
/// Opzioni di esecuzione per <c>AgentLoop.RunTurnAsync</c>: unifica i parametri di
/// campionamento LLM e i limiti del loop in un unico oggetto, così il costruttore di
/// <c>AgentLoop</c> e i backend (CLI, serve) non devono passare 10+ argomenti separati.
/// </summary>
public sealed record AgentLoopOptions
{
    /// <summary>Se true, l'output va su <see cref="IAgentSink"/>/trace invece che su Console.</summary>
    public bool IsMachineMode { get; init; }

    /// <summary>Se true, è la modalità --serve: alcuni tool sono bloccati.</summary>
    public bool IsServeMode { get; init; }

    /// <summary>Tool bloccati in modalità serve.</summary>
    public IReadOnlySet<string> ServeBlockedTools { get; init; } = new HashSet<string>();

    /// <summary>Numero massimo di step per turno.</summary>
    public int MaxSteps { get; init; } = 30;

    /// <summary>Dimensione massima del contesto in token.</summary>
    public int MaxContextTokens { get; init; } = 81920;

    /// <summary>Numero massimo di ripetizioni identiche di uno stesso tool call.</summary>
    public int MaxRepeats { get; init; } = 3;

    /// <summary>Numero massimo di letture consecutive prima del nudge.</summary>
    public int MaxConsecutiveReadOnly { get; init; } = 6;

    /// <summary>Numero massimo di righe leggibili per file.</summary>
    public int MaxLinesPerFile { get; init; } = 400;

    // ── Parametri di campionamento LLM ──────────────────────────────────────────

    public float Temperature { get; init; } = 0.0f;
    public float FrequencyPenalty { get; init; } = 0.3f;
    public float? PresencePenalty { get; init; }
    public float? TopP { get; init; }
    public int? TopK { get; init; }
    public float? MinP { get; init; }
    public float? RepeatPenalty { get; init; }
    public int? RepeatLastN { get; init; }
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

    [JsonPropertyName("PersistenceSettings")]
    public PersistenceSettings PersistenceSettings { get; set; } = new();
}

/// <summary>
/// Configurazione della persistenza delle sessioni. Mappa il blocco "PersistenceSettings"
/// di appsettings.json. Le sessioni (storico + metadati) vengono salvate in una cartella
/// centrale così sopravvivono ai riavvii del backend.
/// </summary>
public class PersistenceSettings
{
    /// <summary>
    /// Cartella dove salvare i file JSON delle sessioni. Default: "sessions" relativo alla
    /// cartella di lavoro del backend (es. C:\pm-code\agent-backend\sessions). Se vuoto,
    /// si usa AppContext.BaseDirectory + "sessions".
    /// </summary>
    [JsonPropertyName("SessionsDir")]
    public string SessionsDir { get; set; } = "";
}
