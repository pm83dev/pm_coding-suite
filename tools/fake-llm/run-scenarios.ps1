<#
  Ri-esegue tutti gli scenari fake-llm (a..k) in modalità REPL tramite il driver e salva i
  risultati (trace NDJSON + stdout/stderr di pm-code) in una cartella di destinazione, per
  poterli confrontare prima/dopo un refactor.

  Uso:
    .\run-scenarios.ps1 -OutDir <cartella> [-Config Release]

  Ogni scenario produce in <OutDir>\<nome>\ :
    trace.ndjson   (il file NDJSON prodotto da pm-code)
    stderr.log     (stderr di pm-code)
    stdout.log     (stdout di pm-code)
    exitcode.txt   (codice di uscita di pm-code)
#>
param(
  [Parameter(Mandatory = $true)][string]$OutDir,
  [string]$Config = "Release"
)

$ErrorActionPreference = "Stop"
$root = "C:\DevAgentPM\pm_coding-suite"
$fakeLlm = "$root\tools\fake-llm\bin\$Config\net8.0\fake-llm.dll"
$driver  = "$root\tools\fake-llm\driver\bin\$Config\net8.0\driver.dll"
$pmcode  = "$root\agent\pm-code\bin\$Config\net8.0\pm-code.dll"
$scenDir = "$root\tools\fake-llm\scenarios"
$scriptDir = "$root\tools\fake-llm\scripts"

if (-not (Test-Path $fakeLlm)) { throw "fake-llm.dll non trovato: $fakeLlm — compila prima." }
if (-not (Test-Path $driver))  { throw "driver.dll non trovato: $driver — compila prima." }
if (-not (Test-Path $pmcode))  { throw "pm-code.dll non trovato: $pmcode — compila prima." }

$names = @('a-normale','b-reset','c-guard-loop','d-ctrlc','e-kill','f-http-error','g-truncated','h-secret','i-length-broken-toolcall','j-truncated-toolcall','k-length-context-full')

# Flag speciali per scenario (vedi tools/fake-llm/README.md)
$flags = @{
  'd-ctrlc' = @('--ctrlc-after-ms','2500')
  'e-kill'  = @('--kill-after-ms','1500')
}

$port = 8900
foreach ($n in $names) {
  $port += 11
  $work = Join-Path $OutDir "work-$n"
  $out  = Join-Path $OutDir $n
  New-Item -ItemType Directory -Force -Path $out | Out-Null
  Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue

  $args = [System.Collections.Generic.List[string]]::new()
  $args.Add($driver)
  $args.Add('--pmcode'); $args.Add($pmcode)
  $args.Add('--workdir'); $args.Add($work)
  $args.Add('--fakellm'); $args.Add($fakeLlm)
  $args.Add('--scenario'); $args.Add("$scenDir\$n.json")
  $args.Add('--script'); $args.Add("$scriptDir\$n.txt")
  $args.Add('--port'); $args.Add("$port")
  $args.Add('--trace'); $args.Add('on')
  if ($flags.ContainsKey($n)) { foreach ($f in $flags[$n]) { $args.Add($f) } }

  Write-Host "=== $n (port $port) ==="
  $proc = Start-Process -FilePath "dotnet" -ArgumentList $args `
           -NoNewWindow -PassThru -RedirectStandardOutput "$out\driver.stdout.log" `
           -RedirectStandardError "$out\driver.stderr.log"
  $proc.WaitForExit(120000)
  if ($proc.HasExited) { Write-Host "  driver exit=$($proc.ExitCode)" } else { $proc.Kill(); Write-Host "  driver TIMEOUT, killed" }

  # Copia i prodotti del driver nella cartella del scenario
  $traces = Get-ChildItem (Join-Path $work "traces") -Filter *.ndjson -ErrorAction SilentlyContinue
  if ($traces) { Copy-Item $traces[0].FullName (Join-Path $out "trace.ndjson") -Force }
  Copy-Item (Join-Path $work "logs\pm-code.stderr.log") (Join-Path $out "stderr.log") -Force -ErrorAction SilentlyContinue
  Copy-Item (Join-Path $work "logs\pm-code.stdout.log") (Join-Path $out "stdout.log") -Force -ErrorAction SilentlyContinue
  Write-Host "  trace events: $(if (Test-Path (Join-Path $out 'trace.ndjson')) { (Get-Content (Join-Path $out 'trace.ndjson')).Count } else { 'NESSUNO' })"
  Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Fatto. Risultati in: $OutDir"
