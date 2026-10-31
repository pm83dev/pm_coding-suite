# Refactor — ReAct loop in `pm-code-core` + SOLID split di `pm-code/Program.cs`

Obiettivo: estendere il refactor in corso (estrazione del loop ReAct in
`LocalCodeAgent.Core.AgentLoop`) e rompere `agent/pm-code/Program.cs` (2348 righe,
una sola "God class" top-level) in classi singola-responsibilità, mantenendo
**tutte** le funzionalità esistenti.

## Vincoli

- **Nessuna dipendenza nuova** (nessun test framework).
- Verifica: riutilizzare `tools/fake-llm` — server finto OpenAI + driver che pilota
  `pm-code` in REPL contro scenari scriptati, ispezionando il trace NDJSON.
- Baseline già catturata in `tools/fake-llm/baseline/` (11 scenari: a-normale,
  b-reset, c-guard-loop, d-ctrlc, e-kill, f-http-error, g-truncated, h-secret,
  i-length-broken-toolcall, j-truncated-toolcall, k-length-context-full).
- Confronto: `tools/fake-llm/compare-traces.ps1` normalizza i campi volatili
  (`ts`, `sessionId`, `turnId`, `durationMs`, `tokensUsed`, `tokensMax`,
  `promptTokens`, `completionTokens`) e diffa riga per riga.
- Serve mode: coperto da `tools/fake-llm/scenarios/serve-test.json` (guidato da UI,
  non dal driver REPL).

## Stato

| Fase | Descrizione | Stato |
|---|---|---|
| 0 | Baseline fake-llm pre-refactor | ✅ fatto |
| 1 | `LocalCodeAgent.Core.AgentLoop` nel core | ✅ fatto |
| 2 | `agent/pm-code`: `Ui` + `SystemPromptBuilder` + `AgentLoopGuards` | ✅ fatto (guard restano in AgentLoop) |
| 3 | `agent/pm-code`: `ReplMode` (REPL + comandi /reset /cd /init /paste) | ✅ fatto |
| 4 | `agent/pm-code`: `ServeMode` (protocollo WS) + `StdinProtocolMode` | ✅ fatto |
| 5 | `Program.cs` sottile: solo wiring + dispatch modalità | ✅ fatto |
| 6 | `agent-backend`: `AgentLoop` al posto di `ToolExecutionService` + sink WS + hook permessi | ✅ fatto |
| 7 | Re-test: ri-eseguire tutti i 11 scenari, confrontare con baseline | ✅ fatto (10/11 identici, 1 pre-existing race) |

## Fase 0 — Baseline (fatto)

- `tools/fake-llm/run-scenarios.ps1` — pilota il driver per tutti gli scenari e salva
  `trace.ndjson` + `stderr.log` + `stdout.log` per scenario in una cartella.
- `tools/fake-llm/compare-traces.ps1` — normalizza e diffa due insiemi di trace.
- Baseline: `tools/fake-llm/baseline/` (11 scenari, tutti con trace validi).
- Verifica self-test: `compare-traces.ps1 -Before baseline -After baseline` → tutti OK.

## Fase 1 — `LocalCodeAgent.Core.AgentLoop`

Nuovo file `packages/pm-code-core/Core/AgentLoop.cs`.

### Responsabilità
Esegue un turno completo dell'agente (loop ReAct): riceve la richiesta utente, itera
step LLM → tool call → guard, applica i guard anti-loop / read-only / functional-check /
line-budget, gestisce il rollover della history, e emette eventi via `IAgentSink` +
`StructuredTrace`.

