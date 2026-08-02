# Publish pm-code-agent come eseguibile singolo autocontenuto per Windows x64
# Uso: .\publish.ps1

$ProjectDir = "$PSScriptRoot\pm-code"
$OutDir     = "$PSScriptRoot\dist"

Write-Host "Build & Publish..." -ForegroundColor Cyan

dotnet publish "$ProjectDir\pm-code.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o "$OutDir"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish fallito." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Distribuibile pronto in: $OutDir" -ForegroundColor Green
Write-Host ""
Write-Host "File da copiare sul PC di destinazione:" -ForegroundColor Yellow
Get-ChildItem "$OutDir" | Where-Object { $_.Extension -in '.exe','.json' } |
    Select-Object Name, @{N='Dimensione';E={ '{0:N1} MB' -f ($_.Length / 1MB) }} |
    Format-Table -AutoSize
