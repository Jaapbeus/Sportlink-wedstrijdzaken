// ── Function App module — Flex Consumption (epic #1063 / FLEX-04) ───────────────
// Beschrijft de NIEUWE Function App op het Flex Consumption-plan.
//
// In-place migratie van Linux Consumption naar Flex bestaat niet (bevestigd via Microsoft Learn,
// zie #1063/#1064): dit is dus een aparte resource naast de bestaande app in modules/function-app.bicep,
// nooit een wijziging daarvan. De bestaande module blijft ongewijzigd de live productie-app
// beschrijven totdat de cutover (FLEX-09) is afgerond en FLEX-13 de oude resources opruimt.
//
// Bewust nog net9.0 — de bump naar net10.0 gebeurt in FLEX-10/FLEX-11, ná de plan-migratie
// (volgorde-onderbouwing: zie epic #1063 "Gekozen volgorde: eerst het plan, dan het framework").
//
// Instance memory en maximumInstanceCount zijn GEEN aannames maar gemeten/onderbouwde keuzes:
// - instanceMemoryMB default 2048 — FLEX-02 heeft empirisch aangetoond dat 512 MB op alle 31
//   gemeten dagen wordt overschreden (gemiddeld 546 MB, piek 1.205 MB). 2048 MB verbruikt
//   62–77% van het gratis maandtegoed (100.000 GB-s) — geen comfortmarge, maar wel gratis.
// - maximumInstanceCount default 5, NIET de platform-default van 100 — Flex kent geen
//   automatische kostenrem (geen spending limit op Pay-As-You-Go, budgetten zijn alleen
//   meldingen). Bij de default van 100 is het theoretische maandmaximum ~$13.663; bij 5 blijft
//   dat begrensd. Zie FLEX-02 vervolgonderzoek (comment op #1065, 2026-09-12).

@description('Azure-regio voor alle resources in dit module')
param location string = resourceGroup().location

@description('Naam van de NIEUWE Flex Consumption Function App — bijv. func-<clubcode>-sportlink-flex')
param flexFunctionAppName string

@description('Naam van het NIEUWE App Service Plan (Flex Consumption, SKU FC1)')
param flexAppServicePlanName string

@description('Naam van het BESTAANDE Storage Account — wordt hergebruikt, niet opnieuw aangemaakt')
param storageAccountName string

@description('Naam van de blob-container voor het Flex-deploymentpakket (moet bestaan vóór de eerste deploy)')
param deploymentContainerName string = 'app-package-sportlink-flex'

@description('Instance-geheugen in MB — toegestane waarden 512, 2048, 4096. Zie moduleheader voor de onderbouwing van de default.')
@allowed([
  512
  2048
  4096
])
param instanceMemoryMB int = 2048

@description('Maximaal aantal on-demand instances — bewust laag, want Flex kent geen kostenrem. Nooit de platform-default van 100 overnemen zonder herbeoordeling van de kostengate.')
@minValue(1)
@maxValue(1000)
param maximumInstanceCount int = 5

@description('Application Insights connection string')
@secure()
param appInsightsConnectionString string = ''

@description('SQL connection string')
@secure()
param sqlConnectionString string = ''

@description('Entra ID tenant ID voor Easy Auth (single-tenant) — via GitHub Variable AZURE_AD_TENANT_ID')
param tenantId string = ''

@description('Entra ID client ID van de App Registration — via GitHub Variable AZURE_AD_CLIENT_ID')
param clientId string = ''

// ── Bestaand storage account hergebruiken — NIET opnieuw declareren ────────────
// 'existing' voorkomt een dubbele resource-declaratie van dezelfde storageAccountName als in
// modules/function-app.bicep. Alleen de nieuwe deployment-container wordt hier toegevoegd.

resource existingStorageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: storageAccountName
}

resource existingBlobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' existing = {
  name: 'default'
  parent: existingStorageAccount
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = {
  name: deploymentContainerName
  parent: existingBlobServices
  properties: {
    publicAccess: 'None'
  }
}

// ── App Service Plan — Flex Consumption (FC1) ───────────────────────────────────

resource flexAppServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: flexAppServicePlanName
  location: location
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    reserved: true
  }
}

// ── Function App ─────────────────────────────────────────────────────────────
// System-assigned managed identity i.p.v. een storage-connection-string-secret voor
// AzureWebJobsStorage — CISO-verbetering t.o.v. de Consumption-app, mogelijk gemaakt doordat
// deze app toch al opnieuw moet worden aangemaakt (in-place migratie bestaat niet).

resource flexFunctionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: flexFunctionAppName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: flexAppServicePlan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${existingStorageAccount.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
        // alwaysReady bewust niet gezet: bij always-ready instances vervalt het gratis tegoed
        // volledig (zie epic #1063). Leeg = geen always-ready instances.
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '9.0' // bewust nog 9.0 — zie moduleheader; bump naar 10.0 in FLEX-10/FLEX-11
      }
    }
  }
}

resource flexFunctionAppSettings 'Microsoft.Web/sites/config@2023-01-01' = {
  name: 'appsettings'
  parent: flexFunctionApp
  properties: {
    AzureWebJobsStorage__accountName: existingStorageAccount.name
    AzureWebJobsStorage__credential: 'managedidentity'
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsightsConnectionString
    SqlConnectionString: sqlConnectionString
  }
}

// ── Rechten voor de managed identity op het gedeelde storage account ───────────
// Nodig voor zowel de host-status (AzureWebJobsStorage) als het ophalen van het
// deploymentpakket uit de blob-container.

var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'

resource roleAssignmentBlobDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(existingStorageAccount.id, flexFunctionApp.id, 'Storage Blob Data Owner')
  scope: existingStorageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalId: flexFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource roleAssignmentQueueDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(existingStorageAccount.id, flexFunctionApp.id, 'Storage Queue Data Contributor')
  scope: existingStorageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: flexFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource roleAssignmentTableDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(existingStorageAccount.id, flexFunctionApp.id, 'Storage Table Data Contributor')
  scope: existingStorageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: flexFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Easy Auth (authsettingsV2) ────────────────────────────────────────────────
// Zelfde resource-vorm als de Consumption-app (modules/function-app.bicep) — Flex-compatibel,
// want authsettingsV2 hangt onder Microsoft.Web/sites/config ongeacht het hostingplan.
// FLEX-04 raakt alleen de vorm; de inhoudelijke herconfiguratie (registration, 3-user-test)
// gebeurt in FLEX-06.

resource flexFunctionAppAuthSettings 'Microsoft.Web/sites/config@2023-01-01' = if (!empty(tenantId) && !empty(clientId)) {
  name: 'authsettingsV2'
  parent: flexFunctionApp
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

output flexFunctionAppId string = flexFunctionApp.id
output flexFunctionAppDefaultHostname string = flexFunctionApp.properties.defaultHostName
output flexFunctionAppPrincipalId string = flexFunctionApp.identity.principalId
