# Integrazione core pm-code ↔ UI web (protocollo v1)

> Analisi di sola lettura. Nessun file di codice è stato modificato. Il documento precedente in
> questo stesso percorso descriveva un piano di refactor (Fase A/B) mai realizzato: i punti
> "CancellationToken per turno" e "ConsoleSink riproduce esattamente l'output attuale" erano
> aspirazionali e non corrispondono al codice attuale — vedi punti 2 e 1 sotto.

## 1. Dove il core scrive oggi (console / stdout)

pm-code ha **due percorsi di output separati e non unificati**, selezionati dal flag
`--stdin-protocol` in testa a [`agent/pm-code/Program.cs`](../agent/pm-code/Program.cs#L11):

- **REPL (default)**: output "umano" su stderr tramite la classe `UI` definita in fondo a
  Program.cs ([Program.cs:2066-2125](../agent/pm-code/Program.cs#L2066-L2125)) — `UI.Ok`,
  `UI.Error`, `UI.Info`, `UI.Dim`, `UI.ToolCall`, `UI.ToolResult`, `UI.StepIndicator`. Il testo in
  streaming viene scritto direttamente con `Console.Write(token)` dentro il loop
  ([Program.cs:1205](../agent/pm-code/Program.cs#L1205)).
- **`--stdin-protocol` (usato oggi dall'estensione VS Code)**: un'unica funzione locale
  `EmitEvent` ([Program.cs:42-46](../agent/pm-code/Program.cs#L42-L46)) scrive su **stdout** una
  riga JSON per evento. Tutto il resto (UI, trace, errori di parsing) va forzatamente su stderr o
  su file, per non rompere il parsing NDJSON lato estensione (vedi commento a
  [Program.cs:2111-2115](../agent/pm-code/Program.cs#L2111-L2115)).

Punti di emissione concreti in `--stdin-protocol`, tutti dentro `RunStdinProtocolAsync`
([Program.cs:1747](../agent/pm-code/Program.cs#L1747)) o passati come delegati al loop
`RunAgentLoopAsync`:

| Cosa | Dove | Evento emesso |
|---|---|---|
| Testo in streaming | callback `onToken`, [Program.cs:1856](../agent/pm-code/Program.cs#L1856); consumato dentro `StreamChatAsync` a [Program.cs:1191-1206](../agent/pm-code/Program.cs#L1191-L1206) | `{ type: "token", text }` |
| Avanzamento turno (heartbeat, "Elaborazione in corso…") | `onStatus`, [Program.cs:1874](../agent/pm-code/Program.cs#L1874); invocato a [Program.cs:1189](../agent/pm-code/Program.cs#L1189) e come `onHeartbeat` dentro `StreamChatAsync` | `{ type: "status", text }` |
| Tool call avviata | `onToolCall`, [Program.cs:1857-1864](../agent/pm-code/Program.cs#L1857-L1864) | `{ type: "tool_call", tool, args }` |
| Tool call finita | `onToolResult`, [Program.cs:1865-1873](../agent/pm-code/Program.cs#L1865-L1873) | `{ type: "tool_result", tool, result }` |
| Fine turno | [Program.cs:1878](../agent/pm-code/Program.cs#L1878), dopo il ritorno di `RunAgentLoopAsync` | `{ type: "done" }` |
| Avviso/intervento automatico del motore (anti-loop, rollover, auto-build) | `Notice()`, [Program.cs:61-66](../agent/pm-code/Program.cs#L61-L66) | `{ type: "notice", text }` |
| Proposta di scrittura file (richiede conferma) | dentro `proposeWrite`, [Program.cs:203](../agent/pm-code/Program.cs#L203) | `{ type: "edit_proposal", path, content }` |
| Errori di protocollo (riga non valida) | [Program.cs:1759](../agent/pm-code/Program.cs#L1759) | solo `Console.Error`, **nessun evento** |
| Errori di dominio (LLM irraggiungibile, loop rilevato, risposta troncata) | `EmitFinalNotice()`, [Program.cs:757-763](../agent/pm-code/Program.cs#L757-L763), chiamata da ~10 punti del loop (es. [Program.cs:1233](../agent/pm-code/Program.cs#L1233), [1290](../agent/pm-code/Program.cs#L1290), [1316](../agent/pm-code/Program.cs#L1316), [1572](../agent/pm-code/Program.cs#L1572)) | **testo libero come `{ type: "token", text }`** (vedi `onToken` dentro `EmitFinalNotice`), non un evento "error" dedicato |

**Nota importante**: esiste già una classe `ConsoleSink`
([agent/pm-code/ConsoleSink.cs](../agent/pm-code/ConsoleSink.cs)) che implementa un'interfaccia
`IAgentEvents` ([agent/pm-code/IAgentEvents.cs](../agent/pm-code/IAgentEvents.cs)) con metodi che
ricalcano quasi esattamente gli eventi del protocollo v1 (`TurnStarted`, `AssistantDelta`,
`AssistantDone`, `ToolCallStarted`, `ToolCallFinished`, `TurnFinished`, `Error`, `Notice`). Viene
istanziata a [Program.cs:423-424](../agent/pm-code/Program.cs#L423-L424)
(`var consoleSink = new ConsoleSink(...); IAgentEvents events = consoleSink;`) **ma la variabile
`events` non viene mai più chiamata in nessun punto del file**: è codice morto, scollegato dal
loop reale. Il loop usa i suoi delegati inline (`onToken`/`onToolCall`/`onToolResult`/`onStatus`)
e la funzione locale `EmitEvent`, con un formato di eventi diverso (`token`, `tool_call`,
`tool_result`, `status`, `done`, `notice`, `edit_proposal`) da quello di `ConsoleSink`/v1
(`assistant_delta`, `tool_call_started`, `tool_call_finished`, `turn_finished`, ecc.). Qualunque
riuso di `ConsoleSink` per parlare v1 richiederebbe prima di capire quale dei due percorsi deve
sopravvivere.

## 2. Annullamento oggi (Ctrl+C) e CancellationToken per turno

**Non esiste nessun `CancellationToken` di turno nel codice**, né in REPL né in
`--stdin-protocol`:

- `LlamaClient.StreamChatAsync` ([agent/pm-code/LlamaClient.cs:151](../agent/pm-code/LlamaClient.cs#L151)) non accetta un `CancellationToken` come parametro.
- In REPL, Ctrl+C non è intercettato esplicitamente dal loop applicativo: l'unico handler di
  `Console.CancelKeyPress` registrato in tutto il progetto è quello di `StructuredTrace.Init()`
  ([StructuredTrace.cs:67-68](../agent/pm-code/StructuredTrace.cs#L67-L68)), che fa solo flush del
  trace e **non imposta `e.Cancel = true`**: il comportamento di default del CLR resta attivo,
  cioè Ctrl+C termina l'intero processo (non interrompe "solo" il turno corrente). `ProcessJob`
  ([agent/pm-code/ProcessJob.cs](../agent/pm-code/ProcessJob.cs)) garantisce che morendo il
  processo muoiano anche i figli (dev server, comandi in background) tramite un Job Object di
  Windows.
- In `--stdin-protocol` non c'è alcuna gestione di Ctrl+C (lo stdin è una pipe verso il processo
  padre, non una console — vedi commento a [StructuredTrace.cs:68](../agent/pm-code/StructuredTrace.cs#L68)). Il protocollo odierno di stdin **non prevede nessun messaggio di tipo "cancel"**:
  l'unico modo che ha l'estensione per interrompere un turno in corso è terminare il processo
  (`TerminateProcess`, da cui il commento a [Program.cs:13-15](../agent/pm-code/Program.cs#L13-L15) su `ProcessJob.KillChildrenOnExit()`), perdendo tutto lo stato in memoria (history, piano
  non salvato su disco, workspace).
- Il protocollo v1 della UI prevede invece un messaggio client `cancel` dedicato
  ([ui/protocol/src/types.ts:73](../ui/protocol/src/types.ts#L73) + esempio
  [cancel.json](../ui/protocol/examples/cancel.json)), pensato per annullare il turno **senza
  uccidere il processo/la sessione**. Oggi il core non ha alcun aggancio per questo: andrebbe
  introdotto un `CancellationToken` propagato da `RunAgentLoopAsync` fino a `StreamChatAsync` e ai
  tool in esecuzione (in particolare i comandi avviati da `run_command`/`TerminalTools`).

## 3. Conferma prima di scrivere file o eseguire comandi

- **Scrittura file** (`write_file`/`edit_file`): SÌ, ma solo se `--stdin-protocol` e
  `autoApprove == false`. Il meccanismo è il delegato `proposeWrite`, costruito a
  [Program.cs:189-233](../agent/pm-code/Program.cs#L189-L233) e passato a `FileSystemTools`
  tramite `ToolDispatcher` ([ToolDispatcher.cs:27-29](../agent/pm-code/ToolDispatcher.cs#L27-L29)
  → [FileSystemTools.cs:8](../agent/pm-code/FileSystemTools.cs#L8)). Flusso:
  1. `FileSystemTools` chiama `proposeWrite(path, content)` invece di scrivere subito
     ([FileSystemTools.cs:52-57](../agent/pm-code/FileSystemTools.cs#L52-L57) e
     [:435](../agent/pm-code/FileSystemTools.cs#L435)).
  2. `proposeWrite` emette `{ type: "edit_proposal", path, content }` su stdout
     ([Program.cs:203](../agent/pm-code/Program.cs#L203)) e si blocca in lettura **sincrona** su
     stdin con un `Task.Run(() => Console.In.ReadLine())` e timeout di 5 minuti
     ([Program.cs:213-218](../agent/pm-code/Program.cs#L213-L218)).
  3. L'estensione deve rispondere sulla stessa pipe con una riga
     `{ "type": "edit_decision", "accepted": true|false }`; qualunque altra riga che arrivi nel
     frattempo da quel lato (es. una nuova `request`) sarebbe ignorata/scartata, perché in questo
     istante l'unico consumer legittimo di stdin è `proposeWrite` ([Program.cs:1768-1771](../agent/pm-code/Program.cs#L1768-L1771)).
  4. Se `autoApprove == true` (flag per-turno inviato dall'estensione, o `/auto-approve` in REPL,
     persistito in `.pm-agent.json` solo per il REPL — vedi [Program.cs:143-233](../agent/pm-code/Program.cs#L143-L233)), lo step 2-3 viene saltato del tutto e si scrive subito.
  5. In REPL `proposeWrite` è `null`: `FileSystemTools` scrive sempre direttamente su disco,
     nessuna conferma mai richiesta.
- **Esecuzione comandi** (`run_command`/`TerminalTools`): **NO, nessuna conferma in nessuna
  modalità.** `TerminalTools` ([agent/pm-code/TerminalTools.cs](../agent/pm-code/TerminalTools.cs))
  non riceve e non usa alcun `proposeWrite`-equivalente: ha solo blocchi statici che **vietano**
  certe operazioni a priori (cancellazione file, scrittura/sovrascrittura di file di codice via
  shell — [TerminalTools.cs:302-315](../agent/pm-code/TerminalTools.cs#L302-L315)), ma qualunque
  comando permesso (build, test, `git`, chiamate HTTP, avvio di processi) **esegue immediatamente**,
  senza passare da alcuna `approval_request`.
- Conseguenza per il protocollo v1: il payload `ApprovalRequestPayload` prevede
  `kind: "write" | "command"` ([ui/protocol/src/types.ts:165-176](../ui/protocol/src/types.ts#L165-L176)), ma **oggi il core produce solo l'equivalente informale del kind "write"**
  (via `edit_proposal`); non esiste alcuna emissione per `kind: "command"` perché i comandi non
  passano mai da un'approvazione.

## 4. Dove vive lo stato di sessione

Tutto lo stato "di conversazione" vive **in memoria, dentro il singolo processo pm-code**, non in
un `SessionManager` che lo persiste (il nome è un po' ingannevole):

- **Cronologia** (`history: List<ChatMessage>`): variabile locale di `Program.cs`, dichiarata a
  [Program.cs:426](../agent/pm-code/Program.cs#L426), ricostruita da `ResetHistory()`
  ([Program.cs:432-438](../agent/pm-code/Program.cs#L432-L438)) e mutata direttamente nel loop
  (`history.Add(...)`). In `--stdin-protocol`, l'estensione può "seminare" la history inviando un
  campo `context` ad ogni richiesta ([Program.cs:1807-1817](../agent/pm-code/Program.cs#L1807-L1817)); il core decide se è una nuova chat o una continuazione guardando `history.Count`
  e `contextMessages.Count` ([Program.cs:1819-1850](../agent/pm-code/Program.cs#L1819-L1850)). Non
  c'è alcun ID di sessione, né la possibilità di avere più conversazioni aperte nello stesso
  processo: **un processo pm-code = una conversazione** (coerente con `SessionStatus`/`SessionInfo`
  del protocollo v1 che invece presuppone N sessioni per connessione).
- **Modello attivo**: proprietà mutabile `llm.Model` su `LlamaClient` ([Program.cs:1794](../agent/pm-code/Program.cs#L1794)), aggiornata ad ogni richiesta dal campo `model` opzionale
  in arrivo da stdin; se assente, ricade sul default di `appsettings.json`
  ([Program.cs:81-82](../agent/pm-code/Program.cs#L81-L82)). Analogo per l'endpoint
  (`llm.EndpointOverride`, [Program.cs:1803](../agent/pm-code/Program.cs#L1803)).
- **Workspace**: oggetto `WorkspaceContext workspace`, root impostata all'avvio da `--workspace`
  o da `AgentSettings:Workspace` ([Program.cs:90-99](../agent/pm-code/Program.cs#L90-L99)), con
  `SetRoot()` disponibile per cambiarla a runtime (tool `set_workspace`, non coperto da questa
  analisi in dettaglio).
- **Riassunto di rollover del contesto** (gestito da `SessionManager`,
  [agent/pm-code/SessionManager.cs](../agent/pm-code/SessionManager.cs)): QUESTO sì viene salvato
  su disco, in `<workspace>/.pm-agent/rollover/rollover-*.md`
  ([SessionManager.cs:108-130](../agent/pm-code/SessionManager.cs#L108-L130)), ma è un log di
  debug, non uno storage da cui ricostruire la conversazione.
- **Stato persistente collaterale** (fuori processo, dentro `<workspace>/.pm-agent/`, gestito da
  `AgentStateDir` — [agent/pm-code/AgentStateDir.cs](../agent/pm-code/AgentStateDir.cs)): piano
  todo (`plan.json`, letto da `dispatcher.PlanSummary()`), note di progetto, output lunghi dei
  comandi. `.pm-agent.json` nella root del workspace persiste solo il flag `autoApprove` del REPL.
  Nessuno di questi contiene la cronologia dei messaggi.

Conseguenza: non esiste alcun `SessionsListPayload`/`SessionStatePayload` realizzabile oggi senza
stato aggiuntivo — il processo non sa elencare "le sessioni", ne ha solo una implicita (sé stesso).

## 5. Eventi del protocollo v1: dove verrebbero emessi, cosa manca

| Evento v1 | Emissione oggi nel loop | Note / cosa manca |
|---|---|---|
| `turn_started` | Nessun evento equivalente oggi. Il turno comincia implicitamente alla ricezione della riga `request` in [Program.cs:1766-1774](../agent/pm-code/Program.cs#L1766-L1774) | Andrebbe emesso subito dopo aver accodato `ChatMessage.User(prompt)` e prima di chiamare `RunAgentLoopAsync`. Richiede di generare un `turnId` (oggi non esiste: `StructuredTrace.BeginTurn()` ne genera uno **solo interno** al file di trace, [StructuredTrace.cs:175-184](../agent/pm-code/StructuredTrace.cs#L175-L184), il valore di ritorno viene scartato a [Program.cs:1096](../agent/pm-code/Program.cs#L1096)) |
| `assistant_delta` | Corrisponde a `{ type: "token", text }`, [Program.cs:1856](../agent/pm-code/Program.cs#L1856) | Payload v1 richiede anche `messageId` ([types.ts:133-137](../ui/protocol/src/types.ts#L133-L137)): oggi non esiste un id di messaggio assistant, solo lo `step` del loop |
| `assistant_done` | Nessun punto di emissione dedicato: l'`usage` (prompt/completion tokens) è disponibile solo nei log (`Trace(...)` a [Program.cs:1411](../agent/pm-code/Program.cs#L1411)) e in `StructuredTrace.LlmRequest` | Andrebbe aggiunto esplicitamente dopo `history.Add(message)` ([Program.cs:1408](../agent/pm-code/Program.cs#L1408)), quando si conoscono `usage` e si potrebbe assegnare un `messageId` |
| `tool_call_started` | Corrisponde a `{ type: "tool_call", tool, args }`, [Program.cs:1857-1864](../agent/pm-code/Program.cs#L1857-L1864) | Mancano `turnId`/`callId` nel payload attuale (solo `tool` + `args`); il v1 richiede entrambi ([types.ts:150-155](../ui/protocol/src/types.ts#L150-L155)) |
| `tool_call_finished` | Corrisponde a `{ type: "tool_result", tool, result }`, [Program.cs:1865-1873](../agent/pm-code/Program.cs#L1865-L1873) | Mancano `callId`, `ok` (booleano esplicito — oggi va dedotto da prefissi testuali come `ERRORE`/`⚠️`, vedi `isError` a [Program.cs:1445-1452](../agent/pm-code/Program.cs#L1445-L1452)) e `durationMs` (il core misura durate solo per `StructuredTrace`, es. [Program.cs:1596-1601](../agent/pm-code/Program.cs#L1596-L1601), non le riporta all'esterno) |
| `turn_finished` | Corrisponde grossolanamente a `{ type: "done" }`, [Program.cs:1878](../agent/pm-code/Program.cs#L1878) | Manca `reason` (`completed`/`cancelled`/`error`): oggi l'unico posto che conosce la ragione è `StructuredTrace.EndTurn(reason)`, scritto solo nel file di trace, mai esposto come evento esterno. Il caso "error" oggi è quasi sempre un `return false` silenzioso dal loop o un `EmitFinalNotice` camuffato da testo libero (`token`), non un evento distinto |
| `error` | **Non esiste come evento dedicato.** Gli errori di dominio passano per `EmitFinalNotice` (diventano testo in chat, non un evento tipizzato) o restano solo su stderr/trace file (es. riga JSON non valida, [Program.cs:1759](../agent/pm-code/Program.cs#L1759)); non c'è alcuna mappatura agli `ErrorCode` del protocollo (`unauthorized`, `unsupported_version`, `unknown_session`, `bad_request`) perché il core non fa autenticazione, non versiona il protocollo di stdin, non ha sessioni con ID | Il protocollo stdin attuale non ha né `hello`/`hello_ok`, né un campo versione: è implicito e specifico di questa integrazione, non riusabile 1:1 |

Eventi v1 **senza alcuna controparte** nel core attuale, da costruire da zero:
`hello`/`hello_ok`, `list_sessions`/`sessions_list`, `new_session`/`open_session`/`session_state`,
`models_list` (il core conosce solo il modello richiesto per il turno corrente, non un catalogo),
`permission_state`/`set_permission` (esiste solo il booleano `autoApprove`, non i tre modi
`readonly`/`ask`/`auto` del protocollo), `approve`/`reject`/`approval_request`/`approval_resolved`
con `kind: "command"` (vedi punto 3), `cancel` (vedi punto 2).

## 6. Confronto con `ui/protocol/`

`ui/protocol/` ([ui/protocol/src/types.ts](../ui/protocol/src/types.ts),
[ui/protocol/README.md](../ui/protocol/README.md)) è **completo e coerente al suo interno**: tipi
TypeScript, validazione runtime (`parseClientMessage`/`parseServerMessage`), un file di esempio
JSON per ciascuno dei 10 messaggi client e 13 messaggi server, test automatici che verificano che
ogni esempio passi la validazione. Non è il protocollo a essere incompleto — è il core pm-code a
non implementarlo: oggi parla un protocollo stdin ad-hoc, molto più povero, con 7 tipi di evento
lato server (`token`, `status`, `tool_call`, `tool_result`, `notice`, `edit_proposal`, `done`) e un
solo tipo di messaggio client vero e proprio (`request`, con `edit_decision` come caso speciale
sincrono), senza busta comune (`Envelope`: niente `v`, `id`, `ts`, `sessionId` strutturati), senza
multi-sessione, senza autenticazione (`hello`), senza selezione/elenco modelli, senza modalità di
permesso granulare, senza annullamento, senza approvazione dei comandi.

Riepilogo differenze (nessuna delle voci sotto "manca nel protocollo" — il file è completo):

- **Nel protocollo ma non nel core**: tutto il punto 5 sopra, più `hello`, multi-sessione,
  `models_list`, `permission_state` a 3 modalità, `cancel`, approvazione dei comandi.
- **Nel core ma non modellato nel protocollo v1**: `status` (heartbeat "Elaborazione in corso…"),
  `notice` (interventi automatici del motore: anti-loop, rollover, auto-build-check — concetti che
  in v1 andrebbero probabilmente dentro `assistant_delta`/testo o in un nuovo tipo di evento), e il
  flusso sincrono `edit_proposal`/`edit_decision` via stdin, che è l'antenato diretto ma non
  equivalente di `approval_request`/`approve`/`reject` (manca `approvalId`, `unifiedDiff` esplicito
  — oggi si manda il contenuto intero del file, non un diff — e `expiresAt`, anche se esiste già un
  timeout implicito lato core di 5 minuti, [Program.cs:214](../agent/pm-code/Program.cs#L214)).
