// ── Monitoring module ────────────────────────────────────────────────────────
// Maakt een Application Insights resource aan zonder expliciete
// workspaceResourceId. Klassieke (workspace-loze) Application Insights bestaat
// niet meer: Microsoft heeft dit type per februari 2024 uitgefaseerd en heeft
// bestaande en nieuw aangemaakte resources automatisch gemigreerd/omgezet naar
// workspace-based, met een door Azure beheerde Log Analytics-workspace
// eromheen. Het ontbreken van workspaceResourceId hieronder bepaalt dus niet
// meer of dit kostenvrij blijft — Azure koppelt sowieso een workspace.
// Empirisch bevestigd op de draaiende installatie: beide Application
// Insights-componenten rapporteren `IngestionMode: LogAnalytics`.
//
// ⚠️  KOSTENWAARSCHUWING:
// Ingestie en retentie lopen volledig via die (automatisch aangemaakte)
// Log Analytics-workspace. Er is geen Legacy Free Tier meer beschikbaar voor
// nieuwe workspaces (vervallen 1 juli 2022) — het enige gratis budget is de
// 5 GB/maand gratis data-allowance per billing account (gedeeld over alle
// workspaces in dat account). Zie CLAUDE.md architectuurregels (kostenbeleid).
//
// Daadwerkelijke beheersmaatregelen tegen onverwacht hoog volume:
// - samplingPercentage op 10% via host.json in de FunctionApp (beperkt volume
//   vóórdat het geïngest wordt — de enige hendel die in dít bestand ligt)
// - Een daily cap kan NIET via Bicep op de Application Insights-resource zelf
//   worden gezet (Microsoft Learn: "Currently, Azure doesn't provide a way to
//   set the daily cap for Application Insights with a Bicep template"). Een
//   cap zet je op de gekoppelde Log Analytics-workspace via
//   `Microsoft.OperationalInsights/workspaces` → `properties.workspaceCapping.
//   dailyQuotaGb` — die workspace-resource bestaat niet in dit bestand (hij
//   wordt automatisch door Azure beheerd), dus dit is vooralsnog alleen
//   handmatig in te stellen in de portal.

@description('Azure-regio voor alle resources in dit module')
param location string = resourceGroup().location

@description('Naam van de Application Insights resource')
param appInsightsName string

// ── Application Insights (workspace-based, automatisch beheerde workspace) ──

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    // 90 dagen retentie — workspace-based Application Insights biedt dit
    // kostenvrij (retentie zelf is niet het kostenbepalende ingestievolume).
    RetentionInDays: 90
    IngestionMode: 'ApplicationInsights'
  }
}

output appInsightsConnectionString string = appInsights.properties.ConnectionString
output appInsightsInstrumentationKey string = appInsights.properties.InstrumentationKey
