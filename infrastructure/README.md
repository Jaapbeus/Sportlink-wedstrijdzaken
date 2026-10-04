# Infrastructure as Code (Bicep)

Declaratieve beschrijving van alle Azure-resources voor Sportlink Wedstrijdzaken.

## Structuur

```
infrastructure/
├── main.bicep                    # Top-level deployment, referenceert modules
├── main.parameters.json          # Resource-namen en parameters (geen secrets)
└── modules/
    ├── function-app.bicep        # OUDE Function App (Linux Consumption, gestopt) + Storage Account
    ├── function-app-flex.bicep   # Function App op Flex Consumption (productie sinds 2026-10-03, epic #1063)
    ├── static-web-app.bicep      # Static Web App (Free tier, Blazor WASM)
    └── monitoring.bicep          # Application Insights (workspace-based, gratis tot 5 GB/maand)
```

## Gebruik

### Vereisten

```bash
az login --tenant [TENANT_ID]
az account set --subscription [SUBSCRIPTION_ID]
```

### What-if (standaard — geen wijzigingen)

```bash
az deployment group what-if \
  --resource-group myAppGroup \
  --template-file infrastructure/main.bicep \
  --parameters infrastructure/main.parameters.json
```

Toont drift zonder iets te wijzigen. Verwachte output: "No changes detected" als de
resources overeenkomen.

### Deploy (alleen na expliciete goedkeuring)

> ⚠️ **Een `create` van `main.bicep` wijzigt de bestaande productie-app.** De module
> `function-app.bicep` declareert maar zes app settings in `siteConfig.appSettings`; een `create`
> vervangt daarmee de volledige lijst van de live app (secrets, `POSTGRES_CONNECTION_STRING`, de
> SAS-URL in `WEBSITE_RUN_FROM_PACKAGE`). De "Modify"-diffs die `what-if` toont zijn dus geen
> onschuldige drift zodra je ze uitvoert. Voor de Flex-app (FLEX-05) rol je **alleen**
> `modules/function-app-flex.bicep` uit — zie [docs/RUNBOOK-FLEX-MIGRATIE.md](../docs/RUNBOOK-FLEX-MIGRATIE.md) §3.

```bash
az deployment group create \
  --resource-group myAppGroup \
  --template-file infrastructure/main.bicep \
  --parameters infrastructure/main.parameters.json \
  --parameters appInsightsConnectionString="<uit Azure Portal>" \
               sqlConnectionString="<uit GitHub secret>"
```

### Monitoring deployen (kostenwaarschuwing — zie sectie kosten)

```bash
az deployment group create \
  --resource-group myAppGroup \
  --template-file infrastructure/main.bicep \
  --parameters infrastructure/main.parameters.json \
  --parameters deployMonitoring=true
```

## Kostenstatus

| Module | Status | Kosten |
|---|---|---|
| `function-app.bicep` | Beschrijft de oude Linux Consumption-app (gestopt, bewaard tot 2027-01-03) | Gratis (Consumption Plan) |
| `function-app-flex.bicep` | Beschrijft de productie-app (Flex); in `main.bicep` achter `deployFlexApp` (standaard `false`) | Gratis binnen het Flex-tegoed, mits `instanceMemoryMB`/`maximumInstanceCount` bewust laag blijven — zie hieronder |
| `static-web-app.bicep` | Beschrijft bestaande resources | Gratis (Free SKU) |
| `monitoring.bicep` | Aanwezig, **niet auto-uitgerold** | Gratis tot 5 GB/maand (gedeeld per billing account) |

### ⚠️ Kostenwaarschuwing: Flex Consumption-app (epic #1063, FLEX-04/FLEX-05)

`function-app-flex.bicep` beschrijft een **nieuwe** Function App op het Flex Consumption-plan,
naast de bestaande Linux Consumption-app in `function-app.bicep`. In-place migratie naar Flex
bestaat niet (bevestigd via Microsoft Learn) — dit is dus altijd een aparte resource, nooit een
wijziging van de bestaande app.

