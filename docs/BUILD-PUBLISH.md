# Build e pubblicazione (agent-backend + UI Angular)

Miniguida di riferimento: come compilare la UI, copiarla in `wwwroot` e pubblicare il backend.

## Come funziona

La UI Angular (`ui/web`) viene servita dal backend come file statici da
`packages/agent-backend/wwwroot`. La copia è automatica in due punti:

| Livello | Dove | Cosa fa |
|---|---|---|
| Angular | `ui/web/package.json` → script `postbuild` | Dopo `npm run build` esegue `ui/web/scripts/copy-to-wwwroot.mjs`: svuota `wwwroot` (rimuove i vecchi bundle con hash) e copia `dist/web/browser` |
| MSBuild | `packages/agent-backend/agent-backend.csproj` → target `BuildAngularUi` | Prima di ogni build/publish lancia `npm run build` in `ui/web` (e `npm install` solo se manca `node_modules`) |

Quindi un `dotnet publish` del backend ricompila la UI e la include già aggiornata.

## Pubblicare in `C:\pm-code\agent-backend`

1. **Fermare il processo** `agent-backend` in esecuzione (altrimenti i file sono bloccati).
2. **Salvare `appsettings.json` di destinazione** (la publish lo sovrascrive con quello del repo):

   ```powershell
   Copy-Item C:\pm-code\agent-backend\appsettings.json $env:TEMP\appsettings.target.json
   ```

3. **Pubblicare**:

   ```powershell
   cd C:\DevAgentPM\pm_coding-suite\packages\agent-backend
   dotnet publish -c Release -r win-x64 --self-contained -o C:\pm-code\agent-backend
   ```

4. **Ripristinare `appsettings.json`**:

   ```powershell
   Copy-Item $env:TEMP\appsettings.target.json C:\pm-code\agent-backend\appsettings.json -Force
   ```

5. **Riavviare** il processo e fare un hard refresh nel browser (**Ctrl+F5**) per evitare i bundle in cache.

`sessions/` e i log in destinazione non vengono toccati dalla publish.

## Solo UI (senza publish)

```powershell
cd C:\DevAgentPM\pm_coding-suite\ui\web
npm run build        # compila e copia in packages/agent-backend/wwwroot
```

Nota: `ng build` lanciato direttamente **non** esegue la copia; usare `npm run build`.

## Solo backend (saltare la build della UI)

```powershell
dotnet publish -c Release -r win-x64 --self-contained -o C:\pm-code\agent-backend -p:SkipUiBuild=true
```

Utile se la UI non è cambiata o se Node/npm non è disponibile (es. CI senza Node).

## Requisiti

- .NET SDK 8
- Node.js + npm (per la build della UI; non servono con `-p:SkipUiBuild=true`)

## Problemi comuni

- **File bloccati durante la publish**: il processo `agent-backend` è ancora attivo, fermarlo.
- **La UI non cambia dopo il deploy**: cache del browser, Ctrl+F5; verificare che `wwwroot/index.html` punti al `main-*.js` presente nella cartella.
- **Bundle vecchi in `wwwroot`**: non dovrebbe succedere (lo script svuota la cartella); in caso, rilanciare `npm run build`.
- **`appsettings.json` cambiato dopo la publish**: ripetere il passo 4 (ripristino).
