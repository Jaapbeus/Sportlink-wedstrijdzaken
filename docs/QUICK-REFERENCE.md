# Quick Reference — Sportlink Wedstrijdzaken (v3.2)

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

Route verschilt per tier: SQL Server gebruikt `/api/sync-matches`, Postgres gebruikt
`/api/postgres/sync-matches`.

```powershell
# Incrementeel (vorige week t/m seizoenseinde — zelfde bereik als de timer)
Invoke-RestMethod http://localhost:7094/api/sync-matches            # SQL Server
Invoke-RestMethod http://localhost:7094/api/postgres/sync-matches   # Postgres

# Volledig seizoen opnieuw ophalen (zelfde patroon, beide routes)
Invoke-RestMethod "http://localhost:7094/api/sync-matches?reset=true&season=2026"
```

---

## local.settings.json aanmaken

```powershell
# SQL Server-tier
cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json
# Stel daarna SqlConnectionString in

# Postgres-tier
cp FunctionApp.Postgres/local.settings.template.json FunctionApp.Postgres/local.settings.json
# Stel daarna PostgresConnectionString in
```

---

## Lokale database (Docker — identiek op Windows en macOS)

```bash
docker compose up -d                              # SQL Server starten (vereist MSSQL_SA_PASSWORD)
docker compose --profile postgres up -d postgres  # Postgres starten (vereist POSTGRES_USER/PASSWORD/DB)
docker compose ps                                 # status/gezondheid
docker compose down                               # stoppen, data blijft staan
```

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
| FunctionApp start niet (503) | `dotnet --list-runtimes` — .NET 9 aanwezig? Windows: `winget install Microsoft.DotNet.Runtime.9` · macOS: `/tmp/dotnet-install.sh --channel 9.0 --runtime dotnet` (zie DEVELOPER-SETUP.md §1) |
| Database verbinding mislukt | Draait de container? `docker compose ps` — anders `SqlConnectionString` in `local.settings.json` controleren (zie §4.1) |
| Sportlink API 401 | `UPDATE [dbo].[AppSettings] SET SportlinkClientId = '...'` |
| Azurite niet actief | Windows: `Get-NetTCPConnection -LocalPort 10000` · macOS: `lsof -nP -iTCP:10000 -sTCP:LISTEN` — start via `Start-Debug.ps1` |
| Blazor "An unhandled error" | Stop services → `dotnet clean BlazorAdmin` → `Start-Debug.ps1` |
| Schema-drift (Test-App.ps1 faalt) | `.\scripts\dev\Test-App.ps1 -Fix` (macOS: `./scripts/dev/Test-App.ps1 -Fix`) |

---

## Entra-configuratie (productie, eenmalig)

```powershell
# Diagnose (read-only)
.\scripts\azure\Verify-AzureAuthSetup.ps1

# Configuratie toepassen (idempotent)
.\scripts\azure\Configure-EntraApp.ps1 -WhatIf   # preview
.\scripts\azure\Configure-EntraApp.ps1            # apply
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

**Versie:** 3.2 — bijgewerkt 2026-09-07 (productie-databasetier Postgres sinds #976; beide tiers lokaal ondersteund)
