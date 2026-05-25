# Start-Debug.ps1
# Start alle lokale services voor v2 ontwikkelen en testen.
# Vereisten: .NET 10 SDK, Azure Functions Core Tools v4, Azurite, SQL Server met SportlinkSqlDb.
#
# Gebruik:
#   .\Start-Debug.ps1          → Azurite + FunctionApp + BlazorAdmin (met hot reload)
#   .\Start-Debug.ps1 -Swa     → bovenstaande + SWA emulator op http://localhost:4280
#   .\Start-Debug.ps1 -NoWatch → BlazorAdmin zonder hot reload (dotnet run i.p.v. dotnet watch)
#
# Hot reload gedrag:
#   BlazorAdmin  :5242  → HOT RELOAD actief via 'dotnet watch'. Wijzigingen in .razor/.cs/.css
#                          worden automatisch herladen zonder herstart. Browser ververst vanzelf.
#   FunctionApp  :7094  → GEEN hot reload. Azure Functions isolated worker ondersteunt dit niet.
#                          Na een codewijziging in FunctionApp: sluit het venster en herstart
#                          Start-Debug.ps1. Alternatief: dotnet build in het FunctionApp-venster
#                          gevolgd door func start --port 7094.
#
# Poorten:
#   Azurite      :10000 (blob), :10001 (queue), :10002 (table)
#   FunctionApp  :7094  → http://localhost:7094/api/health
#   BlazorAdmin  :5242  → http://localhost:5242  (direct, zonder auth-emulatie)
#   SWA emulator :4280  → http://localhost:4280  (met auth-emulatie en routeregels)

param(
    [switch]$Swa,      # Start ook de Azure SWA emulator (vereist swa CLI)
    [switch]$NoWatch   # Gebruik dotnet run i.p.v. dotnet watch voor BlazorAdmin
)

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")

# --- Stop eventueel draaiende apps (op de bekende poorten) ---
Write-Host "Controleer op draaiende services..." -ForegroundColor DarkGray

foreach ($port in @(7094, 5242, 4280)) {
    $conn = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($conn) {
        $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
        if ($proc) {
            Write-Host "  Stoppen: $($proc.ProcessName) (PID $($proc.Id)) op poort $port" -ForegroundColor Yellow
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

Start-Sleep -Seconds 2

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
Write-Host "  ⚠  FunctionApp heeft GEEN hot reload. Na codewijzigingen: venster sluiten + Start-Debug.ps1 opnieuw." -ForegroundColor DarkYellow
Start-Process powershell -ArgumentList @(
    '-NoExit', '-Command',
    "Set-Location '$root\FunctionApp'; Write-Host 'FunctionApp — poort 7094  (geen hot reload — herstart vereist na codewijziging)' -ForegroundColor Cyan; func start --port 7094"
) -WindowStyle Normal

# --- BlazorAdmin ---
if ($NoWatch) {
    Write-Host "BlazorAdmin starten op http://localhost:5242 (geen hot reload) ..." -ForegroundColor Cyan
    Start-Process powershell -ArgumentList @(
        '-NoExit', '-Command',
        "Set-Location '$root\BlazorAdmin'; Write-Host 'BlazorAdmin — poort 5242  (geen hot reload)' -ForegroundColor Cyan; dotnet run --launch-profile http"
    ) -WindowStyle Normal
} else {
    Write-Host "BlazorAdmin starten op http://localhost:5242 (hot reload actief) ..." -ForegroundColor Cyan
    Start-Process powershell -ArgumentList @(
        '-NoExit', '-Command',
        "Set-Location '$root\BlazorAdmin'; Write-Host 'BlazorAdmin — poort 5242  (hot reload: wijzigingen in .razor/.cs/.css herladen automatisch)' -ForegroundColor Green; dotnet watch run --launch-profile http"
    ) -WindowStyle Normal
}

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
Write-Host "  FunctionApp   http://localhost:7094/api/health  (herstart vereist na C#-wijziging)" -ForegroundColor White
if ($NoWatch) {
    Write-Host "  BlazorAdmin   http://localhost:5242  (geen hot reload)" -ForegroundColor White
} else {
    Write-Host "  BlazorAdmin   http://localhost:5242  (hot reload actief - browser ververst automatisch)" -ForegroundColor Green
}
if ($Swa -and (Get-Command swa -ErrorAction SilentlyContinue)) {
    Write-Host "  SWA emulator  http://localhost:4280  (auth-emulatie actief)" -ForegroundColor White
    Write-Host ""
    Write-Host "Gebruik http://localhost:4280 voor de v2 Admin GUI met SWA routeregels." -ForegroundColor DarkGray
    Write-Host "Mock-login: http://localhost:4280/.auth/login/aad  (vul username + rol 'admin' in)" -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "Wacht ~15 seconden tot alle services klaar zijn." -ForegroundColor DarkGray
Write-Host "Hot reload status: BlazorAdmin=JA (.razor/.cs/.css), FunctionApp=NEE (herstart vereist)" -ForegroundColor DarkGray
Write-Host "Sluit de aparte vensters om te stoppen." -ForegroundColor DarkGray