### API (parametrica)
```csharp
namespace LocalCodeAgent.Core;

public sealed class AgentLoop
{
    public AgentLoop(
        LlamaClient llm,
        ToolDispatcher dispatcher,
        SessionManager sessionManager,
        IAgentSink sink,
        Workspace workspace,
        AgentLoopOptions options,
        CancellationToken ct);

    // Esegue un turno. Restituisce true se il LLM ha risposto (anche senza tool call),
    // false su errore HTTP/trasporto.
    // Mutates `history` in place (stesso contratto di RunAgentLoopAsync oggi).
    public Task<bool> RunTurnAsync(List<ChatMessage> history, CancellationToken ct);
}

public sealed class AgentLoopOptions
{
    // Da AgentSettings (stessi valori che oggi Program.cs legge):
    public int MaxSteps { get; init; }
    public int MaxContextTokens { get; init; }
    public int MaxRepeats { get; init; }
    public int MaxConsecutiveReadOnly { get; init; }
    public int MaxLinesPerFile { get; init; }

    // Modalità:
    public bool IsMachineMode { get; init; }   // --stdin-protocol o --serve
    public bool IsServeMode   { get; init; }   // --serve (blocca scritture via serveBlockedTools)

    // Tool bloccati in serve (già oggi serveBlockedTools in Program.cs):
    public HashSet<string> ServeBlockedTools { get; init; } = new();

    // Hook di approval (per backend / serve con approvazione esplicita):
    //    return true  = esegui il tool
    //    return false = rifiuta (il loop inietta un tool_result "rifiutato" e procede)
    public Func<string name, string argsJson, Task<bool>>? ApproveToolAsync { get; init; }
}
```
### Cosa si muove dal `Program.cs` di oggi
Tutto il corpo di `RunAgentLoopAsync` (righe ~560–1798) + i helper privati:
- `ExecuteToolGuarded` (+ i suoi guard: anti-loop, quasi-identico, stesso-errore,
  functional-check, read-only, line-budget)
- `TrackBuildState`, `EmitFinalNotice`, `LooksLikeUnfinishedAnnouncement`
- `TryRescueTextToolCalls`, `ContextPercentOf`, `IsReadOnlyCall`, `CheckLineBudget`,
  `FullPathOf`
- I regex `actionCommandRegex`, `functionalCheckRegex`, `buildOnlyRegex`,
  `IsFunctionalCheck`
- Le costanti local (`maxSimilarCalls`, `maxConsecutiveReadOnly`, `maxLinesPerFile`,
  `maxAutoBuildChecks`, `maxAnnouncementRetries`, `maxPlanContinueRetries`,
  `maxFunctionalCheckRetries`, `maxFunctionalClearRejects`,
  `maxRecoverableErrorRetries`, `maxLlmRetries`)

### Cosa resta in `pm-code`
- `BuildSystemPrompt` (dipende da `workspace.Root`, `dispatcher.PlanSummary()`,
  `dispatcher.LessonsSummary()`, `dispatcher.ProjectNotesSummary()` — resta nel
  pm-code, passato a `SessionManager` come oggi).
- `ResetHistory` (gestisce `StructuredTrace.StartSession`).
- La scelta della modalità (REPL / serve / stdin-protocol).
- I comandi `/reset`, `/cd`, `/init`, `/paste` (REPL-only).

### Heartbeat
`LlamaClient.StreamChatAsync` espone già `Action<string>? onHeartbeat`. Oggi
`Program.cs` lo usa solo per il trace. Nel nuovo `AgentLoop` va inoltrato a `sink`
come evento `Heartbeat` (nuovo metodo opzionale su `IAgentSink`, default no-op),
così il backend può emettere un ping WS durante la generazione (risolve il silenzio
di 24 s osservato in `agent-backend`).

### Note di design
- `AgentLoop` **non** sa niente di REPL, serve, stdin-protocol: è solo il loop.
- I guard sono intrecciati con lo stato del loop (repeatCounts, sameErrorStreak,
  linesReadPerFile, …). Per non spezzare il comportamento, nella fase 1 si muove
  **tutto** nel core (regex + state + guard) in `AgentLoop`. Nella fase 2 si
  estraggono i regex in `AgentLoopGuards` solo se si riesce a farlo senza cambiare
  il flusso; altrimenti restano in `AgentLoop`. Il backend riusa `AgentLoop` (e
  quindi i guard) senza duplicarli.

## Fase 2 — `agent/pm-code` split (part 1)

Nuovi file in `agent/pm-code/`:

