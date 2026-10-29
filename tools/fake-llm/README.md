# fake-llm

Finto server OpenAI-compatible per pilotare `pm-code` in modalità REPL/CLI (l'unica
modalità usata ora — l'estensione VS Code/`--stdin-protocol` non è più in uso) **senza un
llama-server reale**, per verificare il trace NDJSON strutturato (`StructuredTrace.cs`)
aggiunto ad `agent/pm-code` in una sessione precedente.

Nessuna modifica a `pm-code` in questa fase, a parte eventuali correzioni a bug del
trace emersi dai test — **nessuno è emerso** in questa fase (vedi §Bug trovati). Nessun
commit.

## Cosa c'è qui

```
tools/fake-llm/
  fake-llm.csproj / Program.cs   il finto server (vedi §Protocollo sotto)
  driver/                         driver che avvia fake-llm + pm-code, scripta l'input,
                                   opzionalmente manda Ctrl+C o Kill, salva i log
  scenarios/*.json                scenari scriptati per il finto server
  scripts/*.txt                   righe da inviare allo stdin di pm-code per ogni scenario
```

## 1. Il finto server (`fake-llm`)

Implementa solo quello che `agent/pm-code/LlamaClient.cs` usa DAVVERO (verificato leggendo
quel file, non supposto):

- `GET /v1/models` → 200 (serve a `HealthCheckAsync()`: senza questo pm-code si ferma
  subito con "Server non raggiungibile" e **non arriva mai a scrivere `session_start`** —
  scoperto nei primi tentativi, vedi §Insidie del test harness).
- `POST /v1/chat/completions` con `stream: true` → risposta SSE (`data: {...}\n\n`, poi
  `data: [DONE]\n\n`) nello stesso formato OpenAI che `StreamChatAsync` deserializza
  (`choices[].delta.content` / `.tool_calls[].function`, `choices[].finish_reason`,
  `usage.prompt_tokens`/`.completion_tokens`).

Uno scenario (`scenarios/*.json`) è un elenco di risposte; la N-esima richiesta riceve
`responses[N]`. Tipi supportati (copre tutto il punto 1 della richiesta):

| type | cosa fa |
|---|---|
| `text` | risposta di solo testo, a parole separate (come un vero streaming a token) |
| `tool_call` | una `tool_calls` delta con `name`/`arguments` scriptati |
| `http_error` | risponde con lo `status` HTTP indicato (testa il path "errore HTTP" di `StreamChatAsync`) |
| `truncated` | scrive un chunk parziale poi chiude la connessione SENZA `[DONE]` né `finish_reason` |

`delayMs` (opzionale) ritarda l'inizio della risposta — usato per il test Ctrl+C (serve una
finestra in cui pm-code sia davvero in attesa della risposta).

## 2-3. Scenari eseguiti e cosa è stato controllato

Tutti eseguiti con `driver` (vedi `driver/Program.cs`), workspace isolato per test (fuori da
questo repository, nello scratchpad di sessione — mai in `agent/pm-code` o `c:/pm-code`),
poi cancellato a fine verifica. Di seguito i fatti osservati, non un riassunto postumo.

### (a) Sessione normale, 2 turni, uscita con `quit`

Script: un messaggio di solo testo, poi un messaggio che fa leggere `hello.txt` (tool_call
+ testo finale), poi `quit`. Risultato (`traces/*.ndjson`, un file): **un solo
`session_start`**, due turni (`turn_start`/`turn_end` con `reason":"completed"`),
il `tool_call` `read_file` con `ok":true`, `durationMs` e `bytes` del risultato reale,
`llm_request` con `finishReason` corretto (`"stop"` per il testo, `"tool_calls"` per la
chiamata), `context_size` coerente con `promptTokens`, e un `session_end` finale con
`reason":"process_exit"` (uscita pulita via `quit`). **Nessun segreto o contenuto di file
integrale negli argomenti** (il tool_call porta solo `{"path":"hello.txt"}`, non il
contenuto del file).

### (b) Reset a metà conversazione

