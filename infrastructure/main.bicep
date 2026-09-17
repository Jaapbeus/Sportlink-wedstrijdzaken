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

@description('Naam van de Function App — bijv. func-<clubcode>-sportlink')
param functionAppName string

@description('Naam van het App Service Plan')
param appServicePlanName string = 'WestEuropeLinuxDynamicPlan'

@description('Naam van het Storage Account — bijv. st<clubcode>sportlink (max 24 tekens, lowercase)')
param storageAccountName string

@description('Naam van de Static Web App — bijv. swa-<clubcode>-sportlink')
param staticWebAppName string

@description('Naam van de Application Insights resource — bijv. ai-<clubcode>-sportlink')
param appInsightsName string

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

@description('Entra ID tenant ID voor Easy Auth — via GitHub Variable AZURE_AD_TENANT_ID. Leeg = Easy Auth niet declaratief geconfigureerd.')
param tenantId string = ''

@description('Entra ID client ID van de App Registration — via GitHub Variable AZURE_AD_CLIENT_ID. Leeg = Easy Auth niet declaratief geconfigureerd.')
param clientId string = ''

// ── Flex Consumption-migratie (epic #1063, FLEX-04) ─────────────────────────────
// deployFlexApp staat standaard op false: dit is de FLEX-05-kostengate. Alleen op true zetten
// (via een los --parameters deployFlexApp=true, nooit in dit bestand) na expliciete
// bevestiging van de eigenaar, en alleen voor een echte `deployment group create`. Voor
// `what-if` (FLEX-04) mag de conditie tijdelijk true zijn — what-if wijzigt niets.
@description('Nieuwe Flex Consumption Function App uitrollen? Alleen true na expliciete kostengoedkeuring eigenaar (FLEX-05).')
param deployFlexApp bool = false

@description('Naam van de NIEUWE Flex Consumption Function App — bijv. func-<clubcode>-sportlink-flex')
param flexFunctionAppName string = ''

@description('Naam van het NIEUWE App Service Plan (Flex Consumption, SKU FC1)')
param flexAppServicePlanName string = ''

@description('Instance-geheugen (MB) voor de Flex-app — zie modules/function-app-flex.bicep voor de onderbouwing van de default (2048)')
@allowed([
  512
  2048
  4096
])
param flexInstanceMemoryMB int = 2048

@description('Maximaal aantal on-demand instances voor de Flex-app — bewust laag, Flex kent geen automatische kostenrem')
@minValue(1)
@maxValue(1000)
param flexMaximumInstanceCount int = 5

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
    tenantId: tenantId
    clientId: clientId
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

// Flex Consumption-app (epic #1063) — alleen uitgerold als deployFlexApp=true (FLEX-05-kostengate).
// De bestaande functionApp-module hierboven blijft ongewijzigd: in-place migratie naar Flex
// bestaat niet, dus dit is een aparte, nieuwe resource naast de bestaande app.
module functionAppFlex 'modules/function-app-flex.bicep' = if (deployFlexApp) {
  name: 'function-app-flex'
  params: {
    location: location
    flexFunctionAppName: flexFunctionAppName
    flexAppServicePlanName: flexAppServicePlanName
    storageAccountName: storageAccountName
    instanceMemoryMB: flexInstanceMemoryMB
    maximumInstanceCount: flexMaximumInstanceCount
    appInsightsConnectionString: appInsightsConnectionString
    sqlConnectionString: sqlConnectionString
    tenantId: tenantId
    clientId: clientId
  }
}

// ── Outputs ──────────────────────────────────────────────────────────────────

output functionAppUrl string = 'https://${functionApp.outputs.functionAppDefaultHostname}'
output staticWebAppUrl string = 'https://${staticWebApp.outputs.staticWebAppDefaultHostname}'
var flexFunctionAppHostname = functionAppFlex.?outputs.?flexFunctionAppDefaultHostname
output flexFunctionAppUrl string = flexFunctionAppHostname == null ? '' : 'https://${flexFunctionAppHostname}'
