# Start-Debug.ps1
# Start alle lokale services voor v2 ontwikkelen en testen.
# Vereisten: .NET 10 SDK, Azure Functions Core Tools v4, Azurite, SQL Server met SportlinkSqlDb.
#
# Gebruik:
#   .\Start-Debug.ps1          → Azurite + FunctionApp + BlazorAdmin
#   .\Start-Debug.ps1 -Swa     → bovenstaande + SWA emulator op http://localhost:4280
#                                 (vereist: npm install -g @azure/static-web-apps-cli)
#
# Poorten:
#   Azurite      :10000 (blob), :10001 (queue), :10002 (table)
#   FunctionApp  :7094  → http://localhost:7094/api/health
#   BlazorAdmin  :5242  → http://localhost:5242  (direct, zonder auth-emulatie)
#   SWA emulator :4280  → http://localhost:4280  (met auth-emulatie en routeregels)

param(
    [switch]$Swa   # Start ook de Azure SWA emulator (vereist swa CLI)
)

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

# --- SWA emulator (optioneel) ---
if ($Swa) {
    $swaCmd = Get-Command swa -ErrorAction SilentlyContinue
    if (-not $swaCmd) {
        Write-Host ""
        Write-Host "SWA CLI niet gevonden. Installeer eenmalig via:" -ForegroundColor Yellow
        Write-Host "  npm install -g @azure/static-web-apps-cli" -ForegroundColor Yellow
        Write-Host "SWA emulator wordt overgeslagen." -ForegroundColor Yellow
    } else {
        Write-Host "SWA emulator starten op http://localhost:4280 ..." -ForegroundColor Cyan
        Start-Process powershell -ArgumentList @(
            '-NoExit', '-Command',
            "Set-Location '$root'; Write-Host 'SWA emulator — poort 4280' -ForegroundColor Cyan; swa start sportlink-admin"
        ) -WindowStyle Normal
    }
}

# --- Samenvatting ---
Write-Host ""
Write-Host "Gestart:" -ForegroundColor Green
Write-Host "  FunctionApp   http://localhost:7094/api/health" -ForegroundColor White
Write-Host "  BlazorAdmin   http://localhost:5242  (direct, LocalDev admin)" -ForegroundColor White
if ($Swa -and (Get-Command swa -ErrorAction SilentlyContinue)) {
    Write-Host "  SWA emulator  http://localhost:4280  (auth-emulatie actief)" -ForegroundColor White
    Write-Host ""
    Write-Host "Gebruik http://localhost:4280 voor de v2 Admin GUI met SWA routeregels." -ForegroundColor DarkGray
    Write-Host "Mock-login: http://localhost:4280/.auth/login/aad  (vul username + rol 'admin' in)" -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "Wacht ~15 seconden tot alle services klaar zijn, daarna: .\Test-App.ps1" -ForegroundColor DarkGray
Write-Host "Sluit de aparte vensters om te stoppen." -ForegroundColor DarkGray