Script: un messaggio, poi `/reset`, poi un altro messaggio, poi `quit`. Risultato: **due
file NDJSON separati** — il primo si chiude con `session_end` `reason":"new_session"`
subito dopo il suo unico turno, il secondo riparte con `turnId` da 1. Confini di sessione
esattamente dove ci si aspetta (nessun evento del secondo turno finito nel primo file, e
viceversa — era proprio questo il bug corretto nella sessione precedente, qui riverificato
su un vero processo pm-code, non solo su una chiamata diretta a `StructuredTrace`).

### (c) Guard anti-loop

Script: un messaggio che fa scriptare 4 `tool_call` identici (`read_file` sullo stesso
`hello.txt`) di fila. Con `AgentSettings:MaxRepeats=3` (default), il guard di
`ExecuteToolGuarded` blocca la 4ª chiamata. Risultato: **3 eventi `tool_call`** (le prime 3
letture, eseguite davvero) **+ 1 evento `guard_triggered`** con `"guard":"anti-loop"` e la
`reason` testuale `"[LOOP-BLOCK] Tool 'read_file' chiamato 4 volte..."` — la 4ª chiamata non
produce un `tool_call` duplicato, solo il guard. Il turno si chiude comunque con
`turn_end reason":"completed"` (il loop è un'uscita anticipata DEL turno, non uno dei 4
valori di `EndReason` previsti — limite già documentato in `tools/supervisor-lab/README.md`).

### (d) Ctrl+C durante un turno

Script: un messaggio, risposta scriptata con `delayMs: 6000` (pm-code resta in attesa della
risposta per 6 secondi). Il driver invia un vero `CTRL_C_EVENT` di Windows al processo
pm-code dopo 2 secondi (quindi mentre è sicuramente ancora in attesa), via
`AttachConsole`/`GenerateConsoleCtrlEvent` (vedi `driver/Program.cs: SendCtrlC`). Risultato:
pm-code esce con il codice `-1073741510` (`0xC000013A`, `STATUS_CONTROL_C_EXIT`: la prova
che un vero Ctrl+C di Windows è arrivato, non una chiusura normale). Il file NDJSON contiene
`session_start`/`turn_start`/`user_message` **ma nessun `llm_request` né `turn_end`** (il
turno non è mai arrivato a una risposta) **e un `session_end` finale con
`reason":"process_exit"`** — la prova che `StructuredTrace.Shutdown()` (agganciata sia a
`ProcessExit` che a `Console.CancelKeyPress`, vedi `StructuredTrace.cs`) è scattata ed ha
fatto il flush anche in questo caso. Comportamento corretto: un turno interrotto a metà non
ha un `turn_end` fittizio — l'assenza stessa è il segnale giusto per chi legge il file dopo.

### (e) Kill subito dopo un turno

Script: un messaggio, nessun `quit`. Il driver aspetta che il turno finisca (verificabile
dal log) poi chiama `Process.Kill()` (TerminateProcess, nessuna possibilità per l'agente di
reagire). Risultato: il file NDJSON contiene **tutti gli eventi del turno completato fino al
`turn_end reason":"completed"`**, per intero e senza righe troncate a metà — ma **nessun
`session_end`** (impossibile: TerminateProcess non lascia eseguire nessun handler). Confirma
il requisito "flush degli ultimi eventi anche dopo kill": tutto ciò che era stato scritto PRIMA
del kill è integro, nulla di scritto va perso o corrotto (coerente con `StructuredTrace.cs`
che fa `Flush()` sincrono dopo ogni singola riga, non in batch — vedi il bug corretto nella
sessione precedente).

### Scenari aggiuntivi (oltre l'elenco a-e, per completare il punto 1)

- **http_error**: risposta HTTP 500 scriptata → `turn_end reason":"error"` (nessun retry:
  `LlamaClient` ritenta solo sui fallimenti di TRASPORTO senza status code, non su un errore
  HTTP esplicito — verificato leggendo `Program.cs`, non supposto).