`deployFlexApp` staat standaard op `false` in `main.parameters.json`. Dit is de **FLEX-05-kostengate**:
alleen op `true` zetten via een los `--parameters deployFlexApp=true` (nooit in dit bestand commit)
na expliciete kostengoedkeuring van de eigenaar. Voor `what-if` mag de conditie tijdelijk `true` zijn
— `what-if` wijzigt niets.
Voor het daadwerkelijk aanmaken (FLEX-05) **niet** `main.bicep` met `deployFlexApp=true` uitrollen,
maar de module zelfstandig — anders gaat de bestaande app mee (zie de waarschuwing bij *Deploy*
hierboven). Commando's, de vier kostencontroles en de volgorde tot en met de cutover staan in
[docs/RUNBOOK-FLEX-MIGRATIE.md](../docs/RUNBOOK-FLEX-MIGRATIE.md).

Onderbouwde defaults (FLEX-02, gemeten 2026-09-12 over 31 dagen echt verbruik):
- `flexInstanceMemoryMB = 2048` — 512 MB is **niet haalbaar**: op alle 31 gemeten dagen lag het
  geheugengebruik boven 512 MB (gemiddeld 546 MB, piek 1.205 MB). Bij 2048 MB wordt 62–77% van het
  gratis maandtegoed (100.000 GB-s) verbruikt — gratis, maar zonder ruime marge.
- `flexMaximumInstanceCount = 5` — bewust ver onder de platform-default van 100. Flex kent **geen**
  automatische kostenrem (geen spending limit op Pay-As-You-Go, budgetten zijn alleen meldingen);
  bij de default van 100 is het theoretische maandmaximum ~$13.663, bij 5 blijft dat begrensd.

De Flex-app gebruikt een system-assigned managed identity voor `AzureWebJobsStorage` en de
deployment-storage-authenticatie — geen storage-connection-string-secret, een CISO-verbetering
t.o.v. de Consumption-app.

### ⚠️ Kostenwaarschuwing: Log Analytics

Klassieke (workspace-loze) Application Insights bestaat niet meer — Microsoft heeft dit
per februari 2024 uitgefaseerd. `monitoring.bicep` maakt de resource aan zonder
`workspaceResourceId`, maar Azure koppelt er zelf een automatisch beheerde Log
Analytics-workspace aan; het al dan niet instellen van `workspaceResourceId` bepaalt dus
niet meer of dit kostenvrij blijft. Ingestie en retentie lopen altijd via die workspace.
**De Legacy Free Tier voor Log Analytics is niet beschikbaar voor nieuwe workspaces**
(vervallen 1 juli 2022) — het gratis budget is de gedeelde 5 GB/maand data-allowance per
billing account. Daarboven: pay-as-you-go op verbruik.

Maatregel: `deployMonitoring` staat standaard op `false` in `main.parameters.json`.
Vereist expliciete `--parameters deployMonitoring=true` bij deployment.

## Runtime: .NET 10 op Flex Consumption

De productie-app draait sinds 2026-10-03 op Flex Consumption met stack `dotnet-isolated 10.0`
(`function-app-flex.bicep`, `runtime.version: '10.0'`); alle csproj's targeten `net10.0`. Csproj-target en
stackwaarde moeten altijd overeenkomen, anders volgt een 503 "Function host is not running".

`function-app.bicep` beschrijft de **oude Linux Consumption-app** (`DOTNET-ISOLATED|9.0`). Die app is gestopt
maar bewaard tot minimaal 2027-01-03 (#1076) en wordt daarna opgeruimd; zet er nooit een `net10.0`-build op.
Voor een **nieuwe club** rol je uitsluitend `function-app-flex.bicep` uit (zie
[SETUP-NIEUWE-CLUB.md](../SETUP-NIEUWE-CLUB.md) §2a). De migratiegeschiedenis staat in
[docs/RUNBOOK-FLEX-MIGRATIE.md](../docs/RUNBOOK-FLEX-MIGRATIE.md).

## CI/CD — GitHub Actions

> **What-if-poort (#1455, #1495).** Bij `action=deploy` draait `infrastructure.yml` eerst een what-if en stopt als een
> bestaande app setting van één van de Function Apps in die what-if zou verdwijnen (een ARM-PUT met
> `siteConfig.appSettings` vervangt de hele lijst). De controle loopt per Function App
> (`scripts/ci/check-whatif-appsettings.sh`); het log toont alleen namen, nooit waarden, en onleesbare
> uitvoer stopt de deploy ook.

De workflow `.github/workflows/infrastructure.yml` is alleen handmatig te triggeren
(`workflow_dispatch`). Geen automatische deploy bij push — dit is bewust om
onbedoelde infrastructuurwijzigingen te voorkomen.
