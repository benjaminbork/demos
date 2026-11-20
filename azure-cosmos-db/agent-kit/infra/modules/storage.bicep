// Blob Storage for the MP3 files. Private container, shared keys disabled:
// the API signs short-lived read URLs with a *user delegation* key obtained via its managed identity.

param location string
param accountName string
param containerName string = 'audio'
@description('Principals ({ id, type }) that write audio (worker).')
param contributorPrincipals array
@description('Principals ({ id, type }) that only read and sign SAS URLs (API).')
param readerPrincipals array

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: accountName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    defaultToOAuthAuthentication: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: containerName
  properties: { publicAccess: 'None' }
}

var blobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var blobDataReader = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1' // includes generateUserDelegationKey

resource contributors 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principal in contributorPrincipals: {
  scope: storage
  name: guid(storage.id, principal.id, blobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributor)
    principalId: principal.id
    principalType: principal.type
  }
}]

resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principal in readerPrincipals: {
  scope: storage
  name: guid(storage.id, principal.id, blobDataReader)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataReader)
    principalId: principal.id
    principalType: principal.type
  }
}]

output blobEndpoint string = storage.properties.primaryEndpoints.blob
output accountName string = storage.name
