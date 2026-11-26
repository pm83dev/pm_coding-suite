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
/// Configurazione opzionale del sotto-agente "explore" (tool di esplorazione in sola
/// lettura). Se presente, l'ExploreSubAgent usa questo endpoint/modello al posto di
/// quello principale (LlmSettings): tipicamente un'istanza llama-server dedicata su un'altra
/// GPU, così le decine di letture/ricerche del sotto-agente non competono con il modello
/// coordinatore. Se assente (o con BaseUrl/Model vuoti), il sotto-agente usa lo stesso
/// LlamaClient del loop principale — comportamento invariato.
/// Mappa il blocco "ExploreLlmSettings" di appsettings.json.
/// </summary>
public class ExploreLlmSettings
{
    [JsonPropertyName("BaseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("Model")]
    public string Model { get; set; } = "";

    /// <summary>
    /// Timeout della singola chiamata HTTP al modello di lettura (secondi). Applicato SOLO a
    /// livello di chiamata del sotto-agente (CancellationTokenSource collegato al ct del turno) —
    /// non cambia <see cref="LlamaClient"/>, il cui HttpClient resta a timeout infinito per gli
    /// altri usi. Default: 120s.
    /// </summary>
    [JsonPropertyName("TimeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Percorso di un file con il system prompt del sotto-agente. Se assente/non leggibile, usa il default nel codice.</summary>
    [JsonPropertyName("SystemPromptPath")]
    public string? SystemPromptPath { get; set; }

    [JsonPropertyName("MaxTokens")]
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// "auto" (nessun campo extra inviato — decide il server/template), "on"/"off" (inviati come
    /// "chat_template_kwargs": {"enable_thinking": true|false} — stessa convenzione di
    /// <see cref="SecondOpinionSettings.ReasoningMode"/>, non specifica di un modello). Default
    /// "off": per un lettore che deve restituire citazioni verbatim e numeri di riga, il
    /// ragionamento interno aggiunge solo latenza e rischio di esaurire MaxTokens prima della
    /// risposta finale (visto in pratica con ask_second_opinion).
    /// </summary>
    [JsonPropertyName("ReasoningMode")]
    public string ReasoningMode { get; set; } = "off";

    // Sampling: tutti opzionali e NON ereditati da LlmSettings (il coordinatore) — se assenti il
    // campo non viene incluso nella richiesta HTTP (il server usa i default di avvio).
    [JsonPropertyName("Temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("TopP")]
    public float? TopP { get; set; }

    [JsonPropertyName("TopK")]
    public int? TopK { get; set; }

    [JsonPropertyName("MinP")]
    public float? MinP { get; set; }

    [JsonPropertyName("RepeatPenalty")]
    public float? RepeatPenalty { get; set; }

    [JsonPropertyName("PresencePenalty")]
    public float? PresencePenalty { get; set; }

    [JsonPropertyName("FrequencyPenalty")]
    public float? FrequencyPenalty { get; set; }

    /// <summary>
    /// Righe massime restituite da una singola chiamata a read_file_range DENTRO il sotto-agente
    /// explore (non tocca il comportamento di read_file_range per il loop principale — vedi
    /// ExploreSubAgent.Execute: la clamp riscrive solo gli argomenti passati al dispatcher
    /// PROPRIO di explore). Se la richiesta supera il limite, si restituiscono solo le prime N
    /// righe con un avviso su quante ne restano e come continuare. Default 300.
    /// </summary>
    [JsonPropertyName("MaxLinesPerReadCall")]
    public int MaxLinesPerReadCall { get; set; } = 300;

    /// <summary>
    /// Soglia (stima di token, ~caratteri/4) sulla history INTERNA del sotto-agente explore oltre
    /// la quale l'esplorazione si ferma e restituisce quanto trovato finora con un avviso, invece
    /// di continuare ad accumulare contesto. Non è un rollover: nessuna compressione, solo uno
    /// stop anticipato. Default 20000 token stimati — verificato contro n_ctx=128000 del server
    /// di lettura attuale (Qwen3.5-9B-Q4_K_M.gguf su :9005, controllato via GET /props): ampio
    /// margine sotto il limite reale, pensato per intercettare un'esplorazione fuori controllo,
    /// non il caso normale.
    /// </summary>
    [JsonPropertyName("MaxHistoryTokensEstimate")]
    public int MaxHistoryTokensEstimate { get; set; } = 20000;

    /// <summary>Troncamento nel trace diagnostico (argomenti/risultato) SOLO per explore.</summary>
    [JsonPropertyName("TraceMaxChars")]
    public int TraceMaxChars { get; set; } = 1000;

    /// <summary>
    /// true se il blocco è realmente configurato (BaseUrl e Model non vuoti): in caso
    /// contrario il chiamante ricade sul client principale.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>
/// Endpoint multimodale per il tool "analyze_image" (vedi ImageTools). Senza BaseUrl/Model il
/// tool non viene esposto. Tipicamente llama-server avviato con un modello a visione + --mmproj.
/// </summary>
public class VisionSettings
{
    [JsonPropertyName("BaseUrl")] public string BaseUrl { get; set; } = "";
    [JsonPropertyName("Model")] public string Model { get; set; } = "";
    [JsonPropertyName("ApiKey")] public string ApiKey { get; set; } = "";
    [JsonPropertyName("TimeoutSeconds")] public int TimeoutSeconds { get; set; } = 180;
    [JsonPropertyName("MaxTokens")] public int MaxTokens { get; set; } = 2048;
    [JsonPropertyName("Temperature")] public float Temperature { get; set; } = 0.2f;
    [JsonPropertyName("MaxImageBytes")] public long MaxImageBytes { get; set; } = 8 * 1024 * 1024;
    [JsonPropertyName("MaxAnswerChars")] public int MaxAnswerChars { get; set; } = 6000;

    /// <summary>Cartelle assolute (es. gli screenshot) da cui è permesso leggere immagini oltre al workspace.</summary>
    [JsonPropertyName("AllowedDirs")] public List<string> AllowedDirs { get; set; } = [];

    [JsonPropertyName("DefaultQuestion")]
    public string DefaultQuestion { get; set; } =
        "Descrivi in dettaglio questa immagine: layout, componenti UI, tutti i testi visibili (trascritti alla lettera), colori e stati.";

    [JsonPropertyName("SystemPrompt")]
    public string SystemPrompt { get; set; } =
        "Sei un analista di immagini per un agente di programmazione. Rispondi in italiano, in modo fedele e strutturato: " +
        "trascrivi i testi esattamente come appaiono, non inventare ciò che non è visibile e indica ciò che è illeggibile.";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>
/// Configurazione opzionale del sotto-agente "review_changes" (revisione in sola lettura
/// del diff pendente). Stesso schema di <see cref="ExploreLlmSettings"/>: se presente, usa
/// un'istanza llama-server dedicata (tipicamente un modello "coder" su un'altra GPU/porta);
/// se assente, il sotto-agente usa lo stesso LlamaClient del loop principale.
/// Mappa il blocco "CoderLlmSettings" di appsettings.json.
/// </summary>
public class CoderLlmSettings
{
    [JsonPropertyName("BaseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("Model")]
    public string Model { get; set; } = "";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>
/// Configurazione dedicata del sotto-agente "ask_second_opinion" (<see cref="ConsultSubAgent"/>
/// in pm-code-core). INDIPENDENTE da <see cref="CoderLlmSettings"/> (usato da "review_changes"):
/// se questa sezione non è configurata (BaseUrl/ModelName vuoti), il chiamante ricade su
/// <see cref="CoderLlmSettings"/> per non cambiare il comportamento attuale — ma "review_changes"
/// stesso continua a usare SOLO <see cref="CoderLlmSettings"/>, mai questa sezione. Mappa il
/// blocco "SecondOpinionSettings" di appsettings.json. Nessun valore di default lega il codice a
/// un modello specifico: tutti i parametri di campionamento sono opzionali e, se assenti, non
/// vengono inviati al server (il server usa i propri default di avvio).
/// </summary>
public class SecondOpinionSettings
{
    [JsonPropertyName("BaseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("ModelName")]
    public string ModelName { get; set; } = "";

    /// <summary>Opzionale: inviata come header "Authorization: Bearer &lt;ApiKey&gt;" se non vuota.</summary>
    [JsonPropertyName("ApiKey")]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Timeout della singola chiamata HTTP al modello di supporto (secondi). Applicato SOLO a
    /// livello di chiamata del sotto-agente (CancellationTokenSource collegato al ct del turno) —
    /// non cambia <see cref="LlamaClient"/>, il cui HttpClient resta a timeout infinito per gli
    /// altri usi. Default: 300s (5 minuti, generoso per un modello CPU-bound locale).
    /// </summary>
    [JsonPropertyName("TimeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 300;

    [JsonPropertyName("MaxTokens")]
    public int MaxTokens { get; set; } = 12288;

    [JsonPropertyName("MaxAnswerChars")]
    public int MaxAnswerChars { get; set; } = 6000;

    /// <summary>Numero massimo di step (tool call) del sotto-agente per singola chiamata al tool.</summary>
    [JsonPropertyName("MaxStepsSubAgent")]
    public int MaxStepsSubAgent { get; set; } = 8;

    /// <summary>Numero massimo di chiamate ad ask_second_opinion per turno del loop principale.</summary>
    [JsonPropertyName("MaxCallsPerTurn")]
    public int MaxCallsPerTurn { get; set; } = 3;

    // Sampling: tutti opzionali e NON ereditati da LlmSettings/CoderLlmSettings — se assenti il
    // campo non viene incluso nella richiesta HTTP (il server usa i default di avvio).
    [JsonPropertyName("Temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("TopP")]
    public float? TopP { get; set; }

    [JsonPropertyName("TopK")]
    public int? TopK { get; set; }

    [JsonPropertyName("MinP")]
    public float? MinP { get; set; }

    [JsonPropertyName("RepeatPenalty")]
    public float? RepeatPenalty { get; set; }

    /// <summary>
    /// "auto" (default, nessun campo extra inviato — decide il server/template), "on"/"off"
    /// (inviati come "chat_template_kwargs": {"enable_thinking": true|false} — convenzione
    /// adottata da più chat template "thinking-capable", non da un singolo modello — vedi
    /// nota nel report: se l'endpoint/chat-template non la supporta, il campo viene ignorato
    /// dalla maggior parte dei server OpenAI-compatibili). Se il server non ha un parametro
    /// per-richiesta per questo, va impostato all'avvio del server (fuori da questa app).
    /// </summary>
    [JsonPropertyName("ReasoningMode")]
    public string ReasoningMode { get; set; } = "auto";

    /// <summary>Percorso di un file con il system prompt del sotto-agente. Se assente/non leggibile, usa il default nel codice.</summary>
    [JsonPropertyName("SystemPromptPath")]
    public string? SystemPromptPath { get; set; }

    /// <summary>Troncamento nel trace diagnostico (argomenti/risultato) SOLO per ask_second_opinion.</summary>
    [JsonPropertyName("TraceMaxChars")]
    public int TraceMaxChars { get; set; } = 2000;

    /// <summary>
    /// Soglia di "stesso errore ripetuto" (stesso tool, stesso errore, argomenti diversi) oltre
    /// la quale AgentLoop suggerisce ask_second_opinion prima di fermarsi e chiedere all'utente.
    /// Default 3 — identico al valore finora hardcoded in AgentLoop (nessuna regressione).
    /// </summary>
    [JsonPropertyName("SameErrorThreshold")]
    public int SameErrorThreshold { get; set; } = 3;

    /// <summary>
    /// Soglia di "troppe letture senza agire" (letture consecutive senza modifiche) oltre la
    /// quale il nudge esistente (AgentSettings.MaxConsecutiveReadOnly) include anche il
    /// suggerimento di ask_second_opinion. Default 6, volutamente più alta dell'attuale
    /// maxReadBlocks (3, non configurabile) usato per il rifiuto duro delle letture: qui si
    /// tratta di un suggerimento, non di un blocco, quindi ha senso scatti più avanti.
    /// </summary>
    [JsonPropertyName("ReadWithoutActionThreshold")]
    public int ReadWithoutActionThreshold { get; set; } = 6;

    /// <summary>
    /// true se BaseUrl e ModelName sono entrambi configurati: in caso contrario il chiamante
    /// ricade su CoderLlmSettings (vedi classe).
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ModelName);
}

/// <summary>
/// Configurazione del tool opzionale "ask_revision" (<see cref="RevisionSubAgent"/>): revisione/coordinamento
/// affidati al CLI di Claude Code in modalità non interattiva (<c>claude -p</c>), con il login dell'utente —
/// nessuna chiave API. DISABILITATO di default: il tool non viene nemmeno esposto al modello se
/// <see cref="Enabled"/> è false. Indipendente da <see cref="SecondOpinionSettings"/> (che resta locale).
/// </summary>
public class RevisionSettings
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; }

    /// <summary>Eseguibile del CLI (default "claude", risolto dal PATH).</summary>
    [JsonPropertyName("ClaudeCliPath")]
    public string ClaudeCliPath { get; set; } = "claude";

    /// <summary>Opzionale: alias/nome modello passato a <c>--model</c>. Vuoto = default del CLI.</summary>
    [JsonPropertyName("Model")]
    public string? Model { get; set; }

    /// <summary>Tetto ai turni interni del CLI (<c>--max-turns</c>).</summary>
    [JsonPropertyName("MaxTurns")]
    public int MaxTurns { get; set; } = 6;

    [JsonPropertyName("TimeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 300;

    [JsonPropertyName("MaxAnswerChars")]
    public int MaxAnswerChars { get; set; } = 8000;

    /// <summary>Numero massimo di chiamate ad ask_revision per turno del loop principale (0 o negativo = illimitato).</summary>
    [JsonPropertyName("MaxCallsPerTurn")]
    public int MaxCallsPerTurn { get; set; } = 0;

    /// <summary>
    /// Regole <c>Read(...)</c> aggiuntive negate al CLI per non fargli leggere segreti. Si sommano a
    /// quelle di default (appsettings*.json, .env*, chiavi/certificati).
    /// </summary>
    [JsonPropertyName("DenyRead")]
    public List<string> DenyRead { get; set; } = [];

    /// <summary>Troncamento nel trace diagnostico (argomenti/risultato) per ask_revision.</summary>
    [JsonPropertyName("TraceMaxChars")]
    public int TraceMaxChars { get; set; } = 2000;
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

    /// <summary>
    /// Limite di INATTIVITÀ in secondi per il watchdog del turno nel backend (default 300).
    /// Se per questo tempo il turno non emette nessun evento (delta, tool call, heartbeat di stato)
    /// e non è in attesa di approvazione, viene cancellato tramite CancelTurn(). Un turno lungo ma
    /// attivo non viene tagliato: per la durata totale vedi <see cref="TurnMaxSeconds"/>.
    /// </summary>
    [JsonPropertyName("TurnTimeoutSeconds")]
    public int TurnTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Tetto assoluto opzionale in secondi sulla durata totale di un turno. 0 (default) = nessun
    /// tetto: una sessione che lavora correttamente non viene mai interrotta per la sola durata.
    /// Se impostato, non può essere inferiore a <see cref="TurnTimeoutSeconds"/>.
    /// </summary>
    [JsonPropertyName("TurnMaxSeconds")]
    public int TurnMaxSeconds { get; set; } = 0;

    /// <summary>
    /// Max attesa (secondi) per header + prima riga dello stream LLM, incluso il prompt processing.
    /// Scaduto: la chiamata viene ritentata dal loop. 0 = disattivato.
    /// </summary>
    [JsonPropertyName("LlmFirstByteTimeoutSeconds")]
    public int LlmFirstByteTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Max silenzio (secondi) tra due righe dello stream LLM. Scaduto: stream considerato bloccato
    /// e chiamata ritentata. 0 = disattivato.
    /// </summary>
    [JsonPropertyName("LlmIdleTimeoutSeconds")]
    public int LlmIdleTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Quante volte il watchdog del turno può far ripartire da solo un turno bloccato che ha ancora
    /// un piano con passi aperti (messaggio "prosegui" automatico). 0 = mai.
    /// </summary>
    [JsonPropertyName("WatchdogAutoContinueMax")]
    public int WatchdogAutoContinueMax { get; set; } = 2;
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

    // ── Limiti dedicati ad ask_second_opinion (vedi SecondOpinionSettings) ──────────

    /// <summary>Numero massimo di chiamate ad ask_second_opinion per turno.</summary>
    public int SecondOpinionMaxCallsPerTurn { get; init; } = 3;

    /// <summary>Numero massimo di chiamate ad ask_revision per turno (0 o negativo = illimitato), e troncamento nel trace (vedi RevisionSettings).</summary>
    public int RevisionMaxCallsPerTurn { get; init; } = 0;
    public int RevisionTraceMaxChars { get; init; } = 2000;

    /// <summary>Soglia "stesso errore ripetuto" oltre la quale si suggerisce ask_second_opinion.</summary>
    public int SecondOpinionSameErrorThreshold { get; init; } = 3;

    /// <summary>Soglia "troppe letture senza agire" oltre la quale si suggerisce ask_second_opinion.</summary>
    public int SecondOpinionReadWithoutActionThreshold { get; init; } = 6;

    /// <summary>Troncamento nel trace diagnostico (argomenti/risultato) SOLO per ask_second_opinion.</summary>
    public int SecondOpinionTraceMaxChars { get; init; } = 2000;

    /// <summary>Troncamento nel trace diagnostico (argomenti/risultato) SOLO per explore.</summary>
    public int ExploreTraceMaxChars { get; init; } = 1000;
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

    /// <summary>
    /// Endpoint/modello opzionali per il sotto-agente "explore" (vedi <see cref="ExploreLlmSettings"/>).
    /// Se assente o non configurato, il sotto-agente usa <see cref="LlmSettings"/>.
    /// </summary>
    [JsonPropertyName("ExploreLlmSettings")]
    public ExploreLlmSettings ExploreLlmSettings { get; set; } = new();

    /// <summary>
    /// Endpoint/modello opzionali per il sotto-agente "review_changes" (vedi <see cref="CoderLlmSettings"/>).
    /// Se assente o non configurato, il sotto-agente usa <see cref="LlmSettings"/>.
    /// </summary>
    [JsonPropertyName("CoderLlmSettings")]
    public CoderLlmSettings CoderLlmSettings { get; set; } = new();

    /// <summary>
    /// Endpoint/modello/parametri dedicati del sotto-agente "ask_second_opinion" (vedi
    /// <see cref="SecondOpinionSettings"/>). Se assente o non configurato, ricade su
    /// <see cref="CoderLlmSettings"/> (comportamento storico) e poi su <see cref="LlmSettings"/>.
    /// </summary>
    [JsonPropertyName("SecondOpinionSettings")]
    public SecondOpinionSettings SecondOpinionSettings { get; set; } = new();
    public RevisionSettings RevisionSettings { get; set; } = new();

    /// <summary>Modello multimodale per "analyze_image" (vedi <see cref="VisionSettings"/>); assente = tool non esposto.</summary>
    [JsonPropertyName("VisionSettings")]
    public VisionSettings VisionSettings { get; set; } = new();

    [JsonPropertyName("AgentSettings")]
    public AgentSettings AgentSettings { get; set; } = new();

    [JsonPropertyName("WebSearchSettings")]
    public WebSearchSettings WebSearchSettings { get; set; } = new();

    [JsonPropertyName("ServeSettings")]
    public ServeSettings ServeSettings { get; set; } = new();

    [JsonPropertyName("PersistenceSettings")]
    public PersistenceSettings PersistenceSettings { get; set; } = new();

    [JsonPropertyName("TraceSettings")]
    public TraceSettings TraceSettings { get; set; } = new();

    /// <summary>
    /// VisionSettings da usare davvero per "analyze_image": se la sezione "VisionSettings" non è
    /// configurata esplicitamente (BaseUrl/Model vuoti), ricade su LlmSettings — il modello
    /// principale che già serve /v1/chat/completions con --mmproj può anche rispondere alle
    /// richieste con "image_url", quindi il tool si attiva senza un endpoint/porta dedicati.
    /// Chiunque debba passare un VisionSettings al ToolDispatcher usa questo, non VisionSettings
    /// direttamente.
    /// </summary>
    public VisionSettings EffectiveVisionSettings() => VisionSettings.IsConfigured
        ? VisionSettings
        : new VisionSettings
        {
            BaseUrl = LlmSettings.BaseUrl,
            Model = LlmSettings.Model,
            ApiKey = LlmSettings.ApiKey,
            TimeoutSeconds = VisionSettings.TimeoutSeconds,
            MaxTokens = VisionSettings.MaxTokens,
            Temperature = VisionSettings.Temperature,
            MaxImageBytes = VisionSettings.MaxImageBytes,
            MaxAnswerChars = VisionSettings.MaxAnswerChars,
            AllowedDirs = VisionSettings.AllowedDirs,
            DefaultQuestion = VisionSettings.DefaultQuestion,
            SystemPrompt = VisionSettings.SystemPrompt,
        };
}

/// <summary>
/// Trace strutturato NDJSON (vedi StructuredTrace) per agent-backend. Le variabili d'ambiente
/// PM_TRACE / PM_TRACE_DIR / PM_TRACE_MAX_FILES / PM_TRACE_MAX_MB, se presenti, hanno la precedenza.
/// </summary>
public class TraceSettings
{
    /// <summary>Default attivo. false = nessun file di trace.</summary>
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Cartella dei file .ndjson. Vuoto = "traces" accanto all'eseguibile.</summary>
    [JsonPropertyName("Dir")]
    public string Dir { get; set; } = "";

    /// <summary>Conserva solo gli ultimi N file (0 = default 200).</summary>
    [JsonPropertyName("MaxFiles")]
    public int MaxFiles { get; set; } = 0;

    /// <summary>Tetto in MB sul totale della cartella (0 = nessun tetto).</summary>
    [JsonPropertyName("MaxMb")]
    public int MaxMb { get; set; } = 0;

    /// <summary>
    /// Default false (nel trace solo hash e conteggi). true = ogni "prompt_fingerprint" include un breve
    /// estratto (~80 caratteri prima/dopo, ripulito da chiavi/token) del punto in cui il prompt diverge
    /// dalla richiesta precedente: dice COSA è cambiato, ma scrive nel trace frammenti del prompt.
    /// </summary>
    [JsonPropertyName("DiffSnippets")]
    public bool DiffSnippets { get; set; } = false;
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
