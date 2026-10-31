using System.Text.Json;
using System.Text.RegularExpressions;
using LocalCodeAgent.Cli;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using Microsoft.Extensions.Configuration;

// Modalità headless per l'integrazione con extension/: legge richieste NDJSON da stdin,
// scrive eventi NDJSON su stdout, mai un banner/prompt interattivo. Il flag va controllato
// PRIMA di EnsureConsole(): in questa modalità stdin/stdout sono pipe verso il processo
// padre (VS Code) e non dobbiamo mai allocare/agganciare una finestra console.
var isStdinProtocol = args.Contains("--stdin-protocol");

// Modalità server WebSocket (ui/protocol v1) — vedi ServeServer.cs/ServeSink.cs. Mutuamente
// esclusiva con --stdin-protocol: chi avvia il processo scegliere UNA modalità.
var isServeMode = args.Contains("--serve");

// Vero in entrambe le modalità "macchina" (--stdin-protocol, --serve): in nessuna delle due si
// deve stampare la formattazione REPL (prefisso "Agent ›", step indicator, righe vuote di
// cortesia) sulla console del processo — in --stdin-protocol perché romperebbe il parsing NDJSON
// su stdout, in --serve perché quella console non è protocollo, è solo log dell'operatore.
var isMachineMode = isStdinProtocol || isServeMode;

// Tool bloccati del tutto in modalità --serve (scritture e comandi shell): vedi il gate in
// ExecuteToolGuarded più sotto. git_add/git_commit/git_checkout sono scritture sul repository,
// non sul filesystem del workspace, ma rientrano nello stesso principio ("nessuna azione che
// cambi stato senza un'approvazione esplicita, non ancora implementata in questa fase").
var serveBlockedTools = new HashSet<string>(StringComparer.Ordinal)
{
    "write_file", "edit_file", "delete_file", "move_file", "create_directory",
    "run_command", "run_dotnet", "sol_analyze", "stop_background_job",
    "git_add", "git_commit", "git_checkout",
};

// Unico punto da cui il loop (RunAgentLoopAsync) emette testo/tool-call/errori/notice, sia in
// REPL sia in --stdin-protocol/--serve: vedi IAgentSink.cs. Il valore reale per --serve (un
// ServeSink per connessione, invece di questo ConsoleSink) viene assegnato più sotto da
// RunServeModeAsync() per ogni turno — sink è una normale variabile locale (non readonly),
// quindi RunAgentLoopAsync (chiusura) vede sempre il valore più recente.
IAgentSink sink = new ConsoleSink(isStdinProtocol);

// Lo Stop in chat termina questo processo con TerminateProcess: senza job i processi avviati
// dall'agente (dev server, app di prova, pwsh in attesa) restavano vivi senza più nessun padre.
ProcessJob.KillChildrenOnExit();

// Trace NDJSON strutturato (additivo, vedi StructuredTrace.cs): attivo di default in TUTTE le
// modalità, PM_TRACE=off per disattivarlo. Va inizializzato prima di qualunque ResetHistory()
// (il primo avviene poche righe più sotto) perché quella chiamata apre già la prima sessione.
StructuredTrace.Init();

// L'extension scrive su stdin in UTF-8, ma su Windows Console.In decodifica con la codepage OEM
// (437): "già" arrivava al modello come "gi├á". Non si usa Console.InputEncoding perché con stdin
// rediretto e nessuna console agganciata SetConsoleCP può fallire.
if (isStdinProtocol)
    Console.SetIn(new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false)));

if (!isStdinProtocol)
{
    // Garantisce una finestra console anche quando il processo viene avviato
    // senza terminale (debugger VS Code in modalità "internalConsole", doppio-click, ecc.)
    ConsoleHelper.EnsureConsole();
}

var eventJsonOpts = new System.Text.Json.JsonSerializerOptions
{
    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
};

// Unico punto che scrive su stdout in modalità --stdin-protocol: una riga JSON per evento.
// Mai usato in modalità REPL (dove stdout resta libero per l'output umano esistente).
void EmitEvent(object evt)
{
    Console.Out.WriteLine(JsonSerializer.Serialize(evt, eventJsonOpts));
    Console.Out.Flush();
}

// Traccia diagnostica della modalità --stdin-protocol: la chat di VS Code mostra le tool
// call solo come progress transitorio, quindi senza questa traccia un turno che consuma
// tutti gli step è indistinguibile da un blocco. Va su stderr (console dell'Extension Host)
// e in pm-code-trace.log accanto all'eseguibile.
var traceFile = Path.Combine(AppContext.BaseDirectory, "pm-code-trace.log");
void Trace(string line)
{
    Console.Error.WriteLine($"[trace] {line}");
    try { File.AppendAllText(traceFile, line + Environment.NewLine); } catch { }
}
// Intervento automatico del motore (auto-continue, richiesta di test, verifica mancante):
// finisce nella traccia e, in --stdin-protocol, come evento "notice" mostrato in chat dalla
// cronaca dell'estensione. Le estensioni precedenti ignorano i tipi di evento sconosciuti.
void Notice(string text)
{
    if (!isMachineMode) return;
    Trace($"  !! {text}");
    sink.Notice(text);
}
static string OneLine(string s, int max)
{
    var flat = s.Replace("\r", "").Replace('\n', ' ');
    return flat.Length <= max ? flat : flat[..max] + $"…(+{flat.Length - max})";
}

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

string GetRequiredSetting(string key) =>
    config[key] ?? throw new InvalidOperationException($"Configurazione mancante: '{key}' in appsettings.json");

var serverUrl = GetRequiredSetting("LlmSettings:BaseUrl");
var model = GetRequiredSetting("LlmSettings:Model");

// --workspace <path>: usato dall'extension per far coincidere il workspace sandbox
// dell'agent con la cartella REALMENTE aperta in VS Code. Senza questo, il processo
// figlio spawnato da child_process erediterebbe la cwd dell'Extension Host (es. la
// cartella di installazione di VS Code) e "./AgentWorkspace" (default di appsettings.json)
// finirebbe risolto lì — una cartella vuota scollegata dal progetto dell'utente.
// In modalità REPL/CLI resta il default di AgentSettings:Workspace, invariato.
var workspaceArgIndex = Array.IndexOf(args, "--workspace");
var workspacePath = (workspaceArgIndex >= 0 && workspaceArgIndex + 1 < args.Length)
    ? args[workspaceArgIndex + 1]
    : GetRequiredSetting("AgentSettings:Workspace");

var maxStepsRaw = GetRequiredSetting("AgentSettings:MaxSteps");
if (!int.TryParse(maxStepsRaw, out var maxSteps))
    throw new InvalidOperationException("Valore non valido per 'AgentSettings:MaxSteps': deve essere un intero.");

// --port <n>: porta del server WebSocket in modalità --serve. Se assente, ricade su
// ServeSettings:Port in appsettings.json, poi su 8765. Ignorato in REPL/--stdin-protocol.
var portArgIndex = Array.IndexOf(args, "--port");
var servePort = (portArgIndex >= 0 && portArgIndex + 1 < args.Length && int.TryParse(args[portArgIndex + 1], out var portArg))
    ? portArg
    : (int.TryParse(config["ServeSettings:Port"], out var portCfg) ? portCfg : 8765);

var fullWorkspacePath = Path.GetFullPath(workspacePath);
if (!Directory.Exists(fullWorkspacePath))
    Directory.CreateDirectory(fullWorkspacePath);

var braveApiKey = config["WebSearchSettings:BraveApiKey"];
var maxContextTokens = int.TryParse(config["AgentSettings:MaxContextTokens"], out var mct) ? mct : 6000;

// Parametri di campionamento e soglie anti-loop, tutti opzionali: se assenti restano i
// valori storici (temperature 0, frequency_penalty 0.3, nessun altro parametro inviato).
// InvariantCulture obbligatoria: con cultura it-IT "0.4" verrebbe letto come 4.
float? GetFloatSetting(string key) =>
    float.TryParse(config[key], System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
int? GetIntSetting(string key) => int.TryParse(config[key], out var v) ? v : null;

var temperature      = GetFloatSetting("LlmSettings:Temperature") ?? 0.0f;
var frequencyPenalty = GetFloatSetting("LlmSettings:FrequencyPenalty") ?? 0.3f;
var presencePenalty  = GetFloatSetting("LlmSettings:PresencePenalty");
var topP             = GetFloatSetting("LlmSettings:TopP");
var topK             = GetIntSetting("LlmSettings:TopK");
var minP             = GetFloatSetting("LlmSettings:MinP");
var repeatPenalty    = GetFloatSetting("LlmSettings:RepeatPenalty");
var repeatLastN      = GetIntSetting("LlmSettings:RepeatLastN");
var maxRepeats             = GetIntSetting("AgentSettings:MaxRepeats") ?? 3;
var maxConsecutiveReadOnly = GetIntSetting("AgentSettings:MaxConsecutiveReadOnly") ?? 6;
var maxLinesPerFile        = GetIntSetting("AgentSettings:MaxLinesPerFile") ?? 400;

UI.Dim($"Sampling: temperature={temperature} frequency_penalty={frequencyPenalty} " +
       $"presence_penalty={presencePenalty?.ToString() ?? "-"} top_p={topP?.ToString() ?? "-"} " +
       $"top_k={topK?.ToString() ?? "-"} min_p={minP?.ToString() ?? "-"} " +
       $"repeat_penalty={repeatPenalty?.ToString() ?? "-"} repeat_last_n={repeatLastN?.ToString() ?? "-"} | " +
       $"anti-loop: MaxRepeats={maxRepeats} MaxConsecutiveReadOnly={maxConsecutiveReadOnly} MaxLinesPerFile={maxLinesPerFile}");

var workspace = new WorkspaceContext(fullWorkspacePath);
var llm = new LlamaClient(serverUrl, model);

// Auto-approvazione: quando attiva, write_file/edit_file non richiedono conferma
// (bypassano il flusso edit_proposal/edit_decision). In modalità REPL l'utente può
// attivarla/disattivarla a runtime tramite i comandi /auto-approve e /no-auto-approve.
// In modalità --stdin-protocol l'extension può inviare un flag autoApprove per turno
// (es. toggle in chat o setting persistente). Default: false (conferma richiesta).
// Persistenza: lo stato viene salvato in .pm-agent.json nella root del workspace,
// così il toggle REPL sopravvive al riavvio del processo. In stdin-protocol il flag
// inviato dall'extension ha sempre la precedenza (è la fonte di verità per quel turno).
var autoApproveStateFile = Path.Combine(fullWorkspacePath, ".pm-agent.json");

bool LoadAutoApproveState()
{
    try
    {
        if (File.Exists(autoApproveStateFile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(autoApproveStateFile));
            if (doc.RootElement.TryGetProperty("autoApprove", out var v) && v.ValueKind == JsonValueKind.True)
                return true;
        }
    }
    catch { /* file corrotto o non leggibile: default false */ }
    return false;
}

void SaveAutoApproveState(bool value)
{
    try
    {
        string existing = "{}";
        if (File.Exists(autoApproveStateFile))
            existing = File.ReadAllText(autoApproveStateFile);
        using var doc = JsonDocument.Parse(existing);
        var root = doc.RootElement;
        var props = new Dictionary<string, string>();
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
                props[prop.Name] = prop.Value.GetRawText();
        }
        props["autoApprove"] = value ? "true" : "false";
        var json = "{" + string.Join(",", props.Select(p => $"\"{p.Key}\":{p.Value}")) + "}";
        File.WriteAllText(autoApproveStateFile, json);
    }
    catch { /* non bloccare il flusso su errori di persistenza */ }
}

var autoApprove = LoadAutoApproveState();

// In modalità --stdin-protocol, write_file/edit_file non scrivono mai su disco
// direttamente: emettono edit_proposal e bloccano in attesa dell'edit_decision
// che arriva come riga successiva su stdin (protocollo sincrono per turno — vedi
// RunStdinProtocolAsync più sotto, l'unico altro consumer di stdin in quel momento
// è fermo in attesa della fine di questo turno).
Func<string, string, bool>? proposeWrite = null;
if (isStdinProtocol)
{
    proposeWrite = (path, content) =>
    {
        if (autoApprove)
        {
            // Auto-approvazione attiva: applica subito senza emettere edit_proposal
            // né attendere edit_decision. L'utente ha esplicitamente scelto questa
            // modalità (tramite /auto-approve in REPL o flag autoApprove in stdin-protocol),
            // quindi il consenso è già dato.
            return true;
        }

        EmitEvent(new { type = "edit_proposal", path, content });

        // Difesa in profondità: se il lato extension ha un bug e non invia MAI
        // l'edit_decision (osservato: un'eccezione non gestita nel comando "Applica"
        // lasciava il processo bloccato per sempre), non restare in attesa all'infinito.
        // Console.In.ReadLine() è sincrono: lo eseguiamo su un task e gli diamo un tetto
        // massimo di attesa, dopo il quale trattiamo la proposta come rifiutata e il
        // turno riprende comunque. Se la riga arriva più tardi (click ritardato
        // dell'utente), verrà semplicemente ignorata dal loop principale (che scarta
        // ogni riga con type diverso da "request").
        var readTask = Task.Run(() => Console.In.ReadLine());
        if (!readTask.Wait(TimeSpan.FromMinutes(5)))
        {
            Console.Error.WriteLine($"[stdin-protocol] Timeout in attesa di edit_decision per '{path}' — trattata come rifiutata.");
            return false;
        }

        var decisionLine = readTask.Result;
        if (decisionLine == null) return false;
        try
        {
            using var d = JsonDocument.Parse(decisionLine);
            var r = d.RootElement;
            if (r.TryGetProperty("type", out var ty) && ty.GetString() == "edit_decision"
             && r.TryGetProperty("accepted", out var acc))
                return acc.GetBoolean();
        }
        catch { /* riga malformata: tratta come rifiuto, non bloccare il turno */ }
        return false;
    };
}

