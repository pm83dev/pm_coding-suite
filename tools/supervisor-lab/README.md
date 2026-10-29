# supervisor-lab

Strumento per RACCOGLIERE DATI in vista di un esperimento: capire se un modello piccolo
(Qwen3.5 9B su un secondo llama-server) può fare da supervisore dell'agente pm-code
(che usa Qwen3.8 attraverso il suo llama-server principale). **Non è il supervisore**:
produce log letti, digest, segnali deterministici, un dataset da etichettare a mano e un
confronto di piani. Le decisioni le prende l'utente guardando i risultati.

Vincoli rispettati: questo progetto non referenzia `agent/pm-code`/`agent/pm-mcp` (niente
`ProjectReference`), legge solo i file che pm-code scrive su disco. Nessun commit è stato
fatto da questo lavoro. Questo tool non scrive mai nei file di log/trace originali, solo in
`tools/supervisor-lab/data/`.

**Aggiornamento**: in una sessione successiva è stata autorizzata (e fatta, separatamente
da questo tool) una modifica ADDITIVA a `agent/pm-code` — un trace NDJSON strutturato
(`StructuredTrace.cs`), che NON cambia il comportamento dell'agente. Il vecchio
`pm-code-trace.log` (§1 sotto) resta com'era, invariato. Questo README copre entrambi i
formati; §1-6 sono la parte originale (dati raccolti dal solo `pm-code-trace.log`), §7 è
il nuovo formato NDJSON e come i comandi `sessions`/`digest`/`signals` lo leggono ora.

## 1. Dove e come scrive il log pm-code (verificato nel codice)

File: `pm-code-trace.log`, nella stessa cartella dell'eseguibile
(`AppContext.BaseDirectory`, quindi `agent/pm-code/bin/<Debug|Release>/net8.0/` in
sviluppo). Scritto in append (`File.AppendAllText`), **nessuna rotazione e nessun limite
di dimensione**: cresce indefinitamente finché qualcuno non lo cancella a mano.
Riferimento: [Program.cs:47-51](../../agent/pm-code/Program.cs#L47-L51).

**Importante**: la scrittura del log è attiva SOLO in modalità `--stdin-protocol`, cioè
quando pm-code è lanciato dall'estensione VS Code (`agent/pm-mcp`/`extension/` non sono
coinvolti: è pm-code stesso, via `RunStdinProtocolAsync`). In modalità REPL/CLI
interattiva il programma non scrive mai in questo file — usa solo `UI.*` su console.
Riferimento: [Program.cs:1728-1732](../../agent/pm-code/Program.cs#L1728-L1732) (il ramo
`if (isStdinProtocol) { await RunStdinProtocolAsync(); return; }`).

Formato (testo semplice, una riga per evento):

```
=== Turno <yyyy-MM-dd HH:mm:ss> | modello '<nome modello>' | history=<N> msg | prompt: <prompt troncato a 200 char>
 [step <k>/<maxSteps>] tool_calls=<n> prompt_tokens=<token reali dal server> testo: <risposta troncata a 200 char>
  -> <tool>(<argomenti troncati a 300 char>)
  <- <tool>: <risultato troncato a 300 char>
  !! <notice: auto-continue, rollover, kill di processi residui, ecc.>
=== Fine turno
```

Campi e cosa significano (tutti verificati leggendo `Program.cs`):

- **Timestamp**: `DateTime.Now` al momento di iniziare il turno — una sola volta per
  turno, non per riga.
