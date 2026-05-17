# Start-Debug.ps1
# Start FunctionApp (poort 7094) + BlazorAdmin (poort 5242) voor lokaal debuggen.
# Vereisten: .NET 10 SDK, Azure Functions Core Tools v4, Azurite, SQL Server met SportlinkSqlDb.

$root = $PSScriptRoot

# --- Azurite ---
$azuriteRunning = [bool](Get-NetTCPConnection -LocalPort 10000 -State Listen -ErrorAction SilentlyContinue)
if ($azuriteRunning) {
    Write-Host "Azurite actief (poort 10000)." -ForegroundColor DarkGray
} else {
    Write-Host "Azurite niet gevonden — starten..." -ForegroundColor Yellow
    $azuriteDir = Join-Path $env:TEMP 'azurite'
    if (-not (Test-Path $azuriteDir)) { New-Item -ItemType Directory -Path $azuriteDir | Out-Null }
    Start-Process powershell -ArgumentList @(
        '-NoExit', '-Command',
        "azurite --location '$azuriteDir' --debug '$azuriteDir\debug.log'"
    ) -WindowStyle Minimized
    # Wacht kort zodat Azurite beschikbaar is voor func start
    Start-Sleep -Seconds 2
}

# --- FunctionApp ---
Write-Host "FunctionApp starten op http://localhost:7094 ..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList @(
    '-NoExit', '-Command',
    "Set-Location '$root\FunctionApp'; Write-Host 'FunctionApp — poort 7094' -ForegroundColor Cyan; func start --port 7094"
) -WindowStyle Normal

# --- BlazorAdmin ---
Write-Host "BlazorAdmin starten op http://localhost:5242 ..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList @(
    '-NoExit', '-Command',
    "Set-Location '$root\BlazorAdmin'; Write-Host 'BlazorAdmin — poort 5242' -ForegroundColor Cyan; dotnet run --launch-profile http"
) -WindowStyle Normal

Write-Host ""
Write-Host "Gestart:" -ForegroundColor Green
Write-Host "  FunctionApp   http://localhost:7094/api/sync" -ForegroundColor White
Write-Host "  BlazorAdmin   http://localhost:5242" -ForegroundColor White
Write-Host ""
Write-Host "Sluit de aparte vensters om te stoppen." -ForegroundColor DarkGray
