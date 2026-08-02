# Local Code Agent

Agente CLI stile Claude Code, in C#, powered by llama.cpp locale.

## Struttura

```
LocalCodeAgent/
├── Program.cs                  ← loop principale + UI
├── LocalCodeAgent.csproj
├── Core/
│   ├── LlamaClient.cs          ← HTTP client per llama-server
│   ├── WorkspaceContext.cs     ← gestione workspace + tree snapshot
│   └── ToolDispatcher.cs      ← registro e routing tool
├── Models/
│   └── ChatModels.cs           ← DTOs OpenAI-compatible
└── Tools/
    ├── FileSystemTools.cs      ← read/write/list/search/delete file
    ├── TerminalTools.cs        ← run_command, run_dotnet
    └── AgentTools.cs           ← get_workspace_tree, set_workspace
```

## Prerequisiti

- llama-server.exe avviato su porta 9000 con `--jinja` (senza `--chat-template`)
- .NET 8 SDK
- Modello: `qwen2.5-7b-instruct-q4_k_m.gguf`

## Avvio

```powershell
# Terminale 1 — LLM server
cd C:\llamacpp
.\llama-server.exe -m qwen2.5-7b-instruct-q4_k_m.gguf --jinja -ngl 0 -c 4096 --parallel 1 --host 0.0.0.0 --port 9000

# Terminale 2 — Agente
cd C:\LocalCodeAgent
dotnet run
```

## Comandi runtime

| Comando      | Azione                                       |
| ------------ | -------------------------------------------- |
| `/reset`     | Nuova conversazione, aggiorna tree workspace |
| `/workspace` | Mostra struttura cartelle corrente           |
| `/tools`     | Elenca tutti i tool disponibili              |
| `/cd <path>` | Cambia directory di lavoro                   |
| `/clear`     | Pulisce lo schermo                           |
| `quit`       | Esci                                         |

## Tool disponibili

### FileSystem
- `read_file` — legge file con numeri di riga
- `write_file` — crea/sovrascrive file
- `list_directory` — elenca contenuto directory
- `search_in_files` — cerca testo in tutti i file
- `delete_file` — elimina file

### Terminal
- `run_command` — esegue comando PowerShell nel workspace
- `run_dotnet` — esegue comandi dotnet (build/test/run/add)

### Agent
- `get_workspace_tree` — albero completo del progetto
- `set_workspace` — cambia workspace

## Task di esempio

```
Crea una console app C# che stampa i numeri di Fibonacci fino a 100
Leggi il file Program.cs e spiegami cosa fa
Esegui dotnet build e dimmi se ci sono errori
Cerca tutti i file che contengono la parola "TODO"
Crea un file README.md per questo progetto
```

🗺️ Architettura e Flusso dell'Agente (ReAct Loop)
L'agente lavora secondo il paradigma ReAct (Reasoning + Acting). Il ciclo non si interrompe finché l'agente non decide di aver completato il compito.

Plaintext
[ UTENTE ] ──> Inserisce il Task (es. "Crea App")
                  │
                  ▼
[ PROGRAM.CS ] ─> Assembla il System Prompt (Workspace Tree + Regole)
                  │
                  ▼
[ LLM SERVER ] ─> Elabora il contesto (Qwen 2.5)
                  │
         ┌────────┴────────┐
         ▼                 ▼
   (Ha finito?)      (Deve agire?)
   [Testo Libero]    [Tool Call JSON]
         │                 │
         │                 ▼
         │           [TOOL DISPATCHER] ──> Valida gli argomenti JSON
         │                 │
         │                 ▼
         │           [WORKSPACE CONTEXT] ──> Sicurezza: Resolve(path)
         │                 │
         │                 ├──> Se OK: Esegue (FileSystem / Terminal)
         │                 └──> Se Fuori: Lancia UnauthorizedAccessException
         │                 │
         │                 ▼
         │           [RISULTATO TOOL] ──> Iniettato nella cronologia chat
         │                 │
         └◄────────────────┘ (Ricomincia il ciclo: Step X)
         │
         ▼
[ RISPOSTA FINALE ] ──> Stampata a schermo per l'utente
🛠️ Le 3 Componenti Chiave
1. Il Direttore d'Orchestra (Program.cs)
Gestisce lo stato della conversazione e il ciclo di vita del processo.

