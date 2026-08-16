using System.Text.Json;
using System.Text.RegularExpressions;
using LocalCodeAgent.Core;
using LocalCodeAgent.Models;
using Microsoft.Extensions.Configuration;

// Modalità headless per l'integrazione con extension/: legge richieste NDJSON da stdin,
// scrive eventi NDJSON su stdout, mai un banner/prompt interattivo. Il flag va controllato
// PRIMA di EnsureConsole(): in questa modalità stdin/stdout sono pipe verso il processo
// padre (VS Code) e non dobbiamo mai allocare/agganciare una finestra console.
var isStdinProtocol = args.Contains("--stdin-protocol");

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

var fullWorkspacePath = Path.GetFullPath(workspacePath);
if (!Directory.Exists(fullWorkspacePath))
    Directory.CreateDirectory(fullWorkspacePath);

var braveApiKey = config["WebSearchSettings:BraveApiKey"];
var maxContextTokens = int.TryParse(config["AgentSettings:MaxContextTokens"], out var mct) ? mct : 6000;

var workspace = new WorkspaceContext(fullWorkspacePath);
var llm = new LlamaClient(serverUrl, model);

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

if (!isStdinProtocol)
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
if (!isStdinProtocol) Console.WriteLine();

// ── System prompt ─────────────────────────────────────────────────────────────
string BuildSystemPrompt()
{
    var agentsMd = Path.Combine(workspace.Root, "AGENTS.md");
    var agentsContext = File.Exists(agentsMd)
        ? $"\nCONTESTO DEL PROGETTO (da AGENTS.md):\n{File.ReadAllText(agentsMd)}\n"
        : "\nNessun AGENTS.md trovato. Usa /init per generarlo.\n";

    return $"""
        Sei un agente di sviluppo software autonomo. Workspace: {workspace.Root}
        {agentsContext}
        REGOLE:
        - CRITICO: se devi leggere un altro file, eseguire un comando o chiamare un altro tool per continuare il task,
          chiamalo SUBITO nella stessa risposta — non scrivere testo che annuncia il prossimo passo ("Ora leggo X",
          "Procedo con Y", "Inizio controllando Z") senza poi chiamare davvero il tool. Una risposta di solo testo
          viene mostrata all'utente come risposta FINALE e il task si interrompe lì: l'utente non vede il messaggio
          finché non scrive di nuovo, quindi annunciare un passo senza eseguirlo blocca il task inutilmente.
          Scrivi testo all'utente SOLO quando il task è davvero concluso o hai bisogno di una decisione da lui.
        - Non leggere mai lo stesso file due volte; i contenuti sono già in cronologia.
        - Se read_file restituisce [WARNING]: usa edit_file o leggi con start_line/end_line.
        - Per file >150 righe: usa search_in_files o glob_files per trovare la sezione, poi read_file con range.
        - Non leggere file .sln/.slnx.
        - Usa SEMPRE edit_file per modificare file esistenti (mai write_file sull'intero file).
        - edit_file: old_string deve contenere MAX 8-10 righe — non copiare interi metodi o classi.
        - write_file: SOLO per file di testo semplice (config, markdown, plain text). MAI per file con codice C# che contiene virgolette, $"...", [Attributi("...")], ecc.
        - Per creare un file C# con codice: 1) write_file con struttura base (namespace + classe vuota), 2) edit_file per aggiungere metodi uno alla volta.
        - Se old_string non è univoco aggiungi contesto circostante.
        - Dopo modifiche .NET: esegui sol_analyze (non run_dotnet build).
        - Comandi che restano in esecuzione e non terminano da soli (dotnet run, npm start, ng serve, server di sviluppo, watch):
          chiama SEMPRE run_command con background=true. Senza background=true il tool va in timeout dopo 60s e il comando viene ucciso.
          Mai usare l'operatore '&' di PowerShell per eseguire in background: non ha questo effetto.
          Dopo l'avvio, usa get_background_output con il PID restituito per leggere i log e verificare che il processo sia partito correttamente.
        - {(OperatingSystem.IsWindows()
            ? "run_command esegue su Windows PowerShell 5.1: NON supporta gli operatori '&&' e '||'. Usa ';' per separare comandi sequenziali, oppure due chiamate separate a run_command se il secondo comando deve eseguire solo se il primo ha successo."
            : "run_command esegue su bash: puoi usare '&&' e '||' per concatenare comandi condizionalmente.")}
        - Dopo [ESITO: SUCCESSO]: rispondi all'utente, niente altri step.
        - Non ripetere mai lo stesso tool call con gli stessi argomenti.
        - Modifiche minimali; no pattern/astrazioni non richiesti.
        - Rispondi sempre in italiano. Riepilogo breve (2-4 righe) a task completato: cosa è cambiato, non come funziona il codice (il codice stesso lo spiega). Niente preamboli tipo "Certo, procedo subito" o riassunti finali di ciò che hai già mostrato passo passo — l'utente vede già ogni tool call. Una domanda semplice merita una risposta diretta, non sezioni e liste puntate.
        - "Non verificabile dal codice disponibile" si usa SOLO per domande su comportamento a runtime,
          dati esterni o cose che il codice statico non può confermare. Se hai già letto il file richiesto
          (è in cronologia), NON è "non verificabile": analizzane il contenuto e rispondi nel merito
          (struttura, problemi, bug, suggerimenti) — non rifiutarti.
        - Hai SEMPRE accesso ai file del workspace tramite i tool — non dichiarare mai di non avere accesso.
          Se ti viene chiesto il contenuto di un file, chiama read_file (o glob_files se non conosci il path esatto).
        - Se la richiesta è ambigua (es. "il file" senza nome) ma un path è già emerso in questa conversazione,
          usa quel path. Altrimenti chiedi all'utente di specificare quale file.
        - STRATEGIA PER FILE GRANDI: Se un file ha più di 150 righe, NON usare 'read_file' per leggerlo interamente. Invece, usa 'search_in_files' o 'sym_search' per individuare la sezione specifica e poi usa 'read_file_range' per leggere solo le righe necessarie. Questo è fondamentale per non superare il limite di contesto del modello.
        - CORREZIONE ERRORI: Se un tool restituisce un [ERROR] o indica che una stringa è ambigua, non ripetere lo stesso comando. Analizza la risposta, identifica l'errore (es. percorso errato, file mancante) e cambia strategia immediatamente (es. cerca il nome del file nel workspace prima di provare a leggerlo).
        - AUTONOMIA: Se un task richiede più passaggi (es. trovare una funzione -> leggere il suo contenuto -> modificarlo), esegui i tool uno dopo l'altro finché non raggiungi l'obiettivo finale. Non fermarti dopo un singolo tool se il lavoro non è concluso.
        - PIANO: per task con 3+ step distinti (refactoring multi-file, migrazioni, debugging con più ipotesi da verificare), chiama manage_todo con action="create" PRIMA di iniziare per mostrare il piano all'utente, poi aggiornalo (action="update") portando ogni item a in_progress/completed man mano che procedi. Non usarlo per task banali (1-2 azioni).
        - MEMORIA DELLA CONVERSAZIONE: la cronologia di questa conversazione include le tue azioni precedenti (file letti, modifiche fatte, comandi eseguiti). Prima di proporre un workaround o creare un nuovo file per aggirare un problema, controlla se in un turno precedente hai già creato un file simile (es. un file di dichiarazione .d.ts, un file di configurazione) — potrebbe essere proprio quello la causa del problema attuale, non una nuova ipotesi da testare da zero.
        - AZIONE DECISIVA: dopo aver ispezionato alcuni file/risultati e formulato un'ipotesi sulla causa, NON continuare a raccogliere altre informazioni "per sicurezza" — esegui subito l'azione correttiva (edit_file/write_file/run_command). Se dopo l'azione il problema persiste, allora ispeziona di nuovo con un'ipotesi diversa. Alternare ispezione e azione, non accumulare solo ispezioni.
        - FILE LOCKATI: se run_command fallisce su un'operazione di cancellazione/scrittura per errore di permessi (file in uso), non ritentare lo stesso comando identico. Controlla prima list_background_jobs: se c'è un processo in background avviato da te (es. dev server), fermalo con stop_background_job e riprova UNA volta. Se il problema persiste comunque, fermati e chiedi all'utente di chiudere manualmente eventuali processi/editor che potrebbero tenere i file aperti (dev server, watcher) prima di continuare.
        - REVISIONE/ANALISI DI CODICE (task tipo "controlla questo file", "trova bug", "rivedi la logica"): non affermare MAI che un problema esiste basandoti su pattern generici o su cosa ricordi da una lettura di turni precedenti. Per OGNI problema che segnali: (1) chiama read_file/read_file_range in QUESTO turno sulla riga esatta prima di scriverne, (2) cita nella risposta il testo letterale della riga come appare nel risultato del tool (con il numero di riga che il tool restituisce, mai un numero stimato a memoria), (3) se vicino al codice c'è un commento che ne spiega il motivo, non segnalarlo come bug a meno che tu non abbia uno scenario concreto (input/stato → output sbagliato o crash) che lo contraddice — "sembra rischioso" non è un problema, è una sensazione. Se non riesci a soddisfare questi tre punti per un presunto problema, non riportarlo.
        - GIT COMMIT: chiama git_add/git_commit SOLO se l'utente lo ha chiesto esplicitamente in questo turno (es. "fai il commit", "committa"). Non committare mai come passo automatico alla fine di un task di modifica codice, anche se il task è concluso con successo — l'utente potrebbe voler rivedere le modifiche prima. Stessa cautela per git_checkout con create_new=true (crea un branch): solo su richiesta esplicita.
        - PRIMA DI DICHIARARE QUALCOSA "MANCANTE" O "NON IMPLEMENTATO" (una funzione, un tool, una classe): non basarti sul contenuto di UN SOLO file o di UN SOLO documento — cerca nell'intero progetto con search_in_files o glob_files (es. per nome della funzione/tool) prima di concludere che non esiste. Un file .md che descrive un'architettura può essere un riferimento/blueprint generico scritto in un altro momento, non la specifica sincronizzata dell'implementazione reale: se hai un dubbio su cosa rappresenti un documento, dillo esplicitamente invece di trattarlo come fonte di verità. Se hai un'ipotesi non verificata ("potrebbe essere altrove"), verificala TU con un tool prima di scriverla come conclusione — non lasciarla come domanda aperta all'utente quando hai già gli strumenti per rispondere da solo.
        """;
}