var dispatcher = new ToolDispatcher(workspace, braveApiKey, proposeWrite);
if (dispatcher.ReapOrphanJobs() is { } reaped)
{
    if (isStdinProtocol) Trace($"  !! {reaped}");
    else UI.Dim($"  [{reaped}]");
}

// Sotto-agente "explore": dispatcher proprio (vedi ExploreSubAgent) e stessi parametri di campionamento.
var exploreAgent = new ExploreSubAgent(llm, new ToolDispatcher(workspace, braveApiKey), workspace, r =>
{
    r.Temperature = temperature;
    r.FrequencyPenalty = frequencyPenalty;
    r.PresencePenalty = presencePenalty;
    r.TopP = topP;
    r.TopK = topK;
    r.MinP = minP;
    r.RepeatPenalty = repeatPenalty;
    r.RepeatLastN = repeatLastN;
});

if (!isMachineMode)
{
    // ── Banner ───────────────────────────────────────────────────────────────
    try { Console.Clear(); } catch { }
    UI.Header("LOCAL CODE AGENT - PM SOFTWARE", "Powered by PM LLM");
    Console.WriteLine();
}

// ── Health check ─────────────────────────────────────────────────────────────
UI.Info("Connessione al server LLM...");
if (!await llm.HealthCheckAsync())
{
    UI.Error($"Server non raggiungibile: {serverUrl}");
    UI.Dim("Avvia llama-server.exe prima di eseguire l'agente.");
    return;
}
UI.Ok($"Server online — modello: {model}");
UI.Dim($"Workspace: {workspace.Root}");
if (!isMachineMode) Console.WriteLine();

// ── System prompt ─────────────────────────────────────────────────────────────
string BuildSystemPrompt() => SystemPromptBuilder.Build(workspace, dispatcher);

// Default 40000 anche se MaxContextTokens è molto più alto: è la soglia di qualità del modello
// locale, non la capienza del server (vedi SessionManager.HardLimit).
var rolloverTokens = GetIntSetting("AgentSettings:RolloverTokens") ?? 40000;
var sessionManager = new SessionManager(llm, maxContextTokens, rolloverTokens, BuildSystemPrompt);

var history = new List<ChatMessage>();

// Solo per l'evento session_start del trace strutturato: nessuna versione esplicita in
// pm-code.csproj, quindi è la versione di assembly di default assegnata dal SDK .NET.
var coreVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

void ResetHistory()
{
    history = [ChatMessage.System(BuildSystemPrompt())];
    // ResetHistory() è chiamata solo quando inizia davvero una NUOVA conversazione (avvio
    // processo, /reset, /cd): ogni chiamata apre una nuova sessione nel trace strutturato.
    StructuredTrace.StartSession(workspace.Root, llm.Model, llm.EndpointOverride ?? serverUrl, coreVersion);
}
ResetHistory();

// ── Rescue tool call dal testo ────────────────────────────────────────────────
// I modelli locali a volte includono la chiamata al tool come JSON nel testo
// invece di eseguirla via API. Cerca pattern {"name":"...","arguments":{...}} o
// block ```json { "name": "write_file", ... }``` e li estrae come coppie (name, argsJson).
List<(string Name, string ArgsJson)> TryRescueTextToolCalls(string text)
{
    var found = new List<(string, string)>();
    try
    {
        // Cerca tutti i blocchi JSON nel testo che hanno la chiave "name" e "arguments"
        int i = 0;
        while (i < text.Length)
        {
            int start = text.IndexOf('{', i);
            if (start < 0) break;

            // Trova la fine bilanciata del blocco JSON
            int depth = 0;
            int end = start;
            while (end < text.Length)
            {
                if (text[end] == '{') depth++;
                else if (text[end] == '}') { depth--; if (depth == 0) break; }
                end++;
            }
            if (depth != 0) { i = start + 1; continue; }

            var candidate = text[start..(end + 1)];
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(candidate);
                var root = doc.RootElement;

                // Pattern 1: { "name": "write_file", "arguments": { ... } }
                if (root.TryGetProperty("name", out var nameProp) &&
                    root.TryGetProperty("arguments", out var argsProp))
                {
                    var name = nameProp.GetString() ?? "";
                    var argsJson = argsProp.GetRawText();
                    if (!string.IsNullOrEmpty(name))
                        found.Add((name, argsJson));
                }
                // Pattern 2: { "path": "...", "content": "..." }  (write_file senza wrapper)
                else if (root.TryGetProperty("path", out _) &&
                         root.TryGetProperty("content", out _))
                {
                    found.Add(("write_file", candidate));
                }
            }
            catch { /* JSON non valido, ignora */ }

            i = end + 1;
        }
    }
    catch { }
    return found;
}

