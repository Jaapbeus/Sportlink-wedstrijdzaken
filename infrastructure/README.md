# Infrastructure as Code (Bicep)

Declaratieve beschrijving van alle Azure-resources voor Sportlink Wedstrijdzaken.

## Structuur

```
infrastructure/
├── main.bicep              # Top-level deployment, referenceert modules
├── main.parameters.json    # Resource-namen en parameters (geen secrets)
└── modules/
    ├── function-app.bicep  # Function App + Consumption Plan + Storage Account
    ├── static-web-app.bicep # Static Web App (Free tier, Blazor WASM)
    └── monitoring.bicep    # Application Insights (workspace-based, gratis tot 5 GB/maand)
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
| `static-web-app.bicep` | Beschrijft bestaande resources | Gratis (Free SKU) |
| `monitoring.bicep` | Aanwezig, **niet auto-uitgerold** | Gratis tot 5 GB/maand (gedeeld per billing account) |

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