var sessionManager = new SessionManager(llm, maxContextTokens, BuildSystemPrompt);

var history = new List<ChatMessage>();
void ResetHistory() => history = [ChatMessage.System(BuildSystemPrompt())];
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

// ── Agent loop ────────────────────────────────────────────────────────────────
// Restituisce true se il LLM ha risposto (anche senza tool call), false su errore HTTP.
// I tre delegate opzionali permettono di riusare lo STESSO loop (retry LLM, anti-loop,
// riflessione interna, rollover history) sia in modalità REPL (default: null → stampa su
// Console/UI come sempre) sia in modalità --stdin-protocol (emettono eventi NDJSON).
async Task<bool> RunAgentLoopAsync(
    int maxTokens = 4096,
    Action<string>? onToken = null,
    Action<string, string>? onToolCall = null,
    Action<string, string>? onToolResult = null)
{
    // Guard anti-loop: blocca chiamate identiche (stesso tool + stessi argomenti)
    // ripetute troppe volte — i modelli locali a volte ignorano il soft-block testuale
    // e ricontinuano a richiamare lo stesso tool all'infinito fino a maxSteps.
    var repeatCounts = new Dictionary<string, int>();
    const int maxRepeats = 3;
    bool loopDetected = false;

    // Tool di polling: rileggere lo stesso PID con gli stessi argomenti è il modo corretto
    // di attendere che un processo in background finisca di avviarsi, non un loop anomalo.
    var pollableTools = new HashSet<string> { "get_background_output", "list_background_jobs" };

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
    };
    const int maxConsecutiveReadOnly = 6;
    int consecutiveReadOnly = 0;

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
            try
            {
                var el = JsonDocument.Parse(argsJson).RootElement;
                var path = el.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (path == null) return;
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    hasUnverifiedDotnetEdit = true;
                else if (codeExtensions.Contains(Path.GetExtension(path)))
                    hasUnverifiedOtherEdit = true;
            }
            catch { }
        }
        else if (toolName == "sol_analyze")
        {
            hasUnverifiedDotnetEdit = result.Contains("FALLITO") || Regex.IsMatch(result, @"error CS\d+");
        }
        else if (toolName == "run_dotnet")
        {
            try
            {
                var el = JsonDocument.Parse(argsJson).RootElement;
                var a = el.TryGetProperty("args", out var av) ? av.GetString() ?? "" : "";
                if (Has(a, "build", "test", "run"))
                    hasUnverifiedDotnetEdit = result.Contains("FALLITO") || Regex.IsMatch(result, @"error CS\d+");
            }
            catch { }
        }
        else if (toolName == "run_command")
        {
            // Non possiamo sapere a priori quale sia il comando di build/lint/test corretto
            // per uno stack non-.NET (npm run build / pytest / eslint / ...): ci basiamo sul
            // fatto che il modello abbia REALMENTE chiamato un tool per verificare, invece di
            // dichiarare a parole un esito mai controllato.
            hasUnverifiedOtherEdit = false;
        }
    }

    bool Has(string s, params string[] kw) =>
        kw.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

    string ExecuteToolGuarded(string name, string argsJson)
    {
        if (pollableTools.Contains(name))
            return dispatcher.Execute(name, argsJson);

        var key = $"{name}|{argsJson}";
        repeatCounts.TryGetValue(key, out var count);
        repeatCounts[key] = ++count;
        if (count > maxRepeats)
        {
            loopDetected = true;
            return $"[LOOP-BLOCK] Tool '{name}' chiamato {count} volte con argomenti identici. " +
                   "Interrotto per evitare un loop infinito.";
        }

        consecutiveReadOnly = readOnlyTools.Contains(name) ? consecutiveReadOnly + 1 : 0;

        return dispatcher.Execute(name, argsJson);
    }

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
        history = await sessionManager.MaybeRolloverAsync(history);

        // ── Call LLM (streaming) ──────────────────────────────────────────────
        ChatMessage message;
        Usage? usage;

        const int maxLlmRetries = 3;
        var llmAttempt = 0;

        while (true)
        {
            llmAttempt++;
            var streamedContent = false;

            try
            {
                var context = history.LastOrDefault(m => m.Role == "user")?.Content ?? "";
                var request = new ChatRequest
                {
                    Messages = history,
                    Tools = dispatcher.GetRelevantDefinitions(context),
                    ToolChoice = "auto",
                    MaxTokens = maxTokens,
                    Temperature = 0.0f
                };

                // Print step indicator (solo REPL: in stdin-protocol andrebbe su stdout e
                // romperebbe il parsing NDJSON lato extension)
                if (onToken == null) Console.WriteLine();
                UI.StepIndicator(step);

                (message, usage) = await llm.StreamChatAsync(request, token =>
                {
                    if (!streamedContent)
                    {
                        if (onToken == null)
                        {
                            // First token: print "Agent ›" prefix (solo REPL, mai in stdin-protocol)
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.Write("Agent › ");
                            Console.ResetColor();
                        }
                        streamedContent = true;
                    }
                    if (onToken != null) onToken(token);
                    else Console.Write(token);
                });

                if (onToken == null && streamedContent) Console.WriteLine();
                break; // risposta ricevuta con successo
            }
            catch (HttpRequestException ex)
            {
                var isJsonError = ex.Message.Contains("parse tool call") ||
                                  ex.Message.Contains("missing closing quote") ||
                                  ex.Message.Contains("invalid string");

                if (isJsonError)
                {
                    UI.Error("Errore LLM 500: JSON non valido nel tool call (virgolette nel codice).");
                    UI.Dim("  → Il modello ha tentato write_file/edit_file con contenuto non escapabile.");
                    // Inietta un hint di recupero come prossimo turno utente
                    history.Add(ChatMessage.User(
                        "ERRORE 500: il tentativo precedente ha generato JSON non valido perché " +
                        "il contenuto del file contiene virgolette o $\" non escapabili. " +
                        "Riprendi il task usando SOLO edit_file con old_string di MAX 5-8 righe. " +
                        "Non usare write_file per file con codice C#. " +
                        "Se devi creare un file da zero: prima write_file con la struttura base (classe vuota), " +
                        "poi edit_file per aggiungere il corpo dei metodi."));
                    return false;
                }

                // ex.StatusCode è null solo per fallimenti di TRASPORTO (connessione persa/rifiutata
                // prima che arrivi una risposta HTTP) — non per risposte HTTP di errore vere e proprie,
                // che qui arrivano già con uno status code impostato esplicitamente. Ritentare un 400/500
                // non cambierebbe nulla (stessa richiesta, stesso esito); un fallimento di trasporto invece
                // è spesso transitorio (server locale sotto carico) e vale la pena ritentare — ma SOLO se
                // non abbiamo già stampato token parziali, altrimenti un retry duplicherebbe l'output.
                if (ex.StatusCode is null && !streamedContent && llmAttempt < maxLlmRetries)
                {
                    var delayMs = 1000 * (int)Math.Pow(2, llmAttempt - 1);
                    UI.Dim($"  [connessione al server LLM persa — retry {llmAttempt}/{maxLlmRetries - 1} tra {delayMs / 1000}s]");
                    await Task.Delay(delayMs);
                    continue;
                }

                UI.Error($"Errore LLM: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                UI.Error($"Errore: {ex.Message}");
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

        history.Add(message);

        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var tc in message.ToolCalls)
            {
                if (onToolCall != null) onToolCall(tc.Function.Name, tc.Function.Arguments);
                else UI.ToolCall(tc.Function.Name, tc.Function.Arguments);

                var result = ExecuteToolGuarded(tc.Function.Name, tc.Function.Arguments);
                TrackBuildState(tc.Function.Name, tc.Function.Arguments, result);

                if (onToolResult != null) onToolResult(tc.Function.Name, result);
                else UI.ToolResult(result);

                history.Add(ChatMessage.ToolResult(tc.Id, result));

                // --- LOGICA DI RIFLESSIONE POTENZIATA ---
                // Se il risultato contiene parole chiave di errore, iniettiamo un comando di sistema 
                // che forza l'LLM a cambiare strategia nel prossimo giro.
                bool isError = result.Contains("[ERROR]") ||
                               result.Contains("non trovato") ||
                               result.Contains("fallito", StringComparison.OrdinalIgnoreCase) ||
                               result.Contains("ambigua") ||
                               result.Contains("[ESITO: FALLITO", StringComparison.OrdinalIgnoreCase);

                if (isError)
                {
                    // Ruolo "user" (non "system"): alcuni chat template (es. Qwen3) impongono che
                    // il ruolo "system" compaia SOLO come primissimo messaggio e sollevano un'eccezione
                    // Jinja ("System message must be at the beginning") se compare altrove.
                    history.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = "[INTERNAL_REFLECTION]: L'operazione precedente ha fallito o è stata ambigua. Analizza il motivo (percorso errato? stringa non univoca? file mancante?) e proponi una soluzione correttiva diversa nel prossimo passo."
                    });
                }
                else if (consecutiveReadOnly >= maxConsecutiveReadOnly)
                {
                    // Troppi step di sola ispezione di fila senza mai agire: forziamo una
                    // decisione ORA invece di lasciare che continui a "rimuginare" chiamando
                    // altri tool di sola lettura con argomenti leggermente diversi (che il
                    // blocco anti-loop sopra non intercetta, dato che non sono identici).
                    history.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = $"[INTERNAL_REFLECTION]: Hai eseguito {consecutiveReadOnly} controlli di sola lettura consecutivi senza eseguire nessuna azione concreta (edit_file/write_file/run_command/...). " +
                                  "STOP: nel prossimo passo devi SOLO una di queste due cose — (1) eseguire ORA l'azione che risolve il problema in base a quanto hai già scoperto, oppure " +
                                  "(2) se non hai abbastanza certezza, fermarti e scrivere all'utente le ipotesi raccolte chiedendo quale approccio preferisce. Non chiamare un altro tool di sola lettura."
                    });
                    consecutiveReadOnly = 0; // dato un ordine esplicito, riparte il conteggio
                }
                else
                {
                    // Se ha successo, incoraggiamo a procedere o a verificare se il task è finito
                    history.Add(new ChatMessage
                    {
                        Role = "user",
                        Content = "[INTERNAL_REFLECTION]: L'operazione è stata eseguita con successo. Valuta se il task è completato o se devi eseguire ulteriori azioni per raggiungere l'obiettivo."
                    });
                }
            }

            // Impediamo loop infiniti (es. lo stesso tool fallisce 5 volte di fila)
            if (loopDetected)
            {
                UI.Error("Loop rilevato: interruzione.");
                return true;
            }
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
                    if (onToolCall != null) onToolCall(name, argsJson);
                    else UI.ToolCall(name, argsJson);

                    var result = ExecuteToolGuarded(name, argsJson);
                    TrackBuildState(name, argsJson, result);

                    if (onToolResult != null) onToolResult(name, result);
                    else UI.ToolResult(result);

                    history.Add(ChatMessage.ToolResult($"rescued-{step}", result));
                }

                if (loopDetected)
                {
                    UI.Error("Loop rilevato: stesso tool richiamato troppe volte con argomenti identici — interruzione.");
                    return true;
                }
                continue;
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

                if (onToolCall != null) onToolCall("sol_analyze", autoArgs);
                else UI.ToolCall("sol_analyze", autoArgs);

                var buildResult = dispatcher.Execute("sol_analyze", autoArgs);
                TrackBuildState("sol_analyze", autoArgs, buildResult);

                if (onToolResult != null) onToolResult("sol_analyze", buildResult);
                else UI.ToolResult(buildResult);

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

            if (usage is { } u)
                UI.Dim($"[tokens: prompt={u.PromptTokens} completion={u.CompletionTokens} | step={step}]");
            if (onToken == null) Console.WriteLine();
            return true;
        }

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
        history.Add(new ChatMessage
        {
            Role = "assistant",
            Content = "Ho raggiunto il limite di step per questo turno; il task non è ancora concluso. " +
                      "Scrivi \"continua\" per proseguire da dove ho lasciato."
        });
    }

    return true;
}

