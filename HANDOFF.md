# HANDOFF — stato del lavoro (27/10/2026)

Documento per riprendere il lavoro da Claude Code sul pc di sviluppo. Contiene cosa è stato fatto in una sessione precedente (Claude via app desktop, senza shell: nessuna build è stata eseguita) e cosa resta da fare. Eliminalo o aggiornalo a lavoro concluso.

## 1. Da verificare subito: build delle modifiche a `agent/pm-code/Program.cs`

Le modifiche sono state scritte ma **mai compilate** (nella sessione precedente non c'era .NET). Primo passo:

```
cd agent/pm-code
dotnet build
git diff --stat
```

Se non compila, correggi. Se qualcosa non convince: `git restore agent/pm-code/Program.cs`.

### Problema di partenza
Il motore si bloccava con `[LOOP-BLOCK]` per "troppe chiamate uguali"; con lo stesso modello (Qwen locale via llama-server) Cline e OpenCode funzionano, quindi il bug è nel loop di `RunAgentLoopAsync`.

### Cosa è stato cambiato (tutto in `RunAgentLoopAsync`)
1. **Contatore anti-loop** (`repeatCounts`): prima contava `nome|argomenti` per tutto il turno senza guardare il risultato, e si azzerava dopo un edit solo per `run_command`/`run_dotnet`/`sol_analyze`. Un `read_file` dello stesso file dopo una modifica contava quindi come ripetizione (e il testo di riflessione stesso diceva al modello di rileggere il file). Ora: la chiave conserva `(Count, ResultHash)`, scatta solo se la stessa chiamata ha dato **lo stesso risultato** `maxRepeats` volte, e `repeatCounts.Clear()` viene chiamato in `TrackBuildState` dopo ogni `write_file`/`edit_file` riuscito.
2. **Uscita dal blocco**: prima `UI.Error` (solo stderr) + `return true`, quindi in modalità `--stdin-protocol` la chat sembrava bloccata e la history finiva con un turno `user` (il messaggio successivo creava due `user` consecutivi, che i template Qwen3 rifiutano). Ora chiama `EmitFinalNotice(...)` (messaggio assistant visibile in chat) in entrambi i punti (tool call normali e tool call recuperate dal testo).
3. **Riflessioni `[INTERNAL_REFLECTION]`**: ora vengono raccolte in `pendingReflections` e accodate **dopo** tutti i risultati dei tool del messaggio (prima erano inserite in mezzo, spezzando la sequenza `assistant(tool_calls)` → `tool`...). Rimosso il messaggio "operazione eseguita con successo" iniettato dopo ogni tool riuscito.

### Cosa NON è stato cambiato (decisione da prendere)
- `Temperature = 0.0f` (Program.cs, creazione di `ChatRequest`), con `FrequencyPenalty = 0.3` in `Chatmodels.cs` come contromisura alla ripetizione. Con decodifica greedy un contesto identico produce la stessa chiamata. Se dopo le correzioni restano ripetizioni, prova 0.3–0.6 (e valuta `top_p`/`top_k`, oggi non inviati). Non è stato testato nulla.

### Come verificare
- Nei log stderr: quando compare `[LOOP-BLOCK] Tool 'X'`, quale tool è? Ora dovrebbe comparire solo con risultati identici.
- Dopo un blocco, il messaggio successivo dell'utente deve essere accettato senza errori di template.
- Provare un task dove prima si bloccava (modifica di un file seguita da rilettura/verifica).

## 2. Osservazioni sul repo
- `agent/CLAUDE.md` sembra in parte obsoleto rispetto al codice (es. cita `CompressHistory`, `analyze_solution`, `search_symbol`, `MaxSteps` 20 di default; il codice usa `SessionManager`, `sol_analyze`, `sym_search` e `appsettings.json` ha `MaxSteps` 30 e `MaxContextTokens` 81920). Da verificare e allineare.
- `README.md` in root descrive correttamente architettura, protocollo NDJSON e anti-loop (da aggiornare dopo le modifiche del punto 1: la regola ">3 volte stesso tool+argomenti" ora vale solo con risultato identico).

## 3. Altro: progetto SDS (`c:\dev\SSA-SDS_Analyzer`)
Stanotte OpenCode (Qwen Flash Next) ha lavorato sull'app SDS e a fine run ha dichiarato di aver cancellato "per sbaglio" 2 file. Non è stato ancora verificato cosa sia successo davvero:
```
cd c:\dev\SSA-SDS_Analyzer
git status --short
git diff --stat
git log --oneline -5
```
Le righe con `D` sono file cancellati: `git restore <file>`. Controllare anche che non abbia modificato altro non dichiarato. Nella cartella ci sono `login.json` e `token.txt` (probabili credenziali): non leggerli né committarli, e verificare `.gitignore`.

## 4. Progetto futuro: agente di sviluppo autonomo che si auto-migliora
Idea di Paolo: dare al suo LLM locale una sandbox in cui sviluppa da solo i propri tool e si corregge in base ai feedback, con un DB che ricorda progetti e richieste. Decisioni prese finora (nessun codice scritto):
- **Base**: il motore di `pm-code` (loop .NET con tool-calling nativo OpenAI-compatible), **non** l'estensione VS Code. Prima di costruirci sopra bisogna che il loop sia affidabile (punto 1).
- **Interfaccia**: console o piccolo Worker Service; l'estensione `pm-chat` si collega dopo.
- **Sandbox**: container Docker senza accesso alla rete interna, con solo `workspace/` montato; unica uscita verso l'endpoint LLM.
- **Tool caricabili**: file in `tools/` con firma fissa (nome, descrizione, schema, funzione) e un test obbligatorio per essere registrati. Il loop e i test di validazione NON sono modificabili dall'agente.
- **Memoria (SQLite)**: tabelle `projects`, `requests`, `feedback`, `lessons` (regole distillate, consolidate periodicamente), `tools` (versione, esito test). Le lezioni attive e lo stato dei progetti entrano nel system prompt a ogni sessione.
- **Sicurezza** (dopo l'episodio dei file cancellati): tutto sotto git con commit a ogni passo, lavoro su branch/worktree separato, cancellazioni negate o con conferma, limite di iterazioni e di tempo, run notturni solo in sandbox, review del diff il mattino dopo.
- **Task piccoli con checkpoint**, perché i modelli locali si perdono sulle sessioni lunghe; i test automatici come condizione di chiusura.
