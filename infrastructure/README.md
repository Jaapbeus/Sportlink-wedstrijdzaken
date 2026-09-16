# Infrastructure as Code (Bicep)

Declaratieve beschrijving van alle Azure-resources voor Sportlink Wedstrijdzaken.

## Structuur

```
infrastructure/
├── main.bicep                    # Top-level deployment, referenceert modules
├── main.parameters.json          # Resource-namen en parameters (geen secrets)
└── modules/
    ├── function-app.bicep        # BESTAANDE Function App + Consumption Plan + Storage Account
    ├── function-app-flex.bicep   # NIEUWE Function App op Flex Consumption (epic #1063)
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
| `function-app.bicep` | Beschrijft bestaande resources | Gratis (Consumption Plan) |
| `function-app-flex.bicep` | Aanwezig, **niet auto-uitgerold** (`deployFlexApp=false`) | Gratis binnen het Flex-tegoed, mits `instanceMemoryMB`/`maximumInstanceCount` bewust laag blijven — zie hieronder |
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

## Kritieke constraint: .NET 9 — met einddatum

```bicep
linuxFxVersion: 'DOTNET-ISOLATED|9.0'  // niet wijzigen zolang dit een Consumption-plan is
```

Het Linux Consumption Plan ondersteunt .NET 10 niet — een `net10.0`-deploy geeft daar 503.

.NET 9 gaat op **10 november 2026** uit support en is de laatste .NET-versie die Linux Consumption
krijgt. De migratie naar Flex Consumption + .NET 10 loopt via **epic #1063**. Let op: in-place
migratie naar Flex bestaat niet — er moet een nieuwe Function App komen, met een nieuwe hostname.
Flex heeft een eigen (kleiner) gratis tegoed; zie CLAUDE.md → Kostenbeleid.

## CI/CD — GitHub Actions

De workflow `.github/workflows/infrastructure.yml` is alleen handmatig te triggeren
(`workflow_dispatch`). Geen automatische deploy bij push — dit is bewust om
onbedoelde infrastructuurwijzigingen te voorkomen.