// ── Modalità --stdin-protocol ───────────────────────────────────────────────────
// Legge richieste NDJSON da stdin ({"type":"request","prompt":"...","context":[...]})
// e scrive eventi NDJSON su stdout (token/tool_call/tool_result/edit_proposal/done).
// Il processo resta vivo per tutta la sessione dell'estensione. La history NON viene
// ricostruita da zero a ogni richiesta: viene mantenuta nel processo tra un turno e
// l'altro, tool call/risultati inclusi — altrimenti l'agent "dimentica" le proprie azioni
// passate (es. un file di workaround creato in un turno precedente) perché il "context"
// che riceve dall'extension è solo il testo visualizzato in chat, non i tool call interni.
// Il "context" ricevuto viene usato SOLO per capire se è iniziata una NUOVA conversazione
// (VS Code lo manda vuoto al primo turno di una chat pulita): in quel caso si resetta.
async Task RunStdinProtocolAsync()
{
    while (true)
    {
        var line = await Console.In.ReadLineAsync();
        if (line == null) break; // stdin chiuso: l'extension ha terminato il processo
        if (string.IsNullOrWhiteSpace(line)) continue;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[stdin-protocol] Riga non valida, ignorata: {ex.Message}");
            continue;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            // Un edit_decision fuori contesto (nessuna proposta in attesa) o un tipo
            // sconosciuto vengono ignorati qui: l'unico consumer legittimo di edit_decision
            // è la proposeWrite dentro FileSystemTools, che legge stdin sincronamente
            // MENTRE questo loop è fermo in attesa (nessuna lettura concorrente possibile).
            if (type != "request") continue;

            var prompt = root.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";

            // Modello scelto dall'utente nel picker nativo di VS Code (request.model),
            // inoltrato dall'extension per questo turno. Se assente o vuoto si ricade sul
            // default di appsettings.json: senza reset esplicito, un modello richiesto in un
            // turno precedente resterebbe attivo anche dopo che l'utente è tornato al default.
            var requestedModel = root.TryGetProperty("model", out var m) ? m.GetString() : null;
            llm.Model = string.IsNullOrWhiteSpace(requestedModel) ? model : requestedModel;

            // Endpoint del server llama-server che serve il modello scelto (se diverso dal
            // default): l'extension lo risolve da chatLanguageModels.json in base al modello.
            // Senza questo, un modello servito da una macchina diversa da quella di default
            // riceverebbe comunque le richieste sul server sbagliato. EndpointOverride (non
            // BaseAddress) perché HttpClient vieta di modificare BaseAddress dopo la prima
            // richiesta già inviata.
            var requestedEndpoint = root.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
            llm.EndpointOverride = string.IsNullOrWhiteSpace(requestedEndpoint) ? null : requestedEndpoint;

            Console.Error.WriteLine($"[stdin-protocol] Turno → modello '{llm.Model}', endpoint '{llm.EndpointOverride ?? serverUrl}'");

            var contextMessages = new List<ChatMessage>();
            if (root.TryGetProperty("context", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ctxEl.EnumerateArray())
                {
                    var role = item.TryGetProperty("role", out var r) ? r.GetString() : null;
                    var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;
                    if (string.IsNullOrEmpty(role) || content == null) continue;
                    contextMessages.Add(new ChatMessage { Role = role, Content = content });
                }
            }

            if (history.Count == 0)
            {
                // Primo turno di vita del processo: se l'extension manda già una history
                // (es. riavvio dell'agent a metà conversazione dopo un crash) la usiamo come
                // seed, altrimenti si parte da zero.
                history = [ChatMessage.System(BuildSystemPrompt()), .. contextMessages, ChatMessage.User(prompt)];
            }
            else if (contextMessages.Count == 0)
            {
                // Context vuoto ma il processo ha già una history: è il segnale che l'utente
                // ha aperto una NUOVA chat in VS Code (context.history riparte da zero) —
                // resettiamo tutto lo stato legato alla conversazione precedente (piano, cache
                // dei file letti, cwd del terminale) per non mescolare conversazioni scollegate.
                // Il processo stesso NON viene ucciso: resta vivo, si resetta solo il suo stato.
                history = [ChatMessage.System(BuildSystemPrompt()), ChatMessage.User(prompt)];
                dispatcher.ResetTodo();
                dispatcher.ClearCache();
                dispatcher.ResetCwd();
            }
            else
            {
                // Continuazione della stessa conversazione: si accoda soltanto il nuovo
                // messaggio, mantenendo intatta tutta la history precedente (tool call e
                // risultati inclusi) — è questo che dà all'agent memoria delle proprie azioni.
                history.Add(ChatMessage.User(prompt));
            }

            await RunAgentLoopAsync(
                maxTokens: 4096,
                onToken: text => EmitEvent(new { type = "token", text }),
                onToolCall: (tool, argsJson) =>
                {
                    object args;
                    try { args = JsonSerializer.Deserialize<JsonElement>(argsJson); }
                    catch { args = argsJson; }
                    EmitEvent(new { type = "tool_call", tool, args });
                },
                onToolResult: (tool, result) => EmitEvent(new { type = "tool_result", tool, result }));

            EmitEvent(new { type = "done" });
        }
    }
}

