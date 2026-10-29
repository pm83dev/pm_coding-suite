# pm-code --serve (protocollo v1, ui/protocol)

Modalità server WebSocket di `pm-code`, per collegare la UI web (o qualunque client che parli
`ui/protocol`) senza passare da stdin/stdout. Mutuamente esclusiva con `--stdin-protocol`: un
processo scelta UNA modalità all'avvio. Il REPL (nessun flag) resta identico a prima.

Stato di questa fase: `hello`, `new_session`, `send_message` con un turno alla volta; scritture
e comandi shell sono sempre bloccati (nessuna esecuzione silenziosa). Approvazioni, `cancel`,
multi-sessione reale, `list_sessions`/`models_list`/`set_permission` sono fasi successive.

## 0. Avvio rapido per sviluppare (core + UI insieme)

Per lavorare senza VS Code, da una qualunque cartella di progetto: `scripts/dev-serve.ps1`
avvia core (`--serve`) e UI (`ng serve`) insieme, aggiungendo automaticamente la cartella
corrente ad `AllowedWorkspaceRoots` se non c'è già.

```powershell
C:\DevAgentPM\pm_coding-suite\scripts\dev-serve.ps1 -Workspace C:\progetti\mio-progetto
```

Per usare semplicemente `pm-code` (senza percorso) da qualunque cartella: aggiungi al tuo
`$PROFILE` (una volta sola)

