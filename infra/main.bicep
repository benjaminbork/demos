// Audio Blog – all Azure resources. Deployed by scripts/deploy.ps1 in two phases:
//   1. deployApps=false: everything except the container apps (so images can be pushed to the registry)
//   2. deployApps=true:  the container apps with the freshly built images
targetScope = 'resourceGroup'

@description('Region for most resources.')
param location string = resourceGroup().location

@description('Region for Azure OpenAI and Speech (model availability differs per region).')
param aiLocation string = 'swedencentral'

@description('Static Web Apps is only offered in a few regions.')
@allowed([ 'westeurope', 'eastus2', 'centralus', 'westus2', 'eastasia' ])
param staticWebAppLocation string = 'westeurope'

@description('Short name used as prefix for all resources.')
@maxLength(12)
param appName string = 'audioblog'

param deployApps bool = false
param apiImage string = ''
param workerImage string = ''

@description('Cosmos DB free tier instead of serverless (only one free-tier account per subscription).')
param cosmosFreeTier bool = false

param chatModelName string = 'gpt-5.4-mini'
param chatModelVersion string = '2026-03-17'
param chatCapacity int = 50
@allowed([ 'S0', 'F0' ])
param speechSku string = 'S0'

@description('Let the worker scale to zero when idle (cheaper; first article after a pause starts ~30 s later).')
param workerScaleToZero bool = false

@description('Optional: object id of your own user, to run the app locally against the Azure resources (az login).')
param developerPrincipalId string = ''

var suffix = uniqueString(resourceGroup().id)
var names = {
  apiIdentity: 'id-${appName}-api'
  workerIdentity: 'id-${appName}-worker'
  registry: 'cr${appName}${suffix}'
  cosmos: 'cosmos-${appName}-${suffix}'
  storage: take('st${appName}${suffix}', 24)
  openAi: 'oai-${appName}-${suffix}'
  speech: 'spch-${appName}-${suffix}'
  logs: 'log-${appName}-${suffix}'
  environment: 'cae-${appName}'
  api: 'ca-${appName}-api'
  worker: 'ca-${appName}-worker'
  web: 'swa-${appName}-${suffix}'
}

// ---------------------------------------------------------------- identities

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.apiIdentity
  location: location
}

resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.workerIdentity
  location: location
}

var developer = empty(developerPrincipalId) ? [] : [ { id: developerPrincipalId, type: 'User' } ]
var apiPrincipal = { id: apiIdentity.properties.principalId, type: 'ServicePrincipal' }
var workerPrincipal = { id: workerIdentity.properties.principalId, type: 'ServicePrincipal' }

// ---------------------------------------------------------------- registry

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: names.registry
  location: location
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

var acrPull = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource apiAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, apiIdentity.id, acrPull)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPull)
    principalId: apiPrincipal.id
    principalType: apiPrincipal.type
  }
}

resource workerAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, workerIdentity.id, acrPull)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPull)
    principalId: workerPrincipal.id
    principalType: workerPrincipal.type
  }
}

// ---------------------------------------------------------------- data + AI

module cosmos 'modules/cosmos.bicep' = {
  name: 'cosmos'
  params: {
    location: location
    accountName: names.cosmos
    useFreeTier: cosmosFreeTier
    // API and worker both read and write article items (create, retry, change voice / processing).
    dataContributorPrincipalIds: concat([ apiPrincipal.id, workerPrincipal.id ], map(developer, d => d.id))
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    accountName: names.storage
    contributorPrincipals: concat([ workerPrincipal ], developer)
    readerPrincipals: [ apiPrincipal ]
  }
}

module ai 'modules/ai.bicep' = {
  name: 'ai'
  params: {
    location: aiLocation
    openAiName: names.openAi
    speechName: names.speech
    chatDeploymentName: chatModelName
    chatModelName: chatModelName
    chatModelVersion: chatModelVersion
    chatCapacity: chatCapacity
    speechSku: speechSku
    userPrincipals: concat([ workerPrincipal ], developer)
  }
}

// ---------------------------------------------------------------- frontend

resource web 'Microsoft.Web/staticSites@2023-12-01' = {
  name: names.web
  location: staticWebAppLocation
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

// ---------------------------------------------------------------- compute

module apps 'modules/apps.bicep' = if (deployApps) {
  name: 'apps'
  dependsOn: [ apiAcrPull, workerAcrPull ]
  params: {
    location: location
    environmentName: names.environment
    logAnalyticsName: names.logs
    apiName: names.api
    workerName: names.worker
    apiImage: apiImage
    workerImage: workerImage
    registryServer: registry.properties.loginServer
    apiIdentityId: apiIdentity.id
    apiIdentityClientId: apiIdentity.properties.clientId
    workerIdentityId: workerIdentity.id
    workerIdentityClientId: workerIdentity.properties.clientId
    allowedOrigins: [ 'https://${web.properties.defaultHostname}' ]
    cosmosEndpoint: cosmos.outputs.endpoint
    blobEndpoint: storage.outputs.blobEndpoint
    openAiEndpoint: ai.outputs.openAiEndpoint
    chatDeploymentName: ai.outputs.chatDeploymentName
    speechRegion: ai.outputs.speechRegion
    speechResourceId: ai.outputs.speechResourceId
    workerScaleToZero: workerScaleToZero
  }
}

output registryName string = registry.name
output registryServer string = registry.properties.loginServer
output staticWebAppName string = web.name
output webUrl string = 'https://${web.properties.defaultHostname}'
output apiUrl string = deployApps ? apps!.outputs.apiUrl : ''
output cosmosEndpoint string = cosmos.outputs.endpoint
output blobEndpoint string = storage.outputs.blobEndpoint
output openAiEndpoint string = ai.outputs.openAiEndpoint
output chatDeploymentName string = ai.outputs.chatDeploymentName
output speechRegion string = ai.outputs.speechRegion
output speechResourceId string = ai.outputs.speechResourceId
