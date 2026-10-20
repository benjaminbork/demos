// Azure Cosmos DB for NoSQL – the only database.
// Serverless by default (pay per request, ~0 € when idle). Optionally the free tier (one per subscription).

param location string
param accountName string
param databaseName string = 'audioblog'
@description('Use the Cosmos DB free tier (1000 RU/s + 25 GB free, one account per subscription) instead of serverless.')
param useFreeTier bool = false
@description('Principals that get the "Cosmos DB Built-in Data Contributor" data-plane role.')
param dataContributorPrincipalIds array

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: accountName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    enableFreeTier: useFreeTier
    capabilities: useFreeTier ? [] : [ { name: 'EnableServerless' } ]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    locations: [ { locationName: location, failoverPriority: 0, isZoneRedundant: false } ]
    disableLocalAuth: true // Entra ID only, no account keys
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: 'Enabled'
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: account
  name: databaseName
  properties: {
    resource: { id: databaseName }
    // Free tier: share the free 1000 RU/s across all containers.
    options: useFreeTier ? { throughput: 1000 } : {}
  }
}

// One container for all article data. Hierarchical partition key [userId, articleId]:
// - all items of one article (status, source text, script) share a logical partition → transactional batch
// - all articles of one user share the userId prefix → "list my articles" is a targeted, single-prefix query
// - no 20 GB logical partition limit per user, because the full key includes articleId
resource articles 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'articles'
  properties: {
    resource: {
      id: 'articles'
      partitionKey: {
        paths: [ '/userId', '/articleId' ]
        kind: 'MultiHash'
        version: 2
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        // Index only what is queried; the large text fields are never indexed (cheaper writes).
        includedPaths: [
          { path: '/userId/?' }
          { path: '/type/?' }
          { path: '/createdAt/?' }
          { path: '/dueAt/?' }
        ]
        excludedPaths: [ { path: '/*' }, { path: '/"_etag"/?' } ]
        compositeIndexes: [
          // list:      WHERE userId = @u AND type = 'article' ORDER BY userId, type, createdAt DESC
          // Must stay identical to CosmosContainers.ArticlesContainerProperties (used for the local emulator).
          [
            { path: '/userId', order: 'ascending' }
            { path: '/type', order: 'ascending' }
            { path: '/createdAt', order: 'descending' }
          ]
          // scheduler: WHERE type = 'article' AND dueAt <= @now
          [
            { path: '/type', order: 'ascending' }
            { path: '/dueAt', order: 'ascending' }
          ]
        ]
      }
    }
  }
}

// Checkpoints of the change feed processor (required by the SDK, partitioned by /id).
resource leases 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'leases'
  properties: {
    resource: {
      id: 'leases'
      partitionKey: { paths: [ '/id' ], kind: 'Hash' }
    }
  }
}

var dataContributorRoleId = '${account.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'

resource roleAssignments 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = [for principalId in dataContributorPrincipalIds: {
  parent: account
  name: guid(account.id, principalId, dataContributorRoleId)
  properties: {
    roleDefinitionId: dataContributorRoleId
    principalId: principalId
    scope: account.id
  }
}]

output endpoint string = account.properties.documentEndpoint
output accountName string = account.name