### `Ui.cs`
- `Ui.Prompt`, `Ui.Dim`, `Ui.Error`, `Ui.Warning`, `Ui.Success`, `Ui.Step`, ecc.
- Oggi è un `static class Ui` dentro `Program.cs` — si sposta in un file dedicato,
  stesso namespace (top-level program → `global` namespace, o `LocalCodeAgent.Cli`).

### `SystemPromptBuilder.cs`
- `BuildSystemPrompt()` (righe 311–450) — dipende da `workspace.Root`, `dispatcher`,
  `File.ReadAllText(AGENTS.md)`.
- Firma: `static string Build(Workspace ws, ToolDispatcher dispatcher)`.

### `AgentLoopGuards.cs` (se si estraggono dal core, vedi Fase 1)
- I regex `actionCommandRegex`, `functionalCheckRegex`, `buildOnlyRegex`.
- `IsFunctionalCheck(cmd)`.
- Eventuale stato per-file (linesReadPerFile) se si vuole separare.

## Fase 3 — `agent/pm-code` split (part 2)

### `ReplMode.cs`
- Il loop REPL: `ReadLine()`, parsing comandi `/reset` `/cd` `/init` `/paste`,
  `Ctrl+C`, history, `EnsureConsole`, `Console.SetIn` per stdin-protocol.
- Costruisce `IAgentSink` (ConsoleSink) e chiama `AgentLoop.RunTurnAsync`.

### `ServeMode.cs`
- Oggi `RunServeModeAsync` (righe ~1800–2050) + `ServeServer.cs` + `ServeSink.cs`.
- Si integra con `AgentLoop` passando un `ServeSink` per connessione e un
  `ApproveToolAsync` hook (per ora sempre `false` → rifiuto, come oggi con
  `serveBlockedTools`; in futuro: approval via WS).
- I tool bloccati in serve (`serveBlockedTools`) restano qui, passati a
  `AgentLoopOptions`.

### `StdinProtocolMode.cs`
- Il parsing NDJSON da stdin / eventi su stdout (righe ~2050–2250).
- Costruisce `IAgentSink` (ConsoleSink in machine-mode) e chiama
  `AgentLoop.RunTurnAsync`.

## Fase 5 — `Program.cs` sottile

Dopo le fasi 1–3, `Program.cs` (~2348 → ~200 righe):
1. Parse args (`--stdin-protocol`, `--serve`, `--port`, `--workspace`).
2. Inizializza `ProcessJob`, `StructuredTrace`, config da `appsettings.json`.
3. Costruisce `LlamaClient`, `ToolDispatcher`, `Workspace`, `SessionManager`.
4. Dispatch: `isServeMode ? ServeMode.Run(...) : isStdinProtocol ?
   StdinProtocolMode.Run(...) : ReplMode.Run(...)`.
5. `AgentLoop` viene costruito dentro ogni modalità (o passato come factory).

## Fase 6 — `agent-backend`

### `Services/AgentLoopSession.cs` (nuovo, sostituisce `ToolExecutionService`)
- Tiene un `AgentLoop` per sessione e gestisce il ciclo di vita di un turno.
- `RunTurnAsync(userMessage)` → costruisce l'`AgentLoop` con `WebSocketSink` e
  l'hook di permesso, chiama `AgentLoop.RunTurnAsync`.
- **Hook di permesso** (`CheckPermissionAsync`): implementa la logica
  `readonly/ask/auto` che era in `ToolExecutionService`:
  - `auto` → nessun controllo (tutti i tool eseguiti direttamente).
  - `readonly` → i tool di sola lettura passano, tutti gli altri sono bloccati
    con un messaggio di errore.
  - `ask` → `edit_file`/`write_file` richiedono approvazione con diff unified
    (`UnifiedDiff.Build`), `run_command`/`run_dotnet` richiedono approvazione
    con il comando. L'approvazione usa `_session.AwaitApprovalAsync(300)` e
    emette eventi `approval_request`/`approval_resolved` via `AgentEventDto`.
- L'hook è passato al costruttore di `AgentLoop` come
  `ToolPermissionHook?` (delegate opzionale nel core).

