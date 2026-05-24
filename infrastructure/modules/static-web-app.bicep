// ── Static Web App module ────────────────────────────────────────────────────
// Beschrijft de bestaande Azure Static Web App (Free tier, Blazor WASM).
// Free tier: 0 kosten, 100 GB bandbreedte/maand, geen serverless functions nodig
// omdat de Function App de API levert.

@description('Azure-regio voor alle resources in dit module')
param location string = 'westeurope'

@description('Naam van de Static Web App')
param staticWebAppName string

@description('SKU naam — altijd Free voor dit project (betaald niet vereist)')
param skuName string = 'Free'

// ── Static Web App ───────────────────────────────────────────────────────────

resource staticWebApp 'Microsoft.Web/staticSites@2023-01-01' = {
  name: staticWebAppName
  location: location
  sku: {
    name: skuName
    tier: skuName
  }
  properties: {
    repositoryUrl: ''
    branch: ''
    buildProperties: {
      skipGithubActionWorkflowGeneration: true
    }
  }
}

output staticWebAppId string = staticWebApp.id
output staticWebAppDefaultHostname string = staticWebApp.properties.defaultHostname
