# Lokaal Debuggen — Sportlink Wedstrijdzaken (v2.7)

Gids voor het lokaal draaien en debuggen van de v2.7-stack: FunctionApp (.NET 9) + BlazorAdmin (.NET 10 Blazor WASM).

---

## Snelstart

```powershell
# 1. Start alle services
.\scripts\dev\Start-Debug.ps1

# 2. Wacht ~20 seconden, dan verificeer
.\scripts\dev\Test-App.ps1

# 3. Open in browser
# - BlazorAdmin: http://localhost:5242
# - FunctionApp health: http://localhost:7094/api/health
```

---

## Overzicht v2.7-stack

```
http://localhost:5242          BlazorAdmin (Blazor WASM, dotnet watch, hot reload)
http://localhost:7094          FunctionApp (Azure Functions isolated .NET 9, func start)
localhost:10000–10002          Azurite (Azure Storage Emulator)
YOUR_SERVER/SportlinkSqlDb     SQL Server
```

### Poorten en services

| Poort | Service | Start | Hot reload |
|-------|---------|-------|-----------|
| 10000–10002 | Azurite | `azurite` | n.v.t. |
| 7094 | FunctionApp | `func start --port 7094` | **Nee** — herstart vereist na codewijziging |
| 5242 | BlazorAdmin | `dotnet watch run` | **Ja** — `.razor/.cs/.css` automatisch doorgevoerd |

---

## Services starten

### Via script (aanbevolen)

```powershell
.\scripts\dev\Start-Debug.ps1
# Start Azurite + FunctionApp + BlazorAdmin elk in een apart PowerShell-venster
```

**Optie: met SWA CLI voor auth-flow testen:**

```powershell
.\scripts\dev\Start-Debug.ps1 -Swa
# Admin GUI met auth-emulatie: http://localhost:4280
```

### Handmatig (als Start-Debug.ps1 niet beschikbaar is)

```powershell
# 1. Azurite
$azuriteDir = Join-Path $env:TEMP 'azurite'
if (-not (Test-Path $azuriteDir)) { New-Item -ItemType Directory -Path $azuriteDir | Out-Null }
Start-Process powershell -ArgumentList "-NoExit -Command azurite --location '$azuriteDir'"
Start-Sleep -Seconds 3

# 2. FunctionApp
Start-Process powershell -ArgumentList "-NoExit -Command Set-Location FunctionApp; func start --port 7094"

# 3. BlazorAdmin met hot reload
Start-Process powershell -ArgumentList "-NoExit -Command Set-Location BlazorAdmin; dotnet watch run --launch-profile http"
```

### Services stoppen

```powershell
Stop-Process -Name "func","dotnet","node" -ErrorAction SilentlyContinue
```

---

## Blazor fingerprint-veiligheidsregel

> **KRITIEK** — NOOIT `dotnet build BlazorAdmin` aanroepen terwijl de BlazorAdmin dev server draait.

BlazorAdmin genereert content-hash fingerprints bij elke compilatie. Twee compilatiepassen = twee sets fingerprints = 404 op framework-JS = "An unhandled error has occurred" in de browser.

**Veilige werkwijze bij codewijzigingen:**

```powershell
# Stap 1: services stoppen
Stop-Process -Name "func","dotnet","node" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# Stap 2: BlazorAdmin cleanen (verwijdert stale fingerprints)
dotnet clean BlazorAdmin/BlazorAdmin.csproj | Out-Null

# Stap 3: herstart
.\scripts\dev\Start-Debug.ps1
```

**Alleen voor build-fout-detectie (server moet NIET draaien):**

```powershell
dotnet build FunctionApp/fa-dev-sportlink-01.csproj -c Debug
dotnet build BlazorAdmin/BlazorAdmin.csproj
```

---

## Verificatie na opstarten

```powershell
# Wacht 15-20 seconden na Start-Debug.ps1
.\scripts\dev\Test-App.ps1

# Met automatisch schema-herstel
.\scripts\dev\Test-App.ps1 -Fix
```