- **truncated**: connessione chiusa a metà streaming, senza `[DONE]` né `finish_reason`.
  **Osservazione (non un bug del trace)**: `LlamaClient.StreamChatAsync` non distingue uno
  stream troncato da uno concluso normalmente — il ciclo `while (!reader.EndOfStream)`
  esce senza eccezione quando la connessione si chiude, quindi pm-code tratta il testo
  parziale ricevuto come risposta finale valida (`turn_end reason":"completed"`, non
  `"error"`). Il trace riporta fedelmente `promptTokens`/`finishReason` null e
  `tokensUsed:0` in questo caso — corretto rispetto a quello che pm-code riceve davvero.
  **Non corretto**: non è un bug del trace, è un comportamento di `LlamaClient.cs` che
  esula da questa fase (solo bug del trace sono correggibili, per regola esplicita).
- **segreto in un argomento reale**: un `write_file` scriptato con
  `"content":"API_KEY=sk-abcdef1234567890ABCDEF non deve comparire..."`. Nel
  `tool_call.args` del NDJSON risultante compare `"API_KEY=[REDACTED] non deve
  comparire..."` — redazione confermata end-to-end su una vera tool call eseguita da
  pm-code (non solo sulla chiamata diretta a `StructuredTrace` della verifica precedente).

## 4. Confronto con/senza trace (`PM_TRACE=off`)

Stesso scenario (a), stesso script, due esecuzioni (`--trace on` / `--trace off`).
`diff` tra gli stdout catturati: **identici byte per byte**. `diff` tra gli stderr: **una
sola riga diversa**, la riga `Workspace: ...` — diversa solo perché le due esecuzioni usano
cartelle di lavoro diverse (necessario per isolarle), non per via del trace. Stessa sequenza
di tool call, stesso ordine, stessa risposta finale, stessi conteggi token in entrambi i
casi. Con `PM_TRACE=off` nessuna cartella `traces/` viene creata (verificato).

## Bug trovati (e corretti)

**Nessuno in questa fase.** Il bug di ordinamento (un evento scritto in coda da un thread
dedicato poteva finire nel file della sessione SUCCESSIVA) era già stato trovato e corretto
nella sessione precedente, prima di questa verifica con un vero processo pm-code — qui è
stato solo riconfermato indirettamente dallo scenario (b), che non mostra lo stesso sintomo.

## Insidie del test harness (non bug di pm-code né del trace)

- `HealthCheckAsync()` chiama `GET /v1/models` **prima** di qualunque `ResetHistory()`
  (quindi prima del primo `session_start`): senza quell'endpoint nel finto server, pm-code
  si ferma subito e non scrive mai traccia — scoperto nel primo tentativo.
- `pm-code.exe` è solo l'apphost nativo: serve copiare TUTTA la cartella di build
  (`pm-code.dll`, `.deps.json`, `.runtimeconfig.json`, dipendenze), non solo `.exe`/`.pdb` —
  il primo tentativo falliva con "The application to execute does not exist".
