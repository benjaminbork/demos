using System.Net;
using AudioBlog.Core.Domain;
using AudioBlog.Core.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace AudioBlog.Core.Data;

public sealed class CosmosArticleStore(CosmosContainers containers, ILogger<CosmosArticleStore> logger) : IArticleStore
{
    private Container Articles => containers.Articles;

    /// <summary>Full hierarchical partition key: [userId, articleId].</summary>
    public static PartitionKey PartitionKeyFor(ArticleRef article) =>
        new PartitionKeyBuilder().Add(article.UserId).Add(article.ArticleId).Build();

    /// <summary>Prefix of the hierarchical key: all articles of one user, still a targeted (non fan-out) query.</summary>
    public static PartitionKey UserPrefix(string userId) => new PartitionKeyBuilder().Add(userId).Build();

    public async Task<ArticleDocument?> GetArticleAsync(ArticleRef article, CancellationToken ct) =>
        await PointReadAsync<ArticleDocument>(article.ArticleId, article, ct);

    public async Task<(ArticleDocument Article, bool Created)> CreateArticleAsync(ArticleDocument article, CancellationToken ct)
    {
        try
        {
            var response = await Articles.CreateItemAsync(article, PartitionKeyFor(article.Ref), cancellationToken: ct);
            Track("create article", isPoint: true, response.RequestCharge, response.Diagnostics);
            return (response.Resource, true);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            Track("create article (conflict)", isPoint: true, ex.RequestCharge, ex.Diagnostics);
            var existing = await GetArticleAsync(article.Ref, ct)
                ?? throw new InvalidOperationException($"Article {article.Ref} reported as existing but could not be read.");
            return (existing, false);
        }
    }

    public async Task<string> ReplaceArticleAsync(ArticleDocument article, string ifMatchETag, CancellationToken ct)
    {
        EnsureWritable(article);
        try
        {
            var response = await Articles.ReplaceItemAsync(
                article,
                article.Id,
                PartitionKeyFor(article.Ref),
                new ItemRequestOptions { IfMatchEtag = ifMatchETag, EnableContentResponseOnWrite = false },
                ct);
            Track("replace article", isPoint: true, response.RequestCharge, response.Diagnostics);
            return response.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            throw new ConcurrencyException($"Article {article.Ref} was changed by someone else ({ex.StatusCode}).", ex);
        }
    }

    public async Task<string> CommitStepAsync<TOutput>(ArticleDocument article, string ifMatchETag, TOutput output, CancellationToken ct)
        where TOutput : ContentDocument
    {
        if (output.UserId != article.UserId || output.ArticleId != article.ArticleId)
            throw new ArgumentException("A step output must live in its article's logical partition.", nameof(output));
        EnsureWritable(article);

        var batch = Articles.CreateTransactionalBatch(PartitionKeyFor(article.Ref))
            .UpsertItem(output, new TransactionalBatchItemRequestOptions { EnableContentResponseOnWrite = false })
            .ReplaceItem(article.Id, article, new TransactionalBatchItemRequestOptions { IfMatchEtag = ifMatchETag, EnableContentResponseOnWrite = false });

        using var response = await batch.ExecuteAsync(ct);
        Track($"commit {output.Type}", isPoint: false, response.RequestCharge, response.Diagnostics);
        if (response.IsSuccessStatusCode)
            return response[1].ETag;

        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound
            || response.Any(r => r.StatusCode is HttpStatusCode.PreconditionFailed))
            throw new ConcurrencyException($"Article {article.Ref} was changed by someone else; step output not committed.");

