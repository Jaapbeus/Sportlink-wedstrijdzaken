# MONITORING.md

Observability, alerting en debugging voor de Sportlink Wedstrijdzaken applicatie.

> **Kostenstatus — Application Insights is NIET onvoorwaardelijk gratis.**
>
> Application Insights is in deze stack **workspace-based**: ingestie en retentie lopen volledig via
> een door Azure beheerde Log Analytics-workspace. Klassieke, workspace-loze Application Insights
> bestaat sinds februari 2024 niet meer; `infrastructure/modules/monitoring.bicep` legt dit vast en
> bevestigt het empirisch — beide componenten rapporteren `IngestionMode: LogAnalytics`. De Legacy
> Free Tier is op **1 juli 2022** vervallen voor nieuwe workspaces. Het enige gratis budget is de
> **5 GB/maand data-allowance per billing account**, gedeeld over álle workspaces in dat account.
>
> `CLAUDE.md` plaatst Application Insights daarom in de tabel *Potentieel betaald — expliciete
> goedkeuring vereist*. Drie verplichtingen volgen daaruit:
>
> 1. **Daily cap van maximaal 100 MB/dag.** Die zet je niet op de Application Insights-resource maar
>    op de gekoppelde Log Analytics-workspace (`properties.workspaceCapping.dailyQuotaGb`). Omdat
>    Azure die workspace zelf beheert en hij niet in onze Bicep staat, is dit **handwerk in de
>    portal**: Log Analytics workspace → Usage and estimated costs → Daily cap.
> 2. **Expliciete goedkeuring van de eigenaar** vóór een nieuwe Application Insights-resource of
>    workspace wordt aangemaakt — conform het kostenbeleid in `CLAUDE.md`.
> 3. **Sampling aanzetten** vóórdat het telemetrievolume groeit; zie
>    [host.json (sampling)](#hostjson-sampling). Dat is de enige kostenrem die in dit repo zelf ligt.
>
> Metric Alert Rules zijn eveneens **betaald** — zie [Alerting](#alerting) voor gratis alternatieven.

---

## Architectuuroverzicht

```
Azure Functions (func-[clubcode]-sportlink)
  └── Application Insights (APPLICATIONINSIGHTS_CONNECTION_STRING)
        → traces, exceptions, dependencies, customEvents

Azure Static Web Apps (swa-[clubcode]-sportlink)
  → Geen eigen Application Insights; SWA logs via Azure Monitor (gratis Activity Log)

Database — één van twee, per fork gekozen via DatabaseTier (zie docs/ARCHITECTUUR-DATABASE-TIERS.md):
  ├── Azure SQL ([database-naam] @ [sql-resource-group])
  │     → Geen Application Insights; Resource Health via Azure Portal; zie "Azure SQL Free-tier
  │       bescherming" hieronder voor het volledige vangnet (SQL Server-tier)
  └── Postgres (bijv. Supabase, productietier sinds #976)
        → Provider-eigen dashboard/monitoring; nog geen los uitvalmonitor-equivalent in deze repo
```

### Welk vangnet geldt voor welke tier

Dit document beschrijft bewaking voor **beide** databasetiers. Niet elk vangnet geldt voor allebei:

| Vangnet | SQL Server-tier | Postgres-tier (productie) |
|---|---|---|
| Application Insights (traces, exceptions, KQL) | ✅ | ✅ |
| `GET /api/health` (`database`, `pendingMigrations`, `lastSync`) | ✅ | ✅ |
| `db-check` in `deploy.yml` (ARM-status vóór migratie/deploy) | ✅ | ❌ — job is tier-gegate op `SqlServer` |
| `DatabaseUitvalMonitorFunction` (dagelijkse uitvalmail) | ✅ | ❌ — **geen equivalent**, bekend open punt |
| `Setup-SqlAlerts.ps1` (Resource Health Alert + e-mail) | ✅ | ❌ — Azure-SQL-specifiek |
| Dagelijkse Supabase-advisorcontrole (beveiliging/performance) | ❌ | ✅ |
| Supabase-dashboard / providermonitoring | ❌ | ✅ |

**Het gat:** een club die op Postgres draait heeft géén losstaande, e-mail-onafhankelijke
uitvalmonitor. De Supabase-advisorcontrole merkt een database die *plat ligt* niet als zodanig op —
de workflow faalt dan op een API-fout, wat een signaal is maar geen gerichte uitvalmelding.
Behandel dit als een bekend, open punt, niet als een verkeerd begrepen architectuur.

---

## Application Insights instellen

### Lokale ontwikkeling

Voeg toe aan het `local.settings.json` van de tier waarop je werkt — standaard
`FunctionApp.Postgres/local.settings.json` (de tier die in productie draait), voor de SQL
Server-tier `FunctionApp/local.settings.json`:

```json
"APPLICATIONINSIGHTS_CONNECTION_STRING": "InstrumentationKey=<key>;IngestionEndpoint=https://..."
```

Haal de connection string op via:
- Azure Portal → Application Insights resource → Overzicht → Connection String
- OF: `az monitor app-insights component show --app <naam> --resource-group <rg> --query connectionString`

### Productie (Azure)

De Function App leest `APPLICATIONINSIGHTS_CONNECTION_STRING` automatisch als App Setting.
Stel in via:

```bash
az functionapp config appsettings set --name func-[clubcode]-sportlink --resource-group [sql-resource-group] --settings "APPLICATIONINSIGHTS_CONNECTION_STRING=<connection-string>"
```

Of via Azure Portal → Function App → Settings → Environment variables.

### host.json (sampling)

**Sampling staat nu niet aan.** Zowel `FunctionApp.Postgres/host.json` (productietier) als
`FunctionApp/host.json` bevat uitsluitend `version` en `functionTimeout` — geen `samplingSettings`.
`infrastructure/modules/monitoring.bicep` noemt sampling op 10% wél als de daadwerkelijke
beheersmaatregel tegen onverwacht volume; die aanname klopt dus niet met de werkelijkheid.

Er is ook geen app-eigen gratis limiet: het budget is 5 GB/maand Log Analytics-ingestie **per
billing account**, gedeeld. Zet sampling daarom aan vóórdat het telemetrievolume groeit, en
combineer het met de daily cap op de workspace (zie de kostenstatus bovenaan).

Voeg toe aan het `host.json` van de actieve tier (`FunctionApp.Postgres/host.json`, respectievelijk
`FunctionApp/host.json`):

```json
{
  "version": "2.0",
  "functionTimeout": "00:10:00",
  "logging": {
    "applicationInsights": {
      "samplingSettings": {
        "isEnabled": true,
        "maxTelemetryItemsPerSecond": 5
      }
    }
  }
}
```

---

## Alerting

### Gratis alert-typen

| Type | Kosten | Wanneer gebruiken |
|---|---|---|
| **Activity Log Alerts** | Gratis | Deploy-fout, resource-verwijdering, config-wijziging |
| **Resource Health Alerts** | Gratis | Function App of SQL onbereikbaar |

### Betaalde alert-typen (expliciete goedkeuring vereist)

| Type | Kosten | Status |
|---|---|---|
| Metric Alert Rules | Betaald per time series | ⚠️ **Niet geïmplementeerd** — vereist goedkeuring eigenaar |

Bron: [Azure Monitor cost — alerts](https://learn.microsoft.com/azure/azure-monitor/fundamentals/best-practices-cost#alerts)

### Alert-drempelwaarden — alleen relevant ná goedkeuring van betaalde alerts

> Deze tabel beschrijft drempelwaarden voor **Metric Alert Rules**, en die zijn betaald per
> gemonitorde tijdreeks. Er staat **geen** alert-implementatie in `infrastructure/`, en zolang de
> eigenaar betaalde alerts niet expliciet goedkeurt komt die er ook niet. Lees de tabel dus als een
> voorbereid ontwerp, niet als een geplande oplevering.

| Metriek | Warning | Critical |
|---|---|---|
| HTTP 5xx error rate | > 1% / 5 min | > 5% / 2 min |
| HTTP 4xx error rate | > 5% / 5 min | > 15% / 2 min |
| p95 response time | > 1 s | > 2 s |
| Health endpoint DOWN | — | 3 achtereenvolgende fouten |

---

## Dagelijkse Supabase-advisorcontrole (#1221, epic #1219)

> De eerste geautomatiseerde productiebewaking in dit repo die **niet** van e-mail afhankelijk is
> en **niet** op een timer in de FunctionApp draait.

`.github/workflows/supabase-advisors.yml` draait elke dag om **05:00 UTC** (07:00 CEST / 06:00 CET,
net na de nachtelijke Sportlink-sync) en is ook handmatig te starten via *Actions → Supabase-advisors
→ Run workflow*.

### Wat het doet

1. Haalt de **Security Advisor** en de **Performance Advisor** op via de Supabase Management API.
2. Filtert op **ERROR/WARN** en `facing = EXTERNAL`.
3. Vergelijkt met `.github/supabase-advisors-baseline.json` (geaccepteerde risico's, op `cache_key`).
4. Nieuwe bevindingen ⇒ één issue met label `supabase-advisor`, of een reactie op het bestaande
   open issue. Niets nieuws ⇒ geen issue.

### Waarom dagelijks

Logretentie op het Supabase Free plan is **één dag**. Een wekelijkse cadans zou het logvenster
structureel missen — het venster dat de agentische laag (#1222) nodig heeft. Bijkomend voordeel: een
Free-project wordt na zeven dagen zonder verkeer automatisch gepauzeerd, en deze run telt als
activiteit.

### Escalatie

| Signaal | Betekenis | Actie |
|---|---|---|
| Issue met `supabase-advisor`, niveau **ERROR** | Supabase meldt een concreet beveiligings- of performanceprobleem | Zelfde dag beoordelen; meestal een nieuwe migratie |
| Issue met `supabase-advisor`, niveau **WARN** | Aandachtspunt | Meenemen in de eerstvolgende iteratie |
| **Workflow rood** | De controle zélf is kapot: ontbrekend secret, API-fout, gewijzigd responseformaat, of een redactie-gate die afging | Direct onderzoeken — zolang dit rood staat wordt er *niets* bewaakt |
| Geen issue, groene run | Niets gevonden buiten de baseline | Geen actie |

Let op het verschil tussen de derde en de vierde regel: "geen issue" en "niet gedraaid" zien er van
buiten identiek uit. Daarom schrijft elke run een samenvatting naar de runsamenvatting, ook als er
niets te melden valt.

### Configuratie

Twee repository-secrets, **allebei als Secret en niet als Variable** — deze repository is publiek,
Actions-logs zijn dat ook, en de project-ref identificeert de club (`CLAUDE.md` regel 4a; ditzelfde
lek werd bij #1204 voor zes andere waarden gedicht):

| Secret | Inhoud |
|---|---|
| `SUPABASE_ACCESS_TOKEN` | Personal access token. Aanbevolen **scoped**: Advisors=Read, Logs=Read, Database Security=Read. Een classic token draagt volledige accounttoegang op elke organisatie en elk project |
| `SUPABASE_PROJECT_REF` | De project-ref (Project Settings → General → Reference ID) |

### Een bevinding accepteren

Voeg de baseline-sleutel toe aan `.github/supabase-advisors-baseline.json`, mét `reden` en `issue`.
De workflow faalt op een regel zonder reden. Zo is "wij accepteren dit risico" een PR-diff met een
onderbouwing, in plaats van een vinkje in een dashboard dat niemand terugziet — hetzelfde principe
als §65 van `docs/ARCHITECTUUR-DATABASE-TIERS.md`.

### De meldketen verifiëren terwijl er niets mis is

Zolang het project schoon is, levert een normale run nul bevindingen en blijft het issue-pad
ongetest. Start de workflow daarom af en toe handmatig met de input `testlint` op bijvoorbeeld
`no_primary_key`: dat vervangt het niveaufilter door dat ene lint, zodat er gegarandeerd
bevindingen zijn en het volledige pad doorlopen wordt. Sluit het testissue daarna. De redactie-gates
draaien bij zo'n run onverkort door.

### Kosten

Management API en GitHub Actions zijn beide gratis binnen de huidige plannen; twee HTTPS-calls per
run. Geen Azure-resource, geen tierwijziging — dit valt buiten het kostenbeleid in `CLAUDE.md`.

---

## Azure SQL Free-tier bescherming

> **Geldt uitsluitend voor de SQL Server-tier.** Sinds 2026-09-04 draait productie op Postgres
> (issue #976, zie `docs/ARCHITECTUUR-DATABASE-TIERS.md`) — deze sectie beschrijft dus vandaag het
> vangnet voor de tier waarop déze installatie niet draait. Er bestaat **nog geen Postgres-
> equivalent** van `DatabaseUitvalMonitorFunction` hieronder — een club die volledig op Postgres
> draait heeft dus geen losstaande, e-mail-onafhankelijke uitvalmonitor. Dit is een bekend, open
> punt, geen verkeerd begrepen architectuur; behandel het als zodanig totdat het is opgepakt.
> Draai je (nog) op de SQL Server-tier, dan is deze sectie onverkort van toepassing.
>
> **Sinds #1221 is een deel hiervan wél gedekt, maar nadrukkelijk niet het uitvaldeel.** De
> dagelijkse Supabase-advisorcontrole (zie hieronder) kijkt naar beveiliging en performance van de
> Postgres-database. Een database die *plat ligt* merkt hij niet als zodanig op: de workflow faalt
> dan op een API-fout, wat een signaal is maar geen gerichte uitvalmelding met noodmail. Het open
> punt blijft dus staan.

De gratis Azure SQL database heeft een maandlimiet van **100.000 vCore-seconden**. Bij uitputting
wordt de database gepauzeerd tot het begin van de volgende kalendermaand. Dit heeft impact op drie lagen.

### Laag 1 — Deployment guard (CI/CD)

De `db-check` job in `deploy.yml` controleert de database-status via de Azure ARM API. **Sinds #599
blokkeert hij de build niet**, en de volledige pipeline dus evenmin. De gate geldt per job:

| Job in `deploy.yml` | Gegate op `db-check`? | `needs:` |
|---|---|---|
| `build` | **Nee** — bewust, zie #599 | *(geen)* |
| `blazor-deploy` | **Nee** | `build` |
| `db-migrate` (alleen `DatabaseTier=SqlServer`) | **Ja** | `[build, db-check]` |
| `db-migrate-postgres` (alleen `DatabaseTier=Postgres`) | **Nee** | `[build]` |
| `deploy` (Function App naar Azure) | **Ja** | `[build, db-check, db-migrate, db-migrate-postgres]` |
| `test` (smoke test) | Indirect, via `deploy` | `[deploy]` |

Wat de gate wél garandeert: er gaat nooit Function App-code of een SQL Server-migratie naar Azure
terwijl de database niet `Online` is. Wat hij **niet** garandeert: `build` en `blazor-deploy` lopen
altijd door, dus **de Blazor-frontend gaat wél live** bij een gepauzeerde database. Lees een
gefaalde of overgeslagen `db-check` daarom nooit als "er is niets gedeployed".

Dat `build` losgekoppeld is, is geen omissie maar de kern van #599: eerder werden `build`,
`blazor-deploy` én `test` allemaal overgeslagen zodra de Free-tier database gepauzeerd was, waardoor
compileerfouten wekenlang onopgemerkt konden blijven. Bouwen heeft geen database nodig; deployen
wel.

**Vereiste GitHub variabele (eenmalig instellen):**
```
Settings → Secrets and variables → Actions → Variables → New repository variable
  Name:  AZURE_SQL_DATABASE_NAME
  Value: [database-naam]   (bijv. myFreeDB)
```

Zonder deze variabele wordt de `db-check` job overgeslagen (backward compatible voor forks
zonder SQL-configuratie). De variabele is naast de al bestaande `AZURE_SQL_SERVER_NAME` en
`AZURE_SQL_RESOURCE_GROUP`.

**Pipeline-volgorde:**
```
db-check ─┐
build ────┼→ db-migrate (SqlServer)  ─┐
          └→ db-migrate-postgres (Postgres) ─┴→ deploy → test
build ─────→ blazor-deploy
```
`db-check` en `db-migrate` draaien alleen bij `DatabaseTier=SqlServer`; `db-migrate-postgres` alleen
bij `DatabaseTier=Postgres` (#1093). `deploy` wacht op de migratiejob van de actieve tier — de
migraties gaan dus altijd vóór de code live. Faalt `db-check` of een migratiejob, dan wordt `deploy`
overgeslagen.

### Laag 2 — In-app overlay (Blazor)

De `DatabaseStatusService` pollt `/api/health` na authenticatie (elke 15 seconden, max 2 minuten).
Het `/api/health` endpoint doet een lichte probe met 5 seconden timeout — `SHOW server_version` op
de Postgres-tier (`FunctionApp.Postgres/HealthFunction.cs`), `SELECT 1` op de SQL Server-tier — en
retourneert:

| `database` waarde | Betekenis |
|---|---|
| `online` | Database bereikbaar |
| `paused` | **Alleen SQL Server-tier.** Auto-paused (fout 40613); Azure begint automatisch te resumeren. De Postgres-tier kent deze status niet — daar is een onbereikbare database altijd `timeout` of `unavailable` |
| `timeout` | Verbinding time-out na 5 seconden |
| `unavailable` | Andere databasefout |
| `unconfigured` | Geen connection string (lokale dev zonder SQL) |

**UI-gedrag:**
- `Starting` (0–2 min): spinner-overlay "Database wordt opgestart..."
- `LimietBereikt` (> 2 min): blokkerende overlay met uitleg + contactadvies
- `Online`: normale app-weergave; alive-check elke 5 minuten

### Laag 3 — E-mail alert (eenmalig instellen)

Voer eenmalig het script uit om een gratis Resource Health Alert te maken:

```powershell
.\scripts\azure\Setup-SqlAlerts.ps1 -ResourceGroup "[sql-resource-group]" -SqlServerName "[sql-servernaam]" -DatabaseName "[database-naam]" -NotificationEmail "beheerder@[club-domein]"
```

Dit maakt aan:
- **Action Group** met e-mailnotificatie (gratis)
- **Resource Health Alert** voor de SQL database (gratis) — e-mail bij `Unavailable` én bij `Resolved`

**Vroege waarschuwing (Metric Alert — controleer kosten eerst):**
Azure Portal → SQL Database → Monitoring → Metrics → Metric: `Free amount remaining`
→ New alert rule → Threshold: `10.000` (= 10% van maandlimiet).
Controleer actuele kosten via het kostenbeleid in `CLAUDE.md` vóór aanmaken.

### Onafhankelijke database-uitvalmonitor (#831)

**Waarom naast Laag 3:** Laag 1-3 hierboven detecteren een uitval óf vóór een deploy (`db-check`),
óf terwijl een gebruiker de Admin GUI open heeft (`DatabaseStatusService`), óf via Resource Health
(dat een normale, korte serverless auto-pause niet als "Unavailable" hoeft te melden — dit is niet
hetzelfde als een uitputting van het gratis maandbudget). De bestaande e-mail-noodmail in
`EmailProcessorFunction` (zie [docs/EMAIL-VERWERKING.md](EMAIL-VERWERKING.md)) wordt bovendien alleen
gecontroleerd als er toevallig inkomende e-mail is die de databaseverbinding opent. Tijdens de 5+
dagen durende uitval van 25-30 augustus 2026 (#799/#808) bleek dát de reden dat er geen enkele
melding is aangekomen: geen relevante e-mail → databaseverbinding nooit geprobeerd → uitval nooit
gedetecteerd.

`DatabaseUitvalMonitorFunction` (`FunctionApp/Monitoring/`) lost dit op met een losstaande,
dagelijkse timer die de management-plane status van de database opvraagt via de Azure SQL Database
REST API (`GET .../providers/Microsoft.Sql/servers/{server}/databases/{database}`) — een
ARM-leesoperatie, geen databaseverbinding, dus deze check kan niet zelf slachtoffer worden van
dezelfde storing. Staat de database langer dan ~6 uur gepauzeerd (`properties.status == "Paused"`,
duur via `properties.pausedDate`), dan gaat er een melding uit naar `GraphMailbox`; bij een
langdurige uitval herhaalt dit met een minimuminterval van **20 uur**
(`MinimaleHerhalingsinterval`). De dagelijkse schedule maakt daar in de praktijk ~1x per dag van;
de 20 uur voorkomt een dubbele mail als de functie een keer vaker dan gepland draait. De
throttle-registratie is dezelfde `INoodmailThrottleStore` *en dezelfde sleutel*
(`database-noodmail`) als de e-mail-noodmail, dus welke van de twee het eerst meldt onderdrukt de
ander voor diezelfde uitval.

**Het ontvangeradres staat niet in het log (#1201).** De functie logt uitsluitend dát er een melding
is verstuurd, plus de uitvalduur — nooit de waarde van `GraphMailbox`. Dit volgt Laag 5 van
[SECURITY.md](../SECURITY.md), die e-mailadressen (afzender én ontvanger) expliciet uitsluit van
Function-logs en Application Insights. Wie wil controleren wáár de melding heen ging, leest de
app-setting `GraphMailbox` — niet het log.

**Kosten: €0.** Eén Function-executie per dag valt ruim binnen de Consumption-plan-limiet
(1M executies/maand), en een ARM-managementaanroep wordt niet gefactureerd als database-compute.

**Configuratie (optioneel — zonder deze stap blijft alleen de bestaande, e-mail-afhankelijke
noodmail actief):**

1. App settings op de Function App (naast de al bestaande `AzureSubscriptionId` en
   `AzureResourceGroupName`, die #27 al introduceerde voor de FETCH_SCHEDULE-herstartfunctie):
   ```
   AzureSqlServerName      = [sql-servernaam]     (zonder .database.windows.net)
   AzureSqlDatabaseName    = [database-naam]
   DATABASE_STATUS_MONITOR_SCHEDULE = 0 0 8 * * *   (VERPLICHT — zie hieronder)
   ```
   > **`DATABASE_STATUS_MONITOR_SCHEDULE` is niet optioneel en heeft geen default in code.** De
   > TimerTrigger is gedeclareerd als `[TimerTrigger("%DATABASE_STATUS_MONITOR_SCHEDULE%")]`, en de
   > Functions-runtime lost die `%…%`-verwijzing op bij het **indexeren** van de functie — vóór de
   > functiebody ooit draait. Ontbreekt de app setting, dan faalt het indexeren en komt de functie
   > niet omhoog; dat symptoom wijst nergens naar deze instelling. De waarde `0 0 8 * * *` staat
   > alleen als voorbeeld in `FunctionApp/local.settings.template.json`.
2. **Eenmalige, gratis roltoewijzing:** de Function App heeft in productie al een Managed Identity
   met een rol op zijn eigen resource (Website Contributor, voor de bestaande herstartfunctie — zie
   `AdminSettingsFunction.TriggerFunctionAppRestartAsync`). Voor déze controle is alleen leestoegang
   nodig:
   ```
   Azure Portal → SQL-server → Access control (IAM) → Add role assignment
     Role:    Reader
     Member:  Managed identity → System-assigned → func-[clubcode]-sportlink
   ```
   Dit voegt geen nieuwe Azure-resource toe en kost niets — het is een leesrol op een bestaande
   resource voor een identity die al bestaat.

Zonder de **vier resource-instellingen** (`AzureSubscriptionId`, `AzureResourceGroupName`,
`AzureSqlServerName`, `AzureSqlDatabaseName`) logt de functie een informatiebericht en doet verder
niets — een club kan dus zonder die configuratie blijven draaien, met alleen de bestaande,
e-mail-pipeline-afhankelijke noodmail als vangnet. Dat geldt **niet** voor
`DATABASE_STATUS_MONITOR_SCHEDULE`: die moet altijd gezet zijn, anders kan de functie niet worden
geïndexeerd.

### Sportlink contract-check-noodmail (#998)

`SportlinkContractCheckTimerFunction` (`FunctionApp.Postgres/Sportlink/`, dagelijks `0 30 6 * * *`)
controleert of de vorm van Sportlinks `Match`-respons nog klopt met wat deze app verwacht — een
vroege waarschuwing voor een stille Sportlink-release, ruim vóórdat dit een mutatie zou laten
mislukken. Bewust géén nieuw alarmeringsmechanisme: bij een afwijking hergebruikt de timer hetzelfde
`INoodmailThrottleStore`/`IEmailGraphService`-patroon als de e-mail- en database-noodmail hierboven,
met een eigen throttle-sleutel **`sportlink-contract-noodmail`** en een interval van 24 uur — dus
geen betaalde Log Analytics/App Insights-alert-regel en geen nieuw verzendpad. Het resultaat van
elke run staat ook in `public.sportlinkcontractcheck` en op de statussectie van Instellingen (zie
[docs/SPORTLINK-WEB-EXTENSION.md](SPORTLINK-WEB-EXTENSION.md) §4.2/§3.1b).

### Activity Log Alert aanmaken (gratis)

Alert bij deploy-fout (Function App restart mislukt):

```bash
az monitor activity-log alert create --name "FunctionApp-deploy-fout" --resource-group [sql-resource-group] --scopes "/subscriptions/<id>/resourceGroups/[sql-resource-group]/providers/Microsoft.Web/sites/func-[clubcode]-sportlink" --condition category=Administrative and operationName=Microsoft.Web/sites/write and status=Failed --action-group /subscriptions/<id>/resourceGroups/<rg>/providers/microsoft.insights/actionGroups/<naam>
```

### Resource Health Alert aanmaken (gratis)

```bash
az monitor activity-log alert create --name "FunctionApp-resource-health" --resource-group [sql-resource-group] --scopes "/subscriptions/<id>/resourceGroups/[sql-resource-group]" --condition category=ResourceHealth and resourceType=Microsoft.Web/sites --action-group /subscriptions/<id>/resourceGroups/<rg>/providers/microsoft.insights/actionGroups/<naam>
```

---

## KQL-queries voor debugging

Gebruik deze queries in Azure Portal → Application Insights → Logs.

### Request tracing via Correlation-ID

Elke admin-response bevat `x-correlation-id` in de response header. Gebruik dat ID om de volledige request-keten te reconstrueren:

```kql
traces
| where customDimensions.CorrelationId == "<id>"
| order by timestamp asc
| project timestamp, message, severityLevel, operation_Name
```

### Fouten in de afgelopen 24 uur

```kql
exceptions
| where timestamp > ago(24h)
| order by timestamp desc
| project timestamp, type, outerMessage, operation_Name, customDimensions.CorrelationId
```

### Trage requests (> 1 seconde)

```kql
requests
| where duration > 1000
| where timestamp > ago(1h)
| order by duration desc
| project timestamp, name, duration, resultCode, url
```

### Email-verwerking overzicht

```kql
traces
| where operation_Name contains "Email"
| where timestamp > ago(7d)
| order by timestamp desc
| project timestamp, message, severityLevel
```

### Dagelijkse sync monitoring

De functienamen verschillen per tier: `PostgresFetchAndStoreApiData` / `PostgresSyncMatchesHttp` op
de Postgres-tier (productie), `FetchAndStoreApiData` / `SyncMatchesHttp` op de SQL Server-tier. De
query hieronder dekt beide, zodat hij niet stilzwijgend nul rijen geeft.

```kql
traces
| where operation_Name in ("PostgresFetchAndStoreApiData", "PostgresSyncMatchesHttp", "FetchAndStoreApiData", "SyncMatchesHttp")
| where timestamp > ago(7d)
| summarize runs=count(), fouten=countif(severityLevel >= 2) by bin(timestamp, 1d)
| order by timestamp desc
```

### 500-fouten per endpoint

```kql
requests
| where resultCode startswith "5"
| where timestamp > ago(7d)
| summarize count() by name, resultCode
| order by count_ desc
```

---

## Escalatiematrix

| Prioriteit | Conditie | Actie | Reactietijd |
|---|---|---|---|
| P1 — Kritiek | Health endpoint DOWN na 3 pogingen | Directe notificatie eigenaar + CISO | < 30 min |
| P1 — Kritiek | Security Gate CI rood na push naar main | STOP — geen merge, directe fix | Onmiddellijk |
| P2 — Hoog | > 5% HTTP 5xx in 2 min | Onderzoek Azure Portal + App Insights | < 2 uur |
| P2 — Hoog | Deploy-fout in GitHub Actions | Bekijk workflow-log, hotfix aanmaken | < 4 uur |
| P3 — Normaal | Dagelijkse sync mislukt | Controleer App Insights, handmatig herstarten | Volgende werkdag |
| P3 — Normaal | Email-verwerking gestopt | Controleer EmailProcessor logs | Volgende werkdag |
| P3 — Normaal | Sportlink contract-check meldt een afwijking (#998) | Controleer `public.sportlinkcontractcheck` en de statussectie op Instellingen — vermoedelijk een Sportlink-release die de responsvorm wijzigde | Volgende werkdag |

---

## GitHub Actions monitoring

### Welke workflows bewaken wat

Naast `deploy.yml` bewaken nog vier workflows de gezondheid van deze applicatie. Ze draaien op
verschillende momenten; alleen samen dekken ze de keten.

| Workflow | Trigger | Wat het bewaakt |
|---|---|---|
| `build.yml` — *Build (PR)* | PR naar `main`/`develop`, push naar `develop` | Build van alle projecten, unit tests, de codekwaliteits- en tier-pariteitsguards, en op een **verse** Postgres-container de databaseguards uit #1220: `scripts/ci/check-rls-enabled.sh` (RLS aan op elke tabel) en `scripts/ci/check-splinter-lints.sh` (Supabase' eigen linter) |
| `deploy.yml` — *Deploy naar Azure* | Push naar `main` | `db-check`, de migratiejobs, de deploy zelf en de smoke tests (401 op admin-endpoints, `settingsLoaded`, `pendingMigrations`) |
| `pre-release-check.yml` — *Pre-release check (develop → main)* | PR naar `main` | Build moet slagen vóór een release-PR gemerged kan worden |
| `pre-release-db-check.yml` — *Pre-release database check* | `workflow_run` na de vorige | Wekt/controleert de database met credentials, bewust via `workflow_run` + `ref: main` zodat PR-inhoud geen productiecredentials kan misbruiken (#1009) |
| `supabase-advisors.yml` — *Supabase-advisors* | Dagelijks 05:00 UTC | Security- en Performance Advisor van de productiedatabase — zie [de sectie hierboven](#dagelijkse-supabase-advisorcontrole-1221-epic-1219) |

### CI-status bekijken

```bash
gh run list --branch main --limit 10
gh run view <run-id>
```

### Deploy-pipeline controleren

```bash
gh run list --workflow deploy.yml --limit 5
```

### PR checks bewaken

```bash
gh pr checks <pr-nr>
```

---

## Bekende beperkingen

| Beperking | Oorzaak | Workaround |
|---|---|---|
| Application Insights niet geconfigureerd | `APPLICATIONINSIGHTS_CONNECTION_STRING` niet ingesteld in Azure | Zie sectie [Application Insights instellen](#application-insights-instellen) |
| Geen Metric Alerts | Betaald; expliciete goedkeuring vereist | Activity Log Alerts als gratis alternatief |
| Blazor WASM heeft geen eigen telemetrie | SWA bevat geen Application Insights SDK | Fouten zichtbaar via browser F12 / SWA-logs |
| Databasemonitoring beperkt | Geen query-telemetrie in Application Insights | Postgres-tier (productie): `pg_stat_statements` en het Supabase-dashboard. SQL Server-tier: `sys.dm_exec_query_stats` |
| Geen uitvalmonitor op de Postgres-tier | `DatabaseUitvalMonitorFunction` is Azure-SQL-specifiek | Bekend open punt; zie "Welk vangnet geldt voor welke tier" bovenaan |

---

## Openstaande databasemigraties (#1098, #1093)

Sinds #1093 past de deploy-pipeline de Postgres-migraties zelf toe, vóór de code live gaat
(`db-migrate-postgres`, zie `ARCHITECTUUR-DATABASE-TIERS.md` §57). Een handmatige ronde met
`Database.Postgres.Cli` blijft mogelijk voor herstel, maar is geen release-stap meer. Loopt de code
toch vooruit op het schema, dan is dat sinds 3.3.0.2 van buiten zichtbaar:

| Veld in `GET /api/health` | Betekenis |
|---|---|
| `pendingMigrations` | Bestandsnamen uit `Database.Postgres/migrations/` die nog niet in de ledger `schema_migrations` staan. Leeg is de gezonde toestand; niet-leeg zet `status` op `degraded` |
| `schemaWarning` | Niet `null` zodra `public.appsettings` een kolom mist die deze versie verwacht; de applicatie draait dan door op de standaardwaarde uit de migratie |
| `settingsLoaded` | Sinds 3.3.0.2 het resultaat van een laadpoging die health zélf doet, niet meer een aanname over een eerdere poging |

**Wat de smoke test in `deploy.yml` hiermee doet.** `settingsLoaded=false` laat de `test`-job
falen: zonder instellingencache antwoordt elk `/api/beheer/*`-endpoint 500, dus dat is een mislukte
deploy. Niet-lege `pendingMigrations` is sinds #1093 óók een fout (was een `::warning::` toen de
pipeline ze nog niet kon toepassen): `db-migrate-postgres` was groen en toch mist de database iets
dat deze versie meelevert. Dat wijst op een secret `POSTGRES_CONNECTION_STRING` dat naar een andere
database wijst dan de gelijknamige Function App-instelling.

**Handeling bij een niet-lege lijst:** controleer eerst of secret en Function App-instelling
dezelfde database benoemen. Daarna eventueel een handmatige ronde met `Database.Postgres.Cli`
(`POSTGRES_CONNECTION_STRING` als omgevingsvariabele, nooit als argument) en controleren dat
`/api/health` `"pendingMigrations": []` toont. Een "ledger-checksum genormaliseerd"-waarschuwing in
het log van de migratiejob is geen fout: dat is de eenmalige CRLF→LF-reparatie uit #1112.

## Verouderde synchronisatie (#1081)

`GET /api/health` bevat twee velden die hierop zien:

| Veld | Betekenis |
|---|---|
| `lastSync` | `MAX(lastsynctimestamp)` over de clubs met `syncenabled = true` — de waarheid uit de database |
| `syncStale` | `true` zodra die waarde ouder is dan `SyncMaxAgeHours` (standaard 36 uur), of ontbreekt |

**Waarom 36 uur.** De timer draait dagelijks. Eén gemiste run valt daarmee op, terwijl een run die
een paar uur later begint of langer duurt geen vals alarm geeft. Installaties met een ander schema
zetten `SyncMaxAgeHours` als Function App-instelling.

**`syncStale` beïnvloedt `status` alleen waar een synchronisatie ook hoort te draaien** — dat wil
zeggen: waar `EgressGuard` uitgaand verkeer toestaat. Lokaal en in CI staat die poort dicht, dus is
een lege of oude `lastsynctimestamp` daar het verwachte gedrag. Zonder dat onderscheid zou elke
ontwikkelmachine permanent `degraded` melden en was het signaal binnen een week waardeloos.

**Aanleiding.** Bij #1077 draaide de synchronisatie acht dagen lang elke nacht, werkte niets bij en
meldde niets: de timer rapporteerde `Success` (de uitzondering werd opgeslokt, zie #1081) en niets
bewaakte de leeftijd van de laatste synchronisatie. Beide gaten zijn nu gedicht — de invocatie
faalt zichtbaar én de leeftijd is opvraagbaar.

**Bewust geen metric alert.** Een Azure metric alert rule wordt per gemonitorde tijdreeks berekend
en valt daarmee buiten het kostenbeleid in CLAUDE.md. Deze velden zijn gratis op te vragen; wie er
een melding op wil, kan `/api/health` periodiek pollen vanaf een bestaande gratis voorziening.