if (isStdinProtocol)
{
    await RunStdinProtocolAsync();
    return;
}

// ── Comandi speciali ──────────────────────────────────────────────────────────
async Task<bool> HandleSpecialCommandAsync(string input)
{
    // Se l'input non inizia con '/', non è un comando speciale, 
    // quindi non dobbiamo fare nulla qui. Lasciamo che passi al resto del programma.
    if (!input.Trim().StartsWith("/"))
    {
        return false;
    }

    var lower = input.ToLower().Trim();

    switch (lower)
    {
        case "/reset":
            ResetHistory();
            dispatcher.ClearCache();
            dispatcher.ResetCwd();
            dispatcher.ResetTodo();
            UI.Ok("Conversazione resettata.");
            return true;

        case "/workspace":
            UI.Dim($"Workspace: {workspace.Root}");
            UI.Dim(workspace.GetTreeSnapshot());
            return true;

        case "/tools":
            foreach (var t in dispatcher.AllDefinitions)
                UI.Dim($"  • {t.Function.Name,-22} — {t.Function.Description[..Math.Min(70, t.Function.Description.Length)]}");
            return true;

        case "/init":
            UI.Info("Analisi del progetto in corso...");
            dispatcher.ClearCache();
            var agentsMdPath = Path.Combine(workspace.Root, "AGENTS.md");
            var agentsMdNote = File.Exists(agentsMdPath)
                ? "AGENTS.md esiste già — aggiornalo, non riscriverlo da zero."
                : "AGENTS.md non esiste — crealo con write_file.";

            history.Add(ChatMessage.User($"""
                Esegui questi passi IN ORDINE STRETTO:
                1. Chiama get_workspace_tree (una sola volta).
                2. Usa glob_files per trovare i file di configurazione chiave (.csproj, package.json, ecc.).
                3. Leggi AL MASSIMO 3 file usando i path ESATTI dal tree. NON inventare path.
                   NON leggere .sln/.slnx. Leggi il .csproj per la versione .NET.
                4. Chiama OBBLIGATORIAMENTE write_file per scrivere AGENTS.md (max 200 parole).
                   NON includere JSON o il contenuto nel testo — usa il tool write_file.

                Se read_file restituisce [WARNING], saltalo e passa al prossimo.

                AGENTS.md deve contenere:
                - Scopo del progetto (1 riga)
                - Stack e versioni reali
                - Comandi build/run con path esatti
                - Struttura cartelle principale (3-4 righe)
                - Note operative per agente AI (2-3 righe)

                {agentsMdNote}
                """));

            Console.WriteLine();
            bool initOk = await RunAgentLoopAsync(maxTokens: 1024);

            // Ritenta solo se il LLM ha risposto ma ha incluso write_file nel testo invece di chiamarlo.
            // Non ritentare su errore HTTP (initOk == false) — il server è già occupato.
            if (initOk && !File.Exists(agentsMdPath))
            {
                UI.Info("write_file non eseguito — forzo secondo tentativo...");
                history.Add(ChatMessage.User(
                    "ERRORE: hai incluso il JSON nel testo invece di chiamare write_file come tool. " +
                    "Chiama ADESSO il tool write_file con path='AGENTS.md' e il contenuto che hai già preparato. " +
                    "Non scrivere altro testo — esegui solo write_file."));
                await RunAgentLoopAsync(maxTokens: 512);
            }

            ResetHistory();
            UI.Ok(File.Exists(agentsMdPath)
                ? "AGENTS.md generato e caricato nel contesto."
                : "AGENTS.md non creato — riprova con /init.");
            return true;

        case "/help":
            UI.Dim("Comandi:");
            UI.Dim("  /init        — Analizza il progetto e genera AGENTS.md");
            UI.Dim("  /reset       — Nuova conversazione (ricarica AGENTS.md)");
            UI.Dim("  /workspace   — Mostra struttura workspace");
            UI.Dim("  /tools       — Elenca tutti i tool disponibili");
            UI.Dim("  /cd <path>   — Cambia workspace");
            UI.Dim("  /clear       — Pulisce lo schermo");
            UI.Dim("  /paste       — Modalità input multiriga (termina con '---')");
            UI.Dim("  ↑ / ↓        — Naviga cronologia comandi");
            UI.Dim("  quit / /quit — Esci");
            return true;

        case "/clear":
            try { Console.Clear(); } catch { }
            return true;

        

        case "/paste":
            UI.Dim("Inserisci il testo (termina con '---' su una riga a sé):");
            var sb = new System.Text.StringBuilder();
            string? ln;
            while ((ln = Console.ReadLine()) != null && ln.Trim() != "---")
                sb.AppendLine(ln);
            var pastedText = sb.ToString().TrimEnd();
            if (!string.IsNullOrEmpty(pastedText))
            {
                history.Add(ChatMessage.User(pastedText));
                Console.WriteLine();
                await RunAgentLoopAsync();
            }
            return true;

        default:
            if (input.StartsWith("/cd ", StringComparison.OrdinalIgnoreCase))
            {
                var newPath = input[4..].Trim();
                try
                {
                    workspace.SetRoot(newPath);
                    ResetHistory();
                    dispatcher.ClearCache();
                    dispatcher.ResetCwd();
                    dispatcher.ResetTodo();
                    UI.Ok($"Workspace: {workspace.Root}");
                    var hasAgentsMd = File.Exists(Path.Combine(workspace.Root, "AGENTS.md"));
                    UI.Dim(hasAgentsMd ? "AGENTS.md trovato." : "Nessun AGENTS.md — usa /init.");
                }
                catch (Exception ex) { UI.Error(ex.Message); }
                return true;
            }
            return false;
    }
}