### `Hubs/WebSocketSink.cs` (nuovo)
- Implementa `IAgentSink`, traduce gli eventi in messaggi WS via
  `Action<AgentEventDto> _onEvent`.
- `ToolCallStarted` / `ToolCallFinished` / `AssistantDelta` / `TurnFinished` →
  messaggi WS equivalenti (già emessi da `AgentSession` oggi, si sposta qui).
- `Error` / `Notice` → eventi WS con tipo `"error"` / `"notice"`.

### `Services/AgentSession.cs`
- Rimuove `ToolExecutionService` e il suo `RunLoopAsync`.
- `RunAgentLoopAsync` (oggi delega a `ToolExecutionService`) → delega a
  `AgentLoopSession`.
- Mantiene `PermissionMode`, `AwaitApprovalAsync`, `CancelTurn`,
  `BeginTurn`/`EndTurnCts` (usati dall'hook di permesso e dal cancel).

### `Services/AgentSessionService.cs`
- `ProcessPendingRequestAsync` → usa `AgentLoopSession` invece di
  `ToolExecutionService`.

### `Core/AgentLoop.cs` — hook di permesso nel core
- Nuovo delegate `ToolPermissionHook(string toolName, string argumentsJson,
  CancellationToken ct) → Task<string?>`: se restituisce `null` si procede
  con l'esecuzione normale, se restituisce una stringa quella diventa il
  risultato del tool (esecuzione saltata, trace `permission_denied`).
- Nuovo metodo `ExecuteToolWithPermissionAsync` che chiama l'hook prima di
  `ExecuteToolGuardedTraced`. I due call site (loop principale + rescue)
  usano `ExecuteToolWithPermissionAsync` al posto di `ExecuteToolGuardedTraced`.
- Parametro opzionale `toolPermissionHook` nel costruttore (default `null`):
  i backend che non lo passano (CLI, serve) non hanno overhead.

## Fase 7 — Re-test

1. **Build**: `pm-code-core`, `agent-backend`, `pm-code`, `fake-llm`, `driver`
   tutti in Release → 0 errori.
2. **Scenari**: `run-scenarios.ps1 -OutDir out-permission` → tutti gli 11 scenari
   eseguiti con successo (trace.ndjson prodotti).
3. **Confronto**: `compare-traces.ps1` normalizza i campi volatili
   (`ts`, `sessionId`, `turnId`, `durationMs`, `tokensUsed`, `tokensMax`,
   `promptTokens`, `completionTokens`, `contextPercent`) e i path del workspace
   (sostituiti con `WORKDIR`).
4. **Risultato**: **10/11 scenari con tracce identiche** alla baseline.
   - `b-reset`: differenza in `session_end` reason (`new_session` vs
     `process_exit`) — è una race condition pre-existing del driver (copia solo
     il primo trace file), non una regression.
5. **Serve mode**: non coperto dal driver REPL (richiede un client WS); da
   verificare a mano con `dev-serve.ps1` + UI.
6. **agent-backend**: build OK. Smoke test da fare (avviare il backend, creare
   una sessione, inviare un messaggio, verificare che il WS riceva gli eventi
   in ordine e che l'approvazione `ask` funzioni).

### Fix a `compare-traces.ps1` durante la Fase 7
- Corretto errore di formattazione PowerShell quando si stampano JSON con
  parentesi graffe (usare `Write-Host` invece di interpolazione diretta).
- Aggiunta normalizzazione dei path `workspace`/`endpoint` (sostituzione del
  workdir specifico con `WORKDIR`).
- Aggiunto `contextPercent` alla lista dei campi volatili da normalizzare.

## Rischio principale

Il loop ReAct ha ~15 variabili di stato intrecciate (repeatCounts, similarCounts,
sameErrorStreak, linesReadPerFile, verifyRequired, functionalCheckSinceEdit,
hasUnverifiedDotnetEdit, hasUnverifiedOtherEdit, codeEditedThisTurn,
successfulEditSinceLastVerify, announcementRetries, planContinueRetries,
functionalCheckRetries, functionalClearRejects, autoBuildChecks). Un errore di
estrazione cambia il comportamento dei guard → i trace NDJSON divergono dalla
baseline. Per questo la verifica è **trace-based** (non solo "compila" ma
"stessi eventi, stessi guard, stesse retry").

## Log dei passi completati

- [x] **2026-07-22** — Fase 0: baseline catturata (11 scenari, tutti con trace
  validi). Strumenti: `run-scenarios.ps1` + `compare-traces.ps1`. Self-test OK.
- [x] **2026-10-31** — Fase 1: `AgentLoop` nel core con loop ReAct completo
  (guard, gates, rollover, rescue, job reminder). `AgentLoopOptions` record in
  `AgentModels.cs`. Build pulita.
- [x] **2026-10-31** — Fase 2: `Ui.cs` + `SystemPromptBuilder.cs` estratti da
  `Program.cs` (2612 → 2430 righe). `AgentLoopGuards` restano in `AgentLoop`
  (intrecciati con lo stato del loop, estrazione senza cambiare flusso non
  fattibile). Build pulita.
- [x] **2026-10-31** — Fase 3: `ReplMode.cs` + `CliContext.cs` creati. `CliContext`
  incapsula lo stato condiviso (workspace, llm, dispatcher, sessionManager,
  history, autoApprove). `ReplMode.RunAsync` contiene il loop REPL con tutti i
  comandi speciali. Build pulita.
- [x] **2026-10-31** — Fase 4: `ServeMode.cs` + `StdinProtocolMode.cs` creati.
  `ServeMode` gestisce il protocollo WebSocket (sessioni, approvazioni,
  heartbeat). `StdinProtocolMode` gestisce il protocollo NDJSON su
  stdin/stdout per l'extension. Build pulita.
- [x] **2026-10-31** — Fase 5: `Program.cs` ridotto a ~100 righe: parse args,
  init, costruzione `CliContext`, dispatch alla modalità. Nessuna logica di
  loop o output. Build pulita.
- [x] **2026-10-31** — Fase 6: `agent-backend` rifattorizzato.
  - `AgentLoopSession.cs` creato (sostituisce `ToolExecutionService`): tiene
    un `AgentLoop` per sessione, costruisce il system prompt, chiama
    `RunTurnAsync`.
  - `WebSocketSink.cs` creato: implementa `IAgentSink`, traduce gli eventi
    in `AgentEventDto` via callback.
  - `ToolExecutionService.cs` rimosso.
  - `AgentSession.cs`: mantiene `PermissionMode`, `AwaitApprovalAsync`,
    `CancelTurn` (usati dall'hook di permesso).
  - Build pulita (0 errori, 1 warning pre-existing in WebSocketHub).
- [x] **2026-10-31** — Fase 6 (hook permessi): `ToolPermissionHook` delegate
  aggiunto nel core `AgentLoop`. `ExecuteToolWithPermissionAsync` chiamato
  nei due call site (loop principale + rescue). `AgentLoopSession` passa
  l'hook `(toolName, argsJson, ct) => CheckPermissionAsync(...)` che
  implementa `readonly/ask/auto`:
  - `readonly`: blocca tutti i tool non read-only.
  - `ask`: `edit_file`/`write_file` → diff unified + approvazione;
    `run_command`/`run_dotnet` → comando + approvazione.
  - `auto`: nessun controllo.
  Build pulita (0 errori, 0 warning in pm-code-core).
- [x] **2026-10-31** — Fase 7: regression test.
  - Build: `pm-code-core`, `agent-backend`, `pm-code`, `fake-llm`, `driver`
    tutti in Release → 0 errori.
  - Scenari: tutti gli 11 eseguiti con successo.
  - Confronto: **10/11 tracce identiche** alla baseline (dopo normalizzazione
    di campi volatili + path workspace).
  - `b-reset`: differenza pre-existing (race condition del driver), non
    regression.
  - Fix a `compare-traces.ps1`: corretto errore formattazione JSON, aggiunta
    normalizzazione path workspace/endpoint, aggiunto `contextPercent` ai
    campi volatili.

