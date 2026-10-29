# pm-mcp

Server MCP .NET (trasporto stdio) **additivo**: espone gli stessi tool di `pm-code`
(`FileSystemTools`, `TerminalTools`, `GitTools`, `AgentTools`, `WebTools`, `TodoTools`,
`ProjectNotesTools`) tramite `ToolDispatcher` — la stessa classe usata da
`agent/pm-code/Program.cs`, riusata come libreria (`ProjectReference`), non duplicata.

Il flusso esistente estensione VS Code ↔ `pm-code.exe` (NDJSON su stdio) **non viene toccato**
da questo progetto: `pm-mcp` è un eseguibile separato, pensato per essere registrato come server
MCP (`.mcp.json` nella root del repo, o `~/.copilot/mcp-config.json`), non per sostituire
`pm-code.exe`.

## Build e avvio

```
cd agent/pm-mcp
dotnet build
dotnet run
```

## Configurazione (sempre via variabili d'ambiente, mai hardcoded)

| Variabile | Default | Descrizione |
|---|---|---|
| `PM_MCP_WORKSPACE` | cartella corrente del processo | Root sandbox per i tool filesystem/terminal/git. |
| `PM_MCP_WRITE_MODE` | `readonly` | `readonly` \| `twostep` \| `direct` — vedi sotto. |
| `PM_CODE_EXE_PATH` | percorso di build Debug locale | Eseguibile `pm-code` usato dal tool opzionale `pm_run_task`. |
| `PM_MCP_BRAVE_API_KEY` | *(vuota)* | API key Brave per `web_search`. Facoltativa. |
| `PM_MCP_ENABLE_RUN_TASK` | `false` | `true` per registrare anche `pm_run_task`. |

### PM_MCP_WRITE_MODE

- **`readonly`** (default): `edit_file`/`write_file`/`delete_file`/`move_file` non scrivono mai;
  restituiscono un errore che spiega come abilitare la scrittura.
