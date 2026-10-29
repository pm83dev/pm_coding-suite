RUOLO E OBIETTIVO
Sei un ingegnere software senior. Devi creare la FASE 1 di una UI web per il mio agente di sviluppo (core C# "pm-code"). La UI è un client sottile: tutta la logica dell'agente resta nel core. In questa fase NON si tocca il core: la UI parla con un MOCK SERVER che implementa il protocollo v1 descritto sotto.

Se esiste docs/architettura-ui.md leggilo, ma in caso di conflitto PREVALE questa specifica.

REGOLE DI LAVORO
- Lavora a piccoli passi, nell'ordine indicato. Dopo OGNI passo: esegui build/test, correggi gli errori, aggiorna ui/PROGRESS.md (checklist dei passi con stato e note brevi) e poi prosegui.
- Prima di scrivere codice controlla l'ambiente (node -v, npm -v, versione Angular CLI/Angular già installata) e usa quelle versioni. Non inventare API: se non sei sicuro di una API di libreria, leggi la documentazione nel pacchetto installato (node_modules) o i tipi .d.ts.
- Il server dev è Windows: script npm cross-platform, niente comandi solo-bash, percorsi con path.join.
- Non modificare nulla fuori da ui/. In particolare NON toccare agent/pm-code, agent/pm-mcp, extension/. Niente commit.
- Se resti bloccato più di due tentativi sullo stesso errore, fermati, annota il problema in PROGRESS.md e riferiscimelo.

STRUTTURA (monorepo npm workspaces, cartella ui/)
  ui/protocol/      tipi TypeScript condivisi + validazione runtime degli messaggi (usa zod se disponibile, altrimenti type guard scritte a mano) + file di esempio JSON
  ui/mock-server/   server Node + WebSocket (libreria "ws") che simula l'agente con scenari scriptati
  ui/web/           app Angular (standalone components, signals), con Monaco editor per i diff
  ui/PROGRESS.md    checklist e note
  ui/README.md      come installare, avviare mock e UI, come lanciare i test

PROTOCOLLO v1 (WebSocket, un messaggio JSON per frame)
Busta comune: { "v": 1, "id": "<uuid>", "ts": "<ISO8601>", "sessionId": "<string|null>", "type": "<tipo>", "payload": { ... } }
Primo messaggio dal client: type "hello" con payload { "token": "...", "clientName": "pm-ui", "clientVersion": "0.1.0" }. Il server risponde "hello_ok" { "serverVersion", "protocol": 1 } oppure "error" { "code": "unauthorized" } e chiude. Messaggi con v diverso da 1 vanno rifiutati con error "unsupported_version".

COMANDI (client -> server)
  hello
  list_sessions
  new_session        { "workspace": "<percorso>" }
  open_session       { "sessionId": "..." }  (il server risponde con session_state e riemette lo storico)
  send_message       { "text": "..." }
  cancel             { }  (interrompe il turno in corso)
  approve            { "approvalId": "..." }
  reject             { "approvalId": "...", "reason": "..." }  (reason opzionale)
  set_model          { "modelId": "..." }
  set_permission     { "mode": "readonly" | "ask" | "auto" }

EVENTI (server -> client)
  hello_ok, error { "code", "message" }
  sessions_list      { "sessions": [ { "id", "title", "workspace", "updatedAt", "status": "idle|running|waiting_approval" } ] }
  session_state      { "session": {...}, "messages": [...], "model": "...", "permission": "..." }
  models_list        { "models": [ { "id", "label", "endpoint", "contextSize" } ], "active": "<id>" }
  permission_state   { "mode": "...", "rules": [] }
  turn_started       { "turnId": "..." }
  assistant_delta    { "turnId", "messageId", "text": "<frammento>" }
  assistant_done     { "turnId", "messageId", "usage": { "promptTokens", "completionTokens" } }
  tool_call_started  { "turnId", "callId", "name", "args": {...} }
  tool_call_finished { "turnId", "callId", "ok": true|false, "summary": "...", "durationMs": 123 }
  approval_request   { "turnId", "approvalId", "kind": "write" | "command", "path": "...", "title": "...", "unifiedDiff": "<diff unificato>" (solo kind write), "command": "..." (solo kind command), "expiresAt": "<ISO8601>" }
  approval_resolved  { "approvalId", "outcome": "approved" | "rejected" | "expired" | "cancelled" }
  turn_finished      { "turnId", "reason": "completed" | "cancelled" | "error" }

REGOLE DI SEMANTICA
- APPROVAZIONE DIFFERITA: quando serve un'approvazione il turno resta sospeso (stato waiting_approval) finché arriva approve/reject o scade expiresAt (nel mock: 120 secondi, poi outcome "expired"). Il server non applica mai una scrittura senza approve esplicito, tranne in modalità "auto".
- Modalità permessi: "readonly" = nessuna scrittura (il server emette tool_call_finished ok=false con summary "scrittura bloccata"); "ask" = approval_request per ogni scrittura/comando; "auto" = applica senza chiedere ma emette comunque tool_call_started/finished.
- cancel durante un turno: il server emette approval_resolved "cancelled" per eventuali approvazioni pendenti e turn_finished con reason "cancelled".

STACK UI (vincolante)
- Usa la versione di Angular già installata (controlla con "ng version") e FISSALA in package.json senza intervalli (versioni esatte). Non aggiornarla.
- Solo API moderne: componenti standalone (nessun NgModule), signals/computed/effect per lo stato, inject() al posto dei costruttori, control flow @if/@for/@switch, ChangeDetectionStrategy.OnPush. Non usare *ngIf/*ngFor né NgModule.
- RxJS solo dove serve davvero (WebSocket con reconnect), il resto con signals.
- Prima di usare una API Angular che non conosci perfettamente, verifica nei tipi in node_modules/@angular/*.
- Niente librerie UI pesanti (Material ecc.): CSS semplice con variabili per il tema.

PASSO 1 - ui/protocol
Tipi per ogni messaggio sopra, union discriminata sul campo "type", funzioni parseServerMessage / parseClientMessage che validano a runtime e restituiscono errore leggibile, funzione helper makeMessage(type, payload, sessionId). Cartella examples/ con un JSON per ogni tipo. Test unitari: tutti gli esempi validano; un messaggio con v=2, uno senza type e uno con payload errato vengono rifiutati.

PASSO 2 - ui/mock-server
Server ws su 127.0.0.1 (porta da variabile PM_MOCK_PORT, default 8765), token da PM_MOCK_TOKEN (default "dev-token"; se manca lo stampa in avvio). Richiede hello con token valido prima di qualsiasi altro comando. Mantiene in memoria 2 sessioni di esempio. Scenari scriptati su send_message in base al testo: contiene "leggi" -> assistant_delta in streaming (frammenti da pochi caratteri ogni ~40 ms) + un tool_call_started/finished di lettura + risposta; contiene "modifica" -> tool_call di scrittura che genera approval_request kind "write" con un unifiedDiff realistico, attende approve/reject/expiry, poi prosegue e conclude; contiene "comando" -> approval_request kind "command"; contiene "errore" -> turn_finished reason "error"; qualunque altro testo -> risposta semplice in streaming. Rispetta readonly/ask/auto, set_model (2-3 modelli finti con contextSize), cancel e open_session. Test automatici con un client ws: hello con token errato, flusso "modifica" con approve, con reject, con scadenza (usa un timeout configurabile corto nei test), cancel a metà streaming.

PASSO 3 - ui/web: scheletro e connessione
App Angular standalone con signals. ProtocolService: connessione WebSocket, hello, validazione dei messaggi in ingresso con ui/protocol, riconnessione con backoff esponenziale (max 15 s) e, riconnessa, open_session dell'ultima sessione. Stato esposto come signal: connessione (connecting/open/closed), sessioni, sessione corrente, messaggi, modello, permessi, turno corrente, approvazioni pendenti, usage. URL del server e token inseriti in una finestra Impostazioni, salvati in sessionStorage; MAI il token nell'URL. Test unitari sul reducer/stato (applicazione degli eventi) e sul parsing.

PASSO 4 - vista chat
Layout: barra laterale con elenco sessioni e pulsante nuova sessione; area centrale con la conversazione; casella di input con invio (Ctrl+Invio) e pulsante Annulla attivo durante un turno. Streaming del testo con rendering Markdown SANITIZZATO (usa marked o equivalente più DOMPurify; i blocchi di codice con evidenziazione leggera). Barra superiore con stato connessione, selettore modello (set_model), selettore modalità permessi (set_permission, con colore diverso per readonly/ask/auto), contatore token dell'ultimo turno.

PASSO 5 - timeline degli strumenti
Per ogni turno mostra le chiamate agli strumenti come schede comprimibili: nome, argomenti (JSON formattato, troncato oltre N righe con "mostra tutto"), esito ok/errore, durata, summary. Stato "in corso" con indicatore finché non arriva tool_call_finished.

PASSO 6 - approvazioni con diff
Per approval_request kind "write": scheda con titolo, percorso e Monaco DiffEditor (sola lettura) ricavato dal unifiedDiff (ricostruisci originale e modificato dal diff; se il parsing fallisce mostra il diff unificato come testo colorato), toggle affiancato/in linea, conto alla rovescia verso expiresAt, pulsanti Approva e Rifiuta (con campo motivo opzionale). Per kind "command": mostra il comando in un blocco monospazio con gli stessi pulsanti. Dopo approval_resolved la scheda diventa di sola consultazione con l'esito. Carica Monaco in modo lazy (non nel bundle iniziale) e verifica che funzioni offline, senza CDN: i file di Monaco devono essere serviti dal build.

PASSO 7 - rifiniture
Gestione errori visibile (banner per error del server e perdita di connessione), stato vuoto con istruzioni, accessibilità di base (focus, aria-label sui pulsanti, contrasto), tema chiaro/scuro che segue il sistema.

PASSO 8 - test e documentazione
Test e2e minimo (Playwright se già disponibile, altrimenti test di integrazione con il mock) che esegue: connessione, invio "modifica", approvazione, verifica dell'esito; poi rifiuto; poi cancel. Completa ui/README.md con: installazione, avvio mock, avvio UI, variabili d'ambiente, come lanciare i test, schema degli eventi in breve.

CRITERI DI "FATTO" DELLA FASE 1
1. npm install, npm run build e npm test dalla cartella ui/ terminano senza errori.
2. npm run mock avvia il server; npm start serve la UI; la UI si connette e completa gli scenari "leggi", "modifica" (approvata e rifiutata), "comando", "errore" e un cancel a metà streaming.
3. Nessuna dipendenza da CDN in esecuzione; nessun token nell'URL o nei log.
4. Riavviando il mock la UI si ricollega da sola e ripristina la sessione.
5. ui/PROGRESS.md aggiornato e ui/README.md completo.

FUORI SCOPE (non farlo ora)
Collegamento al core pm-code, trasporto sul core, autenticazione oltre al token, permessi per regole di percorso/strumento, estensione VS Code, cronologia persistente su disco, internazionalizzazione.

Alla fine consegnami: elenco dei file creati, esito di build e test, come avviare tutto, e i punti incerti o le scelte che hai dovuto fare.

