# Sportlink Wedstrijdzaken — Setup Checklist (v3.2)

Gebruik deze checklist om je setup-voortgang bij te houden. Vink elk item af zodra het klaar is.
Geldt voor **Windows** en **macOS (Apple Silicon)** (#800) — zie
[DEVELOPER-SETUP.md](DEVELOPER-SETUP.md) voor de volledige uitleg per platform.

> **Pad-notatie:** `.\scripts\dev\...`-commando's staan in Windows-stijl. Op macOS: forward
> slashes, bijvoorbeeld `./scripts/dev/Start-Debug.ps1` in plaats van `.\scripts\dev\Start-Debug.ps1`.

---

## Software vereisten

- [ ] PowerShell 7 geïnstalleerd (`pwsh --version` toont `7.x`) — vereist op beide platforms
  ```powershell
  # Windows
  winget install Microsoft.PowerShell
  ```
  ```bash
  # macOS
  brew install powershell
  ```
- [ ] .NET 9 Runtime geïnstalleerd (`dotnet --list-runtimes` toont `Microsoft.NETCore.App 9.x.x`)
  ```powershell
  # Windows
  winget install Microsoft.DotNet.Runtime.9
  ```
  ```bash
  # macOS
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && chmod +x /tmp/dotnet-install.sh && /tmp/dotnet-install.sh --channel 9.0 --runtime dotnet
  ```
- [ ] .NET 10 SDK geïnstalleerd (`dotnet --version` toont `10.x.x`) — macOS: `/tmp/dotnet-install.sh --channel 10.0`
- [ ] Azure Functions Core Tools v4 geïnstalleerd (`func --version` toont `4.x.x`) — macOS: `brew tap azure/functions && brew install azure-functions-core-tools@4`
- [ ] Node.js geïnstalleerd (`node --version`) — macOS: `brew install node`
- [ ] Azurite geïnstalleerd (`azurite --version`) — cross-platform via npm, ongewijzigd
- [ ] Lokale database gestart — kies één tier (identiek op Windows en macOS, Docker is de enige
  ondersteunde manier sinds #800): `docker compose up -d` voor SQL Server, of
  `docker compose --profile postgres up -d postgres` voor Postgres — zie DEVELOPER-SETUP.md §4

---

## Git hooks (gevoelige data bescherming)

- [ ] Hooks geactiveerd: `git config core.hooksPath .githooks`
- [ ] Patroonbestand aangemaakt: `cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt`
- [ ] Eigen waarden toegevoegd aan `sensitive-patterns.txt` (servernaam, client ID, etc.)
- [ ] Hooks werken: `git commit --allow-empty -m "test"` toont scanning-melding

---

## Database

**SQL Server-tier** (rollback-tier, nog volledig ondersteund):
- [ ] SQL Server bereikbaar
- [ ] Database `SportlinkSqlDb` aangemaakt (via `scripts/db/setup-local-database.sql` of Database-project)
- [ ] Schemas aanwezig: `stg`, `his`, `mta`, `dbo`, `planner`
- [ ] Stored procedures aanwezig: `sp_MergeStgToHis`, `sp_CreateTargetTableFromSource`
- [ ] Sportlink API-credentials ingesteld in `dbo.AppSettings`

**Postgres-tier** (productietier sinds 2026-09-04):
- [ ] Postgres bereikbaar (`docker compose --profile postgres up -d postgres`)
- [ ] Migraties toegepast: `.\scripts\dev\Invoke-PostgresMigrations.ps1` (vereist
  `POSTGRES_CONNECTION_STRING` als omgevingsvariabele)
- [ ] Schemas aanwezig: `stg`, `his`, `avg`, `planner`, `public` (i.p.v. `dbo`/`mta`)
- [ ] Sportlink API-credentials ingesteld in `public.appsettings`

**Credentials gebruikt:**

```
SportlinkApiUrl:    https://data.sportlink.com
SportlinkClientId:  _________________________________
```

---

## Lokale configuratie

**SQL Server-tier:**
- [ ] `FunctionApp/local.settings.json` aangemaakt vanuit template:
  ```powershell
  cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json
  ```
- [ ] `SqlConnectionString` verwijst naar jouw SQL Server
- [ ] `AzureWebJobsStorage` staat op `UseDevelopmentStorage=true`
- [ ] `FUNCTIONS_WORKER_RUNTIME` staat op `dotnet-isolated`

**Connection string gebruikt (identiek op Windows en macOS — SQL-login tegen de Docker-container):**

```
Server=localhost,1433;Database=SportlinkSqlDb;User Id=sa;Password=____________;TrustServerCertificate=True;
```

**Postgres-tier:**
- [ ] `FunctionApp.Postgres/local.settings.json` aangemaakt vanuit template:
  ```powershell
  cp FunctionApp.Postgres/local.settings.template.json FunctionApp.Postgres/local.settings.json
  ```
- [ ] `PostgresConnectionString` verwijst naar jouw Postgres-container/instantie

---

## Services starten

- [ ] Services gestart via `.\scripts\dev\Start-Debug.ps1`
  - [ ] Azurite poort 10000 bereikbaar
  - [ ] FunctionApp poort 7094 bereikbaar
  - [ ] BlazorAdmin poort 5242 bereikbaar

---

## Verificatie

- [ ] `.\scripts\dev\Test-App.ps1` geeft exit 0
- [ ] `Invoke-RestMethod http://localhost:7094/api/health` geeft `{ "status": "ok", "version": "3.x.x.x" }`
- [ ] `http://localhost:5242` laadt zonder "An unhandled error has occurred" banner
- [ ] Versienummer zichtbaar in de BlazorAdmin header

---

## GitHub Actions (productie-deployment) — optioneel

Alleen nodig als je naar Azure wilt deployen.

**Secrets (verplicht voor deploy):**

- [ ] `AZURE_CREDENTIALS` — service principal JSON
- [ ] `AZURE_FUNCTION_KEY` — Function App host key
- [ ] `SQL_CONNECTION_STRING` — alleen bij `DatabaseTier=SqlServer`
- [ ] `AZURE_STATIC_WEB_APPS_API_TOKEN` — SWA deployment token

**Variables:**

- [ ] `AZURE_FUNCTIONAPP_NAME`
- [ ] `AZURE_FUNCTIONAPP_URL`
- [ ] `DatabaseTier` — `SqlServer` of `Postgres`
- [ ] `DatabaseTierSwitchConfirmation` — **exact dezelfde waarde als `DatabaseTier`**, anders faalt de deploy met exitcode 3
- [ ] `AZURE_SQL_SERVER_NAME` / `AZURE_SQL_DATABASE_NAME` / `AZURE_SQL_RESOURCE_GROUP` — alleen bij `DatabaseTier=SqlServer`
- [ ] `AZURE_STATIC_WEB_APP_HOSTNAME`
- [ ] `AZURE_AD_TENANT_ID`
- [ ] `AZURE_AD_CLIENT_ID`
- [ ] `POST_LOGOUT_REDIRECT_URL`

**Verificatie na instellen:**

- [ ] Eerste deployment geslaagd: alle CI-jobs `success` of `skipped`
  ```powershell
  gh run view <run-id> --json jobs --jq '.jobs[] | {name: .name, conclusion: .conclusion}'
  ```

---

## Troubleshooting snel-overzicht

| Probleem | Eerste stap |
|---------|-------------|
| FunctionApp start niet (503) | `dotnet --list-runtimes` — .NET 9 aanwezig? |
| "Cannot connect to database" | `SqlConnectionString` in `local.settings.json` controleren; draait de container? `docker compose ps` |
| "401 Unauthorized" Sportlink API | `SELECT * FROM [dbo].[AppSettings]` — credentials correct? |
| "Azurite connection failed" | Windows: `Get-NetTCPConnection -LocalPort 10000` · macOS: `lsof -nP -iTCP:10000 -sTCP:LISTEN` — poort actief? |
| Blazor "An unhandled error" | Services stoppen + `dotnet clean BlazorAdmin` + herstart |

Zie [DEVELOPER-SETUP.md](DEVELOPER-SETUP.md) voor gedetailleerde instructies.

---

**Setup afgerond door:** ______________________
**Datum:** ______________________
