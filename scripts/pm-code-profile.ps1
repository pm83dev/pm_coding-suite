<#
  Dot-sorgente questo file nel tuo $PROFILE per avere il comando `pm-code` disponibile in ogni
  finestra PowerShell, che avvia core (--serve) + UI web già pronti sulla cartella CORRENTE:

    Add-Content $PROFILE "`n. `"C:\DevAgentPM\pm_coding-suite\scripts\pm-code-profile.ps1`""

  Poi apri una nuova finestra PowerShell (o esegui `. $PROFILE` in quella attuale) e, da
  qualunque cartella di progetto:

    cd C:\progetti\mio-progetto
    pm-code

  Una funzione di PowerShell con lo stesso nome di un .exe ha la precedenza su di esso quando lo
  chiami senza estensione: `pm-code.exe` (con estensione) continua a funzionare come sempre
  (REPL/--stdin-protocol), questa funzione intercetta solo `pm-code` "nudo".
#>
function pm-code {
    param(
        [string]$Workspace = (Get-Location).Path,
        [string]$ExePath = "C:\pm-code\pm-code.exe",
        [int]$Port = 8765,
        [int]$UiPort = 4200,
        # Vuoto = usa ServeSettings:Token da appsettings.json se l'hai fissato lì (vedi
        # dev-serve.ps1), altrimenti ne genera uno nuovo per questa sola sessione.
        [string]$Token = ""
    )
    & "C:\DevAgentPM\pm_coding-suite\scripts\dev-serve.ps1" `
        -Workspace $Workspace -ExePath $ExePath -Port $Port -UiPort $UiPort -Token $Token
}
