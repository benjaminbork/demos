// Definition of an "articles"-shaped container: hierarchical partition key + indexing policy.
// Used by the RU test containers (cosmos-partitions.bicep). Must stay identical to the app's container in
// modules/cosmos.bicep, so the RU tests measure exactly the production index.

@export()
@description('The resource block of an articles-shaped container with the given id.')
func articlesContainerResource(id string) object => {
  id: id
  // Hierarchical partition key [userId, articleId]:
  // - all items of one article (status, source text, script) share a logical partition → transactional batch
  // - all articles of one user share the userId prefix → "list my articles" is a targeted, single-prefix query
  // - no 20 GB logical partition limit per user, because the full key includes articleId
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
