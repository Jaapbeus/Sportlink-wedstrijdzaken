# Lokaal Debuggen — Sportlink Wedstrijdzaken (v3.5)

> **Waarvoor dit document?** Dagelijks werk: services starten en stoppen, poorten, hot reload,
> handmatig synchroniseren en troubleshooten. De eenmalige opzet staat in
> [DEVELOPER-SETUP.md](DEVELOPER-SETUP.md), het parametercontract van elk script in
> [VERIFICATIE-SCRIPTS.md](VERIFICATIE-SCRIPTS.md).

Gids voor het lokaal draaien en debuggen van de stack: FunctionApp (.NET 9) + BlazorAdmin (.NET 10 Blazor WASM).
Geldt voor zowel **Windows** als **macOS (Apple Silicon)** (#800) — zie
[DEVELOPER-SETUP.md](DEVELOPER-SETUP.md) voor de volledige installatie-instructies per platform.

> **Pad-notatie:** de `.\scripts\dev\...`-commando's hieronder staan in Windows-stijl. Op macOS
> werkt hetzelfde commando met forward slashes, bijvoorbeeld `./scripts/dev/Start-Debug.ps1` in
> plaats van `.\scripts\dev\Start-Debug.ps1` — een backslash is daar geen pad-scheidingsteken.

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

## Overzicht van de stack

```
http://localhost:5242          BlazorAdmin (Blazor WASM, dotnet watch, hot reload)
http://localhost:7094          FunctionApp (Azure Functions isolated .NET 9, func start)
localhost:10000–10002          Azurite (Azure Storage Emulator)
localhost:5432/sportlink       Postgres (Docker — `docker compose up -d`; standaardtier en de
                                tier die in productie draait sinds #976)
  — óf —
localhost:1433/SportlinkSqlDb  SQL Server (Docker — `docker compose --profile sqlserver up -d sqlserver`)
```
Zie DEVELOPER-SETUP.md §4 voor beide paden — kies er één. Beide tiers zijn gelijkwaardig en
volledig ondersteund (#1266); Postgres is de standaard. Er bestaat géén profile `postgres`, en een
kaal `docker compose down` stopt de SQL Server-service niet — gebruik daarvoor
`docker compose --profile sqlserver down`.

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
# Start Azurite + FunctionApp + BlazorAdmin elk in een apart PowerShell-venster.
# Standaardtier is Postgres; voor de andere tier: -Tier SqlServer
```

**Optie: met SWA CLI voor auth-flow testen:**

```powershell
.\scripts\dev\Start-Debug.ps1 -Swa
# Admin GUI met auth-emulatie: http://localhost:4280
```

Het volledige parametercontract (`-Tier`, `-Swa`, `-NoWatch`, `-Tail`, `-Clean`) staat in
[VERIFICATIE-SCRIPTS.md](VERIFICATIE-SCRIPTS.md).

### Handmatig (als Start-Debug.ps1 niet beschikbaar is)

**Windows** — elke service in een eigen venster:

```powershell
# 1. Azurite
$azuriteDir = Join-Path ([System.IO.Path]::GetTempPath()) 'azurite'
if (-not (Test-Path $azuriteDir)) { New-Item -ItemType Directory -Path $azuriteDir | Out-Null }
Start-Process powershell -ArgumentList "-NoExit -Command azurite --location '$azuriteDir'"
Start-Sleep -Seconds 3

# 2. FunctionApp — Postgres-tier (standaard); op de SQL Server-tier: Set-Location FunctionApp
Start-Process powershell -ArgumentList "-NoExit -Command Set-Location FunctionApp.Postgres; func start --port 7094"

# 3. BlazorAdmin met hot reload
Start-Process powershell -ArgumentList "-NoExit -Command Set-Location BlazorAdmin; dotnet watch run --launch-profile http"
```

**macOS** — `Start-Process` kan hier geen apart venster openen (gedocumenteerde beperking); open
drie Terminal-tabbladen en voer in elk tabblad één van deze commando's uit:

```bash
# Tab 1 — Azurite
mkdir -p /tmp/azurite-sportlink && azurite --location /tmp/azurite-sportlink
```
```bash
# Tab 2 — FunctionApp — Postgres-tier (standaard)
cd FunctionApp.Postgres && func start --port 7094
# SQL Server-tier: cd FunctionApp && func start --port 7094
```
```bash
# Tab 3 — BlazorAdmin met hot reload
cd BlazorAdmin && dotnet watch run --launch-profile http
```

### Services stoppen

```powershell
.\scripts\dev\Stop-Debug.ps1
```

> **Nooit** `Stop-Process -Name "func","dotnet","node"` gebruiken — dat sloopt élk `dotnet`-proces
> op de machine (ook onverwante projecten), en `dotnet watch` start zijn kindproces meteen weer op
> zodra alleen de poort-eigenaar gekilld wordt, waardoor poort 5242 direct weer bezet raakt.
> `Stop-Debug.ps1` stopt hele process-trees en wacht tot de poorten echt vrij zijn.

---

## Blazor fingerprint-veiligheidsregel

> **KRITIEK** — NOOIT `dotnet build BlazorAdmin` aanroepen terwijl de BlazorAdmin dev server draait.

BlazorAdmin genereert content-hash fingerprints bij elke compilatie. Twee compilatiepassen = twee sets fingerprints = 404 op framework-JS = "An unhandled error has occurred" in de browser.

**Veilige werkwijze bij codewijzigingen:**

```powershell
# Stap 1+2: services stoppen + BlazorAdmin cleanen (verwijdert stale fingerprints) — één commando
.\scripts\dev\Stop-Debug.ps1 -Clean

# Stap 3: herstart
.\scripts\dev\Start-Debug.ps1
```

**Alleen voor build-fout-detectie (server moet NIET draaien):**

```powershell
# Postgres-tier (standaard — de tier die in productie draait, #1060)
dotnet build FunctionApp.Postgres/FunctionApp.Postgres.csproj -c Debug

# Alleen als je (ook) aan de SQL Server-tier werkt
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
Write-Host "Versie: $($health.version)"   # verwacht: 3.x.x.x

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

**De route verschilt per tier.** Postgres (de standaardtier) gebruikt `/api/postgres/sync-matches`,
SQL Server gebruikt `/api/sync-matches`.

```powershell
# Incrementele sync (standaard: vorige week t/m seizoenseinde — zelfde bereik als de timer)
Invoke-RestMethod "http://localhost:7094/api/postgres/sync-matches"   # Postgres (standaard)
Invoke-RestMethod "http://localhost:7094/api/sync-matches"            # SQL Server

# Volledig seizoen opnieuw ophalen (zelfde patroon, beide routes)
Invoke-RestMethod "http://localhost:7094/api/postgres/sync-matches?reset=true&season=2026"
Invoke-RestMethod "http://localhost:7094/api/sync-matches?reset=true&season=2026"
```

> De oude route `/api/sync` met parameters `weekOffsetFrom`/`weekOffsetTo` bestaat niet meer — dat
> geeft 404 respectievelijk stille negatie van de parameters. Gevonden bij #662, waar deze sync
> juist werd gebruikt om de lokale omgeving gelijk te trekken met productie.

---

## Lokale omgeving gelijktrekken met productie (#662)

Gedrag dat live wordt gezien is alleen betrouwbaar na te bouwen als schema **én** data gelijk zijn.
Deze vier stappen doen dat; de laatste is de belangrijkste.

```powershell
# 1. Schemadrift herstellen
.\scripts\dev\Test-App.ps1 -Fix          # moet exit 0 geven

# 2. Services starten
.\scripts\dev\Start-Debug.ps1

# 3. Sync in default mode — hetzelfde bereik als de productie-timer
#    Postgres-tier (standaard); op de SQL Server-tier: /api/sync-matches
Invoke-RestMethod "http://localhost:7094/api/postgres/sync-matches" -TimeoutSec 1200
# Antwoord: "Sync completed. WeekOffset range: -1 to <einde seizoen>."  (duurt circa 1,5 minuut)
```

**4. Datapariteit verifiëren.** Draait de sync een tweede keer zonder dat de aantallen veranderen,
dan loopt lokaal gelijk met Sportlink:

**Postgres-tier (standaard)** — lowercase snake_case identifiers, conform de casing-conventie die
`scripts/ci/check-postgres-identifier-casing.sh` bewaakt:

```sql
SELECT 'teams' AS tabel, COUNT(*) AS aantal FROM his.teams
UNION ALL SELECT 'matches',      COUNT(*) FROM his.matches
UNION ALL SELECT 'matchdetails', COUNT(*) FROM his.matchdetails;

-- Moet 0 zijn: een rij zonder clubcode valt buiten elke clubfilter
SELECT COUNT(*) AS zonder_clubcode FROM his.matches WHERE clubcode IS NULL;

-- Datumbereik en laatste sync
SELECT MIN(kaledatum::date) AS van, MAX(kaledatum::date) AS tot FROM his.matches;
SELECT MAX(lastsynctimestamp) AS laatste_sync FROM public.appsettings;
```

**SQL Server-tier:**

```sql
SELECT 'teams'   AS Tabel, COUNT(*) AS Aantal FROM his.teams
UNION ALL SELECT 'matches',      COUNT(*) FROM his.matches
UNION ALL SELECT 'matchdetails', COUNT(*) FROM his.matchdetails;

-- Moet 0 zijn: een rij zonder ClubCode valt buiten elke clubfilter
SELECT COUNT(*) AS ZonderClubCode FROM his.matches WHERE ClubCode IS NULL;

-- Datumbereik en laatste sync
SELECT MIN(CAST(kaledatum AS DATE)) AS Van, MAX(CAST(kaledatum AS DATE)) AS Tot FROM his.matches;
SELECT MAX(LastSyncTimestamp) AS LaatsteSync FROM dbo.AppSettings;
```

> **`kaledatum` is een tekstkolom, geen datum.** De waarde is `2026-08-22 00:00:00.00`, dus
> `WHERE kaledatum = '2026-08-22'` levert nul rijen op. Gebruik altijd `CAST(kaledatum AS DATE)` —
> dat doet de planner-view ook.

**5. Functioneel controleren, niet alleen HTTP 200.** Een Blazor WASM-route geeft altijd 200; dat
zegt niets. Open de Dagplanning op een datum waarvan je uit de database weet dat er wedstrijden zijn
en controleer of ze in de tijdlijn én de tabel staan. Dit is de les uit #635: alle SQL-checks stonden
groen terwijl de planner nul wedstrijden vond.

Let op bij het vergelijken van aantallen: de Dagplanning toont alleen wedstrijden **op de eigen
accommodatie**. Staan er vijf wedstrijden in de database en drie op het scherm, dan zijn de andere
twee uitwedstrijden — dat is correct gedrag, geen ontbrekende data.

## API-endpoints

Alle admin-endpoints vereisen Entra ID auth in productie. Lokaal (zonder `WEBSITE_SITE_NAME`)
worden ze altijd toegestaan; planner-endpoints vereisen in productie een function key en zijn
lokaal vrij.

**De volledige, actuele endpointlijst staat in [API.md](API.md)** en machine-leesbaar in
`docs/api-standaarden/openapi.yaml`. Hier stond eerder een handmatig bijgehouden kopie; die liep
per definitie achter op de code en is daarom vervangen door deze verwijzing.

De routes die je lokaal het vaakst nodig hebt:

| Endpoint | Bestand | Beschrijving |
|----------|---------|-------------|
| `GET /api/health` | `FunctionApp.Postgres/HealthFunction.cs` (Postgres) · `FunctionApp/Planner/PlannerFunction.cs` (SQL Server) | Versie, status en actieve databasetier |
| `GET /api/postgres/sync-matches` | `FunctionApp.Postgres/Sync/SyncFunction.cs` | Handmatige Sportlink-sync — **Postgres-tier** |
| `GET /api/sync-matches` | `FunctionApp/Function1.cs` | Handmatige Sportlink-sync — **SQL Server-tier** |

---

## Troubleshooting

### FunctionApp start niet (503 / "Function host is not running")

```powershell
dotnet --list-runtimes
# Moet BEIDE bevatten: Microsoft.NETCore.App 9.x.x en Microsoft.AspNetCore.App 9.x.x (#1174)
```

Ontbreekt .NET 9? Installeer allebei de frameworks — de base runtime alleen is niet genoeg:

```powershell
# Windows
winget install Microsoft.DotNet.Runtime.9
winget install Microsoft.DotNet.AspNetCore.9
```
```bash
# macOS
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --channel 9.0 --runtime dotnet
/tmp/dotnet-install.sh --channel 9.0 --runtime aspnetcore
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

Identiek op Windows en macOS — de lokale database draait in beide gevallen in de Docker-container
uit `docker-compose.yml` (zie DEVELOPER-SETUP.md §4; een rechtstreeks geïnstalleerde SQL
Server-service wordt niet meer ondersteund). Controleer de tier die je daadwerkelijk draait:

**Postgres-tier (standaard):**

```bash
docker compose ps
docker compose logs postgres
```
```powershell
$env:PGPASSWORD = "<lokaal-wachtwoord>"
.\scripts\dev\Test-PostgresConnection.ps1 -User <gebruikersnaam>
```

**SQL Server-tier:**

```bash
docker compose --profile sqlserver ps
docker compose --profile sqlserver logs sqlserver
```
```powershell
# Wachtwoord via SQLCMDPASSWORD, nooit via -P: argumenten zijn op beide platforms
# zichtbaar in de processenlijst.
$env:SQLCMDPASSWORD = '<zelfde waarde als MSSQL_SA_PASSWORD in .env>'
sqlcmd -S localhost,1433 -U sa -d SportlinkSqlDb -C -Q "SELECT @@VERSION"
```

### Azurite niet bereikbaar

```powershell
# Windows
Get-NetTCPConnection -LocalPort 10000 -State Listen -ErrorAction SilentlyContinue
```
```bash
# macOS
lsof -nP -iTCP:10000 -sTCP:LISTEN
```

Leeg/geen output = Azurite draait niet → `Start-Debug.ps1` opnieuw uitvoeren.

---

**Versie:** 3.5 — bijgewerkt 2026-09-19 (Postgres als standaardtier met eigen sync-route; beide tiers gelijkwaardig, #1266)