// Comandi che run_command esegue come vera AZIONE (build, test, esecuzione, rete, modifica
// di file/processi/git). Tutto il resto è considerato lettura dal guard anti-rimuginio.
var actionCommandRegex = new Regex(
    @"\b(dotnet\s+(build|test|run|ef|publish|add|restore|new|format)|npm|npx|yarn|pnpm|ng\s|pytest|python|py\s|node\s|tsc|" +
    @"cargo|go\s+(build|test|run)|mvn|gradle|make|msbuild|" +
    @"git\s+(add|commit|checkout|switch|restore|stash|reset|merge|rebase|apply|rm|mv)|" +
    @"Invoke-WebRequest|Invoke-RestMethod|iwr|irm|curl|wget|" +
    @"Stop-Process|Start-Process|kill|taskkill|Remove-Item|Move-Item|Rename-Item|New-Item|Set-Content|Add-Content|mkdir|rm\s|mv\s|cp\s)\b" +
    @"|\.(ps1|sh|bat|cmd|exe)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

// Comandi che ESEGUONO il programma o i suoi test (verifica funzionale, non solo build).
var functionalCheckRegex = new Regex(
    @"\b(dotnet\s+(test|run)|npm\s+(test|run|start)|npx|yarn|pnpm|pytest|python|py\s|node\s|go\s+(test|run)|" +
    @"cargo\s+(test|run)|mvn|gradle|Invoke-WebRequest|Invoke-RestMethod|iwr|irm|curl|wget|Start-Process)\b|\.(ps1|sh|bat|cmd|exe)\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

// Comandi che compilano, installano o analizzano senza eseguire il programma: "npx ng build" e
// "npm run build" passavano come verifica funzionale (npx e "npm run" in generale lo sono) e il
// piano si chiudeva dopo la sola build. Valutati per segmento: "npm run build; npm test" resta funzionale.
var buildOnlyRegex = new Regex(
    @"\b(ng\s+(build|lint)|(npm|pnpm|yarn)\s+(run\s+)?(build|lint|install|ci|add|i)|tsc|vite\s+build|webpack|eslint|prettier|" +
    @"dotnet\s+(build|restore)|mvn\s+(compile|package|install)|gradle\s+(build|assemble))\b",
    RegexOptions.IgnoreCase | RegexOptions.Compiled);

bool IsFunctionalCheck(string cmd) =>
    Regex.Split(cmd, @"[;\n]|&&|\|\|").Any(seg => functionalCheckRegex.IsMatch(seg) && !buildOnlyRegex.IsMatch(seg));

// ── Agent loop ────────────────────────────────────────────────────────────────
// Restituisce true se il LLM ha risposto (anche senza tool call), false su errore HTTP.
// Riusa lo STESSO loop (retry LLM, anti-loop, riflessione interna, rollover history) sia in
// modalità REPL sia in modalità --stdin-protocol: tutto il testo/tool-call/errore/notice che il
// loop produce passa SOLO da `sink` (IAgentSink, catturato dalla chiusura — vedi `consoleSink`
// più sopra), che riproduce esattamente l'output di prima per ciascuna modalità (vedi
// ConsoleSink.cs). onStatus resta un parametro separato (heartbeat di avanzamento turno: non fa
// parte degli 8 eventi dell'interfaccia). `ct` è il CancellationToken del turno corrente: oggi
// non annulla nulla (nessun trigger collegato, il Ctrl+C del REPL resta quello di sempre — kill
// di processo), è plumbing per un futuro comando "cancel" lato UI/estensione.
async Task<bool> RunAgentLoopAsync(
    int maxTokens = 32768,
    Action<string>? onStatus = null,
    CancellationToken ct = default)
{
    // Un turno = una chiamata a questa funzione (vedi commento su StructuredTrace.BeginTurn()
    // più sotto): id breve per IAgentSink, non persistito altrove.
    var turnId = Guid.NewGuid().ToString("N")[..8];
    sink.TurnStarted(turnId, workspace.Root);

    // Guard anti-loop: blocca chiamate identiche (stesso tool + stessi argomenti)
    // ripetute troppe volte — i modelli locali a volte ignorano il soft-block testuale
    // e ricontinuano a richiamare lo stesso tool all'infinito fino a maxSteps.
    // Il contatore conta le chiamate identiche CHE DANNO ANCHE LO STESSO RISULTATO: una
    // rilettura che restituisce contenuto diverso (es. file modificato nel frattempo) non è
    // un loop. Viene inoltre azzerato interamente dopo ogni edit/write riuscito (TrackBuildState).
    // Soglia: AgentSettings:MaxRepeats (default 3).
    var repeatCounts = new Dictionary<string, (int Count, int ResultHash)>();
    // Chiamate uguali a meno dei numeri (vedi ExecuteToolGuarded): avviso dalla 3ª, blocco oltre maxSimilarCalls.
    var similarCounts = new Dictionary<string, int>();
    const int maxSimilarCalls = 5;
    int lastErrorHash = 0;
    int sameErrorStreak = 0;
    string? lastErrorTool = null;
    bool loopDetected = false;

    // Tool di polling: rileggere lo stesso PID con gli stessi argomenti è il modo corretto
    // di attendere che un processo in background finisca di avviarsi, non un loop anomalo.
    var pollableTools = new HashSet<string> { "get_background_output", "list_background_jobs" };

    // Tool di verifica (build/test/lint): rilanciare lo STESSO comando dopo ogni fix è la
    // normale sequenza "modifica -> riverifica", non un loop — il guard anti-loop però non
    // lo sa, perché conta solo tool+argomenti identici, e questi comandi hanno sempre gli
    // stessi argomenti (es. "dotnet build") indipendentemente dal fatto che il codice sia
    // cambiato. Se nel frattempo c'è stato almeno un edit_file/write_file riuscito, il
    // conteggio per QUESTI tool riparte da zero invece di sommarsi a tentativi precedenti
    // che si riferivano a uno stato del codice diverso.
    var verificationTools = new HashSet<string> { "run_command", "run_dotnet", "sol_analyze" };
    bool successfulEditSinceLastVerify = false;

    // Guard anti-rimuginio: il blocco sopra ferma solo le chiamate IDENTICHE, ma un modello
    // può girare a vuoto per decine di step limitandosi a ispezionare (read_file, glob_files,
    // search_in_files, ecc. con argomenti sempre leggermente diversi) senza mai eseguire
    // un'azione concreta (edit_file/write_file/run_command/...). Contiamo gli step di sola
    // lettura CONSECUTIVI e, oltre soglia, iniettiamo un ordine esplicito di agire o fermarsi
    // — invece del generico "valuta se continuare" che non forza nessuna decisione.
    var readOnlyTools = new HashSet<string>
    {
        "read_file", "read_file_range", "glob_files", "search_in_files", "sym_search",
        "list_directory", "get_workspace_tree", "get_directory_details",
        "git_status", "git_diff", "git_log", "get_background_output", "list_background_jobs",
        ExploreSubAgent.ToolName,
    };
    // Soglia: AgentSettings:MaxConsecutiveReadOnly (default 6).
    int consecutiveReadOnly = 0;

    // Escalation del guard anti-rimuginio. L'ordine testuale "agisci o fermati" da solo non
    // basta (osservato: il modello lo ignora e continua a leggere fino a maxSteps). Se dopo
    // l'ordine arriva un'altra lettura, si entra in modalità "solo azione": le letture vengono
    // rifiutate senza essere eseguite e i tool di sola lettura spariscono dalla richiesta, finché
    // il modello non fa un'azione concreta. Dopo maxReadBlocks rifiuti il turno si ferma.
    bool readOnlyNudgeGiven = false;
    bool restrictReadOnly = false;
    int readBlocks = 0;
    const int maxReadBlocks = 3;
    const string readBlockedPrefix = "⚠️ LETTURA BLOCCATA";
    const string verifyMissingPrefix = "⚠️ VERIFICA MANCANTE";
    string? stopReason = null;

    // manage_todo non è né lettura né azione: aggiornare il piano non deve azzerare il conteggio
    // delle letture (altrimenti alternare read_file e manage_todo aggirerebbe il guard).
    bool planTouchedThisTurn = false;
    int planContinueRetries = 0;
    const int maxPlanContinueRetries = 2;

    // Processi in background lasciati accesi a fine turno: un'API avviata per il test finale
    // restava viva, teneva porta ed exe bloccati e nella sessione successiva l'agente non poteva
    // più fermarla. Un solo promemoria per turno: se servono all'utente, li dichiara nella risposta.
    bool jobsReminderSent = false;

    // Validazione funzionale: la build da sola non dimostra che il comportamento sia corretto
    // (osservato: import PDF riscritto, build verde, mai eseguito → l'utente doveva testare a mano).
    // Dopo l'ultima modifica al codice deve esserci stata un'esecuzione reale (test, programma,
    // chiamata HTTP, script) prima di chiudere il piano o dichiarare concluso un task multi-step.
    bool codeEditedThisTurn = false;
    bool functionalCheckSinceEdit = false;
    int functionalCheckRetries = 0;
    const int maxFunctionalCheckRetries = 2;
    // Il rifiuto di "clear" per test mancante vale al massimo 2 volte per turno: osservato il
    // modello che, non riuscendo a eseguire l'app (console interattiva che non termina), la
    // rilanciava a ogni rifiuto lasciando processi orfani, senza mai fermarsi a chiedere.
    int functionalClearRejects = 0;
    const int maxFunctionalClearRejects = 2;
    const string functionalMissingPrefix = "⚠️ VERIFICA FUNZIONALE MANCANTE";

    // Dopo un rifiuto per verifica mancante, altre modifiche sono bloccate finché il modello non
    // tenta una build/test: altrimenti accumulava 5 edit e verificava tutto insieme alla fine.
    bool verifyRequired = false;

    // Righe lette per file in questo turno (read_file/read_file_range con range). Le letture
    // a blocchi di 200-300 righe sono tutte diverse tra loro, quindi sfuggono al guard sulle
    // chiamate identiche: osservato un file di 1676 righe letto quasi per intero a pezzi prima
    // di fare qualunque modifica. Il conteggio di un file riparte dopo un edit su quel file.
    var linesReadPerFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    string? FullPathOf(string argsJson)
    {
        try
        {
            var el = JsonDocument.Parse(argsJson).RootElement;
            var p = el.TryGetProperty("path", out var pv) ? pv.GetString() : null;
            return string.IsNullOrEmpty(p) ? null : Path.GetFullPath(Path.Combine(workspace.Root, p));
        }
        catch { return null; }
    }

    // Restituisce il messaggio di rifiuto se la lettura supera il budget del file, altrimenti null.
    string? CheckLineBudget(string name, string argsJson)
    {
        if (name is not ("read_file" or "read_file_range")) return null;
        try
        {
            var el = JsonDocument.Parse(argsJson).RootElement;
            if (!el.TryGetProperty("start_line", out var s) || !el.TryGetProperty("end_line", out var e)
                || s.ValueKind != JsonValueKind.Number || e.ValueKind != JsonValueKind.Number)
                return null; // senza range il tool stesso limita i file grandi a un'anteprima
            var requested = Math.Max(0, e.GetInt32() - s.GetInt32() + 1);
            var full = FullPathOf(argsJson);
            if (full == null) return null;

            linesReadPerFile.TryGetValue(full, out var already);
            if (already > 0 && already + requested > maxLinesPerFile)
                return $"{readBlockedPrefix}: in questo turno hai già letto {already} righe di {Path.GetFileName(full)} " +
                       $"(limite {maxLinesPerFile} per file). Non leggere il file a pezzi per intero: usa search_in_files o " +
                       "sym_search per trovare il punto preciso che serve al passo corrente e leggi solo quelle poche righe, " +
                       "oppure procedi con la modifica in base a quello che hai già letto.";
            linesReadPerFile[full] = already + requested;
        }
        catch { }
        return null;
    }

    bool IsReadOnlyCall(string name, string argsJson)
    {
        if (readOnlyTools.Contains(name)) return true;
        if (name != "run_command") return false;
        try
        {
            var el = JsonDocument.Parse(argsJson).RootElement;
            if (el.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.True) return false;
            var cmd = el.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            // Un run_command conta come AZIONE solo se builda/testa/esegue/modifica qualcosa;
            // "git diff | Select-Object", "Get-Content", "Select-String", "Copy-Item di un diff"
            // sono letture travestite e prima azzeravano il conteggio delle letture consecutive.
            return !actionCommandRegex.IsMatch(cmd);
        }
        catch { return false; }
    }

    // Gate di build: il system prompt chiede di verificare le modifiche prima di dichiararle
    // riuscite, ma è solo un'istruzione testuale in mezzo a un prompt lungo — il modello può
    // semplicemente non rispettarla e dichiarare il task concluso (o "la build è passata")
    // senza aver mai chiamato un tool di verifica. Qui si traccia deterministicamente lo
    // stato (non affidato al prompt), con due livelli:
    //  - hasUnverifiedDotnetEdit (solo .cs): sappiamo ESATTAMENTE come verificarlo
    //    (sol_analyze == dotnet build), quindi lo eseguiamo noi stessi in automatico.
    //  - hasUnverifiedOtherEdit (qualunque altro file di codice: .py, .ts, .svelte, ecc.):
    //    non conosciamo il comando di build/lint/test giusto per uno stack arbitrario, quindi
    //    non possiamo auto-eseguirlo — ci limitiamo a IMPEDIRE la chiusura del turno finché il
    //    modello non ha davvero chiamato run_command almeno una volta dopo la modifica.
    bool hasUnverifiedDotnetEdit = false;
    bool hasUnverifiedOtherEdit = false;
    var autoBuildChecks = 0;
    const int maxAutoBuildChecks = 3;

    // Guard per errori "recuperabili" (tool call troppo grande, JSON non valido per
    // virgolette/backtick non escapati): il messaggio correttivo viene iniettato in history
    // e va rimandato SUBITO al modello, senza aspettare che l'utente riscriva qualcosa —
    // altrimenti il turno si chiude con solo il testo parziale generato prima dell'errore
    // (es. "Ora riscrivo il componente...") e l'utente lo scambia per la risposta finale.
    // Cap per non girare all'infinito se il modello continua a sbagliare nello stesso modo.
    var recoverableErrorRetries = 0;
    const int maxRecoverableErrorRetries = 3;

    // Guard anti-annuncio: il system prompt vieta di scrivere "Ora faccio X" senza poi
    // chiamare davvero il tool nella stessa risposta (vedi regola CRITICO più sopra), ma un
    // modello quantizzato non la rispetta sempre — osservato in pratica: dopo molti step di
    // sola lettura, la risposta finale è un annuncio testuale ("Ho tutto il necessario.
    // Procedo con le modifiche:") SENZA alcuna tool call. Per l'utente è indistinguibile da
    // una risposta finale legittima: il turno si chiude lì e serve scrivere di nuovo per farlo
    // proseguire. Rilevalo euristicamente e forza un altro giro da soli, invece di aspettare
    // l'utente. Cap basso perché è un'euristica testuale (falsi positivi possibili su una
    // risposta finale legittima che finisce per caso con ':') — oltre il cap ci si arrende e
    // si lascia la risposta così com'è.
    var announcementRetries = 0;
    const int maxAnnouncementRetries = 2;

    bool LooksLikeUnfinishedAnnouncement(string text)
    {
        var t = text.TrimEnd();
        if (t.Length == 0) return false;
        if (t.Contains('?')) return false; // probabile domanda genuina all'utente, non un annuncio
        if (t.Contains("Limiti noti", StringComparison.OrdinalIgnoreCase)) return false; // riepilogo finale vero

        // Segnale più forte, indipendente dalla lunghezza del messaggio: la risposta finisce
        // con ':' — non c'è modo che sia una risposta finale completa, per costruzione qualcosa
        // doveva seguire (codice, un elenco, un tool). Un turno lungo (molti step di analisi
        // prima dell'annuncio finale) supera facilmente qualunque soglia di lunghezza fissa,
        // quindi questo controllo NON va limitato ai messaggi brevi.
        if (t.EndsWith(':')) return true;

        // Le frasi ("procedo con", "ora scrivo", ecc.) invece si cercano solo nella CODA del
        // messaggio — non nell'intero testo, che su un turno lungo può contenere centinaia di
        // righe di analisi legittima dove per puro caso compare una di queste parole: è l'ultima
        // frase a decidere se il turno si è davvero chiuso con un'azione o con un annuncio.
        var tail = t.Length > 300 ? t[^300..] : t;
        return Regex.IsMatch(tail,
            @"\b(procedo (con|a)|vado avanti( con)?|continuo con|nel prossimo (step|passo))\b" +
            @"|\b(ora|adesso)\b[^.!?]{0,40}\b(riscrivo|scrivo|modifico|creo|leggo|eseguo|controllo|verifico|aggiorno|implemento|correggo|analizzo|genero)\b",
            RegexOptions.IgnoreCase);
    }

    // I messaggi di errore/rinuncia finivano SOLO nei log dell'agente (UI.Error scrive
    // su stderr — vedi commento sulla classe UI) e mai in chat: in modalità --stdin-protocol
    // il processo terminava il turno "in silenzio", senza alcun evento token per l'utente —
    // la chat sembrava fermarsi nel nulla dopo le ultime tool call, indistinguibile da un
    // crash. Usa questo per rendere visibile in chat qualunque uscita anticipata dal loop
    // (retry esauriti, limite di step raggiunto).
    // NOTA: non passa da sink.AssistantDelta perché in REPL la formattazione è diversa (testo
    // semplice su stdout, senza il prefisso "Agent › " e circondato da righe vuote) — vedi il
    // limite documentato nel report finale di questa modifica.
    void EmitFinalNotice(string text)
    {
        if (isMachineMode) Trace($"  !! {OneLine(text, 200)}");
        history.Add(new ChatMessage { Role = "assistant", Content = text });
        if (isMachineMode) sink.AssistantDelta(turnId, text);
        else { Console.WriteLine(); Console.Write(text); Console.WriteLine(); }
    }

    var codeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".js", ".jsx", ".ts", ".tsx", ".svelte", ".vue", ".go", ".java",
        ".rb", ".php", ".c", ".cpp", ".h", ".hpp", ".rs", ".kt", ".swift",
        ".html", ".scss", ".css", ".less"
    };

    void TrackBuildState(string toolName, string argsJson, string result)
    {
        if (toolName is "write_file" or "edit_file")
        {
            // Un edit rifiutato (limite di dimensione, old_string non univoco, ecc. — vedi
            // isError più sotto) non ha toccato il file: non deve far ripartire il conteggio
            // anti-loop dei tool di verifica, altrimenti un modello che continua a proporre
            // edit rifiutati "resetterebbe" il guard senza aver mai cambiato nulla sul disco.
            if (result.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var el = JsonDocument.Parse(argsJson).RootElement;
                var path = el.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (path == null) return;
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    hasUnverifiedDotnetEdit = true;
                else if (codeExtensions.Contains(Path.GetExtension(path)))
                    hasUnverifiedOtherEdit = true;
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || codeExtensions.Contains(Path.GetExtension(path)))
                {
                    codeEditedThisTurn = true;
                    functionalCheckSinceEdit = false;
                }
                successfulEditSinceLastVerify = true;
                // Il disco è cambiato: qualunque rilettura/verifica successiva si riferisce a
                // uno stato diverso, quindi i conteggi anti-loop precedenti non valgono più.
                repeatCounts.Clear();
                // Solo un file di codice: scrivere un nuovo file di input per il test (test_input5.txt)
                // fa parte del ciclo di ripetizioni, non è una correzione.
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || codeExtensions.Contains(Path.GetExtension(path)))
                    similarCounts.Clear();
                if (FullPathOf(argsJson) is { } editedFull) linesReadPerFile.Remove(editedFull);
            }
            catch { }
        }
        else if (toolName == "sol_analyze")
        {
            hasUnverifiedDotnetEdit = DotnetBuildFailed(result) && (codeEditedThisTurn || hasUnverifiedDotnetEdit);
            verifyRequired = false;
        }
        else if (toolName == "run_dotnet")
        {
            try
            {
                var el = JsonDocument.Parse(argsJson).RootElement;
                var a = el.TryGetProperty("args", out var av) ? av.GetString() ?? "" : "";
                if (Has(a, "build", "test", "run"))
                {
                    hasUnverifiedDotnetEdit = DotnetBuildFailed(result) && (codeEditedThisTurn || hasUnverifiedDotnetEdit);
                    verifyRequired = false;
                }
                if (Has(a, "test", "run")) functionalCheckSinceEdit = true;
            }
            catch { }
        }
        else if (toolName == "run_command")
        {
            // Non possiamo sapere a priori quale sia il comando di build/lint/test corretto
            // per uno stack non-.NET (npm run build / pytest / eslint / ...): ci basiamo sul
            // fatto che il modello abbia REALMENTE chiamato un tool per verificare, invece di
            // dichiarare a parole un esito mai controllato. Conta solo un comando che esegue
            // davvero qualcosa (build/test/run): un "Get-Content" o un comando rifiutato no.
            if (!result.StartsWith("ERRORE", StringComparison.Ordinal) && !IsReadOnlyCall(toolName, argsJson))
            {
                hasUnverifiedOtherEdit = false;
                verifyRequired = false;
                // Un'esecuzione reale (non l'avvio in background, che va poi interrogato con una
                // chiamata vera) conta come verifica funzionale.
                try
                {
                    var el = JsonDocument.Parse(argsJson).RootElement;
                    var isBackground = el.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.True;
                    var cmd = el.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
                    if (!isBackground && IsFunctionalCheck(cmd)) functionalCheckSinceEdit = true;
                }
                catch { }
            }
        }
    }

    // Un fallimento dovuto SOLO al file di output bloccato (MSB3021-3027: exe in uso, es. l'API
    // già avviata da VS) non è un errore di codice: la compilazione è riuscita, fallisce solo la
    // copia finale. Prima veniva trattato come "codice non verificato" e bloccava manage_todo.
    static bool DotnetBuildFailed(string result) =>
        Regex.IsMatch(result, @"error CS\d+") ||
        (result.Contains("FALLITO") && !Regex.IsMatch(result, @"MSB302[1-7]"));

    bool Has(string s, params string[] kw) =>
        kw.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

    string ExecuteToolGuarded(string name, string argsJson)
    {
        // In --serve nessuna scrittura né comando shell viene eseguita: niente write/edit/delete
        // su disco, niente processi avviati, a prescindere da autoApprove (che qui non esiste
        // nemmeno — proposeWrite resta null in questa modalità, vedi sopra). Le approvazioni
        // (conferma esplicita via il protocollo, write come diff) sono una fase successiva: per
        // ora il modello riceve sempre un rifiuto esplicito, mai un'esecuzione silenziosa.
        if (isServeMode && serveBlockedTools.Contains(name))
            return "ERRORE: scrittura non disponibile in modalità serve";

        if (pollableTools.Contains(name))
            return dispatcher.Execute(name, argsJson, ct);

        var key = $"{name}|{argsJson}";

        if (verificationTools.Contains(name) && successfulEditSinceLastVerify)
        {
            repeatCounts.Remove(key);
            successfulEditSinceLastVerify = false;
        }

        // Blocca solo se questa stessa chiamata ha già prodotto maxRepeats volte lo STESSO
        // risultato: il modello non ha nuove informazioni e ripeterla non può servire.
        if (repeatCounts.TryGetValue(key, out var state) && state.Count >= maxRepeats)
        {
            loopDetected = true;
            return $"[LOOP-BLOCK] Tool '{name}' chiamato {state.Count + 1} volte con argomenti identici " +
                   "e risultato identico. Interrotto per evitare un loop infinito.";
        }

        // Chiamate QUASI identiche: stessi argomenti a meno dei numeri. Osservato: lo stesso test
        // rilanciato 14 volte cambiando solo "test_tool5.txt" → "test_tool6.txt"…, sempre con
        // [ESITO: SUCCESSO] e il file creato nella cartella sbagliata. Né il guard sopra (argomenti
        // diversi) né quello sugli errori (nessun errore) lo vedevano. Azzerato dopo ogni edit
        // riuscito: rilanciare il test dopo una correzione è legittimo.
        // Solo i tool che ESEGUONO: delete_file/read_file su file numerati diversi (test_tool5, 6, 7…)
        // sono operazioni distinte, non un loop (osservato: pulizia di 10 file bloccata al 6°).
        string? similarWarning = null;
        if (name is "run_command" or "run_dotnet")
        {
            var similarKey = $"{name}|{Regex.Replace(argsJson, @"\d+", "#")}";
            var similar = similarCounts.TryGetValue(similarKey, out var sc) ? sc + 1 : 1;
            similarCounts[similarKey] = similar;
            if (similar > maxSimilarCalls)
            {
                loopDetected = true;
                stopReason = $"⚠️ Mi fermo: ho rilanciato {similar} volte lo stesso comando '{name}' cambiando solo dei numeri, " +
                             "senza modificare il codice né capire il risultato. Dimmi come proseguire o indicami cosa controllare.";
                return $"[LOOP-BLOCK] '{name}' rilanciato {similar} volte quasi identico (cambiano solo i numeri) senza " +
                       "modifiche al codice nel frattempo. Interrotto.";
            }
            if (similar >= 3)
                similarWarning = $"\n\n[RIPETIZIONE QUASI IDENTICA {similar}/{maxSimilarCalls}] Hai eseguito questa chiamata {similar} volte " +
                                 "cambiando solo numeri o nomi. Rilanciarla non darà informazioni nuove. Verifica l'ESITO REALE " +
                                 "dell'ultima esecuzione (es. il file è stato creato DOVE ti aspettavi? Test-Path sul percorso " +
                                 "atteso, contenuto, messaggi d'errore nell'output) e, se non coincide, cerca la causa nel codice " +
                                 "e correggila. Se non sai come proseguire, fermati e spiega all'utente cosa hai osservato.";
        }

        if (name == "manage_todo")
        {
            planTouchedThisTurn = true;
            // Creare il piano è la risposta richiesta dall'ordine "agisci o spezza il task":
            // riparte il budget di letture, altrimenti la prima lettura per il passo 1 veniva
            // rifiutata subito dopo la creazione del piano. Solo create (non update), così
            // alternare letture e aggiornamenti del piano non aggira il guard.
            if (Regex.IsMatch(argsJson, @"""action""\s*:\s*""create""", RegexOptions.IgnoreCase))
            {
                consecutiveReadOnly = 0;
                readOnlyNudgeGiven = false;
                restrictReadOnly = false;
                readBlocks = 0;
            }
            // Un passo non può risultare "completato" su codice mai compilato/testato:
            // osservato un passo "riscrivi ImportService.cs" segnato completed dopo 14 edit
            // senza nessuna build, con il file che non compilava.
            // Anche "clear" con passi aperti: osservato il modello che, rifiutato il completed,
            // cancellava il piano per aggirare il controllo.
            if ((hasUnverifiedDotnetEdit || hasUnverifiedOtherEdit)
                && (Regex.IsMatch(argsJson, @"""status""\s*:\s*""(completed|done)""", RegexOptions.IgnoreCase)
                    || (Regex.IsMatch(argsJson, @"""action""\s*:\s*""clear""", RegexOptions.IgnoreCase) && dispatcher.HasPendingPlan)))
            {
                verifyRequired = true;
                return $"{verifyMissingPrefix}: non puoi segnare un passo come completato né cancellare il piano — hai modificato codice dopo " +
                       "l'ultima verifica. Prima " + (hasUnverifiedDotnetEdit
                           ? "esegui sol_analyze"
                           : "esegui con run_command il comando di build/test del progetto") +
                       ", correggi eventuali errori, poi richiama manage_todo. Il piano NON è stato aggiornato. " +
                       "Fino ad allora altre modifiche (edit_file/write_file) verranno rifiutate.";
            }

            if (codeEditedThisTurn && !functionalCheckSinceEdit
                && Regex.IsMatch(argsJson, @"""action""\s*:\s*""clear""", RegexOptions.IgnoreCase)
                && functionalClearRejects++ < maxFunctionalClearRejects)
            {
                return $"{functionalMissingPrefix}: non puoi chiudere il piano — dopo l'ultima modifica hai solo compilato, " +
                       "non hai mai ESEGUITO il programma. Esegui ora il test funzionale (test automatici, oppure avvia l'app " +
                       "in background e fai la chiamata reale / lo script di test) e confronta l'output con il risultato atteso. " +
                       "Se coincide chiudi il piano; se no correggi e ritesta. Se ti manca davvero qualcosa per testare, " +
                       "chiedilo all'utente con una domanda precisa.";
            }
        }
        else if (name is "project_notes" or "lessons")
        {
            // Neutro come manage_todo: annotare non è né leggere né agire.
        }
        else if (verifyRequired && name is ("edit_file" or "write_file")
                 && (hasUnverifiedDotnetEdit || hasUnverifiedOtherEdit))
        {
            return $"{verifyMissingPrefix}: modifica rifiutata — devi prima verificare quelle già fatte. Esegui " +
                   (hasUnverifiedDotnetEdit ? "sol_analyze" : "il comando di build/test con run_command") +
                   " e, se fallisce, potrai correggere subito dopo.";
        }
        else if (IsReadOnlyCall(name, argsJson))
        {
            if (readOnlyNudgeGiven) restrictReadOnly = true;
            if (restrictReadOnly)
            {
                readBlocks++;
                if (readBlocks >= maxReadBlocks)
                {
                    loopDetected = true;
                    stopReason = "⚠️ Mi fermo: ho continuato ad analizzare il codice senza riuscire a passare a una modifica concreta. " +
                                 "Indicami il prossimo passo preciso da eseguire (file e cosa cambiare), oppure spezza il task in una richiesta più piccola.";
                }
                return $"{readBlockedPrefix}: hai già fatto troppe letture senza agire e hai ignorato l'ordine di passare all'azione " +
                       $"(rifiuto {readBlocks}/{maxReadBlocks}). Questa lettura NON è stata eseguita. Hai già abbastanza informazioni: " +
                       "ora puoi SOLO (1) applicare la modifica del passo corrente con edit_file/write_file, (2) verificare con " +
                       "sol_analyze o un comando di build/test, (3) aggiornare il piano con manage_todo, oppure (4) rispondere " +
                       "SUBITO all'utente: se la richiesta era una domanda/analisi, rispondi con quello che hai già letto " +
                       "(dichiarando cosa non hai potuto vedere); se ti manca una decisione, fai una domanda precisa.";
            }
            if (CheckLineBudget(name, argsJson) is { } overBudget) return overBudget;
            consecutiveReadOnly++;
        }
        else
        {
            // Azione concreta: si riparte da zero con il budget di letture.
            consecutiveReadOnly = 0;
            readOnlyNudgeGiven = false;
            restrictReadOnly = false;
            readBlocks = 0;
        }

        // explore conta come UNA lettura (è in readOnlyTools): le sue letture interne non
        // passano dai guard del turno, che riguardano la history principale.
        var execResult = name == ExploreSubAgent.ToolName
            ? exploreAgent.RunAsync(argsJson, onStatus ?? (s => UI.Dim("  " + s)), ct).GetAwaiter().GetResult()
            : dispatcher.Execute(name, argsJson, ct);
        if (similarWarning != null) execResult += similarWarning;

        var hash = HashCode.Combine(execResult.Length, execResult);
        var count = repeatCounts.TryGetValue(key, out var prev) && prev.ResultHash == hash ? prev.Count + 1 : 1;
        repeatCounts[key] = (count, hash);

        // Stesso ERRORE dallo stesso tool con argomenti DIVERSI: il modello ritocca un dettaglio
        // del comando e riprova (osservato: 5 upload PowerShell di fila, tutti "Cannot access a
        // disposed object"). Il guard sopra non lo vede perché gli argomenti cambiano. Si confronta
        // l'inizio dell'errore (le prime righe), che resta uguale anche se cambia il comando citato.
        var looksLikeError = execResult.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase)
            || execResult.Contains("Exception") || execResult.Contains("[ESITO: FALLITO")
            || Regex.IsMatch(execResult, @"\b(4\d\d|5\d\d)\b.{0,40}(Bad Request|Unauthorized|Not Found|Internal Server Error|validation errors)", RegexOptions.IgnoreCase);
        if (looksLikeError)
        {
            var signature = Regex.Replace(execResult.Length > 400 ? execResult[^400..] : execResult, @"\s+", " ");
            var errHash = HashCode.Combine(name, signature);
            sameErrorStreak = errHash == lastErrorHash ? sameErrorStreak + 1 : 1;
            lastErrorHash = errHash;
            if (sameErrorStreak >= 3)
                execResult += $"\n\n[STESSO ERRORE {sameErrorStreak} VOLTE] '{name}' ha restituito lo stesso errore {sameErrorStreak} volte di fila " +
                              "anche cambiando gli argomenti: ritoccare il comando non funziona. Cambia APPROCCIO: usa uno script/strumento " +
                              "già esistente nel progetto, un metodo diverso (es. altro cmdlet/API), oppure leggi la documentazione dello " +
                              "strumento; se non vedi alternative, fermati e spiega all'utente l'errore.";
        }
        else
        {
            // Successo dopo almeno 2 errori di fila sullo stesso tool: l'agente ha appena
            // scoperto qualcosa che vale la pena ricordare in ogni progetto futuro.
            if (sameErrorStreak >= 2 && name == lastErrorTool)
            {
                execResult += $"\n\n[LEZIONE] Hai superato un errore che si era ripetuto {sameErrorStreak} volte. Se la soluzione è " +
                              "una regola GENERALE (non specifica di questo progetto), salvala ORA con lessons action=\"add\" in una " +
                              "frase: cosa non funzionava e cosa fare invece. Poi prosegui con il task.";
                Notice($"errore ripetuto {sameErrorStreak} volte poi superato: chiedo di salvare la lezione");
            }
            sameErrorStreak = 0;
            lastErrorHash = 0;
        }
        lastErrorTool = name;

        // Avviso già dalla seconda ripetizione identica, prima del blocco: un risultato che
        // il tool considera riuscito (es. script con try/catch che stampa "ERR: 401" ma esce
        // con codice 0 → [ESITO: SUCCESSO]) non attiva nessuna riflessione, e il modello
        // rilanciava lo stesso comando fino al LOOP-BLOCK senza aver mai ricevuto un segnale.
        if (count >= 2)
            execResult += $"\n\n[RIPETIZIONE {count}/{maxRepeats}] Questa chiamata è identica a una precedente e ha dato " +
                          "ESATTAMENTE lo stesso risultato. Ripeterla non cambierà l'esito: se il risultato indica un " +
                          "errore (es. 401, file mancante, servizio non raggiungibile) cambia approccio, oppure fermati e " +
                          "chiedi all'utente come procedere.";

        return execResult;
    }

    // Wrapper additivo di tracing attorno a ExecuteToolGuarded: stesso risultato, stesso
    // comportamento, misura solo durata ed esito. I prefissi di rifiuto (letture bloccate,
    // verifica mancante, loop) sono già stringhe note definite sopra: li si riusa qui per
    // smistare l'evento come guard_triggered invece che come tool_call, senza duplicare
    // la logica dei guard.
    // Durata inclusa nel valore di ritorno (non solo passata a StructuredTrace): serve anche a
    // sink.ToolCallFinished (IAgentSink), che la riceve come parametro esplicito.
    (string Result, long DurationMs) ExecuteToolGuardedTraced(string name, string argsJson)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = ExecuteToolGuarded(name, argsJson);
        sw.Stop();

        var guard = result.StartsWith("[LOOP-BLOCK]", StringComparison.Ordinal) ? "anti-loop"
            : result.StartsWith(readBlockedPrefix, StringComparison.Ordinal) ? "max-consecutive-readonly"
            : result.StartsWith(verifyMissingPrefix, StringComparison.Ordinal) ? "verify-missing"
            : result.StartsWith(functionalMissingPrefix, StringComparison.Ordinal) ? "functional-verify-missing"
            : null;

        if (guard != null)
            StructuredTrace.GuardTriggered(guard, OneLine(result, 200));
        else
        {
            var isError = result.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase);
            var errorKind = !isError ? null
                : result.StartsWith("ERRORE sicurezza", StringComparison.OrdinalIgnoreCase) ? "security"
                : "tool_error";
            StructuredTrace.ToolCall(name, argsJson, !isError, errorKind, sw.ElapsedMilliseconds,
                System.Text.Encoding.UTF8.GetByteCount(result));
        }

        return (result, sw.ElapsedMilliseconds);
    }

    // La richiesta dell'utente di questo turno: il rollover la conserva per intero.
    var turnRequest = history.LastOrDefault(m => m.Role == "user")?.Content;

    // Trace strutturato: un turno = una chiamata a RunAgentLoopAsync, qualunque sia la modalità
    // o il chiamante (REPL, --stdin-protocol, /init, /paste) — unico punto di ingresso comune.
    StructuredTrace.BeginTurn();
    StructuredTrace.UserMessage(turnRequest ?? "");

    // Set di tool fisso per tutto il turno, scelto sulla richiesta dell'utente. Prima veniva
    // ricalcolato a ogni step sull'ULTIMO messaggio user, che a metà turno è quasi sempre un
    // messaggio del motore ([AUTO-...], nudge): le parole chiave cambiavano, cambiava la lista
    // dei tool, e con il template Qwen (tool nel system prompt, in testa) cambiava l'inizio del
    // prompt. llama.cpp non poteva riusare la cache e rielaborava tutto: ~56 s per 40K token a step.
    var turnTools = dispatcher.GetRelevantDefinitions(turnRequest ?? "");
    turnTools.Add(ExploreSubAgent.Definition);

    int step = 0;
    while (step < maxSteps)
    {
        step++;

        // Ricontrolla ad OGNI step, non solo all'inizio del turno: un singolo messaggio
        // utente può generare molti step di tool-call, e la cronologia può superare la
        // soglia di contesto anche a metà di un turno lungo (es. avvia backend+frontend
        // e verifica più volte l'output). Senza questo controllo per-step, il rollover
        // scatterebbe solo al turno SUCCESSIVO, dopo che il server ha già rifiutato o
        // droppato la richiesta per contesto eccessivo.
        var beforeRollover = history;
        history = await sessionManager.MaybeRolloverAsync(history, atTurnStart: step == 1, dispatcher.HasPendingPlan, turnRequest,
            dispatcher.PlanSummary(), workspace.Root,
            onNotice: text => { if (isMachineMode) Notice(text); else UI.Dim(text); },
            onSummary: text => { if (isMachineMode) Trace("  [rollover] " + text.Replace("\n", "\n  [rollover] ")); });
        // Il contenuto dei file letti è uscito dal contesto: prima di modificarli vanno riletti.
        // Per questo riparte anche il budget di letture: altrimenti il guard delle letture
        // rifiutava la rilettura e RequireRead la modifica (osservato: stallo dopo un rollover).
        if (!ReferenceEquals(beforeRollover, history))
        {
            dispatcher.ClearCache();
            consecutiveReadOnly = 0;
            readOnlyNudgeGiven = false;
            restrictReadOnly = false;
            readBlocks = 0;
        }

        // ── Call LLM (streaming) ──────────────────────────────────────────────
        ChatMessage message;
        Usage? usage;
        string? finishReason;

        // null se promptTokens non è noto (server che non manda usage, o eccezione prima che
        // arrivi): mai calcolato su un valore mancante, vedi StructuredTrace.LlmRequest.
        double? ContextPercentOf(Usage? u) =>
            u != null && maxContextTokens > 0 ? 100.0 * u.PromptTokens / maxContextTokens : null;

        const int maxLlmRetries = 3;
        var llmAttempt = 0;

        while (true)
        {
            llmAttempt++;
            var streamedContent = false;
            // Dichiarato fuori dal try: serve anche nei catch sotto per misurare la durata
            // della chiamata fallita (trace strutturato), non solo di quella riuscita.
            var llmSw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var tools = turnTools;
                // Modalità "solo azione" (vedi readOnlyNudgeGiven): togliere i tool di lettura
                // dalla richiesta è più efficace di qualunque istruzione testuale. Costa una
                // rielaborazione completa del prompt, ma scatta di rado.
                if (restrictReadOnly)
                    tools = [.. tools.Where(t => !readOnlyTools.Contains(t.Function.Name))];
                var request = new ChatRequest
                {
                    Messages = history,
                    Tools = tools,
                    ToolChoice = "auto",
                    MaxTokens = maxTokens,
                    Temperature = temperature,
                    FrequencyPenalty = frequencyPenalty,
                    PresencePenalty = presencePenalty,
                    TopP = topP,
                    TopK = topK,
                    MinP = minP,
                    RepeatPenalty = repeatPenalty,
                    RepeatLastN = repeatLastN
                };

                // Print step indicator (solo REPL: in stdin-protocol andrebbe su stdout e
                // romperebbe il parsing NDJSON lato extension)
                if (!isMachineMode) Console.WriteLine();
                UI.StepIndicator(step);

                // Ping immediato appena si inizia a generare: senza questo, dopo un
                // Applica/Rifiuta la chat resta silenziosa finché non arriva il primo token
                // o l'intera tool call successiva è completa (può volerci molto — vedi
                // heartbeat sotto), indistinguibile da un blocco per chi guarda la chat.
                onStatus?.Invoke("Elaborazione in corso…");

                (message, usage, finishReason) = await llm.StreamChatAsync(request, token =>
                {
                    streamedContent = true;
                    // Il prefisso "Agent › " e il Console.Write(token) in REPL, o l'evento
                    // {type:"token"} in --stdin-protocol, sono interamente dentro ConsoleSink —
                    // stesso output di prima, vedi ConsoleSink.AssistantDelta.
                    sink.AssistantDelta(turnId, token);
                }, ct, onHeartbeat: onStatus);
                llmSw.Stop();
                StructuredTrace.LlmRequest(usage?.PromptTokens, llmSw.ElapsedMilliseconds, finishReason,
                    contextPercent: ContextPercentOf(usage), maxTokens: maxTokens);
                StructuredTrace.ContextSize(usage?.PromptTokens ?? 0, maxContextTokens);

                // Riga vuota di chiusura SOLO se questa risposta ha streammato testo (stessa
                // condizione di prima: "onToken == null && streamedContent"), e reset del
                // prefisso per la prossima risposta di questo turno (tool-call seguita da testo,
                // o un nuovo tentativo dopo un retry) — vedi ConsoleSink.AssistantDone.
                if (streamedContent)
                    sink.AssistantDone(turnId, usage?.PromptTokens ?? 0, usage?.CompletionTokens ?? 0);
                break; // risposta ricevuta con successo
            }
            catch (ToolCallTooLargeException ex)
            {
                // Interruzione preventiva lato client (vedi LlamaClient.StreamChatAsync): niente
                // attesa di minuti per un fallimento 500 garantito — la stessa correzione della
                // tool call troppo grande, ma quasi istantanea.
                UI.Error($"Tool call '{ex.ToolName}' interrotta: troppo grande, avrebbe fallito comunque.");
                if (ex.Degenerate)
                {
                    Notice($"generazione di '{ex.ToolName}' interrotta: argomenti lunghissimi, probabile testo ripetuto in loop");
                    history.Add(ChatMessage.User(
                        $"ERRORE: la tool call '{ex.ToolName}' è stata interrotta: gli argomenti avevano superato migliaia di " +
                        "caratteri, segno che stavi ripetendo lo stesso testo in loop. Rifai la chiamata con argomenti BREVI " +
                        "(per manage_todo: solo id, status e una note di una riga) e prosegui con il passo corrente."));
                    if (recoverableErrorRetries < maxRecoverableErrorRetries)
                    {
                        recoverableErrorRetries++;
                        continue;
                    }
                    EmitFinalNotice($"⚠️ Il modello ha generato più volte argomenti in loop per '{ex.ToolName}'. Mi fermo: scrivi \"prosegui\" per riprendere dal piano salvato.");
                    StructuredTrace.EndTurn("error");
                    sink.TurnFinished(turnId, "error");
                    return false;
                }
                UI.Dim("  → Il modello ha tentato di scrivere un intero file/blocco di codice in una sola tool call.");
                history.Add(ChatMessage.User(
                    $"ERRORE: la tool call '{ex.ToolName}' è stata interrotta perché il contenuto generato superava " +
                    "la soglia di sicurezza prima ancora di essere completo — avrebbe comunque fallito con un errore " +
                    "500 di JSON troncato lato server. Riprendi il task usando SOLO edit_file con new_string di MAX " +
                    "32000 caratteri, una funzione/metodo o un piccolo blocco alla volta. " +
                    "Se devi creare un file da zero: prima write_file con SOLO lo scheletro minimo (max 32000 caratteri), " +
                    "poi edit_file ripetutamente per aggiungere il resto un pezzo alla volta."));

                if (recoverableErrorRetries < maxRecoverableErrorRetries)
                {
                    recoverableErrorRetries++;
                    UI.Dim($"  [auto-retry {recoverableErrorRetries}/{maxRecoverableErrorRetries}] rimando subito al modello con l'istruzione correttiva...");
                    continue;
                }
                UI.Error("Troppi tentativi falliti per lo stesso motivo — mi fermo, riprova scrivendo un nuovo messaggio.");
                EmitFinalNotice(
                    $"⚠️ Ho provato {maxRecoverableErrorRetries + 1} volte a scrivere '{ex.ToolName}' ma il contenuto generato " +
                    "supera sempre la soglia di sicurezza per una singola tool call (probabile tentativo di scrivere un intero " +
                    "file/componente in un colpo solo). Mi fermo qui — riprova chiedendomi esplicitamente di procedere " +
                    "\"un pezzo alla volta\" (scheletro minimo, poi singoli metodi/blocchi con edit_file).");
                StructuredTrace.EndTurn("error");
                sink.TurnFinished(turnId, "error");
                return false;
            }
            catch (IncompleteLlmResponseException ex)
            {
                // Risposta incompleta (stream troncato o finish_reason="length"): mai nessuna
                // tool call da questa risposta (l'eccezione viene sollevata in LlamaClient
                // PRIMA di ricostruire il ChatMessage — non ne esiste un'istanza parziale usabile
                // qui, e questo catch non fa mai history.Add: il frammento troncato non entra
                // MAI nella cronologia, nemmeno quando si ritenta la stessa richiesta sotto).
                llmSw.Stop();
                var contextPercent = ContextPercentOf(ex.Usage);
                StructuredTrace.LlmRequest(ex.Usage?.PromptTokens, llmSw.ElapsedMilliseconds, null,
                    ok: false, errorKind: ex.Kind, contextPercent: contextPercent, maxTokens: maxTokens);

                if (ex.Kind == "length")
                {
                    // finish_reason="length": NESSUN retry (a differenza di stream_truncated) —
                    // è un limite deterministico del server/modello per QUESTA richiesta, non un
                    // guasto transitorio: ripetere la stessa richiesta darebbe lo stesso esito.
                    // SOGLIA e TESTI facili da trovare: cercare "LengthContextFullThreshold".
                    const double LengthContextFullThreshold = 0.85; // % di maxContextTokens oltre cui "length" è probabilmente il contesto, non il budget di risposta
                    const string LengthContextFullMessage =
                        "⚠️ Contesto quasi pieno: avvia una nuova sessione (/reset) o lascia comprimere la conversazione " +
                        "(rollover automatico) prima di continuare — la risposta si è interrotta per limite di lunghezza " +
                        "e il contesto occupato è oltre l'85% del massimo configurato.";
                    const string LengthResponseLimitMessage =
                        "⚠️ Limite di token della risposta raggiunto (finish_reason=\"length\"): il turno si è interrotto " +
                        "prima di finire. Non ho eseguito nessuna tool call da quella risposta (poteva essere tagliata a " +
                        "metà). Riprova con una richiesta più piccola o scomponi il task in passi più piccoli.";

                    UI.Error($"Risposta LLM incompleta (length): {ex.Message}");
                    EmitFinalNotice(contextPercent is double p && p / 100.0 > LengthContextFullThreshold
                        ? LengthContextFullMessage
                        : LengthResponseLimitMessage);
                    StructuredTrace.EndTurn("error");
                    sink.TurnFinished(turnId, "error");
                    return false;
                }

                // stream_truncated: 1 tentativo iniziale + 1 retry (STESSA richiesta, history
                // invariata), backoff fisso 1500ms — qui sì che vale la pena ritentare: è
                // tipicamente un guasto di rete/server transitorio, non un limite deterministico.
                const int maxIncompleteAttempts = 2;
                const int incompleteRetryDelayMs = 1500;
                if (llmAttempt < maxIncompleteAttempts)
                {
                    // Separatore richiesto esplicitamente: senza, il testo del tentativo
                    // successivo si attaccherebbe visivamente al frammento troncato in chat,
                    // sembrando la continuazione della stessa risposta invece di una nuova.
                    if (isMachineMode) sink.AssistantDelta(turnId, "\n\n[risposta interrotta, nuovo tentativo]\n\n");
                    else { sink.AssistantDone(turnId, 0, 0); UI.Dim("[risposta interrotta, nuovo tentativo]"); }
                    UI.Dim($"  [stream troncato — retry {llmAttempt}/{maxIncompleteAttempts - 1} tra {incompleteRetryDelayMs}ms]");
                    StructuredTrace.LlmRetry(llmAttempt, maxIncompleteAttempts, ex.Kind, incompleteRetryDelayMs);
                    await Task.Delay(incompleteRetryDelayMs);
                    continue;
                }

                UI.Error($"Risposta LLM incompleta (stream_truncated): {ex.Message}");
                EmitFinalNotice(
                    $"⚠️ La connessione con il server LLM si è interrotta a metà di una risposta in streaming " +
                    $"{maxIncompleteAttempts} volte di seguito. Non ho eseguito nessuna azione parziale. " +
                    "Riprova scrivendo di nuovo, oppure verifica che il server LLM sia stabile.");
                StructuredTrace.EndTurn("error");
                sink.TurnFinished(turnId, "error");
                return false;
            }
            catch (HttpRequestException ex)
            {
                var isJsonError = ex.Message.Contains("parse tool call") ||
                                  ex.Message.Contains("missing closing quote") ||
                                  ex.Message.Contains("invalid string");

                if (isJsonError)
                {
                    UI.Error("Errore LLM 500: JSON non valido nel tool call (virgolette/backtick nel codice).");
                    UI.Dim("  → Il modello ha tentato write_file/edit_file con contenuto non escapabile.");
                    // Inietta un hint di recupero come prossimo turno utente
                    history.Add(ChatMessage.User(
                        "ERRORE 500: il tentativo precedente ha generato JSON non valido perché " +
                        "il contenuto del file contiene virgolette, apici o template literal (backtick, ${...}, $\") non escapabili. " +
                        "Riprendi il task usando SOLO edit_file con old_string di MAX 5-8 righe. " +
                        "Non usare write_file per file di codice (C#, TypeScript/JavaScript, ecc.) con virgolette o backtick. " +
                        "Se devi creare un file da zero: prima write_file con la struttura base (classe/componente vuoto), " +
                        "poi edit_file per aggiungere il corpo dei metodi."));

                    if (recoverableErrorRetries < maxRecoverableErrorRetries)
                    {
                        recoverableErrorRetries++;
                        UI.Dim($"  [auto-retry {recoverableErrorRetries}/{maxRecoverableErrorRetries}] rimando subito al modello con l'istruzione correttiva...");
                        continue;
                    }
                    UI.Error("Troppi tentativi falliti per lo stesso motivo — mi fermo, riprova scrivendo un nuovo messaggio.");
                    EmitFinalNotice(
                        $"⚠️ Ho provato {maxRecoverableErrorRetries + 1} volte ma il server continua a rifiutare il JSON generato " +
                        "(virgolette/backtick non escapati nel contenuto del file). Mi fermo qui — riprova chiedendomi " +
                        "esplicitamente di usare edit_file con blocchi piccoli (max 5-8 righe) invece di riscrivere l'intero file.");
                    StructuredTrace.EndTurn("error");
                    sink.TurnFinished(turnId, "error");
                    return false;
                }

                // ex.StatusCode è null solo per fallimenti di TRASPORTO (connessione persa/rifiutata
                // prima che arrivi una risposta HTTP) — non per risposte HTTP di errore vere e proprie,
                // che qui arrivano già con uno status code impostato esplicitamente. Ritentare un 400/500
                // non cambierebbe nulla (stessa richiesta, stesso esito); un fallimento di trasporto invece
                // è spesso transitorio (server locale sotto carico) e vale la pena ritentare — ma SOLO se
                // non abbiamo già stampato token parziali, altrimenti un retry duplicherebbe l'output.
                llmSw.Stop();
                StructuredTrace.LlmRequest(null, llmSw.ElapsedMilliseconds, null, ok: false, errorKind: "http_error", maxTokens: maxTokens);

                if (ex.StatusCode is null && !streamedContent && llmAttempt < maxLlmRetries)
                {
                    var delayMs = 1000 * (int)Math.Pow(2, llmAttempt - 1);
                    UI.Dim($"  [connessione al server LLM persa — retry {llmAttempt}/{maxLlmRetries - 1} tra {delayMs / 1000}s]");
                    StructuredTrace.LlmRetry(llmAttempt, maxLlmRetries, "http_error", delayMs);
                    await Task.Delay(delayMs);
                    continue;
                }

                UI.Error($"Errore LLM: {ex.Message}");
                StructuredTrace.EndTurn("error");
                sink.TurnFinished(turnId, "error");
                return false;
            }
            catch (Exception ex)
            {
                UI.Error($"Errore: {ex.Message}");
                StructuredTrace.EndTurn("error");
                sink.TurnFinished(turnId, "error");
                return false;
            }
        }

        // Alcuni modelli (es. Kimi-Linear) scrivono le tool call nel proprio formato nativo
        // "functions.nome:indice{...}" invece che nel campo tool_calls OpenAI-standard, quando
        // llama-server non riconosce il loro chat_format e non le traduce. Va tentato PRIMA del
        // guard sotto, altrimenti un messaggio con solo la tool call testuale (Content non vuoto
        // ma privo di ToolCalls) passerebbe come risposta finale invece di essere eseguito.
        message.TryExtractFallbackToolCalls();

        // Uno stream che finisce senza token di contenuto né tool_calls (connessione persa
        // a metà generazione, risposta vuota dal server) produce un ChatMessage con entrambi
        // i campi null: serializzato, llama-server lo rifiuta con 400 "Assistant message must
        // contain either 'content' or 'tool_calls'" — e lo rifiuta di nuovo ad ogni turno
        // successivo perché il messaggio resta in history. Diamogli un contenuto minimo.
        if (string.IsNullOrEmpty(message.Content) && message.ToolCalls is not { Count: > 0 })
            message.Content = "(nessuna risposta dal modello)";

        // Ricalibra la stima token del SessionManager con il conteggio REALE del server,
        // usando la history esattamente come inviata in questa richiesta (request.Messages
        // sopra è la stessa lista, non ancora modificata: history.Add(message) è la riga
        // subito dopo). Deve stare qui, prima di qualunque Add su history in questo step.
        sessionManager.RecordRealUsage(usage?.PromptTokens ?? 0, history);

        history.Add(message);

        if (isMachineMode)
            Trace($" [step {step}/{maxSteps}] tool_calls={message.ToolCalls?.Count ?? 0} " +
                  $"prompt_tokens={usage?.PromptTokens ?? 0} testo: {OneLine(message.Content ?? "", 200)}");

        if (message.ToolCalls is { Count: > 0 })
        {
            // Le riflessioni (ruolo "user") vanno accodate DOPO tutti i risultati dei tool di
            // questo messaggio: inserirle in mezzo spezza la sequenza assistant(tool_calls) ->
            // tool, tool, ... che i chat template si aspettano contigua.
            var pendingReflections = new List<string>();

            foreach (var tc in message.ToolCalls)
            {
                if (isMachineMode) Trace($"  -> {tc.Function.Name}({OneLine(tc.Function.Arguments, 300)})");
                sink.ToolCallStarted(turnId, tc.Function.Name, tc.Function.Arguments);

                var (result, toolDurationMs) = ExecuteToolGuardedTraced(tc.Function.Name, tc.Function.Arguments);
                TrackBuildState(tc.Function.Name, tc.Function.Arguments, result);

                // --- LOGICA DI RIFLESSIONE POTENZIATA ---
                // Se il risultato contiene parole chiave di errore, iniettiamo un comando di sistema
                // che forza l'LLM a cambiare strategia nel prossimo giro.
                //
                // BUG CRITICO CORRETTO: questo controllo non intercettava "ERRORE:" — il prefisso
                // usato da QUASI TUTTE le validazioni di FileSystemTools (write_file/edit_file rifiutati
                // per dimensione, file non letto, argomenti mancanti, ecc. — vedi FileSystemTools.cs).
                // Un write_file rifiutato per limite di 2000 caratteri veniva quindi trattato come riuscito
                // (all'epoca con il messaggio "l'operazione è stata eseguita con successo", oggi rimosso) —
                // un FALSO POSITIVO che spiegava perché il modello, dopo un fallimento reale, dichiarava il
                // task concluso con un annuncio testuale invece di correggere la tool call.
                bool isError = result.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase) ||
                               result.StartsWith("⚠️", StringComparison.Ordinal) ||
                               result.StartsWith("[LOOP-BLOCK]", StringComparison.Ordinal) ||
                               result.Contains("[ERROR]") ||
                               result.Contains("non trovato") ||
                               result.Contains("fallito", StringComparison.OrdinalIgnoreCase) ||
                               result.Contains("ambigua") ||
                               result.Contains("[ESITO: FALLITO", StringComparison.OrdinalIgnoreCase);

                if (isMachineMode) Trace($"  <- {tc.Function.Name}: {OneLine(result, 300)}");
                sink.ToolCallFinished(turnId, tc.Function.Name, !isError, result, toolDurationMs);

                history.Add(ChatMessage.ToolResult(tc.Id, result));

                if (result.StartsWith(readBlockedPrefix, StringComparison.Ordinal) ||
                    result.StartsWith(verifyMissingPrefix, StringComparison.Ordinal) ||
                    result.StartsWith(functionalMissingPrefix, StringComparison.Ordinal))
                {
                    // Il rifiuto spiega già cosa fare: una riflessione generica "l'operazione è
                    // fallita, analizza il motivo" spingerebbe il modello a indagare ancora.
                }
                else if (isError)
                {
                    // Ruolo "user" (non "system"): alcuni chat template (es. Qwen3) impongono che
                    // il ruolo "system" compaia SOLO come primissimo messaggio e sollevano un'eccezione
                    // Jinja ("System message must be at the beginning") se compare altrove.
                    // Il [LOOP-BLOCK] segnala che il modello ha GIÀ ripetuto lo stesso comando
                    // troppe volte: il generico "analizza e riprova diversamente" non basta, perché
                    // è esattamente quello che ha ignorato per arrivare fin qui. Serve un'istruzione
                    // che vieti esplicitamente di ripetere e suggerisca l'alternativa corretta.
                    var reflection = result.StartsWith("[LOOP-BLOCK]", StringComparison.Ordinal)
                        ? "[INTERNAL_REFLECTION]: Hai richiamato lo stesso comando con gli stessi argomenti troppe volte di fila senza che il risultato cambiasse. NON ripetere lo stesso comando. Se stai verificando una modifica .NET usa sol_analyze (mai run_command/dotnet build). Se il comando è corretto ma l'errore persiste, il problema non è nel comando: rileggi l'output con attenzione, controlla se le tue modifiche precedenti sono state davvero applicate (es. rileggi il file), e correggi la causa reale — oppure fermati e spiega all'utente cosa hai provato e dove sei bloccato."
                        : "[INTERNAL_REFLECTION]: L'operazione precedente ha fallito o è stata ambigua. Analizza il motivo (percorso errato? stringa non univoca? file mancante?) e proponi una soluzione correttiva diversa nel prossimo passo.";
                    pendingReflections.Add(reflection);
                }
                else if (consecutiveReadOnly >= maxConsecutiveReadOnly)
                {
                    // Troppi step di sola ispezione di fila senza mai agire: forziamo una
                    // decisione ORA invece di lasciare che continui a "rimuginare" chiamando
                    // altri tool di sola lettura con argomenti leggermente diversi (che il
                    // blocco anti-loop sopra non intercetta, dato che non sono identici).
                    pendingReflections.Add($"[INTERNAL_REFLECTION]: Hai eseguito {consecutiveReadOnly} letture consecutive (file, ricerche, diff, comandi di sola lettura) senza nessuna azione concreta. " +
                                  "STOP ANALISI: hai già abbastanza informazioni per il passo corrente. Nel prossimo step devi fare SOLO una di queste cose — " +
                                  "(1) applicare ORA la modifica del passo corrente con edit_file/write_file, (2) verificare con sol_analyze o un comando di build/test, " +
                                  "(3) se ti manca davvero una decisione, chiederla all'utente con una domanda precisa. " +
                                  "Se il passo corrente è troppo grande per farlo subito, spezzalo con manage_todo in passi più piccoli e inizia dal primo. " +
                                  "ATTENZIONE: la prossima lettura verrà rifiutata.");
                    consecutiveReadOnly = 0; // dato un ordine esplicito, riparte il conteggio
                    readOnlyNudgeGiven = true;
                }
                // Nessun messaggio dopo un tool riuscito: il risultato del tool è già l'ultimo
                // elemento della history e il modello continua da solo. Il vecchio messaggio
                // "operazione eseguita con successo" aggiungeva un turno "user" per ogni tool call
                // senza dare informazione.
            }

            // Impediamo loop infiniti (es. lo stesso tool fallisce 5 volte di fila).
            // Il turno si chiude con un messaggio assistant (visibile in chat) così la history
            // non finisce su un turno "user" e il messaggio successivo dell'utente non crea due
            // "user" consecutivi (che i template tipo Qwen3 rifiutano).
            if (loopDetected)
            {
                UI.Error("Loop rilevato: interruzione.");
                EmitFinalNotice(stopReason ??
                    "⚠️ Mi fermo: ho ripetuto più volte la stessa chiamata ottenendo lo stesso risultato. " +
                    "Dimmi come proseguire o indicami un approccio diverso.");
                StructuredTrace.EndTurn("completed");
                sink.TurnFinished(turnId, "completed");
                return true;
            }

            if (pendingReflections.Count > 0)
                history.Add(new ChatMessage { Role = "user", Content = string.Join("\n\n", pendingReflections.Distinct()) });
            continue; // Torna all'inizio del ciclo per far elaborare i nuovi dati alla LLM
        }

        // ── Risposta testuale finale ──────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            var rescued = TryRescueTextToolCalls(message.Content);
            if (rescued.Count > 0)
            {
                UI.Dim("  [recupero tool call dal testo]");
                foreach (var (name, argsJson) in rescued)
                {
                    if (isMachineMode) Trace($"  -> {name}({OneLine(argsJson, 300)})");
                    sink.ToolCallStarted(turnId, name, argsJson);

                    var (result, rescuedDurationMs) = ExecuteToolGuardedTraced(name, argsJson);
                    TrackBuildState(name, argsJson, result);

                    var rescuedIsError = result.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase);
                    if (isMachineMode) Trace($"  <- {name}: {OneLine(result, 300)}");
                    sink.ToolCallFinished(turnId, name, !rescuedIsError, result, rescuedDurationMs);

                    history.Add(ChatMessage.ToolResult($"rescued-{step}", result));
                }

                if (loopDetected)
                {
                    UI.Error("Loop rilevato: stesso tool richiamato troppe volte con argomenti identici — interruzione.");
                    EmitFinalNotice(stopReason ??
                        "⚠️ Mi fermo: ho ripetuto più volte la stessa chiamata ottenendo lo stesso risultato. " +
                        "Dimmi come proseguire o indicami un approccio diverso.");
                    StructuredTrace.EndTurn("completed");
                    sink.TurnFinished(turnId, "completed");
                    return true;
                }
                continue;
            }

            // ── Guard anti-annuncio ──────────────────────────────────────────────
            if (LooksLikeUnfinishedAnnouncement(message.Content))
            {
                if (announcementRetries < maxAnnouncementRetries)
                {
                    announcementRetries++;
                    Notice($"ha annunciato un'azione senza eseguirla, lo rimando a farla ({announcementRetries}/{maxAnnouncementRetries})");
                    UI.Dim($"  [auto-continue {announcementRetries}/{maxAnnouncementRetries}] risposta rilevata come annuncio senza tool call — forzo l'esecuzione...");
                    history.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = "[INTERNAL_REFLECTION]: Hai scritto un annuncio del prossimo passo (es. \"Procedo con...\") " +
                                  "senza chiamare il tool corrispondente in questa stessa risposta. Il task NON è concluso. " +
                                  "Chiama SUBITO ORA il tool necessario per eseguire quanto hai appena annunciato — niente altro testo di annuncio."
                    });
                    continue;
                }

                // Il modello ha ripetuto lo stesso annuncio senza mai eseguirlo neppure dopo
                // maxAnnouncementRetries correzioni esplicite. Il testo del modello è già stato
                // mostrato in streaming (onToken, sopra) mentre veniva generato — qui aggiungiamo
                // SOLO l'avviso, senza ripeterlo, altrimenti finirebbe due volte in history
                // (message è già stato accodato) e due volte in chat.
                UI.Error("Il modello continua ad annunciare senza eseguire — mi fermo.");
                EmitFinalNotice(
                    $"⚠️ Ho corretto {maxAnnouncementRetries} volte un annuncio senza esecuzione (\"procedo con...\" " +
                    "senza chiamare il tool), ma il modello ha ripetuto lo stesso testo. Mi fermo qui — riprova scrivendo " +
                    "di nuovo, magari indicando esplicitamente il primo file/tool da usare.");
                StructuredTrace.EndTurn("completed");
                sink.TurnFinished(turnId, "completed");
                return true;
            }

            // ── Gate di build (.NET) ─────────────────────────────────────────────
            // Il modello sta per chiudere il turno con testo libero (niente tool call, anche
            // dopo il tentativo di recupero) mentre ha modificato almeno un .cs senza mai
            // verificarne la compilazione. Non ci fidiamo del prompt: eseguiamo sol_analyze
            // noi stessi, prima che l'utente veda una risposta che dichiara il task concluso
            // su codice che non compila. Cap a maxAutoBuildChecks per non girare all'infinito
            // se il modello non riesce a correggere gli errori.
            if (hasUnverifiedDotnetEdit && autoBuildChecks < maxAutoBuildChecks)
            {
                autoBuildChecks++;
                const string autoArgs = "{}";
                UI.Dim("  [auto-check] modifiche a file .cs non verificate — eseguo sol_analyze...");

                if (isMachineMode) Trace($"  -> sol_analyze({autoArgs})");
                sink.ToolCallStarted(turnId, "sol_analyze", autoArgs);

                var autoBuildSw = System.Diagnostics.Stopwatch.StartNew();
                var buildResult = dispatcher.Execute("sol_analyze", autoArgs, ct);
                autoBuildSw.Stop();
                var autoBuildIsError = buildResult.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase);
                StructuredTrace.ToolCall("sol_analyze", autoArgs, !autoBuildIsError, autoBuildIsError ? "tool_error" : null,
                    autoBuildSw.ElapsedMilliseconds, System.Text.Encoding.UTF8.GetByteCount(buildResult));
                TrackBuildState("sol_analyze", autoArgs, buildResult);

                if (isMachineMode) Trace($"  <- sol_analyze: {OneLine(buildResult, 300)}");
                sink.ToolCallFinished(turnId, "sol_analyze", !autoBuildIsError, buildResult, autoBuildSw.ElapsedMilliseconds);

                history.Add(ChatMessage.ToolResult($"auto-build-{step}", buildResult));
                history.Add(new ChatMessage
                {
                    Role = "user",
                    Content = hasUnverifiedDotnetEdit
                        ? "[AUTO-BUILD] Hai modificato file .cs senza verificarne la build. Il controllo automatico ha trovato errori (vedi risultato sopra): correggili con edit_file. Non dichiarare il task concluso finché la build non è pulita."
                        : "[AUTO-BUILD] Hai modificato file .cs senza verificarne la build. Il controllo automatico è passato: la build è pulita. Ora rispondi all'utente con il riepilogo."
                });
                continue;
            }

            // ── Gate di verifica (stack non-.NET) ───────────────────────────────
            // Per file .py/.ts/.svelte/... non conosciamo il comando di build/lint/test
            // corretto: non possiamo auto-eseguirlo come sopra. Ci limitiamo a bloccare la
            // chiusura del turno finché il modello non ha chiamato ESPLICITAMENTE run_command
            // almeno una volta dopo la modifica — impedisce di dichiarare "build riuscita" o
            // "task completato" senza aver mai davvero verificato nulla.
            else if (hasUnverifiedOtherEdit && autoBuildChecks < maxAutoBuildChecks)
            {
                autoBuildChecks++;
                Notice("ha modificato codice senza verificarlo, richiesta build/test");
                UI.Dim("  [auto-check] modifiche a file di codice non verificate — richiedo verifica esplicita...");
                history.Add(new ChatMessage
                {
                    Role = "user",
                    Content = "[AUTO-CHECK] Hai modificato file di codice in questo turno ma non hai mai chiamato run_command " +
                              "per verificarli (build/lint/test). NON dichiarare il task concluso, la build riuscita o le modifiche " +
                              "corrette finché non hai eseguito ORA, con run_command, il comando di build/lint/test appropriato per " +
                              "questo progetto e ne hai controllato l'esito REALE nel risultato del tool."
                });
                continue;
            }

            // ── Gate di avanzamento del piano ────────────────────────────────────
            // Il modello chiude il turno con testo mentre il piano su cui ha lavorato IN QUESTO
            // turno ha ancora passi aperti e non sta chiedendo nulla all'utente: tipicamente un
            // riepilogo intermedio dopo un singolo passo. Lo rimandiamo al passo successivo (max
            // maxPlanContinueRetries volte). planTouchedThisTurn evita di forzare il piano su una
            // domanda scollegata fatta in una chat che ha un piano salvato da prima.
            if (planTouchedThisTurn && dispatcher.HasPendingPlan && !message.Content.Contains('?')
                && planContinueRetries < maxPlanContinueRetries && step < maxSteps - 1)
            {
                planContinueRetries++;
                Notice($"il piano ha ancora passi aperti, lo rimando a proseguire ({planContinueRetries}/{maxPlanContinueRetries})");
                UI.Dim($"  [auto-continue piano {planContinueRetries}/{maxPlanContinueRetries}] passi ancora aperti — proseguo...");
                history.Add(new ChatMessage
                {
                    Role = "user",
                    Content = "[AUTO-CONTINUE] Il piano ha ancora passi non completati:\n" + dispatcher.PlanSummary() +
                              "\nNon fermarti tra un passo e l'altro: aggiorna con manage_todo il passo appena concluso (se non l'hai fatto) " +
                              "e prosegui SUBITO con il successivo. Se invece ti serve una decisione dell'utente, chiedila con una domanda esplicita."
                });
                continue;
            }

            // ── Gate di validazione funzionale ───────────────────────────────────
            // Task multi-step che ha cambiato codice: il modello chiude con testo avendo solo
            // compilato. Lo rimandiamo a eseguire davvero il programma/i test (max N volte), a
            // meno che stia facendo una domanda all'utente (es. gli mancano le credenziali).
            if (planTouchedThisTurn && codeEditedThisTurn && !functionalCheckSinceEdit && !message.Content.Contains('?')
                && functionalCheckRetries < maxFunctionalCheckRetries && step < maxSteps - 1)
            {
                functionalCheckRetries++;
                Notice($"ha solo compilato, gli chiedo di eseguire un test reale ({functionalCheckRetries}/{maxFunctionalCheckRetries})");
                UI.Dim($"  [auto-test {functionalCheckRetries}/{maxFunctionalCheckRetries}] modifiche mai eseguite — richiedo il test funzionale...");
                history.Add(new ChatMessage
                {
                    Role = "user",
                    Content = "[AUTO-TEST] Hai modificato il codice e verificato solo la compilazione: il task non è concluso finché " +
                              "non hai ESEGUITO il programma e controllato l'esito reale. Fallo ora: test automatici, oppure avvia " +
                              "l'app con run_command background=true, attendi che sia pronta con get_background_output, ed esegui la " +
                              "chiamata/lo script di test reale; confronta l'output con il risultato atteso (se l'utente l'ha fornito, " +
                              "campo per campo). Aggiungi al piano il passo di test se manca. Se l'esito non è quello atteso, correggi e " +
                              "ritesta. Se ti manca qualcosa per testare che non trovi nel progetto, chiedilo all'utente con una domanda precisa."
                });
                continue;
            }

            // ── Promemoria processi in background ────────────────────────────────
            if (!jobsReminderSent && dispatcher.RunningJobs() is { Count: > 0 } running && step < maxSteps - 1)
            {
                jobsReminderSent = true;
                var list = string.Join("\n", running.Select(j => $"  PID {j.Pid}: {OneLine(j.Command, 150)}"));
                Notice($"processi in background ancora attivi ({running.Count}), gli chiedo se vanno fermati");
                UI.Dim($"  [auto-jobs] {running.Count} processi in background ancora attivi — chiedo di gestirli...");
                history.Add(new ChatMessage
                {
                    Role = "user",
                    Content = "[PROCESSI ATTIVI] Stai chiudendo il turno con questi processi in background ancora in esecuzione:\n" + list +
                              "\nSe li hai avviati solo per verificare il tuo lavoro, fermali ORA con stop_background_job: altrimenti " +
                              "restano accesi, tengono occupate porte ed exe e bloccano build e test successivi. Se invece l'utente " +
                              "ti ha chiesto di avviarli o gli servono accesi, lasciali e scrivilo nella risposta (PID e indirizzo)."
                });
                continue;
            }
            dispatcher.SnapshotJobs();

            if (usage is { } u)
                UI.Dim($"[tokens: prompt={u.PromptTokens} completion={u.CompletionTokens} | step={step}]");
            if (!isMachineMode) Console.WriteLine();
            StructuredTrace.EndTurn("completed");
            sink.TurnFinished(turnId, "completed");
            return true;
        }

        StructuredTrace.EndTurn("completed");
        sink.TurnFinished(turnId, "completed");
        return true;
    }

    if (step >= maxSteps)
    {
        UI.Error("Limite step raggiunto — loop interrotto.");

        // Senza questo messaggio la history termina sul turno "user" (l'ultimo
        // [INTERNAL_REFLECTION] iniettato dopo il tool call). Al giro successivo il nuovo
        // input utente verrebbe accodato subito dopo, creando due messaggi "user" consecutivi:
        // il chat template di Qwen3 lo rifiuta ("system/user/assistant must alternate"),
        // bloccando la chat finché non si fa /reset. Chiudiamo il turno con un messaggio
        // assistant così la history torna in uno stato valido e il task può riprendere.
        // EmitFinalNotice lo mostra anche in chat nel turno CORRENTE — prima veniva solo
        // accodato alla history e comparsa solo al turno successivo, lasciando la chat
        // silenziosa esattamente come nel caso dei retry esauriti sopra.
        EmitFinalNotice(
            "Ho raggiunto il limite di step per questo turno; il task non è ancora concluso. " +
            "Scrivi \"continua\" per proseguire da dove ho lasciato.");
    }

    StructuredTrace.EndTurn(step >= maxSteps ? "max_turns" : "completed");
    sink.TurnFinished(turnId, step >= maxSteps ? "max_turns" : "completed");
    return true;
}