Test-App.ps1 controleert:
- Database-schema (tabellen, kolommen, stored procedures)
- FunctionApp health (`GET /api/health`)
- Admin API-endpoints
- BlazorAdmin WASM-pagina's (Blazor laadt correct zonder foutbanner)

### Handmatige health-check

```powershell
# FunctionApp
$health = Invoke-RestMethod http://localhost:7094/api/health
Write-Host "Versie: $($health.version)"   # verwacht: 2.x.x

# BlazorAdmin
(Invoke-WebRequest http://localhost:5242/ -UseBasicParsing).StatusCode   # verwacht: 200
```

### Blazor fingerprint consistency check

```powershell
$html = (Invoke-WebRequest "http://localhost:5242/" -UseBasicParsing).Content
$importmapMatch = [regex]::Match($html, '<script type="importmap"[^>]*>(.*?)</script>',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($importmapMatch.Success) {
    $dotnetEntry = ($importmapMatch.Groups[1].Value | ConvertFrom-Json).imports."./_framework/dotnet.js" -replace '^\.\/', ''
    $check = Invoke-WebRequest "http://localhost:5242/$dotnetEntry" -UseBasicParsing -ErrorAction SilentlyContinue
    Write-Host "Fingerprint check: $($check.StatusCode)"   # moet 200 zijn
}
```

---

## Handmatige synchronisatie

```powershell
# Incrementele sync (standaard: vorige week t/m seizoenseinde)
Invoke-RestMethod "http://localhost:7094/api/sync"

# Met weekoffset (bijv. week -2 t/m +4)
Invoke-RestMethod "http://localhost:7094/api/sync?weekOffsetFrom=-2&weekOffsetTo=4"
```

---

## Admin API-endpoints overzicht

Alle admin-endpoints vereisen Entra ID auth in productie. Lokaal (zonder `WEBSITE_SITE_NAME`) worden ze altijd toegestaan.

| Endpoint | Bestand | Beschrijving |
|----------|---------|-------------|
| `GET /api/health` | `Function1.cs` | Versie en status |
| `GET /api/sync` | `Function1.cs` | Handmatige Sportlink-sync |
| `GET/PUT /api/beheer/settings` | `AdminSettingsFunction.cs` | Club-instellingen |
| `GET /api/beheer/geocode` | `AdminSettingsFunction.cs` | GPS-coördinaten opzoeken |
| `GET /api/beheer/sync/status` | `AdminSyncFunction.cs` | Sync-status |
| `POST /api/beheer/sync/trigger` | `AdminSyncFunction.cs` | Sync starten (fire-and-forget) |
| `GET /api/beheer/teams` | `AdminTeamsFunction.cs` | Teamlijst |
| `GET/PUT/POST/DELETE /api/beheer/templates` | `AdminTemplatesFunction.cs` | E-mailtemplates |
| `GET /api/beheer/email-log` | `AdminEmailLogFunction.cs` | E-mail verwerkingslog |
| `GET/POST/DELETE /api/beheer/uitgesloten-emails` | `AdminUitgeslotenEmailFunction.cs` | Uitsluitingslijst |
| `GET/POST/PUT/DELETE /api/beheer/velden` | `AdminVeldBeschikbaarheidFunction.cs` | Velden |
| `GET/POST/PUT/DELETE /api/beheer/veldbeschikbaarheid` | `AdminVeldBeschikbaarheidFunction.cs` | Beschikbaarheidsregels |
| `GET/POST/PUT/DELETE /api/beheer/voorkeurstijden` | `AdminVoorkeurTijdenFunction.cs` | Team-voorkeurstijden |
| `GET/POST/PUT/DELETE /api/beheer/teamregels` | `AdminVoorkeurTijdenFunction.cs` | Teamregels |
| `GET /api/beheer/clubs` | `AdminClubsFunction.cs` | Club-lijst (multi-club) |
| `GET/PUT /api/beheer/theme` | `AdminThemeFunction.cs` | Club-thema kleuren |
| `POST /api/beheer/theme/extract` | `AdminThemeFunction.cs` | Kleuren extraheren uit website |
| `GET/POST/PUT/DELETE /api/beheer/speeltijden` | `AdminSpeeltijdenFunction.cs` | Speeltijden per leeftijdscategorie |
| `GET /api/beheer/leermomenten` | `AdminLeermomentenFunction.cs` | Classificatie-leermomenten |
| `GET /api/beheer/leermomenten/stats` | `AdminLeermomentenFunction.cs` | Leermoment-statistieken |
| `PUT /api/beheer/leermomenten/{id}/valideer` | `AdminLeermomentenFunction.cs` | Leermoment valideren |
| `GET/GET /api/beheer/teambegeleiding` | `AdminTeambegeleidingFunction.cs` | Teambegeleiding |
| `POST /api/beheer/teambegeleiding/doorsturen` | `AdminTeambegeleidingFunction.cs` | Email doorsturen |
| `POST /api/test/email` | `EmailTestFunction.cs` | Email dry-run |
| `POST /api/feedback/validate` | `FeedbackFunction.cs` | Feedback valideren |
| `POST /api/feedback/submit` | `FeedbackFunction.cs` | Feedback indienen |