BuildSystemPrompt(): Viene eseguito a ogni turno. Cattura lo stato aggiornato del workspace tramite workspace.GetTreeSnapshot() e lo inietta nel prompt insieme alle regole comportamentali (es. "Non usare il markdown per i comandi").

Il Ciclo while (Engine):

Invia la cronologia dei messaggi a LlamaClient.

Riceve la risposta.

Bivio: Se la risposta contiene ToolCalls, estrae il JSON, invoca il ToolDispatcher, appende il risultato come messaggio di tipo tool e cicla di nuovo (step++). Se non ci sono tool, interrompe il ciclo e mostra il testo all'utente.

2. Lo Scudo di Sicurezza (WorkspaceContext.cs)
È la sandbox dell'applicazione. Nessun tool tocca il disco senza passare da qui.

Constructor: Normalizza la cartella di lavoro in un percorso assoluto immutabile tramite Path.GetFullPath(root).

Resolve(relativePath): Il guardiano del sistema.

Unisce la root al percorso richiesto dall'agente.

Risolve i vari .. o / trasformando tutto in un percorso assoluto pulito.

Verifica 1: Controlla che inizi con la stringa della Root.

Verifica 2 (Anti-Sibling): Controlla che il carattere immediatamente successivo alla lunghezza della root sia un separatore di directory (\), impedendo l'accesso a cartelle "sorelle" (es. AgentWorkspaceSalame).

3. I Muscoli (FileSystemTools.cs & TerminalTools.cs)
I moduli operativi che espongono le capacità all'agente.

Definitions: Proprietà che restituisce la struttura JSON richiesta dallo standard OpenAI/Ollama per descrivere cosa fa il tool e quali parametri accetta.

Execute(toolName, argumentsJson):

Decodifica il payload JSON inviato dall'LLM tramite JsonElement.

Passa il parametro path a workspace.Resolve() per ottenere il path sicuro.

Esegue l'operazione nativa C# (File.WriteAllText, Directory.GetFiles, ecc.).

Ritorna sempre una stringa (il feedback per l'agente), formattata per facilitare la comprensione dell'LLM (come la numerazione delle righe in read_file).

📝 Ciclo di Vita di un Singolo Tool Call
Quando Qwen decide di scrivere un file, il flusso esatto delle funzioni è il seguente:

LLM restituisce: Name = "write_file", Arguments = "{\"path\": \"src/Main.cs\", \"content\": \"...\"}".

Program.cs intercetta il tool e chiama ToolDispatcher.Dispatch("write_file", arguments).

FileSystemTools.Execute riceve la chiamata e parsa il JSON.

Viene invocato WorkspaceContext.Resolve("src/Main.cs").

Resolve verifica la sicurezza e restituisce C:\DevAgent\...\AgentWorkspace\src\Main.cs.

FileSystemTools.WriteFile crea la cartella src (se manca) e scrive il file.

Il testo "✓ File scritto..." torna a Program.cs, che lo salva nei messaggi della chat.

L'agente legge il successo dell'operazione nel turno successivo.
## Note

- `--chat-template chatml` NON deve essere passato a llama-server (rompe il tool calling)
- Solo `--jinja` per il template corretto con Qwen2.5-Instruct
- Il workspace è sandboxed: path traversal bloccato
- Output terminale troncato a 4000 caratteri per non saturare il context

Per pubblicare exe
dotnet publish -c Release -r win-x64 --self-contained true
+ aggiungere appsettings e readme per utilizzo


Publish riuscito. Nella cartella dist\ ci sono:

File	Dimensione	Scopo
pm-code.exe	~34 MB	Eseguibile unico, runtime .NET incluso
appsettings.json	216 B	Configurazione (URL LLM, workspace, ecc.)
pm-code.pdb	25 KB	Simboli debug (opzionale, puoi eliminarlo)
Per distribuire basta copiare sul PC di destinazione:


pm-code.exe
appsettings.json       ← modifica BaseUrl e Model prima di copiarlo
Il PC di destinazione non ha bisogno di .NET installato. L'unico prerequisito è che llama-server.exe sia raggiungibile all'URL configurato in appsettings.json.

Per la prossima volta basta eseguire lo script:


.\publish.ps1