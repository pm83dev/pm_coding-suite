<#
  Confronta due insiemi di tracci NDJSON (baseline vs post-refactor) scenario per scenario,
  normalizzando via campi volatili (ts, sessionId, turnId, durationMs, e valori di token che
  possono variare di qualche unita' tra run). Report: uguale / diverso (con diff riga per riga).

  Uso:
    .\compare-traces.ps1 -Before <cartella> -After <cartella>
#>
param(
  [Parameter(Mandatory = $true)][string]$Before,
  [Parameter(Mandatory = $true)][string]$After
)

$ErrorActionPreference = "Stop"

# Normalizza una riga NDJSON: rimuove i campi volatili e riordina le chiavi in modo stabile.
function Normalize([string]$line) {
  if ([string]::IsNullOrWhiteSpace($line)) { return $null }
  try {
    $o = $line | ConvertFrom-Json
  } catch { return $line }
  $d = [ordered]@{}
  foreach ($p in ($o.PSObject.Properties | Sort-Object Name)) {
    $name = $p.Name
    if ($name -in @('ts','sessionId','turnId','durationMs','tokensUsed','tokensMax','promptTokens','completionTokens','contextPercent')) {
      continue
    }
    # Normalizza i path: il workdir cambia tra run (baseline vs after), quindi
    # sostituisce il path assoluto con un placeholder stabile.
    if ($name -in @('workspace','endpoint') -and $p.Value -is [string]) {
      $d[$name] = $p.Value -replace '[A-Za-z]:\\[^\\]+\\pm_coding-suite\\.*?\\work-[a-z-]+', 'WORKDIR'
    } else {
      $d[$name] = $p.Value
    }
  }
  return ($d | ConvertTo-Json -Compress -Depth 10)
}

function NormFile([string]$path) {
  if (-not (Test-Path $path)) { return @() }
  return (Get-Content $path | ForEach-Object { Normalize $_ } | Where-Object { $_ })
}

$names = (Get-ChildItem $Before -Directory).Name
$allOk = $true
foreach ($n in $names) {
  $b = NormFile (Join-Path $Before $n\trace.ndjson)
  $a = NormFile (Join-Path $After  $n\trace.ndjson)
  $same = ($b.Count -eq $a.Count)
  if ($same) { for ($i=0; $i -lt $b.Count; $i++) { if ($b[$i] -ne $a[$i]) { $same=$false; break } } }
  if ($same) {
    Write-Host ("[OK]   {0}  ({1} eventi)" -f $n, $b.Count)
  } else {
    $allOk = $false
    Write-Host ("[DIFF] {0}  (before={1}, after={2})" -f $n, $b.Count, $a.Count)
    $max = [Math]::Max($b.Count, $a.Count)
    for ($i=0; $i -lt $max; $i++) {
      $bl = if ($i -lt $b.Count) { $b[$i] } else { '<assente>' }
      $al = if ($i -lt $a.Count) { $a[$i] } else { '<assente>' }
      if ($bl -ne $al) {
        Write-Host ("    [{0}] B: " -f $i) + $bl
        Write-Host ("    [{0}] A: " -f $i) + $al
      }
    }
  }
}
Write-Host ""
if ($allOk) { Write-Host "TUTTI I TRACCE UGUALI (normalizzati)." } else { Write-Host "CI SONO DIFFERENZE — vedi sopra." }
