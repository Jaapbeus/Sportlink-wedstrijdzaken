# Sportlink Wedstrijdzaken — Developer Setup (v2.7)

Volledige setupgids voor een nieuwe developer die de v2.7-stack lokaal wil draaien.

---

## Snelstart (TL;DR)

```powershell
# 1. Kopieer en configureer local.settings.json
cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json
# Stel SqlConnectionString in op jouw SQL Server

# 2. Configureer Sportlink API-credentials in dbo.AppSettings (zie sectie 4)

# 3. Start alle services
.\scripts\dev\Start-Debug.ps1

# 4. Verificeer (wacht 20s na Start-Debug)
.\scripts\dev\Test-App.ps1
# exit 0 = alles werkt
```

---

## Inhoudsopgave

1. [Vereisten](#1-vereisten)
2. [Software installeren](#2-software-installeren)
3. [Git hooks activeren](#3-git-hooks-activeren)
4. [Database opzetten](#4-database-opzetten)
5. [local.settings.json configureren](#5-localsettingsjson-configureren)
6. [Services starten (Start-Debug.ps1)](#6-services-starten)
7. [Verificatie (Test-App.ps1)](#7-verificatie)
8. [Projectstructuur](#8-projectstructuur)
9. [GitHub Actions — productie-deployment configureren](#9-github-actions-productie-deployment-configureren)
10. [Troubleshooting](#10-troubleshooting)

---

## 1. Vereisten

### Software

- [ ] **.NET 9 Runtime** — vereist voor FunctionApp (Linux Consumption Plan ondersteunt net10.0 niet)
  ```powershell
  winget install Microsoft.DotNet.Runtime.9
  ```
- [ ] **.NET 10 SDK** — vereist voor BlazorAdmin
  ```powershell
  winget install Microsoft.DotNet.SDK.10
  ```
- [ ] **Azure Functions Core Tools v4**
  ```powershell
  npm install -g azure-functions-core-tools@4 --unsafe-perm true
  ```
- [ ] **Node.js** (LTS) — voor Azurite
  ```powershell
  # Download van https://nodejs.org/
  ```
- [ ] **Azurite** (Azure Storage Emulator)
  ```powershell
  npm install -g azurite
  ```
- [ ] **SQL Server** (lokale instantie of bereikbare server) + database `SportlinkSqlDb`

### Toegang en credentials

- [ ] Sportlink API URL en Client ID
- [ ] SQL Server instantienaam en inloggegevens

---

## 2. Software installeren

### Versies controleren

```powershell
dotnet --list-runtimes   # moet 'Microsoft.NETCore.App 9.x.x' bevatten
dotnet --version         # moet 10.x.x zijn (SDK)
func --version           # moet 4.x.x zijn
azurite --version        # moet aanwezig zijn
node --version           # moet LTS zijn
```

### Azure Functions Core Tools installeren (indien ontbreekt)

```powershell
npm install -g azure-functions-core-tools@4 --unsafe-perm true
func --version  # verwacht: 4.x.x
```

---

## 3. Git hooks activeren

De repository bevat pre-commit en pre-push hooks die scannen op gevoelige data (wachtwoorden, API-keys, servernamen) vóórdat een commit of push naar GitHub kan.

### 3.1 Hooks inschakelen

```bash
git config core.hooksPath .githooks
```

### 3.2 Gevoelige patronen configureren

```bash
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

Vul `sensitive-patterns.txt` aan met je eigen waarden: servernaam, SQL-login, Sportlink Client ID, Azure resource namen.

### 3.3 Verificatie

```bash
git commit --allow-empty -m "test hooks"
# Verwacht: "Scanning staged files for sensitive data..." → "No sensitive data detected."
```

---

## 4. Database opzetten

### 4.1 Database aanmaken

Open SSMS, verbind met je SQL Server en voer de volgende scripts uit in deze volgorde:

```sql
-- Stap 1: database, schemas, tabellen aanmaken
-- Voer uit: scripts/db/setup-local-database.sql

-- Stap 2: metadata-tabellen aanmaken
-- Voer uit: Database/dbo/System Stored Procedures/ (sp_CreateTargetTableFromSource + sp_MergeStgToHis)
```

Het Database-project (`.sqlproj`) bevat alle actuele schemandefities. Publiceer via SSMS of SqlPackage:

```powershell
# Deploy via SqlPackage (optioneel)
cd Database
sqlpackage /Action:Publish /SourceFile:SportlinkSqlDb.dacpac /TargetServerName:YOUR_SERVER /TargetDatabaseName:SportlinkSqlDb
```

### 4.2 Sportlink API-credentials instellen

```sql
USE SportlinkSqlDb;
GO

UPDATE [dbo].[AppSettings]
SET
    [SportlinkApiUrl]    = 'https://data.sportlink.com',
    [SportlinkClientId]  = 'YOUR_ACTUAL_CLIENT_ID'   -- ⚠️ vervang door echte waarde
WHERE Id = 1;

SELECT * FROM [dbo].[AppSettings];   -- controleer resultaat
GO
```

### 4.3 Database verificatie

```sql
USE SportlinkSqlDb;
GO

-- Schemas aanwezig?
SELECT name FROM sys.schemas WHERE name IN ('stg','his','mta','dbo','planner','avg','pub');

-- Stored procedures aanwezig?
SELECT name FROM sys.procedures WHERE name IN ('sp_MergeStgToHis','sp_CreateTargetTableFromSource');

-- AppSettings correct?
SELECT [SportlinkApiUrl], [SportlinkClientId] FROM [dbo].[AppSettings];
```

---

## 5. local.settings.json configureren

```powershell
cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json
```

Stel de `SqlConnectionString` in:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "SqlConnectionString": "Server=YOUR_SERVER;Database=SportlinkSqlDb;Integrated Security=True;TrustServerCertificate=True;"
  }
}
```

**SQL-authenticatie (geen Windows Auth):** vervang `Integrated Security=True` door `User Id=[sql-login];Password=[sql-wachtwoord]` in de connection string. Zie Microsoft Docs voor de exacte syntax.

> `local.settings.json` staat in `.gitignore` en wordt nooit gecommit.

---

## 6. Services starten

De aanbevolen manier is via het Start-Debug.ps1-script. Dit start Azurite, FunctionApp en BlazorAdmin elk in een eigen PowerShell-venster.

```powershell
.\scripts\dev\Start-Debug.ps1
```

**Poorten:**

| Service | Poort | Opmerkingen |
|---------|-------|-------------|
| Azurite (blob/queue/table) | 10000–10002 | Azure Storage Emulator |
| FunctionApp | 7094 | `func start` — géén hot reload |
| BlazorAdmin | 5242 | `dotnet watch run` — hot reload actief |

**BlazorAdmin hot reload:** wijzigingen in `.razor`, `.cs` en `.css` worden automatisch doorgevoerd zonder herstart. Voor FunctionApp-wijzigingen moet je de services stoppen en `Start-Debug.ps1` opnieuw uitvoeren.

### Handmatig starten (als Start-Debug.ps1 niet beschikbaar is)

```powershell
# 1. Azurite
$azuriteDir = Join-Path $env:TEMP 'azurite'
if (-not (Test-Path $azuriteDir)) { New-Item -ItemType Directory -Path $azuriteDir | Out-Null }
Start-Process powershell -ArgumentList "-NoExit -Command azurite --location '$azuriteDir'"
Start-Sleep -Seconds 3

# 2. FunctionApp (geen hot reload)
Start-Process powershell -ArgumentList "-NoExit -Command Set-Location FunctionApp; func start --port 7094"

# 3. BlazorAdmin met hot reload
Start-Process powershell -ArgumentList "-NoExit -Command Set-Location BlazorAdmin; dotnet watch run --launch-profile http"
```

### Services stoppen

```powershell
Stop-Process -Name "func","dotnet","node" -ErrorAction SilentlyContinue
```

> **Fingerprint-regel:** roep NOOIT `dotnet build BlazorAdmin` aan terwijl de BlazorAdmin dev server draait.
> Twee compilatiepassen genereren twee sets content-hash fingerprints, wat leidt tot 404's op framework-JS.
> Bouw detectie: `dotnet build BlazorAdmin/BlazorAdmin.csproj` — daarna stoppen, cleanen en herstart via `Start-Debug.ps1`.

---

## 7. Verificatie

Wacht 15–20 seconden na `Start-Debug.ps1`, dan:

```powershell
# Basis verificatie
.\scripts\dev\Test-App.ps1

# Met automatisch herstel van schema-drift
.\scripts\dev\Test-App.ps1 -Fix
```

**Test-App.ps1 controleert:**
- Database-schema (tabellen, kolommen, stored procedures)
- FunctionApp gezondheid (`GET /api/health`)
- Admin API-endpoints
- BlazorAdmin pagina's (Blazor WASM laden zonder foutbanner)

**Geslaagd als:** exit code 0, health-endpoint 200, geen "An unhandled error has occurred" in Blazor.

### Handmatige health-check

```powershell
# FunctionApp
Invoke-RestMethod http://localhost:7094/api/health
# Verwacht: { "status": "ok", "version": "2.x.x" }

# BlazorAdmin
Invoke-WebRequest http://localhost:5242/ -UseBasicParsing
# Verwacht: HTTP 200 + Blazor WASM HTML (index.html)
```

---

## 8. Projectstructuur

```
sportlink-wedstrijdzaken/
├── sportlink-wedstrijdzaken.sln       # Solution (FunctionApp + Database)
├── FunctionApp/
│   ├── fa-dev-sportlink-01.csproj     # .NET 9 Azure Functions isolated worker
│   ├── Function1.cs                   # Timer + HTTP sync triggers
│   ├── Utilities.cs                   # AppSettings, DatabaseConfig, SeasonHelper
│   ├── Admin/                         # 12 Admin-endpoint bestanden (beheer/*)
│   ├── Planner/                       # Planner-endpoints (check-availability, auto-plan, ...)
│   ├── Email/                         # Email-verwerkingspipeline
│   ├── Feedback/                      # Feedback-widget (→ GitHub Issues)
│   ├── local.settings.json            # NIET in git — bevat SqlConnectionString
│   └── local.settings.template.json   # Template, wél in git
├── BlazorAdmin/
│   ├── BlazorAdmin.csproj             # .NET 10 Blazor WebAssembly
│   ├── Pages/                         # Razor-pagina's
│   ├── Shared/                        # Gedeelde componenten (TimeInput, MainLayout, ...)
│   └── wwwroot/
│       ├── appsettings.json           # Localhost-config (in git)
│       ├── appsettings.Production.template.json  # CI-template (in git)
│       └── appsettings.Production.json           # NIET in git — gegenereerd door CI
├── Database/
│   └── SportlinkSqlDb.sqlproj         # SQL Server Database Project
├── scripts/
│   ├── dev/
│   │   ├── Start-Debug.ps1            # Start alle lokale services
│   │   └── Test-App.ps1               # Verificatie na opstarten
│   ├── azure/
│   │   ├── Verify-AzureAuthSetup.ps1  # Diagnose Entra-configuratie (read-only)
│   │   └── Configure-EntraApp.ps1     # Idempotente Entra-configuratie (apply)
│   └── db/
│       └── setup-local-database.sql   # Database-initialisatie
└── docs/                              # Documentatie
```

---

## 9. GitHub Actions — Productie-deployment configureren

De CI/CD-pipeline in `.github/workflows/deploy.yml` deployt automatisch naar Azure bij elke push naar `main`. Hiervoor zijn twee soorten GitHub-configuratie nodig:

- **Secrets** — versleuteld opgeslagen, nooit zichtbaar in logs
- **Variables** — leesbaar in logs, niet bedoeld voor gevoelige waarden

Navigeer naar: **GitHub → jouw fork → Settings → Secrets and variables → Actions**

### 9.1 Secrets instellen

Klik op **New repository secret** voor elk van de volgende:

| Naam | Beschrijving | Waar te vinden |
|------|-------------|----------------|
| `AZURE_CREDENTIALS` | JSON van Azure service principal | Zie stap 9.2 hieronder |
| `AZURE_FUNCTION_KEY` | Host key van de Function App | Azure Portal → Function App → App keys → Host keys → `default` |
| `SQL_CONNECTION_STRING` | Productie SQL-verbindingsstring | Azure Portal → SQL Database → Connection strings → ADO.NET |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | SWA deployment token | Azure Portal → Static Web App → Manage deployment token |

**`AZURE_CREDENTIALS` aanmaken via Azure CLI:**

```bash
az ad sp create-for-rbac \
  --name "sp-[clubcode]-sportlink-deploy" \
  --role contributor \
  --scopes /subscriptions/<subscription-id>/resourceGroups/<resource-group> \
  --sdk-auth
```

Kopieer de volledige JSON-output (inclusief accolades) als waarde voor het secret.

**`SQL_CONNECTION_STRING` formaat:**

```
Server=tcp:[sql-servernaam].database.windows.net,1433;Initial Catalog=[database-naam];
Persist Security Info=False;User ID=[username];Password=[password];
Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
```

### 9.2 Variables instellen

Klik op het tabblad **Variables** → **New repository variable** voor elk van de volgende:

| Naam | Voorbeeld | Beschrijving |
|------|-----------|-------------|
| `AZURE_FUNCTIONAPP_NAME` | `func-[clubcode]-sportlink` | Naam van de Function App (zonder `.azurewebsites.net`) |
| `AZURE_FUNCTIONAPP_URL` | `https://func-[clubcode]-sportlink.azurewebsites.net` | Volledige URL inclusief `https://` — voor Blazor-configuratie |
| `AZURE_SQL_SERVER_NAME` | `[sql-servernaam]` | SQL-servernaam **zonder** `.database.windows.net` |
| `AZURE_SQL_DATABASE_NAME` | `[database-naam]` | Naam van de SQL-database |
| `AZURE_SQL_RESOURCE_GROUP` | `rg-[clubcode]-sportlink` | Azure resource group van de SQL-server |
| `AZURE_STATIC_WEB_APP_HOSTNAME` | `[naam].azurestaticapps.net` | Hostname van de Static Web App **zonder** `https://` |
| `AZURE_AD_TENANT_ID` | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` | Azure Entra tenant ID (GUID) |
| `AZURE_AD_CLIENT_ID` | `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` | App Registration client ID (GUID) |
| `POST_LOGOUT_REDIRECT_URL` | `https://[naam].azurestaticapps.net/` | URL na uitloggen (inclusief trailing slash) |

### 9.3 Welke configuratie is optioneel?

| Jobs | Vereiste configuratie | Gedrag zonder configuratie |
|------|-----------------------|---------------------------|
| `db-check` + `db-migrate` | `AZURE_SQL_SERVER_NAME`, `AZURE_SQL_DATABASE_NAME`, `AZURE_SQL_RESOURCE_GROUP`, `SQL_CONNECTION_STRING` | Jobs worden overgeslagen |
| `blazor-deploy` + SWA smoke test | `AZURE_STATIC_WEB_APPS_API_TOKEN`, `AZURE_STATIC_WEB_APP_HOSTNAME` | Job wordt overgeslagen |
| `build` + `test` | `AZURE_CREDENTIALS`, `AZURE_FUNCTIONAPP_NAME`, `AZURE_FUNCTION_KEY` | Verplicht — mislukken bij ontbreken |

### 9.4 Verificatie na instellen

```powershell
# Haal run-ID op
gh run list --branch main --limit 3

# Controleer alle jobs
gh run view <run-id> --json jobs --jq '.jobs[] | {name: .name, conclusion: .conclusion}'
# Alle jobs moeten "success" of "skipped" zijn
```

---

## 10. Troubleshooting

### FunctionApp start niet — 503 of "Function host is not running"

Controleer de .NET runtime-versie:

```powershell
dotnet --list-runtimes
# Moet bevatten: Microsoft.NETCore.App 9.x.x
# Als .NET 9 ontbreekt:
winget install Microsoft.DotNet.Runtime.9
```

> .NET 10 als runtime voor FunctionApp geeft een 503 op Azure Consumption Plan. Zie CLAUDE.md voor details.

### "Cannot connect to database"

```powershell
# Controleer SQL Server service
Get-Service -Name 'MSSQLSERVER' | Select-Object Status, Name

# Test verbinding
sqlcmd -S YOUR_SERVER -E -Q "USE SportlinkSqlDb; SELECT @@VERSION"
```

1. Controleer `SqlConnectionString` in `local.settings.json`
2. Controleer of `SportlinkSqlDb` bestaat
3. Controleer Windows Authentication / SQL-login

### "401 Unauthorized" op Sportlink API

```sql
SELECT [SportlinkApiUrl], [SportlinkClientId] FROM [dbo].[AppSettings];
```

Controleer of de waarden niet de placeholder `YOUR_ACTUAL_CLIENT_ID` bevatten.

### "Azurite connection failed"

```powershell
# Controleer of Azurite draait op poort 10000
Get-NetTCPConnection -LocalPort 10000 -State Listen -ErrorAction SilentlyContinue

# Start Azurite handmatig
azurite --silent --location $env:TEMP\azurite
```

### Blazor toont "An unhandled error has occurred"

Dit is bijna altijd een fingerprint-mismatch. Oplossing:

```powershell
# Stop alle services
Stop-Process -Name "func","dotnet","node" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# Clean Blazor fingerprints
dotnet clean BlazorAdmin/BlazorAdmin.csproj | Out-Null

# Herstart
.\scripts\dev\Start-Debug.ps1
```

Open daarna `http://localhost:5242` in een **nieuw Incognito-venster** (Ctrl+Shift+F5 werkt soms niet voldoende).

### Stored procedure niet gevonden

```sql
SELECT name FROM sys.procedures WHERE name IN ('sp_MergeStgToHis','sp_CreateTargetTableFromSource');
```

Publiceer het Database-project opnieuw (zie sectie 4.1).

---

**Versie:** 2.7 — bijgewerkt 2026-05-31
