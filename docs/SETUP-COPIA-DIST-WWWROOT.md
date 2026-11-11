# Setup: copia automatica di `dist` Angular in `wwwroot` (.NET)

Guida per replicare in un altro progetto .NET + Angular la copia automatica della build Angular nella `wwwroot` del backend, in modo che `dotnet build/publish` produca sempre backend e UI allineati.

Esempio di struttura (adattare i percorsi):

```
repo/
├─ ui/web/                      # progetto Angular
│  ├─ package.json
│  ├─ angular.json
│  └─ scripts/copy-to-wwwroot.mjs   (nuovo)
└─ packages/agent-backend/      # progetto ASP.NET Core
   ├─ agent-backend.csproj
   └─ wwwroot/                  # destinazione dei file statici
```

## Prerequisiti

- Backend ASP.NET Core che serve i file statici (`app.UseDefaultFiles(); app.UseStaticFiles();` in `Program.cs`, più eventuale `MapFallbackToFile("index.html")` per il routing Angular).
- Node.js + npm e .NET SDK installati sulla macchina che compila.

## Parte 1 — Angular

### 1.1 Creare lo script di copia

File nuovo: `ui/web/scripts/copy-to-wwwroot.mjs`

```js
// Copia la build Angular (dist/<app>/browser) nella wwwroot del backend,
// rimuovendo prima i vecchi bundle con hash (main-*.js, styles-*.css, ecc.).
import { cpSync, existsSync, mkdirSync, readdirSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const src = join(root, 'dist', 'web', 'browser');                         // <-- nome app in dist/
const dest = resolve(root, '..', '..', 'packages', 'agent-backend', 'wwwroot'); // <-- wwwroot del backend

if (!existsSync(src)) {
  console.error(`[copy-to-wwwroot] Build non trovata: ${src}`);
  process.exit(1);
}
mkdirSync(dest, { recursive: true });
for (const f of readdirSync(dest)) rmSync(join(dest, f), { recursive: true, force: true });
cpSync(src, dest, { recursive: true });
console.log(`[copy-to-wwwroot] ${src} -> ${dest}`);
```

Da adattare:
- `src`: la cartella di output della build. Con l'application builder di Angular è `dist/<nome-progetto>/browser` (il nome progetto è la chiave sotto `projects` in `angular.json`).
- `dest`: percorso relativo alla `wwwroot` del backend.

> Attenzione: lo script **svuota** la cartella di destinazione. Non metterci file che non vengono dalla build Angular.

### 1.2 Agganciarlo a `npm run build`

File: `ui/web/package.json`, sezione `scripts`: aggiungere `postbuild`.

```json
"scripts": {
  "build": "ng build",
  "postbuild": "node scripts/copy-to-wwwroot.mjs"
}
```

npm esegue automaticamente `postbuild` dopo `build`. Funziona solo con `npm run build`: un `ng build` lanciato direttamente non fa la copia.

> Se si usa `pnpm` o `yarn` berry, gli hook `pre/post` possono non essere eseguiti: in quel caso concatenare nel comando, es. `"build": "ng build && node scripts/copy-to-wwwroot.mjs"`.

## Parte 2 — .NET (MSBuild)

File: `packages/agent-backend/agent-backend.csproj`. Aggiungere, prima di `</Project>`:

```xml
<!-- Compila la UI Angular prima della build/publish: `npm run build` esegue anche
     lo script postbuild che copia dist in wwwroot. Disattivabile con -p:SkipUiBuild=true. -->
<PropertyGroup>
  <UiWebDir>$(MSBuildProjectDirectory)/../../ui/web/</UiWebDir>
</PropertyGroup>
<Target Name="BuildAngularUi" BeforeTargets="BeforeBuild"
        Condition="'$(SkipUiBuild)' != 'true' And Exists('$(UiWebDir)package.json')">
  <Exec Command="npm install --no-audit --no-fund" WorkingDirectory="$(UiWebDir)"
        Condition="!Exists('$(UiWebDir)node_modules')" />
  <Exec Command="npm run build" WorkingDirectory="$(UiWebDir)" />
</Target>
```

Da adattare:
- `UiWebDir`: percorso dal `.csproj` alla cartella Angular.
- `BeforeTargets="BeforeBuild"`: il target gira prima di ogni build (quindi anche prima di `publish`, che include la build). L'ordine conta: i file in `wwwroot` devono esistere prima che il Web SDK raccolga gli static web assets.
- `Condition ... SkipUiBuild`: permette di saltare la UI (`-p:SkipUiBuild=true`), utile in CI senza Node o quando la UI non è cambiata.
- `Exists('$(UiWebDir)package.json')`: evita errori se la cartella UI non è presente.
- `npm install` parte solo se manca `node_modules`. Per installazioni riproducibili in CI valutare `npm ci`.

## Parte 3 — Opzionale ma consigliato

### Git

Decidere se versionare `wwwroot`:
- **Non versionarla** (consigliato, la build la rigenera): aggiungere a `.gitignore` `packages/agent-backend/wwwroot/*` e, se serve, mantenere un placeholder (`!wwwroot/.gitkeep`).
- **Versionarla**: ogni build cambia gli hash dei bundle e produce diff rumorosi.

Aggiungere sempre `ui/web/dist/` e `ui/web/node_modules/` al `.gitignore`.

### Pubblicazione che sovrascrive la configurazione

`dotnet publish -o <cartella>` sovrascrive `appsettings.json` nella destinazione. Se in produzione ha valori diversi, salvarlo prima e ripristinarlo dopo la publish (vedi `docs/BUILD-PUBLISH.md`), oppure spostare la configurazione locale in `appsettings.Production.json` / variabili d'ambiente.

## Verifica

```powershell
cd packages/agent-backend
dotnet build -c Release
```

Nell'output devono comparire, in ordine, `ng build`, `postbuild` e la riga
`[copy-to-wwwroot] ... -> ...\wwwroot`. Poi controllare che `wwwroot/index.html` punti al `main-*.js` presente nella cartella.

## Riepilogo file toccati

| File | Modifica |
|---|---|
| `ui/web/scripts/copy-to-wwwroot.mjs` | nuovo — copia `dist/.../browser` in `wwwroot` |
| `ui/web/package.json` | aggiunto script `postbuild` |
| `packages/agent-backend/agent-backend.csproj` | aggiunti `UiWebDir` e target `BuildAngularUi` |
| `.gitignore` (opzionale) | ignorare `dist/`, `node_modules/`, eventualmente `wwwroot/*` |
