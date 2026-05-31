// ── Function App module ──────────────────────────────────────────────────────
// Beschrijft de bestaande Azure Function App (Consumption, Linux, .NET 9).
//
// KRITIEKE CONSTRAINT: linuxFxVersion MOET 'DOTNET-ISOLATED|9.0' zijn.
// Linux Consumption Plan ondersteunt .NET 10 NIET — wijziging leidt tot 503.
// Upgradepad: pas mogelijk via Flex Consumption Plan (betaald).

@description('Azure-regio voor alle resources in dit module')
param location string = resourceGroup().location

@description('Naam van de Function App')
param functionAppName string

@description('Naam van het App Service Plan (Consumption)')
param appServicePlanName string

@description('Naam van het Storage Account voor de Function App')
param storageAccountName string

@description('Application Insights connection string')
@secure()
param appInsightsConnectionString string = ''

@description('SQL connection string')
@secure()
param sqlConnectionString string = ''

@description('Storage account connection string voor AzureWebJobsStorage — beheer via GitHub secret')
@secure()
param azureWebJobsStorage string = ''

@description('Entra ID tenant ID voor Easy Auth (single-tenant) — via GitHub Variable AZURE_AD_TENANT_ID')
param tenantId string = ''

@description('Entra ID client ID van de App Registration — via GitHub Variable AZURE_AD_CLIENT_ID')
param clientId string = ''

// ── Bestaande resources ophalen ──────────────────────────────────────────────

resource appServicePlan 'Microsoft.Web/serverFarms@2023-01-01' = {
  name: appServicePlanName
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
  }
}

// ── Function App ─────────────────────────────────────────────────────────────

resource functionApp 'Microsoft.Web/sites@2023-01-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  properties: {
    serverFarmId: appServicePlan.id
    siteConfig: {
      // KRITIEK: linuxFxVersion mag NOOIT worden gewijzigd naar net10.0
      // Zie CLAUDE.md architectuurregels — Linux Consumption Plan constraint
      linuxFxVersion: 'DOTNET-ISOLATED|9.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'AzureWebJobsStorage'
          // Gebruik de azureWebJobsStorage parameter — nooit inline bouwen met AccountKey
          value: empty(azureWebJobsStorage) ? '' : azureWebJobsStorage
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'SqlConnectionString'
          value: sqlConnectionString
        }
      ]
    }
    httpsOnly: true
  }
}

// ── Easy Auth (authsettingsV2) ────────────────────────────────────────────────
// AllowAnonymous: unauthenticated requests mogen door; EasyAuthHelper doet
// endpoint-level role-checks. Niet Return401 — dat breekt health + timer endpoints.
// tenantId/clientId zijn leeg als vars niet geconfigureerd zijn — resource wordt
// dan overgeslagen (Bicep condition). (#418)

resource functionAppAuthSettings 'Microsoft.Web/sites/config@2023-01-01' = if (!empty(tenantId) && !empty(clientId)) {
  name: 'authsettingsV2'
  parent: functionApp
  properties: {
    globalValidation: {
      requireAuthentication: false
      unauthenticatedClientAction: 'AllowAnonymous'
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: clientId
          openIdIssuer: 'https://sts.windows.net/${tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            'api://${clientId}'
          ]
        }
        isAutoProvisioned: false
      }
    }
    login: {
      tokenStore: {
        enabled: false
      }
    }
    platform: {
      enabled: true
      runtimeVersion: '~1'
    }
  }
}

output functionAppId string = functionApp.id
output functionAppDefaultHostname string = functionApp.properties.defaultHostName
