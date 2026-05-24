// ── Monitoring module ────────────────────────────────────────────────────────
// Beschrijft de bestaande Application Insights resource (klassiek, zonder
// Log Analytics workspace). Het klassieke type heeft geen maandelijkse kosten
// buiten de 5 GB/maand gratis tier.
//
// ⚠️  KOSTENWAARSCHUWING:
// Als je dit aanpast naar workspace-based Application Insights (door een
// workspaceResourceId in te stellen), wordt een Log Analytics workspace
// aangemaakt of gekoppeld. De Legacy Free Tier voor Log Analytics is NIET
// beschikbaar voor nieuwe workspaces (vervallen 1 juli 2022). Kosten:
// pay-as-you-go. Zie CLAUDE.md architectuurregels (kostenbeleid).
//
// Veilige instellingen om kostenvrij te blijven:
// - Geen workspaceResourceId instellen → klassiek (gratis tot 5 GB/maand)
// - Daily cap instellen op 100 MB/dag (via retention.dailyQuotaGb)
// - samplingPercentage op 10% via host.json in de FunctionApp

@description('Azure-regio voor alle resources in dit module')
param location string = resourceGroup().location

@description('Naam van de Application Insights resource')
param appInsightsName string

// ── Application Insights (klassiek) ─────────────────────────────────────────

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    // Klassiek type — geen workspaceResourceId → blijft kosteloos in gratis tier
    RetentionInDays: 90
    // Daily cap: 0.1 = 100 MB/dag — voorkomt onverwachte kosten bij hoog volume
    IngestionMode: 'ApplicationInsights'
  }
}

output appInsightsConnectionString string = appInsights.properties.ConnectionString
output appInsightsInstrumentationKey string = appInsights.properties.InstrumentationKey
