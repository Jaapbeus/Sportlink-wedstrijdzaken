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
    └── monitoring.bicep    # Application Insights (klassiek, kosteloos)
```

## Gebruik

### Vereisten

```bash
az login --tenant [TENANT_ID]
az account set --subscription aa36663f-c4b8-4abc-af1e-94f1d5b2dade
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
| `monitoring.bicep` | Aanwezig, **niet auto-uitgerold** | Gratis (klassiek App Insights, < 5 GB/maand) |

### ⚠️ Kostenwaarschuwing: Log Analytics

Als je `monitoring.bicep` aanpast naar workspace-based Application Insights
(door een `workspaceResourceId` toe te voegen), wordt een Log Analytics workspace
aangemaakt. **De Legacy Free Tier voor Log Analytics is niet beschikbaar voor nieuwe
workspaces** (vervallen 1 juli 2022). Kosten: pay-as-you-go op verbruik.

Maatregel: `deployMonitoring` staat standaard op `false` in `main.parameters.json`.
Vereist expliciete `--parameters deployMonitoring=true` bij deployment.

## Kritieke constraint: .NET 9

```bicep
linuxFxVersion: 'DOTNET-ISOLATED|9.0'  // NOOIT wijzigen naar net10.0
```

Het Linux Consumption Plan ondersteunt .NET 10 niet. Zie CLAUDE.md voor het
upgradepad (vereist Flex Consumption Plan).

## CI/CD — GitHub Actions

De workflow `.github/workflows/infrastructure.yml` is alleen handmatig te triggeren
(`workflow_dispatch`). Geen automatische deploy bij push — dit is bewust om
onbedoelde infrastructuurwijzigingen te voorkomen.
