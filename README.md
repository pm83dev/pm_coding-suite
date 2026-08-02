# PM Coding Suite

Estensione VS Code (`extension/`) + agente di sviluppo autonomo .NET (`agent/`) che parlano tra loro via NDJSON su stdin/stdout. L'estensione è **solo frontend**: mostra la chat, gestisce diff e conferme; tutta l'intelligenza (system prompt, tool calling, chiamata al modello) vive nell'agent .NET.

> Nello stesso ambiente esiste anche **PM Autocomplete** (`C:\DevAgentPM\pm-autocomplete-tool`), estensione separata e indipendente per inline completion/fix/edit/webchat. Non fa parte di questa suite, ma condivide il publisher (`pm-software-automation`) — occhio ai nomi dei chat participant se la tocchi (vedi [Conflitti tra estensioni](#conflitti-tra-estensioni)).

## Architettura

```
VS Code  ──@pm──►  extension/ (TS)  ──NDJSON stdin/stdout──►  agent/ pm-code.exe (.NET)  ──HTTP──►  llama-server
                   puro frontend                              tool calling, ReAct loop           (LLM locale)
```

- **Processo persistente per sessione**: l'estensione spawna `pm-code.exe --stdin-protocol` al primo messaggio `@pm` e lo riusa per tutti i messaggi successivi (non un processo per messaggio). Un solo processo per combinazione `agentPath` + cartella workspace aperta.
- **Protocollo**: una riga JSON per messaggio.
  - stdin (extension → agent): `{"type":"request","prompt":"...","context":[{"role":"user"|"assistant","content":"..."}]}`
  - stdout (agent → extension), streaming: `{"type":"token","text":"..."}`, `{"type":"tool_call","tool":"...","args":{...}}`, `{"type":"tool_result","tool":"...","result":"..."}`, `{"type":"edit_proposal","path":"...","content":"..."}`, `{"type":"done"}`
  - stdin (extension → agent), su richiesta modifica file: `{"type":"edit_decision","accepted":true|false}`
  - **stderr**: tutto il logging dell'agent (colori, step indicator, errori) — mai su stdout, altrimenti romperebbe il parsing NDJSON. Visibile in VS Code da `Help → Toggle Developer Tools → Console` (righe `[PM Agent] ...`).
- **Mai scrittura diretta su disco**: `write_file`/`edit_file` emettono `edit_proposal` e aspettano `edit_decision` prima di toccare il filesystem. L'estensione mostra un diff nativo VS Code (o un'anteprima, se il file è nuovo) con bottoni Applica/Rifiuta.

## Struttura repo

```
pm_coding-suite/
├── Specs.md              — spec originale dell'integrazione (contesto storico)
├── extension/             — VS Code extension "PM Chat Participant" (TypeScript)
│   └── src/
│       ├── extension.ts            — activate/deactivate, registrazione comandi
│       ├── chatParticipant.ts      — handler @pm, diff, applica/rifiuta
│       ├── agentProcess.ts         — spawn/gestione processo pm-code.exe, protocollo NDJSON
│       ├── editProposalProvider.ts — TextDocumentContentProvider per il diff
│       ├── edits.ts                — applica un ProposedEdit come WorkspaceEdit
│       ├── api.ts, config.ts       — legacy autocomplete (FIM), non collegati ad @pm
│       └── utils.ts
└── agent/pm-code/          — PM Code Agent (.NET 8 console)
    ├── Program.cs           — REPL CLI + modalità --stdin-protocol, ReAct loop
    ├── appsettings.json     — config LLM/workspace/max steps
    ├── ChatModels.cs, LlamaClient.cs — protocollo OpenAI-compatible verso llama-server
    ├── WorkSpacecontext.cs  — sandbox filesystem (no path traversal)
    ├── ToolDispatcher.cs    — registro/routing di tutti i tool
    ├── FileSystemTools.cs   — read/edit/write/glob/grep/... (edit/write passano da edit_proposal)
    ├── TerminalTools.cs     — run_command, job in background, run_dotnet, sol_analyze
    ├── GitTools.cs, AgentTools.cs, WebTools.cs, TodoTools.cs
    └── SessionManager.cs    — rollover/riassunto quando la history si avvicina al limite di contesto
```

## Prerequisiti

- **.NET 8 SDK** (per buildare/pubblicare `agent/`)
- **Node.js + npm** (per buildare `extension/`)
- **llama-server.exe** in esecuzione con flag `--jinja` (NON `--chat-template`), raggiungibile all'URL configurato in `appsettings.json` (`LlmSettings:BaseUrl`)
- **VS Code** ≥ 1.125.0

## Build & pubblicazione

### Agent (.NET)

Da `agent/pm-code/`:

```powershell
dotnet build                        # build di sviluppo, veloce
dotnet build -c Release             # build release

# Pubblicazione eseguibile standalone self-contained (singolo file .exe):
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish-test
```

L'output (`publish-test\pm-code.exe` + `appsettings.json` copiato accanto) è quello da puntare in `pmChat.agentPath`.

**⚠️ Il file `.exe` viene bloccato mentre il processo è in esecuzione.** Se `dotnet publish` fallisce con `UnauthorizedAccessException` / "Access to the path ... is denied", un `pm-code.exe` è ancora vivo (probabilmente la sessione VS Code che stai usando). Verifica ed eventualmente terminalo prima di ripubblicare:
```powershell
Get-Process pm-code -ErrorAction SilentlyContinue
Stop-Process -Name pm-code -Force
```
(Attenzione: se è la sessione che stai usando tu stesso in quel momento, la interrompi — verifica prima di uccidere un processo che potrebbe essere in uso.)

### Extension (TypeScript)

Da `extension/`:

```powershell
npm install          # solo la prima volta
npm run compile      # tsc -p ./  → genera out/*.js
npm run package       # vsce package → genera pm-chat-participant-<versione>.vsix
```

Installazione/aggiornamento:
```powershell
code --install-extension "extension\pm-chat-participant-<versione>.vsix" --force
```
`--force` serve se la versione nel `.vsix` non è cambiata rispetto a quella già installata. **Dopo l'installazione ricarica sempre la finestra VS Code** (`Ctrl+Shift+P` → *Developer: Reload Window*) — un'estensione già attiva non si aggiorna a caldo.

> Quando modifichi `package.json` o `src/*.ts`, alza la versione (`"version"` in `package.json`) prima di ripacchettare: aiuta a verificare in `Ctrl+Shift+X` che la finestra stia davvero usando la build nuova.

Per iterare più velocemente in sviluppo, in alternativa al `.vsix`: `F5` da VS Code aperto su `extension/` apre un Extension Development Host con l'estensione già caricata (va comunque ricaricato ad ogni modifica).

## Configurazione

Unico setting VS Code richiesto: **`pmChat.agentPath`** — percorso assoluto di `pm-code.exe` (`Ctrl+,` → cerca `pmChat.agentPath`). Senza, `@pm` risponde con un avviso e un link alle impostazioni.

`agent/pm-code/appsettings.json`:
```json
{
  "LlmSettings": { "BaseUrl": "http://...:9000", "Model": "..." },
  "AgentSettings": { "Workspace": "./AgentWorkspace", "MaxSteps": 30, "MaxContextTokens": 128000 },
  "WebSearchSettings": { "BraveApiKey": "" }
}
```
- `AgentSettings:Workspace` è il default usato in modalità **CLI/REPL**. In modalità `--stdin-protocol` (quella usata dall'estensione) viene **sempre sovrascritto** dalla cartella VS Code realmente aperta, passata come argomento `--workspace <path>` — non serve toccare questo file per l'uso quotidiano da estensione.
- `AgentSettings:MaxContextTokens`: il rollover di sessione (riassunto automatico) scatta al 70% di questo valore (soglia fissa in `SessionManager.cs`).

## Comandi disponibili in VS Code

- `@pm <messaggio>` — chat principale
- **"+ New Chat"** (pulsante nel pannello Chat, non un comando digitato) — inizia una conversazione pulita: resetta history, piano (todo), cache dei file letti e cwd del terminale lato agent. **Non** uccide il processo `pm-code.exe`, che resta vivo.
- **Command Palette → "PM Chat: riavvia PM Code Agent"** — uccide davvero il processo e ne fa ripartire uno pulito al prossimo messaggio. Usalo se il processo si blocca in uno stato anomalo (non solo per pulire la conversazione — per quello basta "New Chat").
- **Stop** (durante una risposta in corso) — termina il processo dell'agent (hard stop): non esiste un modo "morbido" di interrompere un turno già in corso, quindi si perde la history accumulata in quel turno specifico. Il prossimo messaggio fa ripartire un processo pulito.
- Non esistono comandi `/exit` o `/new` digitabili in chat — se scritti vengono inviati come testo letterale al prompt.

## Comportamenti da conoscere

- **Memoria tra i turni**: nella stessa conversazione (stessa chat, `context` non vuoto), l'agent mantiene l'intera history nel processo tra un messaggio e l'altro — inclusi i tool call passati. Questo gli dà "memoria" di azioni fatte in turni precedenti (es. un file di workaround creato prima). Su chat nuova, la history si resetta.
- **Todo list (`manage_todo`)**: per task multi-step il modello può creare/aggiornare un piano esplicito (checklist ✅/🔄/⬜), mostrato in chat. Persiste per la conversazione, si azzera su nuova chat.
- **Anti-loop**: blocca chiamate identiche ripetute (>3 volte stesso tool+argomenti) e, separatamente, forza una decisione dopo 6 tool call di sola lettura consecutivi senza un'azione concreta (per evitare che il modello "rimugini" all'infinito senza mai agire).
- **Job in background**: comandi lanciati con `run_command background=true` (es. `dotnet run`, `ng serve`) restano tracciati dall'agent (`list_background_jobs`, `stop_background_job`, `get_background_output`). Se il processo `pm-code.exe` viene riavviato (vedi comando restart sopra), questa lista si azzera in memoria ma i processi reali restano vivi, "orfani" — vanno fermati manualmente (`Get-Process` / `Stop-Process`, o per porta con `Get-NetTCPConnection -LocalPort <porta>`).
- **Diff per file nuovi**: se l'agent propone di creare un file che non esiste ancora, non c'è un "prima" da confrontare — l'estensione mostra un bottone "Visualizza anteprima" invece del diff.

## Conflitti tra estensioni

`PM Autocomplete` (repo separato) registrava in passato un proprio chat participant con lo stesso nome breve (`name: "pm"`) di questa estensione — causava una collisione per cui `@pm` risolveva a caso all'una o all'altra. È stato rimosso da quel lato (solo la registrazione `vscode.chat.createChatParticipant`, la webchat custom e fix/edit/autocomplete di quell'estensione restano intatti). Se in futuro si ripresenta un `@pm` che risponde con tool/stile diversi dal previsto, controllare per prima cosa `Ctrl+Shift+X` → quali estensioni con publisher `pm-software-automation` sono installate e se dichiarano `chatParticipants` con `name` duplicati.

## Debug rapido del protocollo (senza VS Code)

Utile per isolare se un problema è nell'agent o nell'estensione — invia una richiesta NDJSON direttamente all'exe pubblicato:
```powershell
echo '{"type":"request","prompt":"ciao","context":[]}' | .\publish-test\pm-code.exe --stdin-protocol
```
Lo stdout deve contenere **solo** righe JSON valide (`token`/`tool_call`/`tool_result`/`edit_proposal`/`done`); qualunque altra riga lì è un bug di logging finito sul canale sbagliato.