// ── Main REPL ─────────────────────────────────────────────────────────────────
UI.Dim("Task (es: 'Crea una console app Hello World in C#')");
UI.Dim("Comandi: /help  /init  /reset  /workspace  /tools  /paste  /cd <path>  quit\n");

while (true)
{
    UI.Prompt();
    var userInput = ConsoleInput.ReadLine();

    if (userInput == null) { Console.WriteLine(); continue; } // Ctrl+C
    userInput = userInput.Trim();

    if (string.IsNullOrEmpty(userInput)) continue;
    if (userInput.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
        userInput.Equals("/quit", StringComparison.OrdinalIgnoreCase)) break;
    if (await HandleSpecialCommandAsync(userInput)) { Console.WriteLine(); continue; }

    history.Add(ChatMessage.User(userInput));
    Console.WriteLine();
    await RunAgentLoopAsync();
}

UI.Dim("\nArrivederci!");

// ── UI helpers ────────────────────────────────────────────────────────────────
static class UI
{
    public static void Header(string title, string sub)
    {
        Write("╔══════════════════════════════════════════════╗", ConsoleColor.DarkYellow);
        Write($"║  {title,-44}║", ConsoleColor.DarkYellow);
        Write($"║  {sub,-44}║", ConsoleColor.DarkGray);
        Write("╚══════════════════════════════════════════════╝", ConsoleColor.DarkYellow);
    }

