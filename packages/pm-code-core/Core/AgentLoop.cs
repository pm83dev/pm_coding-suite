using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LocalCodeAgent.Models;

namespace LocalCodeAgent.Core
{
    /// <summary>
    /// Loop ReAct dell'agente, estratto da <c>agent/pm-code/Program.cs</c> (RunAgentLoopAsync +
    /// tutti i suoi guard e helper) nella Fase 1 del refactor. È l'UNICO posto che contiene la
    /// logica del loop e dei guard (anti-loop, quasi-identico, stesso-errore, read-only,
    /// functional-check, line-budget, build): i backend (pm-code CLI e agent-backend) la riusano
    /// senza duplicarla. Non sa niente di REPL/serve/stdin-protocol: tutto il testo/tool-call/
    /// errore/notice passa SOLO da <see cref="_sink"/> (IAgentSink), che riproduce l'output di
    /// ciascuna modalità, e da <see cref="_ui"/> (formattazione REPL su stderr) e
    /// <see cref="_trace"/> (log diagnostico su stderr/file, solo in modalità macchina).
    /// </summary>
    /// <summary>
    /// Hook opzionale per il controllo dei permessi sui tool (es. modalità readonly/ask/auto
    /// dell'agent-backend). Se impostato, viene chiamato PRIMA di ogni esecuzione tool:
    /// restituisce <c>null</c> per procedere con l'esecuzione normale, oppure una stringa
    /// (errore/messaggio) da usare come risultato del tool senza eseguirlo.
    /// Il contesto passa il nome del tool, i suoi argomenti JSON e il token di cancellazione.
    /// </summary>
    public delegate Task<string?> ToolPermissionHook(string toolName, string argumentsJson, CancellationToken ct);

    public sealed class AgentLoop
    {
        private readonly LlamaClient _llm;
        private readonly ToolDispatcher _dispatcher;
        private readonly SessionManager _sessionManager;
        private readonly WorkspaceContext _workspace;
        private readonly ExploreSubAgent? _exploreAgent;
        private readonly IAgentSink _sink;
        private readonly AgentLoopOptions _options;
        private readonly Action<string> _ui;
        private readonly Action<string> _trace;
        private readonly Action<string> _notice;
        private readonly Action<string> _onStatus;
        private readonly ToolPermissionHook? _toolPermissionHook;

