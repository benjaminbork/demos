using AudioBlog.Core.Domain;

namespace AudioBlog.Core.Data;

/// <summary>
/// Thrown when an optimistic-concurrency write (If-Match ETag) loses against another writer, or when the
/// stored item was written by a newer app version (overwriting it would drop fields this version doesn't know).
/// </summary>
public sealed class ConcurrencyException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed record ArticlePage(IReadOnlyList<ArticleDocument> Items, string? ContinuationToken);

public interface IArticleStore
{
    Task<ArticleDocument?> GetArticleAsync(ArticleRef article, CancellationToken ct);

    /// <summary>Creates the article. If an article with this id already exists it is returned instead (idempotent create).</summary>
    Task<(ArticleDocument Article, bool Created)> CreateArticleAsync(ArticleDocument article, CancellationToken ct);

    /// <summary>Replaces the article if its ETag still matches; returns the new ETag. Throws <see cref="ConcurrencyException"/> otherwise.</summary>
    Task<string> ReplaceArticleAsync(ArticleDocument article, string ifMatchETag, CancellationToken ct);

    /// <summary>
    /// Atomically upserts a step output and replaces the article (ETag-checked) in one transactional batch.
    /// Either both are written or neither. Returns the article's new ETag.
    /// </summary>
    Task<string> CommitStepAsync<TOutput>(ArticleDocument article, string ifMatchETag, TOutput output, CancellationToken ct)
        where TOutput : ContentDocument;

    Task<SourceDocument?> GetSourceAsync(ArticleRef article, CancellationToken ct);
    Task<ScriptDocument?> GetScriptAsync(ArticleRef article, CancellationToken ct);

    /// <summary>Newest first, single-user partition prefix query.</summary>
    Task<ArticlePage> ListArticlesAsync(string userId, int pageSize, string? continuationToken, CancellationToken ct);

    /// <summary>Articles with pending work whose dueAt has passed (new, retry due, or lease expired).</summary>
    Task<IReadOnlyList<ArticleRef>> QueryDueAsync(DateTimeOffset now, int maxItems, CancellationToken ct);

    /// <summary>Whether any article still has work ahead (due now or later). Used to keep a scale-to-zero worker awake.</summary>
    Task<bool> HasPendingAsync(CancellationToken ct);
}