- **history=N**: dimensione (in numero di messaggi) della history inviata al modello per
  quel turno. È l'unico segnale nel log che indica un reset di conversazione: quando
  l'utente apre una nuova chat in VS Code, pm-code azzera la history
  ([Program.cs:1676-1688](../../agent/pm-code/Program.cs#L1676-L1688)) e il turno
  successivo riparte con `history=2` (system + primo user).
- **prompt_tokens**: conteggio REALE restituito dal server (`usage.PromptTokens`), non
  una stima — ricalibra la stima interna del `SessionManager`
  ([Program.cs:1261-1265](../../agent/pm-code/Program.cs#L1261-L1265)). È la misura di
  crescita del contesto disponibile nel log.
- **tool call/risultato**: una riga `->` e una riga `<-` per ogni tool invocato in quel
  passo, entrambe troncate a 300 caratteri con `OneLine()` (newline schiacciati, ellissi
  con conteggio `…(+N)` se tagliato). **Non esiste un campo booleano ok/errore
  esplicito**: ogni ramo `catch` dei tool (`FileSystemTools.cs`, `GitTools.cs`,
  `AgentTools.cs`, `TerminalTools.cs`, `WebTools.cs`) restituisce una stringa che
  **inizia con `ERRORE`** — è la convenzione che questo strumento usa come euristica per
  "errore" nei digest/segnali (vedi `Models.cs: ToolCallRecord.LooksLikeError`). Non è
  garantito per OGNI possibile fallimento (es. un errore HTTP 500 del server LLM stesso
  non passa da qui, finisce in `UI.Error` e basta — non loggato nel trace file).
- **Notice (`!!`)**: interventi automatici del motore (rollover di history, kill di
  processi residui, avviso di limite step) — non sempre collegati a un singolo tool.
- **Nessun id di sessione o di processo nel log.** Questo strumento raggruppa i turni in
  "sessioni" con un'euristica (vedi sotto), NON un dato osservato.

### Cosa NON è verificabile solo leggendo il codice (dichiarato, non supposto)

- Se in produzione/uso reale il log cresce molto più dei ~13 righe trovate in questo
  repo (vedi sotto): dipende dall'uso effettivo dell'estensione, non osservabile da qui.
- Se llama-server (sia quello principale per Qwen3.8 che un eventuale secondo server per
  Qwen3.5 9B) supporta `"response_format": {"type": "json_schema", ...}` in stile
  OpenAI e in quale versione — non verificato contro un server reale (nessun endpoint "9B"
  raggiungibile in questo ambiente).
- Come disattivare il "ragionamento" di un modello Qwen3 su llama-server. Questo progetto
  tenta `"chat_template_kwargs": {"enable_thinking": false}` (pattern noto da altri runtime
  compatibili OpenAI, es. vLLM) ma **va verificato a mano** la prima volta che si punta
  un server reale — se llama-server lo ignora silenziosamente il ragionamento resterà
  attivo, se è strict sui campi extra la richiesta potrebbe fallire con 400.

## 2. Sessioni trovate

Lette dai due unici file di log realmente presenti in questo repository:

- `agent/pm-code/bin/Debug/net8.0/pm-code-trace.log` — **12 righe**, 3 turni.
- `agent/pm-code/bin/Release/net8.0/pm-code-trace.log` — **1 riga**, una sola notice
  orfana (kill di processi residui), nessun turno completo: ignorato per il resto
  dell'esperimento (non contiene nessuna sessione).

Non ci sono altri `*.log` nel repository (verificato con una ricerca per nome file su
tutta la cartella, esclusi `node_modules`).

**Limite importante dei dati disponibili**: ci sono solo 2 sessioni/3 turni totali in
tutto il repository, nessuna con errori, loop o interruzione per limite di step. I passi
4-6 di questo esperimento (dataset etichette, classificatore, confronto piani) sono quindi
eseguibili ma **su un campione troppo piccolo per essere statisticamente significativo**:
servono log reali più estesi (da un uso prolungato dell'estensione) prima di trarre
conclusioni sul supervisore 9B. Il codice è scritto per scalare a log molto più grandi;
qui lo si è potuto verificare solo su questo campione minimo.

`data/sessions.csv` (generato da `dotnet run -- sessions <logfile>`):

```
id,source_file,start,duration,turn_count,tools_used,has_errors,apparent_outcome
S1,pm-code-trace.log,2026-10-28 12:33:16,00:00:00,1,,False,completata
S2,pm-code-trace.log,2026-10-28 18:23:22,00:40:22,2,explore,False,completata
```

Nota: il prompt e il testo nel log originale contengono caratteri mal codificati
(`├¿` invece di `è`, `├ê` invece di `È`) — un difetto preesistente nella codifica con cui
pm-code scrive quel file su Windows, non introdotto da questo strumento. Visibile anche
in `data/raw/*.redacted.txt` e in `data/labels.csv`, copiato fedelmente dall'originale.

## 3. Segnali deterministici

Implementati in `SignalDetector.cs`, soglie in `data/thresholds.json` (tutte
configurabili, nessuna hardcoded nel codice tranne i default se il file manca):

| Soglia | Default | Significato |
|---|---|---|
| `RepeatCallMin` | 3 | stessa chiamata (tool+argomenti) ripetuta >= N volte di fila |
| `RepeatErrorMin` | 2 | stesso errore (tool+risultato) ripetuto >= N volte di fila |
| `NoFileChangeTurns` | 5 | turni trascorsi senza `write_file`/`edit_file`/`delete_file`/`move_file`/`create_directory` dopo la prima modifica |
| `ContextPctMax` | 0.9 | `prompt_tokens` dell'ultimo step >= X% di `MaxContextTokens` |
| `MaxContextTokens` | 81920 | copiato da `agent/pm-code/appsettings.json` → `AgentSettings:MaxContextTokens` (verificato, non supposto) |
| `DurationMinutesMax` | 20.0 | minuti trascorsi dal primo turno della sessione |

Sui due dati reali disponibili (`dotnet run -- signals <logfile>`):

```
S1: nessun segnale.
S2:
  [duration] al turno 2: 00:40:22 dall'inizio sessione (soglia 20 min)
```

(S2 scatta solo per durata — 40 minuti tra i due turni della stessa conversazione — non
per loop/errori, che non sono presenti nei dati disponibili.)

## 4. Dataset per le etichette

`dotnet run -- labels <logfile> --out data/labels.csv --max 10` seleziona fino a 10
sessioni (prima quelle con esito apparente diverso da "completata", poi per numero di
turni decrescente — con solo 2 sessioni disponibili qui prende entrambe), e per ciascuna
genera una riga per 2-3 punti intermedi più la fine sessione, con digest e colonna
`label` **vuota**. Generato in `data/labels.csv` (3 righe: S2/turno_1, S2/fine_sessione,
S1/fine_sessione). Le etichette (`on_track`/`looping`/`off_task`/`stuck`) vanno compilate
a mano: non sono state indovinate né precompilate.

## 5. Prova del classificatore (9B)

`dotnet run -- classify --labels data/labels.csv [--disable-thinking]`, legge
`SUPERVISOR_LAB_9B_ENDPOINT` e `SUPERVISOR_LAB_9B_MODEL` dall'ambiente, manda ogni digest
a `{endpoint}/v1/chat/completions` con `temperature=0` e `response_format` vincolato allo
schema richiesto (`state` enum, `reason`/`suggestion` stringa max 200 caratteri — vedi
`LlamaServerClient.cs: ClassificationSchema`). Salva le risposte in
`data/classify-results.csv` e, se la colonna `label` di `data/labels.csv` è stata
compilata, stampa la matrice di confusione etichetta-umana × stato-modello.

**Non eseguito con un server reale**: nessun endpoint 9B è configurato o raggiungibile in
questo ambiente (verificato: nessun riferimento a un secondo llama-server nel repo). Il
comando è stato verificato solo per il comportamento di errore quando le variabili
d'ambiente mancano (fallisce con messaggio chiaro, non con un'eccezione generica). La
confusion matrix e il confronto con i segnali deterministici sono da eseguire quando
l'utente compila `data/labels.csv` e punta lo strumento al server 9B vero.

## 6. Confronto piani (solo raccolta)

`dotnet run -- plans --tasks data/tasks.txt [--out data/plans]`, legge 4 variabili
d'ambiente (`SUPERVISOR_LAB_9B_ENDPOINT`/`_MODEL` per il 9B,
`SUPERVISOR_LAB_AGENT_ENDPOINT`/`_MODEL` per Qwen3.8), chiede a entrambi un piano JSON
(`steps`, `files`, `verify_command`, `max_turns`) per ciascun compito in
`data/tasks.txt` e salva i due piani affiancati in `data/plans/task<N>_9b.json` /
`task<N>_agent.json` più il testo del compito in `task<N>_task.txt`. **Non valuta i
piani**, come richiesto.

`data/tasks.txt` è stato creato come template vuoto (solo commenti con le istruzioni):
i 3 compiti da confrontare non sono stati forniti in questa sessione, quindi non inventati.
Compilarlo prima di eseguire `plans`.

## 7. Nuovo formato: trace NDJSON strutturato

`agent/pm-code/StructuredTrace.cs` (aggiunta ADDITIVA, autorizzata separatamente — vedi
nota in cima a questo file) scrive un file NDJSON per sessione/conversazione in
`PM_TRACE_DIR` (default: cartella `traces` accanto all'eseguibile di pm-code), nome
`<data>_<ora>_<sessionId>.ndjson`. A differenza del vecchio `pm-code-trace.log`:

- attivo di default in **tutte** le modalità (REPL e `--stdin-protocol`), non solo
  `--stdin-protocol`; `PM_TRACE=off` lo disattiva;
- i confini di sessione e turno sono eventi espliciti (`session_start`/`session_end`,
  `turn_start`/`turn_end` con un campo `reason`: `completed`/`cancelled`/`error`/`max_turns`)
  — non un'euristica su "history=N" come nel vecchio formato;
  gli eventi sono: `session_start`, `user_message`, `turn_start`, `tool_call`, `llm_request`,
  `guard_triggered`, `context_size`, `turn_end`, `session_end` (vedi `StructuredTrace.cs`
  per i campi di ciascuno).

### Come i comandi lo leggono

`sessions`/`digest`/`signals` accettano, con la **stessa interfaccia** a riga di comando,
il parametro `<logfile>` come:

1. il vecchio `pm-code-trace.log` in testo — comportamento invariato (vedi §1-3 sopra);
2. un singolo file `.ndjson` — una sessione;
3. una cartella di file `.ndjson` (es. l'intero `PM_TRACE_DIR` di pm-code) — ogni file è
   una sessione, numerate `S1`, `S2`, ... in ordine di nome file (cronologico, dato che il
   nome inizia con `yyyyMMdd_HHmmss`).

Instradamento in `LoadSessions()` ([Program.cs](Program.cs)): se il path è una cartella o
finisce in `.ndjson` va a `NdjsonLogParser`, altrimenti al vecchio `LogParser` — nessuna
opzione da riga di comando separata da ricordare.

Per le sessioni lette da NDJSON, `ApparentOutcome`/`AnyTurnHitStepLimit` ([Models.cs](Models.cs))
usano il campo `EndReason` di `turn_end` quando presente (dato VERO, scritto da pm-code) invece
dell'euristica sugli step del vecchio formato — più preciso, non un'approssimazione aggiuntiva.
`HistoryCount` invece non esiste nel formato NDJSON (nessun evento equivalente) e resta `0`
per questi turni: non è usato da nessun digest/segnale, quindi non è un problema.

### Verifica fatta

Non è stato possibile generare un trace NDJSON con un vero turno dell'agente (nessun
llama-server raggiungibile in questo ambiente, stesso limite di §5-6). È stato invece
verificato chiamando `StructuredTrace` direttamente (nessun LLM, nessun loop dell'agente —
un vero e proprio smoke test richiesto esplicitamente come alternativa) con una riga per
ogni tipo di evento, incluso un segreto finto (`OPENAI_API_KEY=sk-...`) per controllare la
redazione: oscurato correttamente in `user_message.text`. I file prodotti sono stati poi
passati a `dotnet run -- sessions/digest/signals` di questo stesso progetto: tool ripetuto
4 volte rilevato come `repeat_call`/`repeat_error`, `turn_end` con `reason":"max_turns"`
riportato come `apparent_outcome = "interrotta (limite step)"`. Il progetto di test e i
file generati sono stati cancellati subito dopo (erano fuori da questo repository, nello
scratchpad di sessione), quindi non compaiono qui: la verifica è descritta, non lasciata
come artefatto.

## Dati e privacy

`Redactor.cs` oscura pattern di chiavi/token/password/variabili d'ambiente sensibili e
tronca ogni riga a 2000 caratteri PRIMA di scrivere una copia di un log in
`data/raw/`. Verificato sui due log reali: non contenevano alcuna credenziale (solo
testo di prompt/risposte e tool call), quindi la copia redatta coincide quasi col
contenuto originale salvo il troncamento. Il redattore gira comunque su qualunque log
futuro passato a `sessions --copy-redacted`, per lo stesso motivo per cui è stato chiesto:
non fidarsi a occhio.

## Come eseguire tutto

```powershell
cd tools/supervisor-lab
dotnet build

$log = "..\..\agent\pm-code\bin\Debug\net8.0\pm-code-trace.log"

dotnet run -- sessions $log --out data/sessions.csv --copy-redacted data/raw
dotnet run -- digest $log --session S2 --turn 1
dotnet run -- signals $log --thresholds data/thresholds.json
dotnet run -- labels $log --out data/labels.csv --max 10
# compilare a mano la colonna "label" di data/labels.csv, poi:
$env:SUPERVISOR_LAB_9B_ENDPOINT = "http://<host-server-2>:<porta>"
$env:SUPERVISOR_LAB_9B_MODEL    = "<nome modello 9B su llama-server>"
dotnet run -- classify --labels data/labels.csv --disable-thinking

# compilare data/tasks.txt con i 3 compiti, poi:
$env:SUPERVISOR_LAB_AGENT_ENDPOINT = "http://100.102.197.68:9000"   # da agent/pm-code/appsettings.json
$env:SUPERVISOR_LAB_AGENT_MODEL    = "PM LLM"
dotnet run -- plans --tasks data/tasks.txt --out data/plans
```

## Esito di `dotnet build`

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```
(.NET SDK 10.0.401, target `net8.0`, eseguito in questo ambiente il 2026-10-29.)

## File creati

```
tools/supervisor-lab/
  supervisor-lab.csproj
  README.md               (questo file)
  Program.cs               CLI: sessions, digest, signals, labels, classify, plans
  Models.cs                 Turn, Session, ToolCallRecord, Thresholds, SignalHit
  LogParser.cs               parsing di pm-code-trace.log + raggruppamento in sessioni
  NdjsonLogParser.cs          parsing del nuovo trace NDJSON (§7) — stessi Session/Turn
  DigestBuilder.cs            digest compatto (punto 2)
  SignalDetector.cs            segnali deterministici (punto 3)
  Redactor.cs                   oscuramento + troncamento per le copie in data/
  LlamaServerClient.cs           client HTTP verso llama-server (punti 5-6)
  CsvUtil.cs                      CSV minimale (escape/parse coerenti tra loro)
  data/
    thresholds.json          soglie dei segnali (punto 3)
    tasks.txt                 template vuoto per i 3 compiti del punto 6 (da compilare)
    sessions.csv              generato: elenco sessioni del log reale disponibile
    labels.csv                 generato: dataset da etichettare (colonna label vuota)
    raw/pm-code-trace.log.redacted.txt   copia redatta del log usato (punto 1)
    plans/                                vuota: nessun compito fornito per il punto 6
```

## Cosa non è stato possibile verificare (riepilogo)

- Supporto di `response_format`/JSON schema e disattivazione del ragionamento su un
  llama-server reale per il 9B: nessun endpoint raggiungibile in questo ambiente.
- Rappresentatività dei segnali/soglie e del dataset di etichette: basati su soli 3 turni
  reali, nessuno con errori/loop — servono log più estesi per un giudizio utile.
- I 3 compiti del punto 6 non sono stati forniti in questa sessione: `data/tasks.txt` è
  un template vuoto, non compiti inventati.
- §7 (NDJSON): nessun trace generato da un vero turno dell'agente (nessun llama-server
  raggiungibile), solo dallo smoke test diretto su `StructuredTrace` descritto sopra.