        // I guard sono intrecciati con lo stato del loop (repeatCounts, sameErrorStreak,
        // linesReadPerFile, ...): per non spezzare il comportamento, nella Fase 1 si muove tutto
        // nel core (regex + state + guard) in AgentLoop. Nella Fase 2 si estraggono i regex in
        // AgentLoopGuards solo se si riesce a farlo senza cambiare il flusso.
        private readonly HashSet<string> _pollableTools =
            new() { "get_background_output", "list_background_jobs" };
        private readonly HashSet<string> _verificationTools =
            new() { "run_command", "run_dotnet", "sol_analyze" };
        private readonly HashSet<string> _readOnlyTools =
        [
            "read_file", "read_file_range", "glob_files", "search_in_files", "sym_search",
            "list_directory", "get_workspace_tree", "get_directory_details",
            "git_status", "git_diff", "git_log", "get_background_output", "list_background_jobs",
            ExploreSubAgent.ToolName,
        ];
        private readonly HashSet<string> _codeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".py", ".js", ".jsx", ".ts", ".tsx", ".svelte", ".vue", ".go", ".java",
            ".rb", ".php", ".c", ".cpp", ".h", ".hpp", ".rs", ".kt", ".swift",
            ".html", ".scss", ".css", ".less"
        };
        private static readonly Regex _actionCommandRegex = new(
            @"^(dotnet |npm |pnpm |yarn |npx |node |python[0-9.]* |pytest |python3? -m|go |cargo |make |cmake |gradle |mvn |swift |ruby|bundle |composer |php |bash |sh |pwsh|powershell)\b" +
            @"|\b(build|test|run|start|exec|serve|install|lint|tsc|vite|webpack|svelte-kit|next|deno|bun)\b" +
            @"|\b(write|edit|create|delete|remove|move|copy|add|commit|push|pull)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _functionalCheckRegex = new(
            @"^(dotnet (test|run)|npm (test|start|run)|pytest|node |python[0-9.]* |go test|cargo test|make test|bash |sh |pwsh|powershell)\b" +
            @"|\b(test|run|start|exec|serve|request|invoke|call)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _buildOnlyRegex = new(
            @"^(dotnet (build|run)|npm run (build|start)|make|cmake|gradle|mvn|swift build|cargo build|tsc|vite build|webpack)\b" +
            @"|\b(build|compile|tsc)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Ogni quanti secondi (min) inviare un ping di stato MENTRE un tool è in esecuzione.
        // Senza questo, durante tool lenti (run_command con timeout fino a 60s, sol_analyze
        // che lancia dotnet build, ...) la chat resta bloccata sull'ultimo heartbeat LLM
        // ("Elaborazione in corso…") finché non arriva tool_call_finished — indistinguibile
        // da un blocco. Stesso pattern di LlamaClient.HeartbeatInterval.
        private static readonly TimeSpan ToolHeartbeatInterval = TimeSpan.FromSeconds(2);

        public AgentLoop(
            LlamaClient llm,
            ToolDispatcher dispatcher,
            SessionManager sessionManager,
            IAgentSink sink,
            WorkspaceContext workspace,
            ExploreSubAgent? exploreAgent,
            AgentLoopOptions options,
            Action<string> ui,
            Action<string> trace,
            Action<string> notice,
            Action<string> onStatus,
            ToolPermissionHook? toolPermissionHook = null)
        {
            _llm = llm;
            _dispatcher = dispatcher;
            _sessionManager = sessionManager;
            _sink = sink;
            _workspace = workspace;
            _exploreAgent = exploreAgent;
            _options = options;
            _ui = ui;
            _trace = trace;
            _notice = notice;
            _onStatus = onStatus;
            _toolPermissionHook = toolPermissionHook;
        }

        public List<ChatMessage>? History { get; private set; }

        /// <summary>
        /// Esegue un turno completo dell'agente (loop ReAct). Restituisce true se il LLM ha
        /// risposto (anche senza tool call), false su errore HTTP/trasporto. Mutates <c>history</c>
        /// in place (stesso contratto di RunAgentLoopAsync oggi): il rollover riassegna la lista e
        /// il chiamante legge il campo <see cref="History"/> dopo il turno.
        /// </summary>
        public async Task<bool> RunTurnAsync(List<ChatMessage> history, CancellationToken ct, int maxTokens = 32768, string? turnId = null)
        {
            History = history;
            // Un turno = una chiamata a questa funzione (vedi commento su StructuredTrace.BeginTurn()
            // più sotto): id breve per IAgentSink, non persistito altrove.
            // Se il chiamante fornisce un turnId (es. agent-backend, che lo usa anche per gli
            // eventi approval_request/status/turn_finished), lo si usa perché l'UI debba vedere
            // lo STESSO turnId in turn_started e negli eventi successivi; altrimenti un id breve.
            turnId ??= Guid.NewGuid().ToString("N")[..8];
            _sink.TurnStarted(turnId, _workspace.Root);

            // ── Helper locali del turno ─────────────────────────────────────────
            static string OneLine(string s, int max)
            {
                var flat = s.Replace("\r", "").Replace('\n', ' ');
                return flat.Length <= max ? flat : flat[..max] + $"…(+{flat.Length - max})";
            }

            // Rileva un "annuncio" di un'azione che il modello non ha eseguito (risposta testuale
            // che finisce con "procedo con...", "ora scrivo...", ecc.): se scatta e non c'è nessuna
            // tool call nel messaggio, il loop lo rimanda a eseguire davvero (maxAnnouncementRetries).
            bool LooksLikeUnfinishedAnnouncement(string text)
            {
                var t = text.TrimEnd();
                if (t.Length == 0) return false;
                if (t.Contains('?')) return false; // probabile domanda genuina all'utente, non un annuncio
                if (t.Contains("Limiti noti", StringComparison.OrdinalIgnoreCase)) return false; // riepilogo finale vero
                // Segnale più forte, indipendente dalla lunghezza: la risposta finisce con ':'.
                if (t.EndsWith(':')) return true;
                // Le frasi ("procedo con", "ora scrivo", ecc.) si cercano solo nella CODA del messaggio.
                var tail = t.Length > 300 ? t[^300..] : t;
                return Regex.IsMatch(tail,
                    @"\b(procedo (con|a)|vado avanti( con)?|continuo con|nel prossimo (step|passo))\b" +
                    @"\b(ora|adesso)\b[^.!?]{0,40}\b(riscrivo|scrivo|modifico|creo|leggo|eseguo|controllo|verifico|aggiorno|implemento|correggo|analizzo|genero)\b",
                    RegexOptions.IgnoreCase);
            }

            // I messaggi di errore/rinuncia finivano SOLO nei log dell'agente (UI.Error scrive su stderr)
            // e mai in chat: in modalità --stdin-protocol il processo terminava il turno "in silenzio".
            // Usa questo per rendere visibile in chat qualunque uscita anticipata dal loop.
            void EmitFinalNotice(string text)
            {
                if (_options.IsMachineMode) _trace("  !! " + OneLine(text, 200));
                history.Add(new ChatMessage { Role = "assistant", Content = text });
                if (_options.IsMachineMode) _sink.AssistantDelta(turnId, text);
                else { Console.WriteLine(); Console.Write(text); Console.WriteLine(); }
            }

            // Guard anti-loop: blocca chiamate identiche (stesso tool + stessi argomenti) ripetute
            // troppe volte; il contatore conta le chiamate identiche CHE DANNO LO STESSO RISULTATO e
            // viene azzerato dopo ogni edit/write riuscito (TrackBuildState). Soglia: MaxRepeats.
            var repeatCounts = new Dictionary<string, (int Count, int ResultHash)>();
            // Chiamate uguali a meno dei numeri (vedi ExecuteToolGuarded): avviso dalla 3ª, blocco oltre maxSimilarCalls.
            var similarCounts = new Dictionary<string, int>();
            const int maxSimilarCalls = 5;
            int lastErrorHash = 0;
            int sameErrorStreak = 0;
            string? lastErrorTool = null;
            bool loopDetected = false;
            bool successfulEditSinceLastVerify = false;
            int consecutiveReadOnly = 0;
            bool readOnlyNudgeGiven = false;
            bool restrictReadOnly = false;
            int readBlocks = 0;
            const int maxReadBlocks = 3;
            const string readBlockedPrefix = "⚠️ LETTURA BLOCCATA";
            const string verifyMissingPrefix = "⚠️ VERIFICA MANCANTE";
            string? stopReason = null;
            bool planTouchedThisTurn = false;
            int planContinueRetries = 0;
            const int maxPlanContinueRetries = 2;
            bool jobsReminderSent = false;
            bool codeEditedThisTurn = false;
            bool functionalCheckSinceEdit = false;
            int functionalCheckRetries = 0;
            const int maxFunctionalCheckRetries = 2;
            int functionalClearRejects = 0;
            const int maxFunctionalClearRejects = 2;
            const string functionalMissingPrefix = "⚠️ VERIFICA FUNZIONALE MANCANTE";
            bool verifyRequired = false;
            var linesReadPerFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            bool hasUnverifiedDotnetEdit = false;
            bool hasUnverifiedOtherEdit = false;
            var autoBuildChecks = 0;
            const int maxAutoBuildChecks = 3;
            var recoverableErrorRetries = 0;
            const int maxRecoverableErrorRetries = 3;
            var announcementRetries = 0;
            const int maxAnnouncementRetries = 2;

            string? FullPathOf(string argsJson)
            {
                try
                {
                    var el = JsonDocument.Parse(argsJson).RootElement;
                    var p = el.TryGetProperty("path", out var pv) ? pv.GetString() : null;
                    return string.IsNullOrEmpty(p) ? null : Path.GetFullPath(Path.Combine(_workspace.Root, p));
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
                    if (already > 0 && already + requested > _options.MaxLinesPerFile)
                        return $"{readBlockedPrefix}: in questo turno hai già letto {already} righe di {Path.GetFileName(full)} " +
                               $"(limite {_options.MaxLinesPerFile} per file). Non leggere il file a pezzi per intero: usa search_in_files o " +
                               "sym_search per trovare il punto preciso che serve al passo corrente e leggi solo quelle poche righe, " +
                               "oppure procedi con la modifica in base a quello che hai già letto.";
                    linesReadPerFile[full] = already + requested;
                }
                catch { }
                return null;
            }

            bool IsReadOnlyCall(string name, string argsJson)
            {
                if (_readOnlyTools.Contains(name)) return true;
                if (name != "run_command") return false;
                try
                {
                    var el = JsonDocument.Parse(argsJson).RootElement;
                    if (el.TryGetProperty("background", out var bg) && bg.ValueKind == JsonValueKind.True) return false;
                    var cmd = el.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
                    // Un run_command conta come AZIONE solo se builda/testa/esegue/modifica qualcosa;
                    // "git diff | Select-Object", "Get-Content", "Select-String", "Copy-Item di un diff"
                    // sono letture travestite e prima azzeravano il conteggio delle letture consecutive.
                    return !_actionCommandRegex.IsMatch(cmd);
                }
                catch { return false; }
            }

            static bool IsFunctionalCheck(string cmd) => _functionalCheckRegex.IsMatch(cmd);

            void TrackBuildState(string toolName, string argsJson, string result)
            {
                if (toolName is "write_file" or "edit_file")
                {
                    // Un edit rifiutato (limite di dimensione, old_string non univoco, ecc.) non ha
                    // toccato il file: non deve far ripartire il conteggio anti-loop dei tool di verifica.
                    if (result.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase))
                        return;

                    try
                    {
                        var el = JsonDocument.Parse(argsJson).RootElement;
                        var path = el.TryGetProperty("path", out var p) ? p.GetString() : null;
                        if (path == null) return;
                        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                            hasUnverifiedDotnetEdit = true;
                        else if (_codeExtensions.Contains(Path.GetExtension(path)))
                            hasUnverifiedOtherEdit = true;
                        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || _codeExtensions.Contains(Path.GetExtension(path)))
                        {
                            codeEditedThisTurn = true;
                            functionalCheckSinceEdit = false;
                        }
                        successfulEditSinceLastVerify = true;
                        // Il disco è cambiato: qualunque rilettura/verifica successiva si riferisce a
                        // uno stato diverso, quindi i conteggi anti-loop precedenti non valgono più.
                        repeatCounts.Clear();
                        // Solo un file di codice: scrivere un nuovo file di input per il test non è una correzione.
                        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || _codeExtensions.Contains(Path.GetExtension(path)))
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
                    if (!result.StartsWith("ERRORE", StringComparison.Ordinal) && !IsReadOnlyCall(toolName, argsJson))
                    {
                        hasUnverifiedOtherEdit = false;
                        verifyRequired = false;
                        // Un'esecuzione reale (non l'avvio in background) conta come verifica funzionale.
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

            // Un fallimento dovuto SOLO al file di output bloccato (MSB3021-3027: exe in uso) non è
            // un errore di codice: la compilazione è riuscita, fallisce solo la copia finale.
            static bool DotnetBuildFailed(string result) =>
                Regex.IsMatch(result, @"error CS\d+") ||
                (result.Contains("FALLITO") && !Regex.IsMatch(result, @"MSB302[1-7]"));

            static bool Has(string s, params string[] kw) =>
                kw.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));

            string ExecuteToolGuarded(string name, string argsJson)
            {
                // In --serve nessuna scrittura né comando shell viene eseguita: niente write/edit/delete
                // su disco, niente processi avviati, a prescindere da autoApprove. Le approvazioni
                // (conferma esplicita via il protocollo, write come diff) sono una fase successiva.
                if (_options.IsServeMode && _options.ServeBlockedTools.Contains(name))
                    return "ERRORE: scrittura non disponibile in modalità serve";

                if (_pollableTools.Contains(name))
                    return _dispatcher.Execute(name, argsJson, ct);

                var key = $"{name}|{argsJson}";

                if (_verificationTools.Contains(name) && successfulEditSinceLastVerify)
                {
                    repeatCounts.Remove(key);
                    successfulEditSinceLastVerify = false;
                }

                // Blocca solo se questa stessa chiamata ha già prodotto maxRepeats volte lo STESSO risultato.
                if (repeatCounts.TryGetValue(key, out var state) && state.Count >= _options.MaxRepeats)
                {
                    loopDetected = true;
                    return $"[LOOP-BLOCK] Tool '{name}' chiamato {state.Count + 1} volte con argomenti identici " +
                           "e risultato identico. Interrotto per evitare un loop infinito.";
                }

                // Chiamate QUASI identiche: stessi argomenti a meno dei numeri. Solo i tool che ESEGUONO.
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
                    // riparte il budget di letture. Solo create (non update).
                    if (Regex.IsMatch(argsJson, @"""action""\s*:\s*""create""", RegexOptions.IgnoreCase))
                    {
                        consecutiveReadOnly = 0;
                        readOnlyNudgeGiven = false;
                        restrictReadOnly = false;
                        readBlocks = 0;
                    }
                    if ((hasUnverifiedDotnetEdit || hasUnverifiedOtherEdit)
                        && (Regex.IsMatch(argsJson, @"""status""\s*:\s*""(completed|done)""", RegexOptions.IgnoreCase)
                            || (Regex.IsMatch(argsJson, @"""action""\s*:\s*""clear""", RegexOptions.IgnoreCase) && _dispatcher.HasPendingPlan)))
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
                    ? _exploreAgent!.RunAsync(argsJson, _onStatus, ct).GetAwaiter().GetResult()
                    : _dispatcher.Execute(name, argsJson, ct);
                if (similarWarning != null) execResult += similarWarning;

                var hash = HashCode.Combine(execResult.Length, execResult);
                var count = repeatCounts.TryGetValue(key, out var prev) && prev.ResultHash == hash ? prev.Count + 1 : 1;
                repeatCounts[key] = (count, hash);

                // Stesso ERRORE dallo stesso tool con argomenti DIVERSI: il guard sopra non lo vede.
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
                    // Successo dopo almeno 2 errori di fila sullo stesso tool: salva la lezione.
                    if (sameErrorStreak >= 2 && name == lastErrorTool)
                    {
                        execResult += $"\n\n[LEZIONE] Hai superato un errore che si era ripetuto {sameErrorStreak} volte. Se la soluzione è " +
                                      "una regola GENERALE (non specifica di questo progetto), salvala ORA con lessons action=\"add\" in una " +
                                      "frase: cosa non funzionava e cosa fare invece. Poi prosegui con il task.";
                        _notice($"errore ripetuto {sameErrorStreak} volte poi superato: chiedo di salvare la lezione");
                    }
                    sameErrorStreak = 0;
                    lastErrorHash = 0;
                }
                lastErrorTool = name;

                // Avviso già dalla seconda ripetizione identica, prima del blocco.
                if (count >= 2)
                    execResult += $"\n\n[RIPETIZIONE {count}/{_options.MaxRepeats}] Questa chiamata è identica a una precedente e ha dato " +
                                  "ESATTAMENTE lo stesso risultato. Ripeterla non cambierà l'esito: se il risultato indica un " +
                                  "errore (es. 401, file mancante, servizio non raggiungibile) cambia approccio, oppure fermati e " +
                                  "chiedi all'utente come procedere.";

                return execResult;
            }

