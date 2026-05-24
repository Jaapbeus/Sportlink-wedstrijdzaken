// ── Sportlink Wedstrijdzaken — Infrastructure as Code ───────────────────────
// Beschrijft de bestaande Azure-resources via Bicep.
// Incrementele aanpak: geen nieuwe resources aanmaken, alleen bestaande
// resources declaratief vastleggen zodat drift detecteerbaar is.
//
// Gebruik:
//   az deployment group what-if  --resource-group myAppGroup \
//     --template-file main.bicep --parameters main.parameters.json
//
//   # Alleen na expliciete goedkeuring:
//   az deployment group create   --resource-group myAppGroup \
//     --template-file main.bicep --parameters main.parameters.json
//
// Zie infrastructure/README.md voor volledig gebruik en kostenwaarschuwingen.

targetScope = 'resourceGroup'

// ── Parameters ───────────────────────────────────────────────────────────────

@description('Azure-regio — altijd westeurope voor dit project')
param location string = 'westeurope'

@description('Naam van de Function App')
param functionAppName string = 'func-vrc-sportlink'

@description('Naam van het App Service Plan')
param appServicePlanName string = 'WestEuropeLinuxDynamicPlan'

@description('Naam van het Storage Account')
param storageAccountName string = 'stvrcsportlink'

@description('Naam van de Static Web App')
param staticWebAppName string = 'swa-vrc-sportlink'

@description('Naam van de Application Insights resource')
param appInsightsName string = 'ai-vrc-sportlink'

@description('Application Insights connection string — beheer via GitHub secret APPLICATIONINSIGHTS_CONNECTION_STRING')
@secure()
param appInsightsConnectionString string = ''

@description('SQL connection string — beheer via GitHub secret AZURE_SQL_CONNECTION_STRING')
@secure()
param sqlConnectionString string = ''

@description('Storage account connection string — beheer via GitHub secret (of lees uit Azure Portal → Storage Account → Access keys)')
@secure()
param azureWebJobsStorage string = ''

@description('Monitoring deployen? Alleen instellen op true na expliciete kostengoedkeuring eigenaar.')
param deployMonitoring bool = false

// ── Modules ──────────────────────────────────────────────────────────────────

module functionApp 'modules/function-app.bicep' = {
  name: 'function-app'
  params: {
    location: location
    functionAppName: functionAppName
    appServicePlanName: appServicePlanName
    storageAccountName: storageAccountName
    appInsightsConnectionString: appInsightsConnectionString
    sqlConnectionString: sqlConnectionString
    azureWebJobsStorage: azureWebJobsStorage
  }
}

module staticWebApp 'modules/static-web-app.bicep' = {
  name: 'static-web-app'
  params: {
    location: location
    staticWebAppName: staticWebAppName
  }
}

// Monitoring wordt alleen uitgerold als deployMonitoring=true (expliciete goedkeuring vereist)
module monitoring 'modules/monitoring.bicep' = if (deployMonitoring) {
  name: 'monitoring'
  params: {
    location: location
    appInsightsName: appInsightsName
  }
}

// ── Outputs ──────────────────────────────────────────────────────────────────

output functionAppUrl string = 'https://${functionApp.outputs.functionAppDefaultHostname}'
output staticWebAppUrl string = 'https://${staticWebApp.outputs.staticWebAppDefaultHostname}'