- **`twostep`**: le stesse operazioni calcolano cosa cambierebbe (diff, o descrizione per
  spostamento/eliminazione) e restituiscono un **token valido 5 minuti**. Il tool `apply_edit`
  applica esattamente quella proposta, ma solo se il file non è cambiato nel frattempo (hash
  SHA256 confrontato al momento dell'apply); `discard_edit` la scarta.
- **`direct`**: scrittura immediata, comportamento identico a prima — da usare solo su workspace
  di prova, mai su un repo di lavoro reale.

In tutti i casi i guard esistenti di `pm-code` (sandbox `WorkspaceContext`, `RequireRead`, limiti
dimensione, blocklist comandi distruttivi) restano attivi e vengono valutati **prima** del
gating: il gating si aggiunge sopra, non li bypassa. Questi guard vengono anche **ri-applicati al
momento di `apply_edit`**, non solo quando la proposta viene creata: per `delete_file`/`move_file`
perché `apply_edit` richiama di nuovo `dispatcher.Execute` (che risolve di nuovo i path con
`WorkspaceContext.Resolve`); per `edit_file`/`write_file` perché `apply_edit` ri-risolve il path
proposto e ri-verifica il limite di 32000 caratteri prima di scrivere.

### ⚠️ `twostep` non è una conferma umana, è solo un secondo passaggio

**Importante**: in modalità `twostep`, un modello client può chiamare `apply_edit` da solo, in
piena autonomia, subito dopo aver ricevuto il token dalla proposta — pm-mcp non ha modo di sapere
se un umano ha davvero visto il diff. Il "cancello" a due passaggi è una vera barriera di
sicurezza **solo se l'host MCP (es. VS Code/Copilot) intercetta il tool `apply_edit` — marcato con
`destructiveHint: true` — e chiede esplicitamente conferma all'utente umano prima di invocarlo**.
Se l'host non lo fa (o lo fa solo per alcuni tool), `twostep` è poco più di una formalità: il
modello vede il diff nella risposta della proposta e può decidere da solo di applicarlo. Verificare
questo comportamento nell'host scelto è uno dei punti da provare a mano prima di fare affidamento
su `twostep` come controllo di sicurezza reale.

### Finestra TOCTOU tra la proposta e `apply_edit`

Per `edit_file`/`write_file`, `apply_edit` legge e verifica l'hash del file e scrive il nuovo
contenuto sotto un **unico lock esclusivo** (`FileShare.None`) sullo stesso file, per restringere
al minimo la finestra tra "controllo l'hash" e "scrivo": un altro processo non può scrivere lo
stesso file mentre questo handle è aperto. Non è una garanzia assoluta — un editor che sovrascrive
con le proprie semantiche, o un filesystem di rete senza locking affidabile, può comunque
intercalarsi appena prima dell'apertura dell'handle — ma è la mitigazione più efficace possibile
senza un lock applicativo cross-processo più ampio. Per `delete_file`/`move_file` il controllo
dell'hash avviene immediatamente prima di richiamare `dispatcher.Execute`: resta una piccola
finestra residua (il tempo tra il controllo e l'apertura del file da parte di `FileSystemTools`),
non eliminabile senza modificare `pm-code` — è un rischio residuo accettato, non un difetto
nascosto.

## Segreti: PM_MCP_BRAVE_API_KEY fuori dal repo

A differenza di `pm-code` (che legge `BraveApiKey` da `appsettings.json`, committato con valore
vuoto e da riempire solo in locale), `pm-mcp` la legge **solo** da variabile d'ambiente — non
esiste alcun file di configurazione in questo progetto dove possa finire per errore in un commit.

Per impostarla senza toccare il repo:

- **Sessione di shell** (temporaneo): `$env:PM_MCP_BRAVE_API_KEY = "..."` (PowerShell) prima di
  `dotnet run`.
- **Variabile d'ambiente utente/sistema** (persistente): Pannello di controllo → Variabili
  d'ambiente, oppure `setx PM_MCP_BRAVE_API_KEY "..."`.
- **Se il server viene lanciato dall'Agent Host di VS Code** tramite `.mcp.json`/
  `~/.copilot/mcp-config.json`: impostala nel blocco `"env"` di **quel** file (il primo è
  comunque dentro il repo — non scriverci la chiave; usa `~/.copilot/mcp-config.json`, che è
  nella home dell'utente e non viene versionato).

Nessuno dei file d'esempio in questo repo (`.mcp.json`, `copilot-mcp-config.example.json`)
contiene chiavi o percorsi assoluti della macchina di sviluppo: usano comandi relativi
(`dotnet run --project agent/pm-mcp`) o placeholder espliciti da sostituire.

## pm_run_task e PM_MCP_WRITE_MODE

`pm_run_task` avvia `pm-code.exe --stdin-protocol` come processo figlio. Quando il figlio
propone una scrittura (evento `edit_proposal`), il protocollo NDJSON di `pm-code` è **sincrono
per turno**: il figlio blocca fino a 5 minuti aspettando una riga `edit_decision` su stdin, senza
alcuna nozione di "conferma differita". `pm_run_task` quindi risponde sempre **subito**:

- `readonly` → rifiuto immediato (nessuna scrittura).
- `direct` → accetto immediato (scrittura immediata nel figlio, comportamento di oggi).
- `twostep` → rifiuto immediato al figlio (che quindi vede l'edit come respinto e continua il suo
  turno di conseguenza — può ritentare, aggirare l'edit o segnalarlo nella risposta finale), ma
  la proposta viene registrata come token applicabile con `apply_edit`, visibile nella nota
  finale del risultato di `pm_run_task`. È un limite del protocollo del figlio, non di questo
  tool: non esiste oggi un modo per "mettere in pausa" il turno di `pm-code` in attesa di una
  conferma umana differita.

### Raccomandazione d'uso

Usa `pm_run_task` solo in **`readonly`** (il figlio non scrive mai, nessuna ambiguità), oppure
in **`direct`** ma **solo** dentro un worktree git "usa e getta" dedicato — cioè un
`git worktree add ../scratch-pm-run-task <branch-temporaneo>` separato dalla working copy
principale, su cui lanciare `pm-mcp` con `PM_MCP_WORKSPACE` puntato lì, da eliminare
(`git worktree remove`) a fine sessione: così una scrittura diretta indesiderata del figlio non
tocca mai il repo di lavoro reale. **`twostep` non è adatto a `pm_run_task`**: come descritto
sopra, il figlio vede comunque la sua proposta come respinta e il token generato non si inserisce
in un flusso coerente col turno già concluso — usarlo dà la falsa impressione di un controllo a
due passaggi che in pratica non c'è per questo tool.

### PM_MCP_ENABLE_RUN_TASK=true + PM_MCP_WRITE_MODE=twostep: bloccato all'avvio

Questa combinazione fallisce esplicitamente all'avvio di `pm-mcp`, con un messaggio d'errore su
stderr che spiega il motivo (lo stesso descritto sopra: il protocollo sincrono del figlio rende
`twostep` incoerente con `pm_run_task`). È stata una scelta deliberata — più sicuro un avvio
fallito e leggibile che lasciare attiva una combinazione che sembra proteggere le scritture del
figlio ma di fatto non lo fa.

## Annotazioni MCP sui tool

I tool esposti dichiarano le annotazioni standard del protocollo MCP tramite
`McpServerToolCreateOptions` (SDK NuGet `ModelContextProtocol` 2.3.0, **supportate nativamente**:
verificato per reflection sul pacchetto — proprietà `ReadOnly`/`Destructive`/`Idempotent`/
`OpenWorld`, tutte `Nullable<bool>`, mappate rispettivamente su `readOnlyHint`/`destructiveHint`/
`idempotentHint`/`openWorldHint` nel JSON del protocollo):

- `readOnlyHint: true` sui tool senza effetti collaterali (`read_file`, `read_file_range`,
  `glob_files`, `search_in_files`, `sym_search`, `list_directory`, `get_workspace_tree`,
  `dir_info`, `git_status`, `git_diff`, `git_log`, `web_search`, `web_fetch`,
  `get_background_output`, `list_background_jobs`).
- `destructiveHint: true` su `edit_file`, `write_file`, `delete_file`, `move_file`, `apply_edit`,
  `run_command`, `run_dotnet`.

Gli altri tool (`manage_todo`, `project_notes`, `lessons`, `git_add`, `git_commit`,
`git_checkout`, `create_directory`, `set_workspace`, `sol_analyze`, `stop_background_job`, ecc.)
non hanno un'azione uniforme (es. `project_notes` fa sia `add` che `remove` che `list`) e restano
senza hint: dichiarare `destructiveHint`/`readOnlyHint` su di loro richiederebbe distinguere il
comportamento in base agli argomenti, cosa che il protocollo MCP non prevede a livello di
annotazioni statiche del tool.

## Guard di pm-code NON spostabili in pm-mcp

Dipendono dalla sequenza di turni del loop ReAct in `agent/pm-code/Program.cs`, non da una
singola chiamata, quindi restano solo nel core: anti-loop (chiamate ripetute identiche),
`MaxConsecutiveReadOnly`, verifica funzionale obbligatoria (non accettare "ho buildato" come
prova), compressione/rollover della history (`SessionManager`).
