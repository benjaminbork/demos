// Cosmos DB with MULTIPLE PHYSICAL PARTITIONS for the RU tests (scripts/ru-test.cs --containers p1,p5,p10).
//
// A serverless container only adds a physical partition per 50 GB of data, so this uses provisioned (manual)
// throughput instead: a new container starts with RU/s ÷ 6,000 physical partitions, created immediately.
//   p1  =  6,000 RU/s →  1 physical partition   (baseline)
//   p5  = 30,000 RU/s →  5 physical partitions
//   p10 = 60,000 RU/s → 10 physical partitions
//
// COST: billed per started hour at ~$0.008 per 100 RU/s → 96,000 RU/s ≈ $7.70 per hour.
// Partitions never merge and the minimum RU/s stays high, so DELETE THE RESOURCE GROUP right after the test:
//   az group delete --name rg-audioblog-partitions --yes
//
//   az group create -n rg-audioblog-partitions -l westeurope
//   az deployment group create -g rg-audioblog-partitions -f infra/cosmos-partitions.bicep \
//     -p developerPrincipalId=$(az ad signed-in-user show --query id -o tsv)
targetScope = 'resourceGroup'

import { articlesContainerResource } from 'modules/articles-container.bicep'

param location string = resourceGroup().location
@maxLength(12)
param appName string = 'audioblog'
@description('Object id of your user (az ad signed-in-user show). Gets data-plane access (Entra ID, no keys).')
param developerPrincipalId string

@description('Test containers and their manual throughput. Physical partitions at creation = throughput / 6,000.')
param layouts array = [
  { name: 'p1', throughput: 6000 }
  { name: 'p5', throughput: 30000 }
  { name: 'p10', throughput: 60000 }
]

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: 'cosmos-${appName}-pt-${uniqueString(resourceGroup().id)}'
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard' // provisioned throughput, not serverless
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    locations: [ { locationName: location, failoverPriority: 0, isZoneRedundant: false } ]
    disableLocalAuth: true
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: 'Enabled'
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: account
  name: 'rutest'
  properties: {
    resource: { id: 'rutest' }
  }
}

resource containers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for layout in layouts: {
  parent: database
  name: layout.name
  properties: {
    resource: articlesContainerResource(layout.name)
    options: { throughput: layout.throughput }
  }
}]

var dataContributorRoleId = '${account.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'

resource developerAccess 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: account
  name: guid(account.id, developerPrincipalId, dataContributorRoleId)
  properties: {
    roleDefinitionId: dataContributorRoleId
    principalId: developerPrincipalId
    scope: account.id
  }
}

output cosmosEndpoint string = account.properties.documentEndpoint
output accountName string = account.name