// ── Modalità --stdin-protocol ───────────────────────────────────────────────────
// Delegato a StdinProtocolMode: legge richieste NDJSON da stdin, scrive eventi su stdout.
if (isStdinProtocol)
{
    var stdinCtx = new CliContext(workspace, llm, dispatcher, sessionManager, serverUrl, model, coreVersion, isMachineMode, isStdinProtocol, autoApproveStateFile, autoApprove);
    stdinCtx.Sink = sink;
    stdinCtx.History = history;

    await StdinProtocolMode.RunAsync(
        stdinCtx,
        runTurn: ct => RunAgentLoopAsync(maxTokens: 32768, onStatus: text => EmitEvent(new { type = "status", text }), ct),
        emitEvent: EmitEvent,
        trace: Trace,
        defaultModel: model);
    return;
}


// ── Modalità --serve ─────────────────────────────────────────────────────────────
// Delegato a ServeMode: server WebSocket, protocollo v1 di ui/protocol.
if (isServeMode)
{
    var serveCtx = new CliContext(workspace, llm, dispatcher, sessionManager, serverUrl, model, coreVersion, isMachineMode, isStdinProtocol, autoApproveStateFile, autoApprove);
    serveCtx.Sink = sink;
    serveCtx.History = history;

    await ServeMode.RunAsync(
        serveCtx,
        config,
        servePort,
        runTurn: ct => RunAgentLoopAsync(ct: ct),
        trace: Trace);
    return;
}

