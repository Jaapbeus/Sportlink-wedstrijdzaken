# Quick Reference — Sportlink Wedstrijdzaken (v3.5)

> **Waarvoor dit document?** Spiekbriefje voor wie het project al draaiend heeft: commando's,
> poorten en queries op één pagina. Eerste opzet staat in [DEVELOPER-SETUP.md](DEVELOPER-SETUP.md),
> dagelijks starten/stoppen/debuggen in [LOKAAL-DEBUGGEN.md](LOKAAL-DEBUGGEN.md), en het volledige
> parametercontract van elk script in [VERIFICATIE-SCRIPTS.md](VERIFICATIE-SCRIPTS.md).

Categorie: **Developers** — snel overzicht van commando's, poorten en veelgebruikte queries.
Geldt voor **Windows** en **macOS (Apple Silicon)** (#800); zie
[DEVELOPER-SETUP.md](DEVELOPER-SETUP.md) voor de volledige uitleg per platform.

> **Pad-notatie:** `.\scripts\dev\...`-commando's staan in Windows-stijl. Op macOS: forward
> slashes, bijvoorbeeld `./scripts/dev/Start-Debug.ps1` in plaats van `.\scripts\dev\Start-Debug.ps1`.

---

## Services starten

```powershell
# Start Azurite + FunctionApp :7094 + BlazorAdmin :5242
.\scripts\dev\Start-Debug.ps1

# Met SWA CLI voor auth-flow testen (poort 4280)
.\scripts\dev\Start-Debug.ps1 -Swa
```

## Services stoppen

```powershell
.\scripts\dev\Stop-Debug.ps1          # stopt process-trees; Azurite blijft draaien
.\scripts\dev\Stop-Debug.ps1 -Clean   # idem + verwijdert stale BlazorAdmin-fingerprints
```

> **Nooit** `Stop-Process -Name "dotnet"` (of `"func","dotnet","node"`) gebruiken — dat sloopt élk
> dotnet-proces op de machine, en `dotnet watch` start zijn kindproces meteen weer op (poort 5242
> raakt dan meteen weer bezet in plaats van vrij). Zie CLAUDE.md Stap 2i.

## Verificatie

```powershell
.\scripts\dev\Test-App.ps1        # basis check
.\scripts\dev\Test-App.ps1 -Fix   # met automatisch schema-herstel
```

---

## Poorten

| Poort | Service | Opmerking |
|-------|---------|-----------|
| 10000–10002 | Azurite | Azure Storage Emulator |
| 7094 | FunctionApp | `func start` — géén hot reload |
| 5242 | BlazorAdmin | `dotnet watch` — hot reload actief |
| 4280 | SWA CLI (optioneel) | Auth-emulatie |

---

## Health & versie

```powershell
Invoke-RestMethod http://localhost:7094/api/health
# { "status": "ok", "version": "3.x.x.x", "timestamp": "...", "tier": "SqlServer" | "Postgres" }
```

---

## Handmatige Sportlink-sync

Route verschilt per tier: Postgres (de standaardtier) gebruikt `/api/postgres/sync-matches`,
SQL Server gebruikt `/api/sync-matches`. De oude route `/api/sync` bestaat niet meer en geeft 404.

```powershell
# Incrementeel (vorige week t/m seizoenseinde — zelfde bereik als de timer)
Invoke-RestMethod http://localhost:7094/api/postgres/sync-matches   # Postgres (standaard)
Invoke-RestMethod http://localhost:7094/api/sync-matches            # SQL Server

# Volledig seizoen opnieuw ophalen (zelfde patroon, beide routes)
Invoke-RestMethod "http://localhost:7094/api/postgres/sync-matches?reset=true&season=2026"   # Postgres
Invoke-RestMethod "http://localhost:7094/api/sync-matches?reset=true&season=2026"            # SQL Server
```

---

## local.settings.json aanmaken

```powershell
# Postgres-tier (standaard)
cp FunctionApp.Postgres/local.settings.template.json FunctionApp.Postgres/local.settings.json
# Stel daarna POSTGRES_CONNECTION_STRING in

# SQL Server-tier
cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json
# Stel daarna SqlConnectionString in
```

---

## Lokale database (Docker — identiek op Windows en macOS)

```bash
docker compose up -d                                 # Postgres starten — de standaardtier
                                                     # (vereist POSTGRES_USER/POSTGRES_PASSWORD in .env)
docker compose --profile sqlserver up -d sqlserver   # SQL Server starten (vereist MSSQL_SA_PASSWORD in .env)
docker compose ps                                    # status/gezondheid
docker compose down                                  # Postgres stoppen, data blijft staan
docker compose --profile sqlserver down              # ook de SQL Server-service stoppen
```

> Er bestaat **geen** profile `postgres`: Postgres is de service zonder profile en start dus met een
> kale `docker compose up -d`. Een service achter een profile wordt door een kaal
> `docker compose down` **niet** gestopt — vandaar de laatste regel.

Zie DEVELOPER-SETUP.md §4 voor beide paden.

## Database-verificatie

**SQL Server:**
```sql
SELECT * FROM [dbo].[AppSettings];
SELECT name FROM sys.schemas WHERE name IN ('stg','his','mta','dbo','planner','avg','pub');
SELECT name FROM sys.procedures WHERE name IN ('sp_MergeStgToHis','sp_CreateTargetTableFromSource');
SELECT [LastSyncTimestamp] FROM [dbo].[AppSettings];
```

**Postgres:**
```sql
SELECT * FROM public.appsettings;
SELECT schema_name FROM information_schema.schemata WHERE schema_name IN ('stg','his','avg','planner','public');
SELECT lastsynctimestamp FROM public.appsettings;
```
Migraties toepassen/verifiëren: `.\scripts\dev\Invoke-PostgresMigrations.ps1`,
`.\scripts\dev\Test-PostgresConnection.ps1`.

---

## Fingerprint-regel (KRITIEK)

NOOIT `dotnet build BlazorAdmin` aanroepen terwijl de dev server draait. Na een build-check altijd:

```powershell
.\scripts\dev\Stop-Debug.ps1 -Clean   # stopt process-trees + verwijdert stale fingerprints
.\scripts\dev\Start-Debug.ps1
```

---

## Veelgebruikte endpoints (lokaal, geen auth vereist)

| Methode | URL | Beschrijving |
|---------|-----|-------------|
| GET | `http://localhost:7094/api/health` | Status, versie en actieve databasetier |
| GET | `http://localhost:7094/api/sync-matches` | Handmatige sync (SQL Server-tier) |
| GET | `http://localhost:7094/api/postgres/sync-matches` | Handmatige sync (Postgres-tier) |
| GET | `http://localhost:7094/api/beheer/settings` | Club-instellingen |
| GET | `http://localhost:7094/api/beheer/teams` | Teamlijst |
| GET | `http://localhost:7094/api/beheer/sync/status` | Sync-status |
| POST | `http://localhost:7094/api/planner/check-availability` | Beschikbaarheidscheck |
| GET | `http://localhost:7094/api/sportlink/match/{wedstrijdcode}` | Sportlink-wedstrijdgegevens (Postgres-tier, epic #986) |

Volledige, actuele endpoint-lijst: [docs/API.md](API.md).

---

## Snel troubleshooting

| Probleem | Oplossing |
|---------|-----------|
| FunctionApp start niet (503) | `dotnet --list-runtimes` — staan **beide** 9.x-frameworks er (`NETCore.App` én `AspNetCore.App`)? Windows: `winget install Microsoft.DotNet.Runtime.9` + `Microsoft.DotNet.AspNetCore.9` · macOS: `/tmp/dotnet-install.sh --channel 9.0 --runtime dotnet` én `--runtime aspnetcore` (zie DEVELOPER-SETUP.md §1) |
| Database verbinding mislukt | Draait de container? `docker compose ps` — anders de verbindingsreeks controleren: Postgres → `POSTGRES_CONNECTION_STRING` in `FunctionApp.Postgres/local.settings.json`; SQL Server → `SqlConnectionString` in `FunctionApp/local.settings.json` (zie DEVELOPER-SETUP.md §4) |
| Sportlink API 401 | `UPDATE [dbo].[AppSettings] SET SportlinkClientId = '...'` |
| Azurite niet actief | Windows: `Get-NetTCPConnection -LocalPort 10000` · macOS: `lsof -nP -iTCP:10000 -sTCP:LISTEN` — start via `Start-Debug.ps1` |
| Blazor "An unhandled error" | Stop services → `dotnet clean BlazorAdmin` → `Start-Debug.ps1` |
| Schema-drift (Test-App.ps1 faalt) | `.\scripts\dev\Test-App.ps1 -Fix` (macOS: `./scripts/dev/Test-App.ps1 -Fix`) |

---

## Entra-configuratie (productie, eenmalig)

Volledig protocol: [ENTRA-AUTH-BEHEER.md](ENTRA-AUTH-BEHEER.md). Beide scripts hebben verplichte
parameters — zonder die waarden blijft het script op een prompt hangen.

```powershell
# Diagnose (read-only) — -ClientId en -ExpectedTenantId zijn verplicht
.\scripts\azure\Verify-AzureAuthSetup.ps1 -ClientId <client-id> -ExpectedTenantId <tenant-id>

# Configuratie toepassen (idempotent) — daarnaast is -AdminUserPrincipalName verplicht
.\scripts\azure\Configure-EntraApp.ps1 -ClientId <client-id> -ExpectedTenantId <tenant-id> -AdminUserPrincipalName <upn> -WhatIf   # preview
.\scripts\azure\Configure-EntraApp.ps1 -ClientId <client-id> -ExpectedTenantId <tenant-id> -AdminUserPrincipalName <upn>           # apply
```

---

## CI/CD bewaken

```powershell
# PR-checks bewaken
gh pr checks <pr-nr> --watch

# Deploy-jobs controleren
gh run list --branch main --limit 3
gh run view <run-id> --json jobs --jq '.jobs[] | {name: .name, conclusion: .conclusion}'
```

---

**Versie:** 3.5 — bijgewerkt 2026-09-19 (productie-databasetier Postgres sinds #976; beide tiers lokaal ondersteund en gelijkwaardig, #1266)