**Planner-endpoints** (function key vereist in productie, lokaal vrij):

| Endpoint | Beschrijving |
|----------|-------------|
| `POST /api/planner/check-availability` | Veldbeschikbaarheid controleren |
| `POST /api/planner/bevestig` | Wedstrijdslot boeken |
| `POST /api/planner/auto-plan` | Automatisch weekplanning genereren |
| `POST /api/planner/auto-plan/toepassen` | Automatisch plan toepassen |
| `POST /api/planner/zoek-wedstrijd` | Bestaande wedstrijd opzoeken |
| `POST /api/planner/herplan-check` | Herplan-alternatieven simuleren |
| `POST /api/planner/herplan-bevestig` | Herplanverzoek registreren |
| `POST /api/planner/optimaliseer` | Planningsoptimalisatie berekenen |
| `POST /api/planner/doordeweeks-beschikbaar` | Doordeweekse beschikbaarheid |
| `GET /api/planner/team-schedule` | Teamschema ophalen |

---

## Troubleshooting

### FunctionApp start niet (503 / "Function host is not running")

```powershell
dotnet --list-runtimes
# Moet bevatten: Microsoft.NETCore.App 9.x.x
# Ontbreekt .NET 9?
winget install Microsoft.DotNet.Runtime.9
```

### Blazor "An unhandled error has occurred"

Fingerprint-mismatch. Volg de [fingerprint-veiligheidsregel](#blazor-fingerprint-veiligheidsregel) hierboven.

### Test-App.ps1 meldt schema-drift

```powershell
.\scripts\dev\Test-App.ps1 -Fix
# -Fix herstelt schema-drift automatisch via migratie-scripts
```

### BlazorAdmin laadt maar toont leeg scherm

Open browser DevTools (F12) → Console-tabblad. Kijk naar rode foutmeldingen.

Mogelijk probleem: MSAL-initialisatie faalt → controleer `appsettings.json` in `BlazorAdmin/wwwroot/`.

### "Cannot connect to database"

```powershell
# SQL Server actief?
Get-Service -Name 'MSSQLSERVER' | Select-Object Status

# Verbinding testen
sqlcmd -S YOUR_SERVER -E -Q "SELECT @@VERSION"
```

### Azurite niet bereikbaar

```powershell
Get-NetTCPConnection -LocalPort 10000 -State Listen -ErrorAction SilentlyContinue
# Leeg = Azurite draait niet → Start-Debug.ps1 opnieuw uitvoeren
```

---

**Versie:** 2.7 — bijgewerkt 2026-05-31