// ── Main REPL ─────────────────────────────────────────────────────────────────
// Delegato a ReplMode: loop interattivo con comandi speciali.
var replCtx = new CliContext(workspace, llm, dispatcher, sessionManager, serverUrl, model, coreVersion, isMachineMode, isStdinProtocol, autoApproveStateFile, autoApprove);
replCtx.Sink = sink;
replCtx.History = history;

await ReplMode.RunAsync(
    replCtx,
    runTurn: ct => RunAgentLoopAsync(ct: ct));

UI.Dim("\nArrivederci!");



// ── Console input con history ─────────────────────────────────────────────────
static class ConsoleInput
{
    private static readonly List<string> _history = [];
    private static int _historyIndex = 0;

    // Rileva se il terminale supporta operazioni avanzate (cursor, ReadKey).
    // In ambienti non interattivi (debugger VS Code, pipe) queste operazioni lanciano IOException.
    private static readonly bool _isInteractive = DetectInteractive();

    private static bool DetectInteractive()
    {
        try { _ = Console.KeyAvailable; _ = Console.CursorLeft; return true; }
        catch { return false; }
    }

    /// <summary>
    /// ReadLine con navigazione storia (↑↓) e editing inline.
    /// Cade automaticamente su Console.ReadLine() in ambienti non interattivi.
    /// </summary>
    public static string? ReadLine() =>
        _isInteractive ? ReadInteractive() : Console.ReadLine();

