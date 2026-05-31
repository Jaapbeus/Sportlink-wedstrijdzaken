# SETUP — Sportlink Wedstrijdzaken voor jouw vereniging

Deze handleiding beschrijft hoe je een eigen instantie van Sportlink Wedstrijdzaken opzet voor jouw voetbalvereniging. Je hebt geen programmeerervaring nodig voor de basis-setup, maar Azure CLI-kennis is handig voor de Entra ID-configuratie.

## Inhoudsopgave

1. [Vereisten](#1-vereisten)
2. [Azure-resources aanmaken](#2-azure-resources-aanmaken)
3. [Database inrichten](#3-database-inrichten)
4. [Entra ID configureren](#4-entra-id-configureren)
5. [GitHub forken en secrets instellen](#5-github-forken-en-secrets-instellen)
6. [Eerste deployment](#6-eerste-deployment)
7. [Clubconfiguratie invullen via Admin GUI](#7-clubconfiguratie-invullen-via-admin-gui)
8. [Lokale ontwikkelomgeving](#8-lokale-ontwikkelomgeving)
9. [Kosten](#9-kosten)

---

## 1. Vereisten

| Vereiste | Versie / Opmerking |
|---|---|
| Microsoft 365 / Entra ID tenant | Gratis bij Microsoft 365 Business of Azure |
| Azure-abonnement | Free tier volstaat |
| Sportlink `clientId` | Opvragen bij jouw eigen Sportlink-beheerder |
| GitHub-account | Voor de repository-fork en CI/CD |
| Azure CLI | Voor Entra-configuratie (`az login`) |

---

## 2. Azure-resources aanmaken

Maak de volgende resources aan in de Azure Portal (of via Azure CLI). Alle resources in één resource group, bijv. `rg-<clubcode>-sportlink`.

### 2a. Azure Functions (Consumption plan)

```bash
az functionapp create \
  --resource-group rg-<clubcode>-sportlink \
  --consumption-plan-location westeurope \
  --runtime dotnet-isolated \
  --runtime-version 10 \
  --functions-version 4 \
  --name func-<clubcode>-sportlink \
  --storage-account <storage-account-naam>
```

**Applicatie-instellingen toevoegen:**

```bash
az functionapp config appsettings set \
  --name func-<clubcode>-sportlink \
  --resource-group rg-<clubcode>-sportlink \
  --settings \
    "SqlConnectionString=<jouw-azure-sql-connection-string>" \
    "GraphTenantId=<entra-tenant-id>" \
    "GraphClientId=<entra-app-client-id>" \
    "GraphClientSecret=<entra-app-secret>" \
    "GraphMailbox=<coordinator@voorbeeld.nl>" \
    "OpenAiApiKey=<openai-api-key>" \
    "GitHubPat=<github-pat>" \
    "GitHubOwner=<github-username>" \
    "GitHubRepo=Sportlink-wedstrijdzaken" \
    "EmailProcessorEnabled=false" \
    "EmailReviewMode=true"
```

> Stel `EmailProcessorEnabled=true` pas in als je de e-mailverwerking wilt activeren. Begin met `false` tijdens de initiële setup.

### 2b. Azure SQL Database (Free tier)

```bash
# Server aanmaken
az sql server create \
  --name sql-<clubcode>-sportlink \
  --resource-group rg-<clubcode>-sportlink \
  --location westeurope \
  --admin-user sqladmin \
  --admin-password <sterk-wachtwoord>

# Database aanmaken (Free tier = 32GB, voldoende voor een club)
az sql db create \
  --resource-group rg-<clubcode>-sportlink \
  --server sql-<clubcode>-sportlink \
  --name SportlinkSqlDb \
  --tier Free
```

> **Let op:** De Free tier is beschikbaar t/m december 2024 per abonnement. Controleer de actuele beschikbaarheid via de Azure Portal.

### 2c. Azure Static Web Apps (Free tier)

Aanmaken via de Azure Portal:
1. Zoek "Static Web Apps" → Create
2. Naam: `swa-<clubcode>-sportlink`
3. Plan type: **Free**
4. Regio: West Europe
5. Deployment: sla de GitHub-koppeling over (wordt later via CI gedaan)

Na aanmaken: kopieer het **Deployment Token** (Settings → Deployment tokens). Dit is je `AZURE_STATIC_WEB_APPS_API_TOKEN`.

---

## 3. Database inrichten

Het database-schema wordt automatisch aangemaakt via het PostDeployment-script bij de eerste deployment. Je hoeft handmatig alleen de initiële data in te vullen via de Admin GUI (stap 7).

**Lokaal (voor development):**
```powershell
# Verbinding verifiëren
sqlcmd -S JOUW-SERVER -E -Q "SELECT @@VERSION"

# Schema aanmaken via Test-App.ps1
.\Test-App.ps1 -Fix
```

---

## 4. Entra ID configureren

### 4a. App Registration aanmaken

1. Azure Portal → Microsoft Entra ID → App registrations → New registration
2. Naam: `Sportlink Admin GUI`
3. Supported account types: **Single tenant** (alleen jouw organisatie)
4. Redirect URI: `https://swa-<clubcode>-sportlink.azurestaticapps.net/authentication/login-callback`
5. Klik Register — kopieer de **Application (client) ID** en **Directory (tenant) ID**

### 4b. App Registration configureren via script

```powershell
az login  # log in met een admin-account van jouw tenant

.\scripts\Configure-EntraApp.ps1 `
  -ClientId "<application-client-id>" `
  -ExpectedTenantId "<directory-tenant-id>" `
  -AdminUserPrincipalName "admin@voorbeeld.nl"
```

Het script configureert idempotent:
- App Roles `admin` en `user`
- `roles`-claim in ID-token
- Assignment Required (alleen pre-toegewezen gebruikers)
- Wijst de opgegeven admin-user toe aan de `admin`-rol

**Verifiëren:**
```powershell
.\scripts\Verify-AzureAuthSetup.ps1 `
  -ClientId "<application-client-id>" `
  -ExpectedTenantId "<directory-tenant-id>"
```

### 4c. Client secret aanmaken

Azure Portal → App registrations → jouw app → Certificates & secrets → New client secret
- Kopieer de waarde direct — je ziet hem maar één keer
- Dit is je `GraphClientSecret` voor de Function App-instellingen

---

## 5. GitHub forken en secrets instellen

### 5a. Repository forken

1. Ga naar [github.com/Jaapbeus/Sportlink-wedstrijdzaken](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken)
2. Klik **Fork** → maak een fork in jouw eigen GitHub-account

### 5b. GitHub Secrets instellen

In jouw fork: Settings → Secrets and variables → Actions → **Secrets**:

| Secret | Waarde |
|---|---|
| `AZURE_CREDENTIALS` | Service Principal JSON (zie hieronder) |
| `AZURE_FUNCTION_KEY` | Function key van jouw Function App |
| `AZURE_STATIC_WEB_APPS_API_TOKEN` | Deployment token van de SWA |

**Service Principal aanmaken voor `AZURE_CREDENTIALS`:**
```bash
az ad sp create-for-rbac \
  --name "sp-github-sportlink-<clubcode>" \
  --role contributor \
  --scopes /subscriptions/<subscription-id>/resourceGroups/rg-<clubcode>-sportlink \
  --json-auth
```
Kopieer de volledige JSON-output als waarde voor `AZURE_CREDENTIALS`.

### 5c. GitHub Variables instellen

In jouw fork: Settings → Secrets and variables → Actions → **Variables**:

| Variable | Waarde |
|---|---|
| `AZURE_FUNCTIONAPP_NAME` | `func-<clubcode>-sportlink` |
| `AZURE_FUNCTIONAPP_URL` | `https://func-<clubcode>-sportlink.azurewebsites.net` |
| `AZURE_STATIC_WEB_APP_HOSTNAME` | `<swa-hostname>.azurestaticapps.net` |
| `AZURE_AD_TENANT_ID` | Jouw Entra Directory (tenant) ID |
| `AZURE_AD_CLIENT_ID` | Jouw Entra Application (client) ID |
| `POST_LOGOUT_REDIRECT_URL` | URL van de website van jouw club |

---

## 6. Eerste deployment

Push naar de `main` branch van jouw fork. De `deploy.yml` workflow wordt automatisch gestart en deployt:
1. Azure Functions app
2. Blazor Admin GUI naar Static Web Apps

```bash
git push origin main
```

Controleer de voortgang via GitHub → Actions.

**Smoke test na deployment:**
```bash
curl https://func-<clubcode>-sportlink.azurewebsites.net/api/health
# → {"status":"ok","timestamp":"..."}
```

---

## 7. Clubconfiguratie invullen via Admin GUI

Na de eerste deployment:

1. Open de Admin GUI: `https://<swa-hostname>.azurestaticapps.net`
2. Log in met het admin-account dat je in stap 4b hebt geconfigureerd
3. Ga naar **Instellingen** en vul in:
   - **Accommodatie**: de naam van jullie sportpark (exact zoals het in Sportlink staat, bijv. `Sportpark De Voorhoede`)
   - **Accommodatieplaats**: de stad (bijv. `Utrecht`)
   - Klik **GPS-coördinaten ophalen** voor de zonsondergangsberekening
   - **Sportlink Client ID**: jouw Sportlink API client ID
   - **Planner-afzendernaam**, **Coördinator-functie**, **E-mailvoetnoot**

> De `Accommodatie`-instelling is **verplicht**. Zonder deze waarde werkt de planner-module niet.

### Eerste sync uitvoeren

Na het invullen van de instellingen:
- Ga naar **Dashboard** → klik **Sync nu uitvoeren**
- De FunctionApp haalt nu de wedstrijdgegevens op uit Sportlink

---

## 8. Lokale ontwikkelomgeving

### Vereisten

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure Functions Core Tools v4](https://github.com/Azure/azure-functions-core-tools#installing)
- SQL Server (lokaal of via Docker)
- [Azurite](https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite) (Azure Storage Emulator)
- (Optioneel) [Azure Static Web Apps CLI](https://github.com/Azure/static-web-apps-cli) voor auth-flow testen

### Configuratie

```powershell
# FunctionApp-configuratie aanmaken
cp FunctionApp/local.settings.template.json FunctionApp/local.settings.json
```

Bewerk `FunctionApp/local.settings.json` en vul in:
- `SqlConnectionString`: jouw lokale SQL Server (bijv. `Server=localhost;Database=SportlinkSqlDb;Integrated Security=True;TrustServerCertificate=True;`)
- Overige velden kun je leeg laten voor basis-functionaliteit

### Starten

```powershell
# Alle services starten (Azurite + FunctionApp + BlazorAdmin)
.\Start-Debug.ps1

# Verifiëren
.\Test-App.ps1
```

### Git hooks activeren (verplicht)

```bash
git config core.hooksPath .githooks
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

### Blazor Admin GUI lokaal

De Blazor GUI gebruikt lokaal `appsettings.json` (niet `appsettings.Production.json`). Authenticatie wordt gesimuleerd — je bent altijd ingelogd als admin. Dit is bedoeld voor snel itereren zonder Azure-omgeving.

---

## 9. Kosten

Alles kan op Azure **Free Tier** draaien:

| Resource | Tier | Geschatte kosten |
|---|---|---|
| Azure Functions | Consumption | €0 (eerste 1M requests/maand gratis) |
| Azure SQL Database | Free (32GB) | €0 |
| Azure Static Web Apps | Free | €0 |
| Azure Storage (Azurite-equivalent) | LRS, minimaal gebruik | < €0,05/maand |

> Schakel de e-mailverwerking (`EmailProcessorEnabled=true`) pas in als je de volledige setup hebt getest. OpenAI-gebruik kost geld per API-aanroep.

---

## Vragen of problemen?

Open een issue via GitHub: [github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues) → gebruik het template "Nieuwe club — hulp bij setup".
