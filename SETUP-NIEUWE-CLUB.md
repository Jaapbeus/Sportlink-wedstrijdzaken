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
10. [Optioneel: Sportlink Web Extension](#10-optioneel-sportlink-web-extension)

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
  --runtime-version 9 \
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
    "<CONNECTIESTRING-INSTELLING — zie hieronder>" \
    "GraphTenantId=<entra-tenant-id>" \
    "GraphClientId=<entra-app-client-id>" \
    "GraphClientSecret=<entra-app-secret>" \
    "GraphMailbox=<coordinator@voorbeeld.nl>" \
    "OpenAiApiKey=<openai-api-key>" \
    "AiModelName=gpt-4o-mini" \
    "GitHubPat=<github-pat>" \
    "GitHubOwner=<github-username>" \
    "GitHubRepo=<naam-van-jouw-fork>" \
    "EmailProcessorEnabled=false" \
    "EmailReviewMode=true"
```

> **De connectiestring-instelling verschilt per databasetier (§2b). Neem er precies één op:**
>
> | Jouw tier | Instelling |
> |---|---|
> | `DatabaseTier=Postgres` | `"POSTGRES_CONNECTION_STRING=<connectiestring met sslmode=verify-ca en sslrootcert>"` |
> | `DatabaseTier=SqlServer` | `"SqlConnectionString=<jouw-azure-sql-connection-string>"` |
>
> De Postgres-tier leest `SqlConnectionString` nergens, en omgekeerd. Zet je de verkeerde, dan start
> de app wel maar bereikt hij de database niet: `/api/health` geeft dan 503 met `"database":"unconfigured"`.
> Maak de database eerst aan (§2b) en kom daarna terug voor deze stap.

> Stel `EmailProcessorEnabled=true` pas in als je de e-mailverwerking wilt activeren. Begin met `false` tijdens de initiële setup.

> **`GitHubRepo` is verplicht (#607).** Heb je de repo onder een andere naam geforkt, vul dan die naam
> in. Ontbreekt de instelling, dan geeft de feedback-widget een duidelijke configuratiefout (503) —
> voorheen probeerde hij stilzwijgend de upstream-repo en kreeg je een verwarrende 404.

> **`AiModelName` is optioneel (#604).** Zonder deze instelling gebruikt het systeem `gpt-4o-mini`.
> Zet hier een andere modelnaam om te upgraden zonder de software opnieuw te deployen.

### 2b. Database aanmaken — kies een tier

> **Kies bewust één tier: SQL Server óf Postgres.** Sinds 2026-09-04 draait de referentie-productie
> op Postgres (bijv. via [Supabase](https://supabase.com)'s gratis tier) — SQL Server blijft een
> volwaardig, ondersteund alternatief. Zie `docs/ARCHITECTUUR-DATABASE-TIERS.md` voor de afweging.
> Wat je hier kiest, moet overeenkomen met de `DatabaseTier`-variabele in §5c.

**Optie A — Azure SQL Database (Free tier):**

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

> **Let op:** Controleer de actuele beschikbaarheid van de Free tier via de [Azure Portal](https://portal.azure.com) of via `mcp__claude_ai_Microsoft_Learn__microsoft_docs_search("Azure SQL Free tier pricing")` — Microsoft kan dit aanbod wijzigen zonder voorafgaande aankondiging.

**Optie B — Postgres (bijv. Supabase free tier):** maak een nieuw project aan bij je gekozen
Postgres-provider en noteer de connectiestring. Die gaat op twee plaatsen naartoe, allebei onder de
naam **`POSTGRES_CONNECTION_STRING`**: als app-instelling van de Function App (§2a) en als GitHub
Secret in jouw fork (§5b). Een andere schrijfwijze werkt niet — de applicatie leest uitsluitend deze
naam.
Zet `sslmode=verify-ca` in de connectiestring, mét `sslrootcert` dat naar het CA-certificaat van je
provider wijst. **Niet `verify-full`**, hoe verleidelijk dat ook klinkt: bij Supabase biedt het
pooler-endpoint een certificaat zonder SubjectAltName aan, en `verify-full` valideert de hostnaam
juist tegen die SAN — de verbinding faalt dan. Zie `docs/ARCHITECTUUR-DATABASE-TIERS.md` §50 en
issue #1187. Verbind je rechtstreeks (niet via de pooler), dan kan `verify-full` wél; controleer dat
dan eerst tegen je eigen endpoint.

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

**SQL Server:** `deploy.yml`'s `db-migrate`-job voert `Database/Script.PostDeployment1.sql`
automatisch en idempotent uit bij elke deploy (mits `AZURE_SQL_SERVER_NAME` gezet is) — geen
handmatige stap nodig.

**Postgres:** `deploy.yml`'s `db-migrate-postgres`-job past de migraties in
`Database.Postgres/migrations/` automatisch en idempotent toe bij elke deploy, vóórdat de nieuwe
code live gaat (#1093). Dezelfde job maakt de `his`-tabellen aan en seedt de demodata van de
democlub (#1246). **Er is geen handmatige migratieronde** — de job faalt wel expliciet als het
Secret `POSTGRES_CONNECTION_STRING` (§5b) ontbreekt. Zie
`docs/ARCHITECTUUR-DATABASE-TIERS.md` voor het migratiemechanisme.

Wil je de migraties lokaal of buiten de pipeline om toepassen, gebruik dan
`scripts/dev/Invoke-PostgresMigrations.ps1` en zet `POSTGRES_CONNECTION_STRING` als
omgevingsvariabele — nooit als scriptparameter, want argumenten staan op elk platform zichtbaar in
de processenlijst.

**Verbinding verifiëren (lokaal, voor development):**
```powershell
# Postgres-tier
$env:PGPASSWORD = '<wachtwoord uit je lokale .env>'
.\scripts\dev\Test-PostgresConnection.ps1

# SQL Server-tier — altijd een SQL-login, nooit -E; wachtwoord via SQLCMDPASSWORD
$env:SQLCMDPASSWORD = '<sa-wachtwoord uit je lokale .env>'
sqlcmd -S localhost,1433 -U sa -C -Q "SELECT @@VERSION"

# Schema controleren en zo nodig herstellen
.\scripts\dev\Test-App.ps1 -Fix
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

.\scripts\azure\Configure-EntraApp.ps1 `
  -ClientId "<application-client-id>" `
  -ExpectedTenantId "<directory-tenant-id>" `
  -AdminUserPrincipalName "admin@voorbeeld.nl"
```

Het script configureert idempotent:
- App Roles `admin`, `user` en `Wedstrijdzaken` — die laatste is de functionele rol voor
  Sportlink Web Extension-mutaties (§10), náást `admin`/`user` en geen vervanging ervan (#988)
- `roles`-claim in ID-token
- Assignment Required (alleen pre-toegewezen gebruikers)
- Wijst de opgegeven admin-user toe aan de `admin`-rol

**Verifiëren:**
```powershell
.\scripts\azure\Verify-AzureAuthSetup.ps1 `
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
| `SQL_CONNECTION_STRING` | Alleen bij `DatabaseTier=SqlServer` — connectiestring voor de `db-migrate`-job |
| `POSTGRES_CONNECTION_STRING` | Alleen bij `DatabaseTier=Postgres` — hiermee past de `db-migrate-postgres`-job de migraties toe vóór elke deploy (#1093). Zelfde waarde als de Function App-instelling uit §2a; ontbreekt hij, dan breekt de eerste deploy af met een expliciete foutmelding. |
| `AZURE_FUNCTIONAPP_NAME` | `func-<clubcode>-sportlink` |
| `AZURE_FUNCTIONAPP_URL` | `https://func-<clubcode>-sportlink.azurewebsites.net` |
| `AZURE_STATIC_WEB_APP_HOSTNAME` | `<swa-hostname>.azurestaticapps.net` |
| `AZURE_AD_TENANT_ID` | Jouw Entra Directory (tenant) ID |
| `AZURE_AD_CLIENT_ID` | Jouw Entra Application (client) ID |
| `POST_LOGOUT_REDIRECT_URL` | URL van de website van jouw club |

> **Waarom die onderste zes Secrets zijn en geen Variables (#1204).** Ze identificeren jouw club.
> Deze repository is publiek en de Actions-logs van een publieke repository zijn dat óók: GitHub
> drukt ingevulde expressies letterlijk in de joblog af en maskeert **alleen** secrets. Als Variable
> belanden je Function App-naam, SWA-hostname en Entra-ID's dus zichtbaar in elke workflow-run. De
> workflows lezen ze als `secrets.X || vars.X`, dus een Variable werkt technisch nog wel — maar dat
> is een fallback voor bestaande installaties, niet de aanbevolen inrichting. Zie SECURITY.md,
> "Laag 2 — GitHub Actions".

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

Hier horen uitsluitend de waarden die **niet** club-identificerend zijn en die een workflow in een
job-`if:` moet kunnen lezen — dat kan namelijk niet met een secret.

| Variable | Waarde |
|---|---|
| `DatabaseTier` | `SqlServer` of `Postgres` — bepaalt welk `.csproj` gebouwd wordt en welke migratiejob draait (§2b) |
| `DatabaseTierSwitchConfirmation` | **Exact dezelfde waarde als `DatabaseTier`** — veiligheidsmechanisme tegen een per-ongeluk-gewijzigde tier; ontbreekt deze of wijkt hij af, dan faalt de eerste deploy met exitcode 3 |
| `AZURE_SQL_SERVER_NAME` / `AZURE_SQL_DATABASE_NAME` / `AZURE_SQL_RESOURCE_GROUP` | Alleen bij `DatabaseTier=SqlServer` — vereist voor de `db-migrate`-job |

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
# → {"status":"ok","version":"…","database":"online","settingsLoaded":true,
#    "tier":"Postgres","pendingMigrations":[], …}
```

`"status":"degraded"` betekent dat één van de onderliggende velden niet klopt — een HTTP 200 alleen
is dus geen geslaagde deploy. Controleer in dat geval:

| Veld | Wat het betekent als het afwijkt |
|---|---|
| `database` | Niet `online`: de connectiestring bereikt de database niet |
| `settingsLoaded` | `false`: de instellingen zijn nog niet ingevuld (§7) of niet leesbaar |
| `pendingMigrations` | Niet leeg: er zijn migraties die nog niet zijn toegepast |
| `tlsWarning` / `schemaWarning` | Gevuld: TLS-configuratie of databaseschema wijkt af van wat de code verwacht |

`"database":"unconfigured"` geeft HTTP 503 en betekent dat er helemaal geen bruikbare
connectiestring is — controleer dan de instelling uit §2a.

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

Een lokale ontwikkelomgeving is **geen voorwaarde voor een werkende clubinstallatie** — de stappen 1
tot en met 7 hierboven volstaan. Wil je wel lokaal ontwikkelen of testen:

- **[docs/DEVELOPER-SETUP.md](docs/DEVELOPER-SETUP.md)** — de volledige setup, per platform
  (Windows en macOS) en per databasetier. Dit is de enige bron voor lokale setup.
- **[docs/SETUP-CHECKLIST.md](docs/SETUP-CHECKLIST.md)** — dezelfde stappen als afvinklijst.
- **[CONTRIBUTING.md](CONTRIBUTING.md)** — branch-strategie, commit-conventies en de Security Gate.

Kort samengevat: `docker compose up -d` voor de database, het `local.settings.template.json` van
jouw tier kopiëren naar `local.settings.json` ernaast, en daarna `.\scripts\dev\Start-Debug.ps1`
gevolgd door `.\scripts\dev\Test-App.ps1`. De Blazor Admin GUI draait lokaal zonder Azure en
zonder echte authenticatie — je bent er altijd ingelogd als admin.

Activeer vóór je eerste commit de git hooks (verplicht):

```bash
git config core.hooksPath .githooks
cp .githooks/sensitive-patterns.template.txt .githooks/sensitive-patterns.txt
```

---

## 9. Kosten

Alles kan op Azure **Free Tier** draaien:

| Resource | Tier | Geschatte kosten |
|---|---|---|
| Azure Functions | Consumption | €0 (eerste 1M requests/maand gratis) |
| Database | Azure SQL Free tier (32GB) óf Postgres free tier (bijv. Supabase) | €0 |
| Azure Static Web Apps | Free | €0 |
| Azure Storage (Azurite-equivalent) | LRS, minimaal gebruik | < €0,05/maand |

> Schakel de e-mailverwerking (`EmailProcessorEnabled=true`) pas in als je de volledige setup hebt getest. OpenAI-gebruik kost geld per API-aanroep.

---

## 10. Optioneel: Sportlink Web Extension

Wil je vanuit de Admin GUI ook wijzigingen (kleedkamers, veld) terugschrijven naar Sportlink Club,
in plaats van alleen lezen? Dat is een aparte, gedeeltelijk gebouwde feature (epic #986) met een
eigen koppelingsproces per functionele rol. Zie **[docs/SPORTLINK-WEB-EXTENSION.md](docs/SPORTLINK-WEB-EXTENSION.md)**
voor de huidige status en hoe je een rol koppelt — dit is geen verplichte stap voor een werkende
basisinstallatie.

De extension start altijd in **dry-run**: elke mutatie wordt gesimuleerd en gelogd, er wordt niets
echt naar Sportlink geschreven, ook niet als je de extension zelf al aanzet. Controleer eerst de
statussectie op Instellingen (rol-koppeling, contract-check) vóórdat je dry-run uitzet.

---

## Vragen of problemen?

Open een issue via GitHub: [github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues](https://github.com/Jaapbeus/Sportlink-wedstrijdzaken/issues) → gebruik het template "Nieuwe club — hulp bij setup".
