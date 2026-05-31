# Quick Reference — Sportlink Wedstrijdzaken (v2.7)

Categorie: **Developers** — snel overzicht van commando's, poorten en veelgebruikte queries.

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
Stop-Process -Name "func","dotnet","node" -ErrorAction SilentlyContinue
```

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
# { "status": "ok", "version": "2.x.x", "timestamp": "..." }
```

---

## Handmatige Sportlink-sync

```powershell
# Incrementeel (vorige week t/m seizoenseinde)
Invoke-RestMethod http://localhost:7094/api/sync

# Met weekoffset
Invoke-RestMethod "http://localhost:7094/api/sync?weekOffsetFrom=-2&weekOffsetTo=4"
```

---

## local.settings.json aanmaken

```powershell
cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json
# Stel daarna SqlConnectionString in
```

---

## Database-verificatie

```sql
-- AppSettings controleren
SELECT * FROM [dbo].[AppSettings];

-- Schema's aanwezig?
SELECT name FROM sys.schemas WHERE name IN ('stg','his','mta','dbo','planner','avg','pub');

-- Stored procedures aanwezig?
SELECT name FROM sys.procedures WHERE name IN ('sp_MergeStgToHis','sp_CreateTargetTableFromSource');

-- Laatste sync-timestamp
SELECT [LastSyncTimestamp] FROM [dbo].[AppSettings];
```

---

## Fingerprint-regel (KRITIEK)

NOOIT `dotnet build BlazorAdmin` aanroepen terwijl de dev server draait. Na een build-check altijd:

```powershell
Stop-Process -Name "func","dotnet","node" -ErrorAction SilentlyContinue
dotnet clean BlazorAdmin/BlazorAdmin.csproj | Out-Null
.\scripts\dev\Start-Debug.ps1
```

---

## Veelgebruikte endpoints (lokaal, geen auth vereist)

| Methode | URL | Beschrijving |
|---------|-----|-------------|
| GET | `http://localhost:7094/api/health` | Status en versie |
| GET | `http://localhost:7094/api/sync` | Handmatige sync |
| GET | `http://localhost:7094/api/beheer/settings` | Club-instellingen |
| GET | `http://localhost:7094/api/beheer/teams` | Teamlijst |
| GET | `http://localhost:7094/api/beheer/sync/status` | Sync-status |
| POST | `http://localhost:7094/api/planner/check-availability` | Beschikbaarheidscheck |

---

## Snel troubleshooting

| Probleem | Oplossing |
|---------|-----------|
| FunctionApp start niet (503) | `dotnet --list-runtimes` — .NET 9 aanwezig? `winget install Microsoft.DotNet.Runtime.9` |
| Database verbinding mislukt | `SqlConnectionString` in `local.settings.json` controleren |
| Sportlink API 401 | `UPDATE [dbo].[AppSettings] SET SportlinkClientId = '...'` |
| Azurite niet actief | `Get-NetTCPConnection -LocalPort 10000` — start via `Start-Debug.ps1` |
| Blazor "An unhandled error" | Stop services → `dotnet clean BlazorAdmin` → `Start-Debug.ps1` |
| Schema-drift (Test-App.ps1 faalt) | `.\scripts\dev\Test-App.ps1 -Fix` |

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

**Versie:** 2.7 — bijgewerkt 2026-05-31
