# Guida integrazione PM Chat + PM Code Agent

## Contesto
Progetto composto da due cartelle indipendenti nello stesso repo:
- `extension/` — PM Chat, estensione VS Code TypeScript, Chat Participant API (`@pm`)
- `agent/` — PM Code Agent, motore .NET console con tool-calling

**Terza codebase esistente, PM Autocomplete (FIM autocomplete), NON fa parte di questa integrazione — non toccarla.**

## Situazione attuale (da correggere)
`extension/` ha una propria logica di tool-calling in TypeScript, abbozzata e incompleta. `agent/` ha un motore .NET maturo (tool `search-files`/`read-file`/`grep-text`, function calling OpenAI-compatible, testato su più modelli) ma oggi funziona solo come CLI a comando singolo, non integrato con l'estensione.

## Obiettivo
Rimuovere il tool-calling TS abbozzato da `extension/`. L'estensione diventa **solo frontend/UI**: spawna il processo `agent/`, gli parla via stdin/stdout, mostra risposte e diff. Tutta l'intelligenza (prompt building, tool execution, chiamata al modello) resta in `agent/`, che diventa l'unica fonte di verità.

## Architettura di comunicazione

**Processo:** `extension/` avvia `agent/` una volta per sessione di chat (non un processo per messaggio), lo tiene vivo per tutta la sessione per non ricaricare contesto/modello a ogni turno. Path dell'eseguibile configurabile via setting VS Code (`pm-chat.agentPath`), non bundlato nel `.vsix` — comodo per iterare durante sviluppo attivo.

**Protocollo:** NDJSON (una riga JSON per messaggio) su stdin/stdout.
- **stdin** → `extension/` scrive richieste
- **stdout** → `agent/` scrive eventi, riga per riga
- **stderr** → tutto il logging del motore .NET va qui, mai su stdout (altrimenti rompe il parsing NDJSON lato TS)

`agent/` va esteso con una nuova modalità di avvio (es. flag `--stdin-protocol`) che attiva questo comportamento. Il comportamento CLI esistente (comando singolo, output umano) resta invariato e disponibile come modalità separata — questa è un'aggiunta, non una riscrittura.

## Formato messaggi

Richiesta (extension → agent):
```json
{"type":"request","prompt":"...","context":[...cronologia messaggi precedenti...]}
```

Eventi di risposta (agent → extension), in streaming man mano che accadono:
```json
{"type":"token","text":"..."}
{"type":"tool_call","tool":"read_file","args":{"path":"..."}}
{"type":"tool_result","tool":"read_file","result":"..."}
{"type":"edit_proposal","path":"...","content":"..."}
{"type":"done"}
```

Decisione utente su una proposta di modifica (extension → agent), se l'agente deve saperlo per proseguire il ragionamento:
```json
{"type":"edit_decision","accepted":true}
```

## Regola critica: mai scrivere file direttamente

`agent/` **non deve mai scrivere su disco**. Il tool di scrittura/edit va modificato: il modello decide cosa scrivere, ma invece di applicare la modifica il motore emette `edit_proposal` e aspetta (se necessario per continuare il ragionamento) l'`edit_decision` di ritorno.

Lato `extension/`: intercetta `edit_proposal`, mostra un diff nativo VS Code (comando `vscode.diff`) tra il file reale e un `TextDocumentContentProvider` custom registrato su uno scheme dedicato (es. `pm-agent-proposed`) che restituisce il `content` proposto. Il file reale viene modificato solo dopo conferma esplicita dell'utente — mai in automatico.

## Cosa fa extension/ (lato TS)
1. Rimuovere completamente la logica di tool-calling esistente
2. Chat Participant handler (`@pm`): su ogni messaggio, se il processo `agent/` non è attivo per questa sessione, avvialo (spawn con path da `pm-chat.agentPath`); altrimenti riusa quello già in esecuzione
3. Scrivere la richiesta come riga JSON su stdin del processo
4. Leggere stdout riga per riga (readline sullo stream), fare `JSON.parse` per riga, smistare per `type`:
   - `token` → append incrementale alla risposta chat (`response.markdown(...)`)
   - `edit_proposal` → apri diff provider, attendi conferma utente
   - `done` → chiudi il turno
5. Passare la cronologia conversazione (`ChatContext.history`) come `context` in ogni richiesta

## Cosa fa agent/ (lato .NET)
1. Aggiungere modalità `--stdin-protocol`: loop che legge righe JSON da stdin, mantiene il processo vivo tra una richiesta e l'altra della stessa sessione
2. Instradare tutto il logging esistente verso stderr
3. Rendere lo streaming della risposta del modello (SSE da llama-server) incrementale verso stdout come eventi `token`, non un blocco unico a fine esecuzione
4. Modificare il tool di scrittura file: invece di scrivere su disco, emettere `edit_proposal`; se il flusso agente richiede sapere l'esito prima di proseguire, bloccarsi in attesa dell'evento `edit_decision` in arrivo su stdin
5. Mantenere invariata la modalità CLI esistente (comando singolo) come percorso alternativo, non sostituita

## Vincoli noti da rispettare, non da "risolvere"
- L'utente deve digitare `@pm` a ogni messaggio in chat: è un vincolo della Chat Participant API di VS Code (instradamento tra participant diversi), non un difetto dell'estensione — non tentare di aggirarlo con soluzioni custom
- Non serve un progetto/build unico: `extension/` (Node/VS Code host) e `agent/` (.NET) restano due artefatti buildati separatamente, uniti solo dal repo comune e dal protocollo stdin/stdout