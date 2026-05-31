# Sportlink Wedstrijdzaken — Setup Checklist (v2.7)

Gebruik deze checklist om je setup-voortgang bij te houden. Vink elk item af zodra het klaar is.

---

## Software vereisten

- [ ] .NET 9 Runtime geïnstalleerd (`dotnet --list-runtimes` toont `Microsoft.NETCore.App 9.x.x`)
  ```powershell
  winget install Microsoft.DotNet.Runtime.9
  ```
- [ ] .NET 10 SDK geïnstalleerd (`dotnet --version` toont `10.x.x`)
- [ ] Azure Functions Core Tools v4 geïnstalleerd (`func --version` toont `4.x.x`)
- [ ] Node.js geïnstalleerd (`node --version`)
- [ ] Azurite geïnstalleerd (`azurite --version`)

---

## Git hooks (gevoelige data bescherming)

- [ ] Hooks geactiveerd: `git config core.hooksPath .githooks`
- [ ] Patroonbestand aangemaakt: `cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt`
- [ ] Eigen waarden toegevoegd aan `sensitive-patterns.txt` (servernaam, client ID, etc.)
- [ ] Hooks werken: `git commit --allow-empty -m "test"` toont scanning-melding

---

## Database

- [ ] SQL Server bereikbaar
- [ ] Database `SportlinkSqlDb` aangemaakt (via `scripts/db/setup-local-database.sql` of Database-project)
- [ ] Schemas aanwezig: `stg`, `his`, `mta`, `dbo`, `planner`
- [ ] Stored procedures aanwezig: `sp_MergeStgToHis`, `sp_CreateTargetTableFromSource`
- [ ] Sportlink API-credentials ingesteld in `dbo.AppSettings`

**Credentials gebruikt:**

```
SportlinkApiUrl:    https://data.sportlink.com
SportlinkClientId:  _________________________________
```

---

## Lokale configuratie

- [ ] `FunctionApp/local.settings.json` aangemaakt vanuit template:
  ```powershell
  cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json
  ```
- [ ] `SqlConnectionString` verwijst naar jouw SQL Server
- [ ] `AzureWebJobsStorage` staat op `UseDevelopmentStorage=true`
- [ ] `FUNCTIONS_WORKER_RUNTIME` staat op `dotnet-isolated`

**Connection string gebruikt:**

```
Server=____________;Database=SportlinkSqlDb;Integrated Security=True;TrustServerCertificate=True;
```

---

## Services starten

- [ ] Services gestart via `.\scripts\dev\Start-Debug.ps1`
  - [ ] Azurite poort 10000 bereikbaar
  - [ ] FunctionApp poort 7094 bereikbaar
  - [ ] BlazorAdmin poort 5242 bereikbaar

---

## Verificatie

- [ ] `.\scripts\dev\Test-App.ps1` geeft exit 0
- [ ] `Invoke-RestMethod http://localhost:7094/api/health` geeft `{ "status": "ok", "version": "2.x.x" }`
- [ ] `http://localhost:5242` laadt zonder "An unhandled error has occurred" banner
- [ ] Versienummer zichtbaar in de BlazorAdmin header

---

## GitHub Actions (productie-deployment) — optioneel

Alleen nodig als je naar Azure wilt deployen.

**Secrets (verplicht voor deploy):**

- [ ] `AZURE_CREDENTIALS` — service principal JSON
- [ ] `AZURE_FUNCTION_KEY` — Function App host key
- [ ] `SQL_CONNECTION_STRING` — productie SQL-verbindingsstring
- [ ] `AZURE_STATIC_WEB_APPS_API_TOKEN` — SWA deployment token

**Variables:**

- [ ] `AZURE_FUNCTIONAPP_NAME`
- [ ] `AZURE_FUNCTIONAPP_URL`
- [ ] `AZURE_SQL_SERVER_NAME`
- [ ] `AZURE_SQL_DATABASE_NAME`
- [ ] `AZURE_SQL_RESOURCE_GROUP`
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
| "Cannot connect to database" | `SqlConnectionString` in `local.settings.json` controleren |
| "401 Unauthorized" Sportlink API | `SELECT * FROM [dbo].[AppSettings]` — credentials correct? |
| "Azurite connection failed" | `Get-NetTCPConnection -LocalPort 10000` — poort actief? |
| Blazor "An unhandled error" | Services stoppen + `dotnet clean BlazorAdmin` + herstart |

Zie [DEVELOPER-SETUP.md](DEVELOPER-SETUP.md) voor gedetailleerde instructies.

---

**Setup afgerond door:** ______________________
**Datum:** ______________________
