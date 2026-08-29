# Stop-Debug.ps1
# Stopt alle lokale debug-services die door Start-Debug.ps1 zijn gestart.
#
# Gebruik:
#   .\Stop-Debug.ps1                  → stop FunctionApp + BlazorAdmin + SWA (Azurite blijft draaien)
#   .\Stop-Debug.ps1 -All             → stop bovenstaande én Azurite
#   .\Stop-Debug.ps1 -Clean           → stop services én 'dotnet clean' op BlazorAdmin
#
# -Clean verwijdert de stale content-hash fingerprints van BlazorAdmin. Dat is precies de
# handeling die de fout "An unhandled error has occurred. Reload" verhelpt: twee
# compilatiepassen leveren twee sets fingerprints op, waardoor framework-JS een 404 geeft.
# Voer -Clean daarom altijd uit nadat de services gestopt zijn, nooit ervoor.
#
# Idempotent: draaien zonder actieve services doet niets en geeft exit 0.

param(
    [switch]$All,     # stop ook Azurite
    [switch]$Clean    # dotnet clean op BlazorAdmin na het stoppen
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')

Import-Module (Join-Path $PSScriptRoot 'DevServices.psm1') -Force

Write-Host ''
Write-Host '=== Stop-Debug — lokale services stoppen ===' -ForegroundColor Cyan

$result = Stop-DebugServices -IncludeAzurite:$All

if ($result.StoppedCount -eq 0) {
    Write-Host '  Geen actieve debug-services gevonden.' -ForegroundColor DarkGray
} else {
    Write-Host "  $($result.StoppedCount) proces(sen) gestopt." -ForegroundColor Green
}

if (-not $result.AllPortsFree) {
    Write-Host ''
    Write-Host 'Niet alle poorten zijn vrijgegeven. Controleer handmatig met:' -ForegroundColor Red
    if ($IsWindows) {
        Write-Host '  Get-NetTCPConnection -LocalPort 7094,5242,4280 -State Listen' -ForegroundColor Yellow
    } else {
        Write-Host '  lsof -nP -iTCP:7094 -iTCP:5242 -iTCP:4280 -sTCP:LISTEN' -ForegroundColor Yellow
    }
    exit 1
}

if ($Clean) {
    Write-Host ''
    Write-Host '  BlazorAdmin cleanen (verwijdert stale fingerprints)...' -ForegroundColor Cyan
    $blazorProj = Join-Path $root 'BlazorAdmin/BlazorAdmin.csproj'
    $cleanOutput = dotnet clean $blazorProj 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host '  dotnet clean mislukt:' -ForegroundColor Red
        $cleanOutput | Select-Object -Last 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host '  BlazorAdmin gecleand.' -ForegroundColor Green
}

Write-Host ''
Write-Host 'Alle services gestopt.' -ForegroundColor Green
exit 0