            // Wrapper additivo di tracing attorno a ExecuteToolGuarded: stesso risultato, stesso
            // comportamento, misura solo durata ed esito.
            (string, double) ExecuteToolGuardedTraced(string name, string argsJson)
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

            // Hook di permesso opzionale (agent-backend: readonly/ask/auto). Se impostato e
            // restituisce una stringa, quella stringa diventa il risultato del tool (esecuzione
            // saltata). Se restituisce null, si procede con l'esecuzione normale.
            async Task<(string Result, double DurationMs)> ExecuteToolWithPermissionAsync(
                string name, string argsJson, CancellationToken ct)
            {
                var hook = _toolPermissionHook;
                if (hook != null)
                {
                    var blocked = await hook(name, argsJson, ct);
                    if (blocked != null)
                    {
                        var ms = 0L;
                        StructuredTrace.ToolCall(name, argsJson, false, "permission_denied", ms,
                            System.Text.Encoding.UTF8.GetByteCount(blocked));
                        return (blocked, (double)ms);
                    }
                }
                return ExecuteToolGuardedTraced(name, argsJson);
            }

            // Esegue un'operazione (tipicamente un tool) in background e, ogni
            // ToolHeartbeatInterval, invia un evento "status" con il tempo trascorso così
            // l'UI non resta bloccata su "Elaborazione in corso…" durante tool lenti
            // (run_command, sol_analyze/dotnet build, ...). Restituisce il risultato dell'operazione.
            // Il task di esecuzione parte subito; il loop di heartbeat non lo rallenta.
            async Task<(string Result, double DurationMs)> ExecuteWithHeartbeatAsync(
                string name, string argsJson, CancellationToken ct,
                Func<Task<(string, double)>> execute)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var task = execute();
                while (!task.IsCompleted)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        await Task.WhenAny(task, Task.Delay(ToolHeartbeatInterval, ct));
                    }
                    catch (OperationCanceledException) { break; }
                    if (task.IsCompleted) break;
                    _onStatus($"Esecuzione '{name}' in corso… ({(int)sw.Elapsed.TotalSeconds}s)");
                }
                var (result, durationMs) = await task;
                return (result, durationMs);
            }

            // Recupera tool call "persi" nel testo della risposta (es. JSON inline che il modello
            // ha scritto come testo invece che come tool_call strutturato).
            List<(string, string)> TryRescueTextToolCalls(string text)
            {
                var found = new List<(string, string)>();
                try
                {
                    int i = 0;
                    while (i < text.Length)
                    {
                        int start = text.IndexOf('{', i);
                        if (start < 0) break;
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
                            if (root.TryGetProperty("name", out var nameProp) &&
                                root.TryGetProperty("arguments", out var argsProp))
                            {
                                var name = nameProp.GetString() ?? "";
                                var argsJson = argsProp.GetRawText();
                                if (!string.IsNullOrEmpty(name))
                                    found.Add((name, argsJson));
                            }
                            else if (root.TryGetProperty("path", out _) &&
                                     root.TryGetProperty("content", out _))
                            {
                                found.Add(("write_file", candidate));
                            }
                        }
                        catch { }
                        i = end + 1;
                    }
                }
                catch { }
                return found;
            }
            var turnRequest = history.LastOrDefault(m => m.Role == "user")?.Content;

            // Trace strutturato: un turno = una chiamata a RunTurnAsync, qualunque sia la modalità
            // o il chiamante (REPL, --stdin-protocol, /init, /paste) — unico punto di ingresso comune.
            StructuredTrace.BeginTurn();
            StructuredTrace.UserMessage(turnRequest ?? "");

            // Set di tool fisso per tutto il turno, scelto sulla richiesta dell'utente.
            var turnTools = _dispatcher.GetRelevantDefinitions(turnRequest ?? "");
            turnTools.Add(ExploreSubAgent.Definition);

            // null se promptTokens non è noto (server che non manda usage, o eccezione prima che
            // arrivi): mai calcolato su un valore mancante, vedi StructuredTrace.LlmRequest.
            double? ContextPercentOf(Usage? u) =>
                u != null && _options.MaxContextTokens > 0 ? 100.0 * u.PromptTokens / _options.MaxContextTokens : null;

            // La risposta "incompleta" (finish_reason length / stream troncato) porta con sé l'usage
            // della richiesta: serve per decidere se "length" è il contesto pieno o il budget risposta.
            double? contextPercentForException;
            Usage? incompleteUsage;

            int step = 0;
            while (step < _options.MaxSteps)
            {
                step++;

                // Ricontrolla ad OGNI step, non solo all'inizio del turno: un singolo messaggio
                // utente può generare molti step di tool-call, e la cronologia può superare la
                // soglia di contesto anche a metà di un turno lungo.
                var beforeRollover = history;
                history = await _sessionManager.MaybeRolloverAsync(history, atTurnStart: step == 1, _dispatcher.HasPendingPlan, turnRequest,
                    _dispatcher.PlanSummary(), _workspace.Root,
                    onNotice: text => { if (_options.IsMachineMode) _notice(text); else _ui(text); },
                    onSummary: text => { if (_options.IsMachineMode) _trace("  [rollover] " + text.Replace("\n", "\n  [rollover] ")); });
                History = history;
                // Il contenuto dei file letti è uscito dal contesto: prima di modificarli vanno riletti.
                if (!ReferenceEquals(beforeRollover, history))
                {
                    _dispatcher.ClearCache();
                    consecutiveReadOnly = 0;
                    readOnlyNudgeGiven = false;
                    restrictReadOnly = false;
                    readBlocks = 0;
                }

                // ── Call LLM (streaming) ──────────────────────────────────────
                ChatMessage message;
                Usage? usage;
                string? finishReason;

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
                        // dalla richiesta è più efficace di qualunque istruzione testuale.
                        if (restrictReadOnly)
                            tools = [.. tools.Where(t => !_readOnlyTools.Contains(t.Function.Name))];
                        var request = new ChatRequest
                        {
                            Messages = history,
                            Tools = tools,
                            ToolChoice = "auto",
                            MaxTokens = maxTokens,
                            Temperature = _options.Temperature,
                            FrequencyPenalty = _options.FrequencyPenalty,
                            PresencePenalty = _options.PresencePenalty,
                            TopP = _options.TopP,
                            TopK = _options.TopK,
                            MinP = _options.MinP,
                            RepeatPenalty = _options.RepeatPenalty,
                            RepeatLastN = _options.RepeatLastN
                        };

                        // Print step indicator (solo REPL: in stdin-protocol andrebbe su stdout e
                        // romperebbe il parsing NDJSON lato extension)
                        if (!_options.IsMachineMode) Console.WriteLine();
                        _ui("__STEP__" + step);

                        // Ping immediato appena si inizia a generare: include il numero di step
                        // così l'utente vede in quale fase del turno si è (es. "Step 3: in attesa…").
                        _onStatus($"Step {step}: in attesa della risposta del LLM…");

                        (message, usage, finishReason) = await _llm.StreamChatAsync(request, token =>
                        {
                            streamedContent = true;
                            _sink.AssistantDelta(turnId, token);
                        }, ct, onHeartbeat: _onStatus);
                        llmSw.Stop();
                        StructuredTrace.LlmRequest(usage?.PromptTokens, llmSw.ElapsedMilliseconds, finishReason,
                            contextPercent: ContextPercentOf(usage), maxTokens: maxTokens);
                        StructuredTrace.ContextSize(usage?.PromptTokens ?? 0, _options.MaxContextTokens);

                        // Riga vuota di chiusura SOLO se questa risposta ha streammato testo.
                        if (streamedContent)
                            _sink.AssistantDone(turnId, usage?.PromptTokens ?? 0, usage?.CompletionTokens ?? 0);
                        break; // risposta ricevuta con successo
                    }
                    catch (ToolCallTooLargeException ex)
                    {
                        // Interruzione preventiva lato client (vedi LlamaClient.StreamChatAsync).
                        _ui("__ERROR__Tool call '" + ex.ToolName + "' interrotta: troppo grande, avrebbe fallito comunque.");
                        if (ex.Degenerate)
                        {
                            _notice($"generazione di '{ex.ToolName}' interrotta: argomenti lunghissimi, probabile testo ripetuto in loop");
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
                            _sink.TurnFinished(turnId, "error");
                            return false;
                        }
                        _ui("__DIM__  → Il modello ha tentato di scrivere un intero file/blocco di codice in una sola tool call.");
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
                            _ui($"__DIM__  [auto-retry {recoverableErrorRetries}/{maxRecoverableErrorRetries}] rimando subito al modello con l'istruzione correttiva...");
                            continue;
                        }
                        _ui("__ERROR__Troppi tentativi falliti per lo stesso motivo — mi fermo, riprova scrivendo un nuovo messaggio.");
                        EmitFinalNotice(
                            $"⚠️ Ho provato {maxRecoverableErrorRetries + 1} volte a scrivere '{ex.ToolName}' ma il contenuto generato " +
                            "supera sempre la soglia di sicurezza per una singola tool call (probabile tentativo di scrivere un intero " +
                            "file/componente in un colpo solo). Mi fermo qui — riprova chiedendomi esplicitamente di procedere " +
                            "\"un pezzo alla volta\" (scheletro minimo, poi singoli metodi/blocchi con edit_file).");
                        StructuredTrace.EndTurn("error");
                        _sink.TurnFinished(turnId, "error");
                        return false;
                    }
                    catch (IncompleteLlmResponseException ex)
                    {
                        // La risposta troncata non entra MAI nella cronologia, nemmeno quando si ritenta
                        // la stessa richiesta sotto.
                        llmSw.Stop();
                        contextPercentForException = ContextPercentOf(ex.Usage);
                        incompleteUsage = ex.Usage;
                        StructuredTrace.LlmRequest(ex.Usage?.PromptTokens, llmSw.ElapsedMilliseconds, null,
                            ok: false, errorKind: ex.Kind, contextPercent: contextPercentForException, maxTokens: maxTokens);

                        if (ex.Kind == "length")
                        {
                            // finish_reason="length": NESSUN retry — è un limite deterministico del
                            // server/modello per QUESTA richiesta, non un guasto transitorio.
                            // SOGLIA e TESTI facili da trovare: cercare "LengthContextFullThreshold".
                            const double LengthContextFullThreshold = 0.85; // % di MaxContextTokens oltre cui "length" è probabilmente il contesto, non il budget di risposta
                            const string LengthContextFullMessage =
                                "⚠️ Contesto quasi pieno: avvia una nuova sessione (/reset) o lascia comprimere la conversazione " +
                                "(rollover automatico) prima di continuare — la risposta si è interrotta per limite di lunghezza " +
                                "e il contesto occupato è oltre l'85% del massimo configurato.";
                            const string LengthResponseLimitMessage =
                                "⚠️ Limite di token della risposta raggiunto (finish_reason=\"length\"): il turno si è interrotto " +
                                "prima di finire. Non ho eseguito nessuna tool call da quella risposta (poteva essere tagliata a " +
                                "metà). Riprova con una richiesta più piccola o scomponi il task in passi più piccoli.";

                            _ui("__ERROR__Risposta LLM incompleta (length): " + ex.Message);
                            EmitFinalNotice(contextPercentForException is double p && p / 100.0 > LengthContextFullThreshold
                                ? LengthContextFullMessage
                                : LengthResponseLimitMessage);
                            StructuredTrace.EndTurn("error");
                            _sink.TurnFinished(turnId, "error");
                            return false;
                        }

                        // stream_truncated: 1 tentativo iniziale + 1 retry, backoff fisso 1500ms.
                        const int maxIncompleteAttempts = 2;
                        const int incompleteRetryDelayMs = 1500;
                        if (llmAttempt < maxIncompleteAttempts)
                        {
                            // Separatore richiesto esplicitamente: senza, il testo del tentativo
                            // successivo si attaccherebbe visivamente al frammento troncato in chat.
                            if (_options.IsMachineMode) _sink.AssistantDelta(turnId, "\n\n[risposta interrotta, nuovo tentativo]\n\n");
                            else { _sink.AssistantDone(turnId, 0, 0); _ui("__DIM__[risposta interrotta, nuovo tentativo]"); }
                            _ui($"__DIM__  [stream troncato — retry {llmAttempt}/{maxIncompleteAttempts - 1} tra {incompleteRetryDelayMs}ms]");
                            StructuredTrace.LlmRetry(llmAttempt, maxIncompleteAttempts, ex.Kind, incompleteRetryDelayMs);
                            await Task.Delay(incompleteRetryDelayMs);
                            continue;
                        }

                        _ui("__ERROR__Risposta LLM incompleta (stream_truncated): " + ex.Message);
                        EmitFinalNotice(
                            $"⚠️ La connessione con il server LLM si è interrotta a metà di una risposta in streaming " +
                            $"{maxIncompleteAttempts} volte di seguito. Non ho eseguito nessuna azione parziale. " +
                            "Riprova scrivendo di nuovo, oppure verifica che il server LLM sia stabile.");
                        StructuredTrace.EndTurn("error");
                        _sink.TurnFinished(turnId, "error");
                        return false;
                    }
                    catch (HttpRequestException ex)
                    {
                        var isJsonError = ex.Message.Contains("parse tool call") ||
                                          ex.Message.Contains("missing closing quote") ||
                                          ex.Message.Contains("invalid string");

                        if (isJsonError)
                        {
                            _ui("__ERROR__Errore LLM 500: JSON non valido nel tool call (virgolette/backtick nel codice).");
                            _ui("__DIM__  → Il modello ha tentato write_file/edit_file con contenuto non escapabile.");
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
                                _ui($"__DIM__  [auto-retry {recoverableErrorRetries}/{maxRecoverableErrorRetries}] rimando subito al modello con l'istruzione correttiva...");
                                continue;
                            }
                            _ui("__ERROR__Troppi tentativi falliti per lo stesso motivo — mi fermo, riprova scrivendo un nuovo messaggio.");
                            EmitFinalNotice(
                                $"⚠️ Ho provato {maxRecoverableErrorRetries + 1} volte ma il server continua a rifiutare il JSON generato " +
                                "(virgolette/backtick non escapati nel contenuto del file). Mi fermo qui — riprova chiedendomi " +
                                "esplicitamente di usare edit_file con blocchi piccoli (max 5-8 righe) invece di riscrivere l'intero file.");
                            StructuredTrace.EndTurn("error");
                            _sink.TurnFinished(turnId, "error");
                            return false;
                        }

                        // ex.StatusCode è null solo per fallimenti di TRASPORTO (connessione persa/rifiutata
                        // prima che arrivi una risposta HTTP) — non per risposte HTTP di errore vere e proprie:
                        // un fallimento di trasporto è spesso transitorio (server locale sotto carico) e vale
                        // la pena ritentare — ma SOLO se non abbiamo già stampato token parziali, altrimenti
                        // un retry duplicherebbe l'output.
                        llmSw.Stop();
                        StructuredTrace.LlmRequest(null, llmSw.ElapsedMilliseconds, null, ok: false, errorKind: "http_error", maxTokens: maxTokens);

                        if (ex.StatusCode is null && !streamedContent && llmAttempt < maxLlmRetries)
                        {
                            var delayMs = 1000 * (int)Math.Pow(2, llmAttempt - 1);
                            _ui($"__DIM__  [connessione al server LLM persa — retry {llmAttempt}/{maxLlmRetries - 1} tra {delayMs / 1000}s]");
                            StructuredTrace.LlmRetry(llmAttempt, maxLlmRetries, "http_error", delayMs);
                            await Task.Delay(delayMs);
                            continue;
                        }

                        _ui("__ERROR__Errore LLM: " + ex.Message);
                        StructuredTrace.EndTurn("error");
                        _sink.TurnFinished(turnId, "error");
                        return false;
                    }
                    catch (Exception ex)
                    {
                        _ui("__ERROR__Errore: " + ex.Message);
                        StructuredTrace.EndTurn("error");
                        _sink.TurnFinished(turnId, "error");
                        return false;
                    }
                }
                // Alcuni modelli (es. Kimi-Linear) scrivono le tool call nel proprio formato nativo

                // "functions.nome:indice{...}" invece che nel campo tool_calls OpenAI-standard.

                // Va tentato PRIMA del guard sotto, altrimenti un messaggio con solo la tool call

                // testuale (Content non vuoto ma privo di ToolCalls) passerebbe come risposta finale.

                message.TryExtractFallbackToolCalls();



                // Uno stream che finisce senza token di contenuto né tool_calls produce un ChatMessage

                // con entrambi i campi null: serializzato, llama-server lo rifiuta con 400.

                if (string.IsNullOrEmpty(message.Content) && message.ToolCalls is not { Count: > 0 })

                    message.Content = "(nessuna risposta dal modello)";



                // Ricalibra la stima token del SessionManager con il conteggio REALE del server.

                _sessionManager.RecordRealUsage(usage?.PromptTokens ?? 0, history);



                history.Add(message);

                History = history;



                if (_options.IsMachineMode)

                    _trace($" [step {step}/{_options.MaxSteps}] tool_calls={message.ToolCalls?.Count ?? 0} " +

                           $"prompt_tokens={usage?.PromptTokens ?? 0} testo: {OneLine(message.Content ?? "", 200)}");



                if (message.ToolCalls is { Count: > 0 })

                {

                    var pendingReflections = new List<string>();



                    foreach (var tc in message.ToolCalls)

                    {

                        // Guard difensivo: un modello può emettere una tool call con
                        // Function null o nome vuoto (JSON maleformato). Senza questo
                        // check, tc.Function.Name lancia un NullReferenceException che
                        // sfugge al try del loop LLM e fa crashare il turno.
                        if (tc.Function == null || string.IsNullOrEmpty(tc.Function.Name))
                        {
                            var skipResult = "ERRORE: tool call maleformata (Function null o nome vuoto) — ignorata.";
                            _sink.ToolCallStarted(turnId, tc.Function?.Name ?? "(null)", tc.Function?.Arguments ?? "");
                            _sink.ToolCallFinished(turnId, tc.Function?.Name ?? "(null)", false, skipResult, 0);
                            history.Add(ChatMessage.ToolResult(tc.Id, skipResult));
                            continue;
                        }

                        if (_options.IsMachineMode) _trace($"  -> {tc.Function.Name}({OneLine(tc.Function.Arguments, 300)})");

                        _sink.ToolCallStarted(turnId, tc.Function.Name, tc.Function.Arguments);



                        var (result, toolDurationMs) = await ExecuteWithHeartbeatAsync(
                            tc.Function.Name, tc.Function.Arguments, ct,
                            () => ExecuteToolWithPermissionAsync(tc.Function.Name, tc.Function.Arguments, ct));

                        TrackBuildState(tc.Function.Name, tc.Function.Arguments, result);



                        bool isError = result.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase) ||

                                       result.StartsWith("\u26A0\uFE0F", StringComparison.Ordinal) ||

                                       result.StartsWith("[LOOP-BLOCK]", StringComparison.Ordinal) ||

                                       result.Contains("[ERROR]") ||

                                       result.Contains("non trovato") ||

                                       result.Contains("fallito", StringComparison.OrdinalIgnoreCase) ||

                                       result.Contains("ambigua") ||

                                       result.Contains("[ESITO: FALLITO", StringComparison.OrdinalIgnoreCase);



                        if (_options.IsMachineMode) _trace($"  <- {tc.Function.Name}: {OneLine(result, 300)}");

                        _sink.ToolCallFinished(turnId, tc.Function.Name, !isError, result, toolDurationMs);



                        history.Add(ChatMessage.ToolResult(tc.Id, result));
                        if (result.StartsWith(readBlockedPrefix, StringComparison.Ordinal) ||

                            result.StartsWith(verifyMissingPrefix, StringComparison.Ordinal) ||

                            result.StartsWith(functionalMissingPrefix, StringComparison.Ordinal))

                        {

                            // Il rifiuto spiega già cosa fare: una riflessione generica spingerebbe

                            // il modello a indagare ancora.

                        }

                        else if (isError)

                        {

                            var reflection = result.StartsWith("[LOOP-BLOCK]", StringComparison.Ordinal)

                                ? "[INTERNAL_REFLECTION]: Hai richiamato lo stesso comando con gli stessi argomenti troppe volte di fila senza che il risultato cambiasse. NON ripetere lo stesso comando. Se stai verificando una modifica .NET usa sol_analyze (mai run_command/dotnet build). Se il comando è corretto ma l'errore persiste, il problema non è nel comando: rileggi l'output con attenzione, controlla se le tue modifiche precedenti sono state davvero applicate (es. rileggi il file), e correggi la causa reale — oppure fermati e spiega all'utente cosa hai provato e dove sei bloccato."

                                : "[INTERNAL_REFLECTION]: L'operazione precedente ha fallito o è stata ambigua. Analizza il motivo (percorso errato? stringa non univoca? file mancante?) e proponi una soluzione correttiva diversa nel prossimo passo.";

                            pendingReflections.Add(reflection);

                        }

                        else if (consecutiveReadOnly >= _options.MaxConsecutiveReadOnly)

                        {

                            pendingReflections.Add($"[INTERNAL_REFLECTION]: Hai eseguito {consecutiveReadOnly} letture consecutive (file, ricerche, diff, comandi di sola lettura) senza nessuna azione concreta. " +

                                          "STOP ANALISI: hai già abbastanza informazioni per il passo corrente. Nel prossimo step devi fare SOLO una di queste cose — " +

                                          "(1) applicare ORA la modifica del passo corrente con edit_file/write_file, (2) verificare con sol_analyze o un comando di build/test, " +

                                          "(3) se ti manca davvero una decisione, chiederla all'utente con una domanda precisa. " +

                                          "Se il passo corrente è troppo grande per farlo subito, spezzalo con manage_todo in passi più piccoli e inizia dal primo. " +

                                          "ATTENZIONE: la prossima lettura verrà rifiutata.");

                            consecutiveReadOnly = 0;

                            readOnlyNudgeGiven = true;

                        }

                    }



                    // Impediamo loop infiniti (es. lo stesso tool fallisce 5 volte di fila).

                    if (loopDetected)

                    {

                        _ui("__ERROR__Loop rilevato: interruzione.");

                        EmitFinalNotice(stopReason ??

                            "\u26A0\uFE0F Mi fermo: ho ripetuto più volte la stessa chiamata ottenendo lo stesso risultato. " +

                            "Dimmi come proseguire o indicami un approccio diverso.");

                        StructuredTrace.EndTurn("completed");

                        _sink.TurnFinished(turnId, "completed");

                        return true;

                    }



                    if (pendingReflections.Count > 0)

                        history.Add(new ChatMessage { Role = "user", Content = string.Join("\n\n", pendingReflections.Distinct()) });

                    continue; // Torna all'inizio del ciclo per far elaborare i nuovi dati alla LLM

                }
                // ── Risposta testuale finale ──────────────────────────────────

                if (!string.IsNullOrWhiteSpace(message.Content))

                {

                    var rescued = TryRescueTextToolCalls(message.Content);

                    if (rescued.Count > 0)

                    {

                        _ui("__DIM__  [recupero tool call dal testo]");

                        foreach (var (name, argsJson) in rescued)

                        {

                            if (_options.IsMachineMode) _trace($"  -> {name}({OneLine(argsJson, 300)})");

                            _sink.ToolCallStarted(turnId, name, argsJson);



                            var (result, rescuedDurationMs) = await ExecuteWithHeartbeatAsync(
                                name, argsJson, ct,
                                () => ExecuteToolWithPermissionAsync(name, argsJson, ct));

                            TrackBuildState(name, argsJson, result);



                            var rescuedIsError = result.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase);

                            if (_options.IsMachineMode) _trace($"  <- {name}: {OneLine(result, 300)}");

                            _sink.ToolCallFinished(turnId, name, !rescuedIsError, result, rescuedDurationMs);



                            history.Add(ChatMessage.ToolResult($"rescued-{step}", result));

                        }



                        if (loopDetected)

                        {

                            _ui("__ERROR__Loop rilevato: stesso tool richiamato troppe volte con argomenti identici — interruzione.");

                            EmitFinalNotice(stopReason ??

                                "⚠️ Mi fermo: ho ripetuto più volte la stessa chiamata ottenendo lo stesso risultato. " +

                                "Dimmi come proseguire o indicami un approccio diverso.");

                            StructuredTrace.EndTurn("completed");

                            _sink.TurnFinished(turnId, "completed");

                            return true;

                        }

                        continue;

                    }



                    // ── Guard anti-annuncio ──────────────────────────────────────

                    if (LooksLikeUnfinishedAnnouncement(message.Content))

                    {

                        if (announcementRetries < maxAnnouncementRetries)

                        {

                            announcementRetries++;

                            _notice($"ha annunciato un'azione senza eseguirla, lo rimando a farla ({announcementRetries}/{maxAnnouncementRetries})");

                            _ui($"__DIM__  [auto-continue {announcementRetries}/{maxAnnouncementRetries}] risposta rilevata come annuncio senza tool call — forzo l'esecuzione...");

                            history.Add(new ChatMessage

                            {

                                Role = "user",

                                Content = "[INTERNAL_REFLECTION]: Hai scritto un annuncio del prossimo passo (es. \"Procedo con...\") " +

                                          "senza chiamare il tool corrispondente in questa stessa risposta. Il task NON è concluso. " +

                                          "Chiama SUBITO ORA il tool necessario per eseguire quanto hai appena annunciato — niente altro testo di annuncio."

                            });

                            continue;

                        }



                        _ui("__ERROR__Il modello continua ad annunciare senza eseguire — mi fermo.");

                        EmitFinalNotice(

                            $"⚠️ Ho corretto {maxAnnouncementRetries} volte un annuncio senza esecuzione (\"procedo con...\" " +

                            "senza chiamare il tool), ma il modello ha ripetuto lo stesso testo. Mi fermo qui — riprova scrivendo " +

                            "di nuovo, magari indicando esplicitamente il primo file/tool da usare.");

                        StructuredTrace.EndTurn("completed");

                        _sink.TurnFinished(turnId, "completed");

                        return true;

                    }
                    // ── Gate di build (.NET) ─────────────────────────────────────

                    // Il modello sta per chiudere con testo libero avendo modificato almeno un .cs

                    // senza mai verificarne la compilazione: eseguiamo sol_analyze noi stessi.

                    if (hasUnverifiedDotnetEdit && autoBuildChecks < maxAutoBuildChecks)

                    {

                        autoBuildChecks++;

                        const string autoArgs = "{}";

                        _ui("__DIM__  [auto-check] modifiche a file .cs non verificate — eseguo sol_analyze...");



                        if (_options.IsMachineMode) _trace($"  -> sol_analyze({autoArgs})");

                        _sink.ToolCallStarted(turnId, "sol_analyze", autoArgs);



                        var (buildResult, autoBuildMs) = await ExecuteWithHeartbeatAsync(
                            "sol_analyze", autoArgs, ct,
                            async () =>
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                var r = _dispatcher.Execute("sol_analyze", autoArgs, ct);
                                sw.Stop();
                                await Task.CompletedTask;
                                return (r, sw.ElapsedMilliseconds);
                            });

                        StructuredTrace.ToolCall("sol_analyze", autoArgs, !buildResult.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase),
                            buildResult.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase) ? "tool_error" : null,
                            (long)autoBuildMs, System.Text.Encoding.UTF8.GetByteCount(buildResult));

                        TrackBuildState("sol_analyze", autoArgs, buildResult);



                        if (_options.IsMachineMode) _trace($"  <- sol_analyze: {OneLine(buildResult, 300)}");

                        _sink.ToolCallFinished(turnId, "sol_analyze", !buildResult.StartsWith("ERRORE", StringComparison.OrdinalIgnoreCase), buildResult, autoBuildMs);



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

                    // ── Gate di verifica (stack non-.NET) ─────────────────────────

                    else if (hasUnverifiedOtherEdit && autoBuildChecks < maxAutoBuildChecks)

                    {

                        autoBuildChecks++;

                        _notice("ha modificato codice senza verificarlo, richiesta build/test");

                        _ui("__DIM__  [auto-check] modifiche a file di codice non verificate — richiedo verifica esplicita...");

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
                    // ── Gate di avanzamento del piano ────────────────────────────

                    if (planTouchedThisTurn && _dispatcher.HasPendingPlan && !message.Content.Contains('?')

                        && planContinueRetries < maxPlanContinueRetries && step < _options.MaxSteps - 1)

                    {

                        planContinueRetries++;

                        _notice($"il piano ha ancora passi aperti, lo rimando a proseguire ({planContinueRetries}/{maxPlanContinueRetries})");

                        _ui($"__DIM__  [auto-continue piano {planContinueRetries}/{maxPlanContinueRetries}] passi ancora aperti — proseguo...");

                        history.Add(new ChatMessage

                        {

                            Role = "user",

                            Content = "[AUTO-CONTINUE] Il piano ha ancora passi non completati:\n" + _dispatcher.PlanSummary() +

                                      "\nNon fermarti tra un passo e l'altro: aggiorna con manage_todo il passo appena concluso (se non l'hai fatto) " +

                                      "e prosegui SUBITO con il successivo. Se invece ti serve una decisione dell'utente, chiedila con una domanda esplicita."

                        });

                        continue;

                    }



                    // ── Gate di validazione funzionale ───────────────────────────

                    if (planTouchedThisTurn && codeEditedThisTurn && !functionalCheckSinceEdit && !message.Content.Contains('?')

                        && functionalCheckRetries < maxFunctionalCheckRetries && step < _options.MaxSteps - 1)

                    {

                        functionalCheckRetries++;

                        _notice($"ha solo compilato, gli chiedo di eseguire un test reale ({functionalCheckRetries}/{maxFunctionalCheckRetries})");

                        _ui($"__DIM__  [auto-test {functionalCheckRetries}/{maxFunctionalCheckRetries}] modifiche mai eseguite — richiedo il test funzionale...");

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
                    // ── Promemoria processi in background ─────────────────────────

                    if (!jobsReminderSent && _dispatcher.RunningJobs() is { Count: > 0 } running && step < _options.MaxSteps - 1)

                    {

                        jobsReminderSent = true;

                        var list = string.Join("\n", running.Select(j => $"  PID {j.Pid}: {OneLine(j.Command, 150)}"));

                        _notice($"processi in background ancora attivi ({running.Count}), gli chiedo se vanno fermati");

                        _ui($"__DIM__  [auto-jobs] {running.Count} processi in background ancora attivi — chiedo di gestirli...");

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

                    _dispatcher.SnapshotJobs();



                    if (usage is { } u)

                        _ui($"__DIM__[tokens: prompt={u.PromptTokens} completion={u.CompletionTokens} | step={step}]");

                    if (!_options.IsMachineMode) Console.WriteLine();

                    StructuredTrace.EndTurn("completed");

                    _sink.TurnFinished(turnId, "completed");

                    return true;

                }



                StructuredTrace.EndTurn("completed");

                _sink.TurnFinished(turnId, "completed");

                return true;
            }
            _ui("__ERROR__Limite step raggiunto — loop interrotto.");

            EmitFinalNotice(

                "Ho raggiunto il limite di step per questo turno; il task non è ancora concluso. " +

                "Scrivi \"continua\" per proseguire da dove ho lasciato.");

            StructuredTrace.EndTurn("max_turns");

            _sink.TurnFinished(turnId, "max_turns");

            return true;
        }
    }
}