        logger.LogWarning("Transactional batch for {Article} failed with {Status}: {Diagnostics}", article.Ref, response.StatusCode, response.Diagnostics);
        throw new CosmosBatchException(response.StatusCode, response.ErrorMessage);
    }

    public Task<SourceDocument?> GetSourceAsync(ArticleRef article, CancellationToken ct) =>
        PointReadAsync<SourceDocument>(SourceDocument.IdFor(article.ArticleId), article, ct);

    public Task<ScriptDocument?> GetScriptAsync(ArticleRef article, CancellationToken ct) =>
        PointReadAsync<ScriptDocument>(ScriptDocument.IdFor(article.ArticleId), article, ct);

    public async Task<ArticlePage> ListArticlesAsync(string userId, int pageSize, string? continuationToken, CancellationToken ct)
    {
        // userId and type are constant after the filter, so leading the ORDER BY with them doesn't change the
        // order. It lets the query use the composite index (userId ASC, type ASC, createdAt DESC) instead of
        // the single-field createdAt index plus a filter.
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.userId = @userId AND c.type = @type ORDER BY c.userId ASC, c.type ASC, c.createdAt DESC")
            .WithParameter("@userId", userId)
            .WithParameter("@type", ArticleDocument.ItemType);

        using var iterator = Articles.GetItemQueryIterator<ArticleDocument>(query, continuationToken, new QueryRequestOptions
        {
            PartitionKey = UserPrefix(userId),
            MaxItemCount = pageSize,
        });

        var page = await iterator.ReadNextAsync(ct);
        Track("list articles", isPoint: false, page.RequestCharge, page.Diagnostics);
        return new ArticlePage(page.Resource.ToList(), page.ContinuationToken);
    }

    public async Task<IReadOnlyList<ArticleRef>> QueryDueAsync(DateTimeOffset now, int maxItems, CancellationToken ct)
    {
        // Cross-partition on purpose: the scheduler serves every user. It is a cheap, index-served range
        // query (composite index type + dueAt) and finished articles have no dueAt at all.
        var query = new QueryDefinition(
                "SELECT TOP @max c.userId, c.articleId FROM c WHERE c.type = @type AND c.dueAt <= @now")
            .WithParameter("@max", maxItems)
            .WithParameter("@type", ArticleDocument.ItemType)
            .WithParameter("@now", UtcTimestampConverter.ToCosmosString(now));

        var result = new List<ArticleRef>();
        using var iterator = Articles.GetItemQueryIterator<DueRow>(query, requestOptions: new QueryRequestOptions { MaxItemCount = maxItems });
        while (iterator.HasMoreResults && result.Count < maxItems)
        {
            var page = await iterator.ReadNextAsync(ct);
            Track("query due articles", isPoint: false, page.RequestCharge, page.Diagnostics);
            result.AddRange(page.Take(maxItems - result.Count).Select(row => new ArticleRef(row.UserId, row.ArticleId)));
        }
        return result;
    }

    public async Task<bool> HasPendingAsync(CancellationToken ct)
    {
        // Existence check, not a COUNT: stops at the first pending article instead of counting all of them.
        var query = new QueryDefinition("SELECT TOP 1 VALUE 1 FROM c WHERE c.type = @type AND IS_DEFINED(c.dueAt)")
            .WithParameter("@type", ArticleDocument.ItemType);
        using var iterator = Articles.GetItemQueryIterator<int>(query, requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            Track("check pending articles", isPoint: false, page.RequestCharge, page.Diagnostics);
            if (page.Count > 0)
                return true;
        }
        return false;
    }

    private async Task<T?> PointReadAsync<T>(string id, ArticleRef article, CancellationToken ct) where T : class
    {
        try
        {
            var response = await Articles.ReadItemAsync<T>(id, PartitionKeyFor(article), cancellationToken: ct);
            Track($"read {typeof(T).Name}", isPoint: true, response.RequestCharge, response.Diagnostics);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Track($"read {typeof(T).Name} (not found)", isPoint: true, ex.RequestCharge, ex.Diagnostics);
            return null;
        }
    }

    /// <summary>
    /// An item written by a newer app version (e.g. during a rolling deployment) may carry fields this version
    /// doesn't know; replacing it would silently drop them.
    /// </summary>
    private static void EnsureWritable(ArticleDocument article)
    {
        if (article.SchemaVersion > ArticleDocument.CurrentSchemaVersion)
            throw new ConcurrencyException(
                $"Article {article.Ref} has schema version {article.SchemaVersion}, newer than this app ({ArticleDocument.CurrentSchemaVersion}); not overwriting it.");
    }

    /// <summary>Logs RU charge and latency of every request; slow or expensive ones at warning level with full diagnostics.</summary>
    private void Track(string operation, bool isPoint, double requestCharge, CosmosDiagnostics? diagnostics)
    {
        if (diagnostics is null)
            return;
        var options = containers.Options;
        var elapsed = diagnostics.GetClientElapsedTime();
        var threshold = isPoint ? options.SlowPointOperationThreshold : options.SlowQueryThreshold;
        if (elapsed > threshold || requestCharge > options.HighRequestChargeThreshold)
            logger.LogWarning("Cosmos DB {Operation} took {ElapsedMs:F0} ms and {RequestCharge:F2} RU: {Diagnostics}",
                operation, elapsed.TotalMilliseconds, requestCharge, diagnostics);
        else
            logger.LogDebug("Cosmos DB {Operation}: {ElapsedMs:F0} ms, {RequestCharge:F2} RU",
                operation, elapsed.TotalMilliseconds, requestCharge);
    }

    private sealed record DueRow(string UserId, string ArticleId);
}

public sealed class CosmosBatchException(HttpStatusCode status, string? message)
    : Exception($"Transactional batch failed with {(int)status}: {message}")
{
    public HttpStatusCode Status { get; } = status;
}