    private static string? ReadInteractive()
    {
        List<char> buffer = [];
        var position = 0;
        _historyIndex = _history.Count;
        var savedInput = "";
        var prevLen = 0;

        int startLeft, startTop;
        try { startLeft = Console.CursorLeft; startTop = Console.CursorTop; }
        catch { return Console.ReadLine(); }

        // Posizione (riga, colonna) del cursore dopo aver scritto i primi `uptoIndex` caratteri
        // a partire da startLeft/startTop: tiene conto sia del wrap automatico a fine riga sia
        // dei newline letterali che un paste multi-riga inserisce nel buffer. Indici oltre la
        // fine del buffer (usati per il padding di cancellazione) sono trattati come spazi.
        (int row, int col) ComputeCursor(int uptoIndex)
        {
            var width = Math.Max(1, Console.BufferWidth);
            int col = startLeft, row = startTop;
            for (var i = 0; i < uptoIndex; i++)
            {
                var c = i < buffer.Count ? buffer[i] : ' ';
                if (c == '\n') { col = 0; row++; }
                else { col++; if (col >= width) { col = 0; row++; } }
            }
            return (row, col);
        }

        int RowsFor(int uptoIndex) => ComputeCursor(uptoIndex).row - startTop + 1;

        void Redraw()
        {
            try
            {
                var width = Math.Max(1, Console.BufferWidth);
                var height = Console.BufferHeight;

                Console.SetCursorPosition(Math.Min(startLeft, width - 1), Math.Min(startTop, height - 1));

                var padLen = Math.Max(0, prevLen - buffer.Count);
                var cursorTopBeforeWrite = Console.CursorTop;
                Console.Write(new string([.. buffer]) + new string(' ', padLen));

                // Se scrivere il testo supera l'ultima riga del buffer console, Windows fa
                // scorrere tutto il contenuto verso l'alto: startTop non punta più alla riga
                // giusta e va corretto, altrimenti i redraw successivi scrivono nel posto
                // sbagliato — è la causa del testo duplicato/corrotto quando l'input è più
                // lungo della larghezza del terminale (es. una frase lunga incollata).
                var rowsWritten = RowsFor(buffer.Count + padLen);
                var overflow = cursorTopBeforeWrite + rowsWritten - height;
                if (overflow > 0)
                    startTop = Math.Max(0, startTop - overflow);

                prevLen = buffer.Count;

                var (targetTop, targetLeft) = ComputeCursor(position);
                Console.SetCursorPosition(Math.Min(targetLeft, width - 1), Math.Min(Math.Max(targetTop, 0), height - 1));
            }
            catch { }
        }

        void MoveTo(int pos)
        {
            try
            {
                var height = Console.BufferHeight;
                var (top, left) = ComputeCursor(pos);
                Console.SetCursorPosition(Math.Min(left, Console.BufferWidth - 1), Math.Min(Math.Max(top, 0), height - 1));
            }
            catch { }
        }

        while (true)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch { return Console.ReadLine(); }

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    // Un paste multi-riga arriva come tasti in rapida sequenza: se subito dopo
                    // l'Enter c'è già altro input in coda, non è una vera pressione di invio
                    // dell'utente ma un a-capo dentro il testo incollato. Lo trattiamo come
                    // newline letterale, altrimenti il paste verrebbe troncato alla prima riga.
                    if (Console.KeyAvailable)
                    {
                        buffer.Insert(position, '\n');
                        position++; Redraw();
                        break;
                    }
                    MoveTo(buffer.Count);
                    Console.WriteLine();
                    var result = new string([.. buffer]);
                    if (!string.IsNullOrWhiteSpace(result) &&
                        (_history.Count == 0 || _history[^1] != result))
                        _history.Add(result);
                    return result;

                case ConsoleKey.Escape:
                    buffer.Clear(); position = 0; Redraw(); break;

                case ConsoleKey.Backspace when position > 0:
                    buffer.RemoveAt(--position); Redraw(); break;

                case ConsoleKey.Delete when position < buffer.Count:
                    buffer.RemoveAt(position); Redraw(); break;

                case ConsoleKey.LeftArrow when position > 0: MoveTo(--position); break;
                case ConsoleKey.RightArrow when position < buffer.Count: MoveTo(++position); break;
                case ConsoleKey.Home: position = 0; MoveTo(0); break;
                case ConsoleKey.End: position = buffer.Count; MoveTo(position); break;

                case ConsoleKey.UpArrow when _historyIndex > 0:
                    if (_historyIndex == _history.Count) savedInput = new string([.. buffer]);
                    _historyIndex--;
                    buffer.Clear(); buffer.AddRange(_history[_historyIndex]);
                    position = buffer.Count; Redraw(); break;

                case ConsoleKey.DownArrow when _historyIndex <= _history.Count:
                    if (_historyIndex < _history.Count)
                    {
                        _historyIndex++;
                        var hist = _historyIndex == _history.Count ? savedInput : _history[_historyIndex];
                        buffer.Clear(); buffer.AddRange(hist);
                        position = buffer.Count; Redraw();
                    }
                    break;

                case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                    Console.WriteLine("^C"); return null;

                default:
                    if (key.KeyChar >= 32 && key.KeyChar != 127)
                    {
                        buffer.Insert(position, key.KeyChar);
                        position++; Redraw();
                    }
                    break;
            }
        }
    }
}