- `ProcessStartInfo.StandardInputEncoding = Encoding.UTF8` (l'istanza statica, che include
  il preambolo BOM) fa scrivere 3 byte BOM come primo dato sullo stdin rediretto: pm-code in
  REPL legge `Console.In` con la codifica di default (NON l'`UTF8Encoding(false)` esplicito
  che usa solo in `--stdin-protocol`), quindi li decodifica come testo visibile
  (`∩╗┐davanti al primo messaggio) — risolto nel driver con `new UTF8Encoding(false)`.
  Confermato che l'evento `user_message` del trace registra fedelmente quello che pm-code
  riceve davvero (compreso questo artefatto, quando presente) — comportamento corretto del
  trace, non suo bug.

## 6. Correzione: stream troncato / finish_reason="length" trattati come errore

Autorizzata una correzione mirata in `agent/pm-code/LlamaClient.cs` e nel loop
(`Program.cs`): prima, uno stream SSE finito senza `[DONE]` e senza `finish_reason`
veniva trattato come risposta completa (osservato nello scenario g-truncated, §3 sopra).
Ora:

- `LlamaClient.StreamChatAsync` solleva `IncompleteLlmResponseException` con
  `Kind="stream_truncated"` se lo stream finisce senza `[DONE]` **e** senza
  `finish_reason`, oppure `Kind="length"` se `finish_reason=="length"` (sempre trattato
  come incompleto, anche se il JSON di un'eventuale tool call risulta per caso valido —
  vedi il commento su `IncompleteLlmResponseException` per il perché). L'eccezione viene
  sollevata PRIMA di ricostruire il `ChatMessage`: non esiste mai un'istanza parziale che
  il chiamante potrebbe eseguire per errore — nessuna tool call da una risposta incompleta
  viene mai eseguita.
- `Program.cs` ritenta la STESSA richiesta fino a 2 tentativi in tutto (backoff fisso
  500ms, non esponenziale), poi chiude il turno con un messaggio d'errore chiaro
  (`turn_end reason="error"`), senza eseguire nulla di parziale.
- Trace: ogni tentativo fallito scrive `llm_request` con `ok:false` e
  `errorKind` ∈ {`stream_truncated`, `length`, `http_error`} (quest'ultimo aggiunto anche
  al path HTTP-error preesistente, che prima non aveva nessun evento `llm_request` per i
  tentativi falliti); ogni retry scrive un evento `llm_retry` (`attempt`, `maxAttempts`,
  `reason`, `delayMs`) — aggiunto anche al retry di trasporto preesistente (connessione
  persa), che prima ritentava silenziosamente senza lasciare traccia.

### Test eseguiti (con un vero processo pm-code, stesso harness di §2-3)

- **g-truncated** (stream troncato su testo, già presente): ora **2 tentativi** — il 1°
  fallisce (`errorKind="stream_truncated"`), il 2° (ricevuto dal fallback neutro del finto
  server, che risponde normalmente) riesce → turno `completed`. Prova che il retry
  recupera davvero da un fallimento transitorio.
- **j-truncated-toolcall** (nuovo: stream troncato mentre si stava accumulando una tool
  call, non testo): 2 tentativi, entrambi `errorKind="stream_truncated"` (scriptati
  identici) → `turn_end reason="error"`, **nessun `tool_call` mai eseguito**, nessun file
  scritto nel workspace di test.
- **i-length-broken-toolcall** (nuovo: `finish_reason="length"` con una tool call
  `write_file` con JSON deliberatamente tagliato a metà): 2 tentativi, entrambi
  `errorKind="length"` → `turn_end reason="error"`, messaggio all'utente che nomina
  esplicitamente la tool call con JSON incompleto, **nessun file scritto**.
- **Confronto con la versione precedente**: gli scenari a/b/c/e (non toccati da questa
  correzione) e f-http-error (stesso esito, solo con in più l'evento `llm_request
  ok:false errorKind:"http_error"` che prima mancava) producono **stdout identico** alla
  versione precedente — verificato rieseguendoli con il nuovo build e confrontando con
  l'output osservato prima della correzione (byte per byte sulle righe "Agent ›" / errori
  mostrati). g-truncated invece cambia esito DI PROPOSITO (è la correzione stessa).

## Decisioni da rivedere (soglie e messaggi)

Non richiesti esplicitamente con un valore preciso, quindi scelti qui — da confermare:

1. **"fino a 2 tentativi"** interpretato come **2 tentativi totali** (1 iniziale + 1
   retry), non 2 retry oltre il primo tentativo (3 totali). Riusa il contatore
   `llmAttempt` già esistente per i retry di trasporto.
2. **Backoff "breve"**: fisso a **500 ms**, non esponenziale (con solo 1 retry
   l'esponenziale non avrebbe effetto). Il retry di trasporto preesistente resta
   esponenziale (1s/2s/4s) e con fino a 3 tentativi: non l'ho toccato, solo aggiunta la
   traccia.
3. **`finish_reason="length"` è SEMPRE trattato come errore**, anche quando il JSON di
   un'eventuale tool call risulta sintatticamente valido (vedi commento su
   `IncompleteLlmResponseException` in `LlamaClient.cs`). Alternativa scartata: controllare
   solo la validità del JSON e procedere normalmente se valido — scartata perché il limite
   di lunghezza può aver tagliato testo o argomenti in un punto che per puro caso lascia il
   JSON sintatticamente valido, e non c'è modo di saperlo da qui.
4. **Nessun gate su `!streamedContent`** per il nuovo retry (a differenza del retry di
   trasporto preesistente, che non ritenta se è già stato stampato testo in chat, per non
   duplicarlo): qui si ritenta comunque, perché un contenuto troncato ha quasi sempre già
   streammato qualcosa — altrimenti il retry non scatterebbe praticamente mai. Effetto
   collaterale noto: in chat può apparire il fragmento della risposta troncata seguito dal
   testo del tentativo successivo (nessuna duplicazione di testo uguale, solo due risposte
   diverse in sequenza) — visibile nello scenario g-truncated sopra.
5. **Testo dei messaggi d'errore** (`EmitFinalNotice` per `length`/`stream_truncated`):
   scritti per questa verifica, in italiano, nello stesso stile delle altre righe
   `EmitFinalNotice` esistenti — da rivedere se non coerenti col tono voluto.

## 8. Dove sono impostati max_tokens, contesto e soglia di rollover (letto nel codice)

Richiesto come ricerca pura, nessun valore cambiato:

- **max_tokens / n_predict per singola chiamata al modello** (budget di generazione della
  RISPOSTA, non la finestra di contesto): parametro `maxTokens` di
  `RunAgentLoopAsync(int maxTokens = 32768, ...)` in `Program.cs`, inviato come
  `ChatRequest.MaxTokens`. Default **32768**; usato **1024** poi **512** solo nelle due
  chiamate interne di `/init` (generazione di AGENTS.md). Non è in `appsettings.json`: è un
  parametro con default hardcoded nel codice.
- **Dimensione del contesto assunta dal core**: `AgentSettings:MaxContextTokens` in
  `appsettings.json` (**81920** nel deployment reale), letto in `maxContextTokens`
  (`Program.cs`, fallback **6000** se la chiave manca). È un valore CONFIGURATO, non
  interrogato dal server: nessuna chiamata verifica la context window reale del modello.
- **Soglia di rollover (contesto "pieno")**: in `SessionManager.cs`,
  `HardLimit = Math.Min(MaxContextTokens * 0.70, RolloverTokens)` dove `0.70` è la costante
  `Threshold` e `RolloverTokens` viene da `AgentSettings:RolloverTokens`
  (**40000** nel deployment reale, anche come fallback se la chiave manca — quindi
  `HardLimit = Min(81920*0.70, 40000) = Min(57344, 40000) = 40000` lì). A INIZIO turno
  (nessun piano aperto) si usa solo il **60%** di `HardLimit` (costante `TurnStartFraction`
  → 24000 nel deployment reale); a metà turno o con un piano aperto si usa l'intero
  `HardLimit` (40000). Nota: il costruttore di `SessionManager` chiama il primo parametro
  `maxTokens` anche lì, ma riceve `maxContextTokens` — stesso nome di variabile per due
  concetti diversi (budget di risposta vs finestra di contesto), non lo stesso valore.
- **Stima dei token usati**: `caratteri_totali_history / charsPerToken`, con `charsPerToken`
  che parte da **4.0** e viene RICALIBRATO ad ogni risposta con il conteggio reale
  (`usage.prompt_tokens`) restituito dal server — non è mai una query diretta al tokenizer.

## 9. Verifica della correzione del punto 2-3 (length senza retry, stream_truncated 1500ms+separatore)

Autorizzata una seconda correzione, sopra quella di §6: `finish_reason="length"` non ha
più retry (prima ne aveva 1 come `stream_truncated`); il messaggio d'errore distingue
"contesto quasi pieno" da "limite di token di risposta" in base a `contextPercent` (soglia
`LengthContextFullThreshold = 0.85` in `Program.cs`); `stream_truncated` ora aspetta 1500ms
(non 500) e stampa una riga separatrice prima del retry; il trace porta anche
`contextPercent`/`maxTokens` su `llm_request`.

Nuovi scenari (`scenarios/i-length-broken-toolcall.json`, `k-length-context-full.json`;
`j-truncated-toolcall.json` riusato da §6), eseguiti con un vero processo pm-code:

- **i-length-broken-toolcall** (`promptTokens=2000`, ~2.4% del contesto): **una sola
  richiesta** al finto server (nessun retry, confermato anche dal log del finto server) →
  messaggio "Limite di token della risposta raggiunto" → `turn_end reason="error"`,
  `llm_request` con `contextPercent:2.44…`, `maxTokens:32768`. Nessun file scritto.
- **k-length-context-full** (nuovo, `promptTokens=75000`, ~91.6% del contesto): stesso
  esito ma messaggio "Contesto quasi pieno: avvia una nuova sessione...".
- **j-truncated-toolcall**: confermato il nuovo backoff di **1500ms** (timestamp dei due
  tentativi nel NDJSON: 1511ms di distanza) e la riga `[risposta interrotta, nuovo
  tentativo]` nello stderr di pm-code (le righe diagnostiche di pm-code in REPL — prompt,
  errori, "dim" — vanno tutte su stderr, non stdout: coerente con `UI.Prompt`/`UI.Error`
  esistenti, non un problema della riga nuova).
- **Verifica esplicita "il frammento troncato non entra in history"**: `fake-llm` ora
  supporta `--log-requests` (usato di default dal driver) e stampa i ruoli/contenuti
  abbreviati di `messages` per ogni richiesta ricevuta. Nello scenario j, la 2ª richiesta
  (il retry) ha **esattamente gli stessi 2 messaggi** della 1ª (`system` + `user`): nessun
  messaggio `assistant` con il frammento troncato è stato aggiunto nel mezzo.
- **`tools/supervisor-lab` — nuovi segnali verificati** (`dotnet run -- signals <traces>`):
  su i-length → `llm_errors` (1, primo al turno 1); su k-length-context-full →
  `llm_errors` + `length_context_full` (contesto al 92%, soglia 85%) + il preesistente
  `context_pct`; su j-truncated-toolcall → `llm_errors` (2, entrambi i tentativi); su
  a-normale (nessun errore) → nessun segnale nuovo.
- **Confronto con la versione precedente**: a/b/c/e/f-http-error rieseguiti con l'ultimo
  build → stdout identico a tutti i giri precedenti (§3 e §6).

## 10. Cosa non è stato possibile verificare

- **La chiusura della finestra del terminale su Windows** (richiesto esplicitamente
  dall'utente come esempio): in questo ambiente pm-code è stato avviato senza una vera
  finestra di console visibile (nessun desktop interattivo raggiungibile da qui), quindi non
  è stato possibile testare la chiusura della finestra (es. click sulla X) — solo
  `Console.CancelKeyPress` (scenario d) e `Kill` di processo (scenario e). Windows invia alla
  console un evento diverso in quel caso (`CTRL_CLOSE_EVENT`, non `CTRL_C_EVENT`): la stessa
  istruzione di `StructuredTrace.cs` lo gestirebbe (`Console.CancelKeyPress` copre anche
  quell'evento secondo la documentazione .NET), ma non è stato osservato direttamente.
- L'invio del Ctrl+C si basa su `AttachConsole`/`GenerateConsoleCtrlEvent`, che richiede che
  il driver stesso abbia una console reale a cui staccarsi/riattaccarsi: ha funzionato in
  questo ambiente (confermato dal codice di uscita `STATUS_CONTROL_C_EXIT`), ma non è
  garantito che funzioni allo stesso modo ovunque (es. un ambiente completamente headless
  senza alcuna console, diverso da quello usato qui).
- Non è stato misurato alcun comportamento di `pm-code` sotto carico reale (più turni/secondo,
  file di trace molto grandi): tutti gli scenari sono brevi per costruzione.
