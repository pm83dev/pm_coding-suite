<#
.SYNOPSIS
  Avvia in un colpo pm-code --serve (core) e la UI web (ui/web), pronti a lavorare su una
  cartella di progetto, senza VS Code: due finestre PowerShell separate (una per il core, una
  per `ng serve`) più le istruzioni per i campi da incollare in Settings nella UI.

.PARAMETER Workspace
  Cartella del progetto su cui vuoi far lavorare l'agente. Obbligatoria. Viene aggiunta
  automaticamente a AgentSettings:AllowedWorkspaceRoots nell'appsettings.json usato da -ExePath
  (se non già presente) — senza questo, new_session la rifiuterebbe.

.PARAMETER ExePath
  Percorso di pm-code.exe. Default: C:\pm-code\pm-code.exe (l'installazione pubblicata, con le
  impostazioni LLM già tarate). Passa il build del repo
  (agent\pm-code\bin\Release\net8.0\pm-code.exe) se stai sviluppando pm-code stesso.

.PARAMETER Port
  Porta del server WebSocket del core. Default 8765.

.PARAMETER UiPort
  Porta del dev server Angular (ng serve). Default 4200.

.PARAMETER Token
  Token richiesto da --serve. Se omesso ne viene generato uno nuovo ad ogni avvio (stampato a
  schermo — incollalo in Settings nella UI).

.EXAMPLE
  .\scripts\dev-serve.ps1 -Workspace C:\progetti\mio-progetto
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Workspace,

    [string]$ExePath = "C:\pm-code\pm-code.exe",

    [int]$Port = 8765,

    [int]$UiPort = 4200,

    # Vuoto = prendi ServeSettings:Token da appsettings.json (se l'hai fissato lì, vedi sotto);
    # se è vuoto anche lì, ne genera uno nuovo per questa sessione.
    [string]$Token = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath)) {
    throw "pm-code.exe non trovato in '$ExePath'. Compilalo (dotnet build agent\pm-code\pm-code.csproj -c Release) o passa -ExePath."
}
$ExePath = (Resolve-Path $ExePath).Path

$Workspace = [System.IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path $Workspace)) {
    throw "La cartella workspace '$Workspace' non esiste."
}

$appsettingsPath = Join-Path (Split-Path $ExePath -Parent) "appsettings.json"
if (-not (Test-Path $appsettingsPath)) {
    throw "appsettings.json non trovato accanto a '$ExePath'."
}

# ── Allow-list: aggiunge $Workspace ad AgentSettings:AllowedWorkspaceRoots se non c'è già ──
$settings = Get-Content $appsettingsPath -Raw | ConvertFrom-Json
if (-not $settings.AgentSettings) {
    throw "'$appsettingsPath' non ha una sezione AgentSettings: non è un appsettings.json valido per pm-code."
}
$existingRoots = @()
if ($settings.AgentSettings.PSObject.Properties.Name -contains "AllowedWorkspaceRoots") {
    $existingRoots = @($settings.AgentSettings.AllowedWorkspaceRoots)
}
$alreadyAllowed = $existingRoots | Where-Object {
    $Workspace.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or
    $Workspace.StartsWith(($_).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
if (-not $alreadyAllowed) {
    Write-Host "[dev-serve] aggiungo '$Workspace' ad AllowedWorkspaceRoots in $appsettingsPath"
    $newRoots = $existingRoots + $Workspace
    if ($settings.AgentSettings.PSObject.Properties.Name -contains "AllowedWorkspaceRoots") {
        $settings.AgentSettings.AllowedWorkspaceRoots = $newRoots
    } else {
        $settings.AgentSettings | Add-Member -NotePropertyName "AllowedWorkspaceRoots" -NotePropertyValue $newRoots
    }
    ($settings | ConvertTo-Json -Depth 10) | Set-Content $appsettingsPath -Encoding UTF8
} else {
    Write-Host "[dev-serve] '$Workspace' è già consentito in AllowedWorkspaceRoots."
}

# ── Token: -Token esplicito, altrimenti ServeSettings:Token fissato in appsettings.json,
# altrimenti uno nuovo per questa sessione sola ────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($Token)) {
    if ($settings.PSObject.Properties.Name -contains "ServeSettings" -and
        -not [string]::IsNullOrWhiteSpace($settings.ServeSettings.Token)) {
        $Token = $settings.ServeSettings.Token
        Write-Host "[dev-serve] token preso da ServeSettings:Token in appsettings.json"
    } else {
        $Token = [guid]::NewGuid().ToString("N")
        Write-Host "[dev-serve] nessun token fissato in ServeSettings:Token: ne genero uno solo per questa sessione."
        Write-Host "            Per non doverlo ricopiare ogni volta, impostalo in '$appsettingsPath' (ServeSettings:Token)."
    }
}

# ── Avvia il core in una finestra separata ──────────────────────────────────────────────────
# Start-Process non ha un parametro per impostare variabili d'ambiente del figlio: eredita
# quelle del processo corrente, quindi la impostiamo qui PRIMA di avviarlo. PM_SERVE_TOKEN ha
# comunque priorità su ServeSettings:Token nel core (vedi Program.cs/RunServeModeAsync):
# impostarlo allo stesso valore letto da appsettings qui sopra è ridondante ma inoffensivo.
$env:PM_SERVE_TOKEN = $Token
Write-Host "[dev-serve] avvio pm-code --serve --port $Port ($ExePath)"
$coreProcess = Start-Process -FilePath $ExePath `
    -ArgumentList @("--serve", "--port", $Port) `
    -WorkingDirectory (Split-Path $ExePath -Parent) `
    -PassThru

# ── Avvia la UI (ng serve) in una finestra separata ─────────────────────────────────────────
$webDir = Join-Path $PSScriptRoot "..\ui\web"
if (-not (Test-Path (Join-Path $webDir "node_modules"))) {
    Write-Host "[dev-serve] node_modules assente in ui/web: eseguo 'npm install' (una volta)..."
    Push-Location $webDir
    npm install
    Pop-Location
}
Write-Host "[dev-serve] avvio ng serve --port $UiPort"
$uiProcess = Start-Process -FilePath "npx" `
    -ArgumentList @("ng", "serve", "--port", $UiPort) `
    -WorkingDirectory $webDir `
    -PassThru

Start-Sleep -Seconds 2
try { Start-Process "http://localhost:$UiPort/" } catch { }

Write-Host ""
Write-Host "========================================================================"
Write-Host " Core (PID $($coreProcess.Id)):  ws://127.0.0.1:$Port/"
Write-Host " UI   (PID $($uiProcess.Id)):    http://localhost:$UiPort/"
Write-Host ""
Write-Host " Nella UI, in Settings, incolla:"
Write-Host "   serverUrl = ws://127.0.0.1:$Port/"
Write-Host "   token     = $Token"
Write-Host ""
Write-Host " Poi 'Nuova sessione' con workspace = $Workspace"
Write-Host ""
Write-Host " Per chiudere: Stop-Process -Id $($coreProcess.Id); Stop-Process -Id $($uiProcess.Id)"
Write-Host "========================================================================"