```powershell
Add-Content $PROFILE "`n. `"C:\DevAgentPM\pm_coding-suite\scripts\pm-code-profile.ps1`""
```

poi, in una nuova finestra PowerShell, da dentro la cartella del progetto:

```powershell
cd C:\progetti\mio-progetto
pm-code
```

(una funzione di PowerShell con lo stesso nome di un `.exe` ha la precedenza quando lo chiami
senza estensione: `pm-code.exe`, con estensione, continua a funzionare come sempre). Il token
viene preso da `ServeSettings:Token` in appsettings.json se l'hai fissato lì (consigliato, così
non cambia a ogni avvio); altrimenti ne genera uno nuovo per quella sola sessione e lo stampa a
schermo. Default: usa `C:\pm-code\pm-code.exe` (l'installazione pubblicata); passa `-ExePath`
per puntare invece al build del repo.

## 1. Configurazione (prima del primo avvio)

In `agent/pm-code/appsettings.json`:

```jsonc
"AgentSettings": {
  ...
  "AllowedWorkspaceRoots": ["C:/progetti/mio-progetto"]   // vuoto di default: NESSUN workspace consentito
},
"ServeSettings": {
  "Port": 8765,
  "Token": "",             // vedi sotto: meglio PM_SERVE_TOKEN
  "BindAddress": "127.0.0.1" // default; vedi §2b per esporlo su un'altra interfaccia (es. Tailscale)
}
```

- **`AgentSettings:AllowedWorkspaceRoots`**: elenco di cartelle assolute. `new_session` accetta
  solo un workspace che coincide con una di queste radici o che ci sta sotto. Vuoto per
  default — di proposito: senza configurarlo, `--serve` non accetta NESSUN workspace (fail-safe,
  non fail-open). Usa `/` anche su Windows (JSON non accetta `\` senza escape).
- **Token**: `--serve` richiede sempre un token nel primo messaggio (`hello.payload.token`).
  Priorità: variabile d'ambiente `PM_SERVE_TOKEN` se impostata, altrimenti `ServeSettings:Token`
  in appsettings.json. Se nessuno dei due è impostato, il processo si ferma subito con un
  errore (non parte "senza protezione"). Preferisci la variabile d'ambiente: non finisce in un
  file versionato per errore.
- **`ServeSettings:BindAddress`**: default `127.0.0.1` (solo loopback, nessun'altra macchina può
  raggiungerlo). Vedi §2b per esporlo su un'altra interfaccia.

## 2. Avvio

```powershell
$env:PM_SERVE_TOKEN = "un-token-lungo-a-caso"
cd agent/pm-code
dotnet run -- --serve --port 8765
# oppure, sul build pubblicato: .\pm-code.exe --serve --port 8765
```

`--port` è opzionale (default: `ServeSettings:Port`, poi `8765`). Per default il server ascolta
SOLO su `127.0.0.1` — non è raggiungibile da altre macchine nemmeno se il firewall lo
permettesse, l'indirizzo non viene nemmeno aperto. Log diagnostici (connessioni, errori) vanno
sulla console del processo, mai sul socket: non fanno parte del protocollo.

## 2b. Raggiungerlo da un'altra macchina (es. un altro PC sulla stessa rete Tailscale)

Per default `--serve` è raggiungibile SOLO dalla stessa macchina (§2). Per aprirlo anche a un
altro PC della tua rete (es. un nodo Tailscale), imposta `ServeSettings:BindAddress`
sull'indirizzo di quell'interfaccia (mai `0.0.0.0`/tutte le interfacce, a meno che non sia
davvero quello che vuoi — più alzi il perimetro, più l'unico token di `hello` è tutto ciò che ti
protegge):

```jsonc
"ServeSettings": { "Port": 8765, "Token": "...", "BindAddress": "100.x.y.z" } // il tuo IP Tailscale
```

Su Windows, `HttpListener` richiede un URL ACL per ascoltare su un indirizzo diverso da
`127.0.0.1`/`localhost` — senza, `--serve` si ferma con un errore chiaro invece di un crash e ti
stampa il comando esatto. Da un prompt **come Amministratore**, UNA volta (poi resta valido tra
un avvio e l'altro):

```powershell
netsh http add urlacl url=http://100.x.y.z:8765/ user=TUO-PC\tuo-utente
```

(per togliere l'ACL in futuro: `netsh http delete urlacl url=http://100.x.y.z:8765/`). Serve
anche che il firewall di Windows permetta connessioni in ingresso su quella porta per
`pm-code.exe` (di solito compare un prompt la prima volta che il processo apre la porta, se non
l'hai già consentito tu).

## 3. Flusso del protocollo (questa fase)

```
client → hello {token, clientName, clientVersion}
server → hello_ok {serverVersion, protocol:1}         (oppure error{code:"unauthorized"} e chiusura)

client → new_session {workspace: "<path assoluto consentito>"}
server → session_state {session, messages:[], model, permission:"readonly"}
          (oppure error{code:"bad_request"} se il workspace non è in AllowedWorkspaceRoots)

client → send_message {text} (con sessionId nella busta)
server → turn_started, assistant_delta* / tool_call_started+tool_call_finished*, assistant_done,
          turn_finished{reason}
          (oppure error{code:"bad_request", message:"busy"} se un turno è già in corso)
```

Qualunque altro tipo di messaggio client valido per il protocollo ma non ancora implementato
(`list_sessions`, `open_session`, `cancel`, `approve`, `reject`, `set_model`,
`set_permission`) riceve `error{code:"bad_request"}` esplicito — non viene ignorato in silenzio.

**Scritture e comandi shell**: `write_file`, `edit_file`, `delete_file`, `move_file`,
`create_directory`, `run_command`, `run_dotnet`, `sol_analyze`, `stop_background_job`,
`git_add`, `git_commit`, `git_checkout` non vengono mai eseguiti in questa modalità: il modello
riceve `tool_call_finished{ok:false, summary:"scrittura non disponibile in modalità serve"}`
ancora prima che il tool venga chiamato (nessun file toccato, nessun processo avviato).

## 4. Test automatico end-to-end

Serve un LLM "finto" (`tools/fake-llm`, già nel repo) per non dipendere da un modello reale.

```powershell
# 1) build (una volta)
dotnet build agent/pm-code/pm-code.csproj -c Release
dotnet build tools/fake-llm/fake-llm.csproj -c Release

# 2) finto server LLM, scenario dedicato al test del serve mode
dotnet tools/fake-llm/bin/Release/net8.0/fake-llm.dll `
    tools/fake-llm/scenarios/serve-test.json 9201 --log fake-llm.log

# 3) pm-code --serve, puntato al finto server (appsettings.json di test: LlmSettings:BaseUrl
#    su 127.0.0.1:9201, AgentSettings:AllowedWorkspaceRoots con la cartella di test,
#    ServeSettings:Port 18766 per non entrare in conflitto con un'istanza reale)
$env:PM_SERVE_TOKEN = "testtoken"
cd agent/pm-code/bin/Release/net8.0
.\pm-code.exe --serve --port 18766

# 4) client di prova (Node, usa @pm-ui/protocol + ws già presenti in ui/node_modules)
cd ui
$env:SERVE_URL = "ws://127.0.0.1:18766/"
$env:SERVE_TOKEN = "testtoken"
$env:SERVE_WORKSPACE = "<path assoluto della cartella di test, presente in AllowedWorkspaceRoots>"
node serve-test-client.mjs
```

`ui/serve-test-client.mjs` valida OGNI messaggio ricevuto con `parseServerMessage` di
`@pm-ui/protocol` (un messaggio che non passa la validazione è un fallimento, non solo un
avviso) ed esegue, in ordine:

- **(a)** `hello` con token errato → `error{code:"unauthorized"}`, poi la connessione si chiude.
- **(b)** un turno di solo testo → `turn_started`, `assistant_delta`×N, `assistant_done`,
  `turn_finished{reason:"completed"}`, tutti con lo stesso `turnId`.
- **(c)** un turno con una lettura file → `tool_call_started`/`tool_call_finished` nell'ordine
  giusto, stesso `callId`, `args` un oggetto (non una stringa), `ok:true`.
- **(d)** un secondo `send_message` mandato mentre il primo turno è ancora in corso (stessa
  connessione) → `error{message:"busy"}` quasi immediato, poi il primo turno arriva comunque a
  `turn_finished`.

Uscita 0 se tutti i test passano, 1 altrimenti (stampa quali sono falliti).

`tools/fake-llm/scenarios/serve-test.json` è lo scenario dedicato a questo test (3 risposte:
testo, tool_call `read_file`, testo finale, più una quarta risposta usata dal test (d) per il
turno "lungo" messo in gara con la busy) — non toccare gli scenari `a-normale.json` ecc., usati
dal banco di regressione del REPL (`tools/fake-llm/README.md`).

## 5. File coinvolti

- `agent/pm-code/ServeServer.cs` — trasporto WebSocket (HttpListener su 127.0.0.1, una coda di
  invio per connessione). Non sa nulla del protocollo applicativo.
- `agent/pm-code/ServeSink.cs` — `IAgentSink` che traduce gli eventi del loop in messaggi v1.
- `agent/pm-code/ServeProtocol.cs` — busta v1 (costruzione ed estrazione), riassunto dei
  risultati di tool per `tool_call_finished.summary`.
- `agent/pm-code/Program.cs` — `RunServeModeAsync()`: handshake, allow-list dei workspace,
  `new_session`/`send_message`, il flag "un turno alla volta", il blocco di scritture/comandi
  shell (`serveBlockedTools`).
- `agent/pm-code/appsettings.json` — nuove chiavi `AgentSettings:AllowedWorkspaceRoots` e
  `ServeSettings:{Port,Token}`.
- `ui/serve-test-client.mjs` — client di prova.
- `tools/fake-llm/scenarios/serve-test.json` — scenario per il client di prova.