    public static void Ok(string msg) => Write($"✓ {msg}", ConsoleColor.Green);
    public static void Error(string msg) => Write($"✗ {msg}", ConsoleColor.Red);
    public static void Info(string msg) => Write($"► {msg}", ConsoleColor.Yellow);
    public static void Dim(string msg) => Write(msg, ConsoleColor.DarkGray);
    public static void Prompt()
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Error.Write("You › ");
        Console.ResetColor();
    }

    public static void StepIndicator(int step)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Error.Write($"  [{step}] ");
        Console.ResetColor();
    }

    public static void ToolCall(string name, string args)
    {
        Console.WriteLine();
        Write("  ⚙ ", ConsoleColor.Yellow, nl: false);
        Write(name, ConsoleColor.Cyan, nl: false);
        Write($"({Truncate(args.Replace('\n', ' '), 100)})", ConsoleColor.DarkGray);
    }

    public static void ToolResult(string result)
    {
        var lines = result.Split('\n');
        var preview = lines.Length <= 3
            ? result.Replace('\n', ' ')
            : string.Join(' ', lines.Take(3)) + $" …(+{lines.Length - 3} righe)";
        Write($"    → {Truncate(preview, 150)}", ConsoleColor.DarkGray);
    }

    // Tutto il logging/UI passa da stderr, non da stdout: in modalità --stdin-protocol
    // stdout è riservato esclusivamente alle righe NDJSON di EmitEvent — se un banner o un
    // messaggio di log finisse su stdout romperebbe il parsing lato extension. In modalità
    // REPL interattiva questo non cambia nulla di visibile: il terminale mostra comunque
    // stdout e stderr mescolati insieme.
    private static void Write(string msg, ConsoleColor color, bool nl = true)
    {
        Console.ForegroundColor = color;
        if (nl) Console.Error.WriteLine(msg); else Console.Error.Write(msg);
        Console.ResetColor();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

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

        // Righe di schermo occupate da un input lungo `len` caratteri a partire da startLeft:
        // un input più lungo della larghezza del terminale va a capo automaticamente (wrap).
        int RowsFor(int len)
        {
            var width = Math.Max(1, Console.BufferWidth);
            var totalCells = startLeft + len;
            return totalCells <= 0 ? 1 : (totalCells - 1) / width + 1;
        }

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

                var targetTotal = startLeft + position;
                var targetTop = startTop + targetTotal / width;
                var targetLeft = targetTotal % width;
                Console.SetCursorPosition(Math.Min(targetLeft, width - 1), Math.Min(Math.Max(targetTop, 0), height - 1));
            }
            catch { }
        }

        void MoveTo(int pos)
        {
            try
            {
                var width = Math.Max(1, Console.BufferWidth);
                var total = startLeft + pos;
                var top = startTop + total / width;
                var left = total % width;
                Console.SetCursorPosition(Math.Min(left, width - 1), Math.Min(Math.Max(top, 0), Console.BufferHeight - 1));
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
