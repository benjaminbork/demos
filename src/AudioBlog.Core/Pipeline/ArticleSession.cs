using AudioBlog.Core.Data;
using AudioBlog.Core.Domain;
using AudioBlog.Core.Json;
using Microsoft.Extensions.Logging;

namespace AudioBlog.Core.Pipeline;

/// <summary>The worker no longer owns the article (another worker took over after the lease expired, or a user changed it).</summary>
public sealed class LeaseLostException(string message) : Exception(message);

/// <summary>
/// A worker's ownership of one article while processing it. Every write is an ETag-conditional replace
/// (or transactional batch) that also extends the lease; a background heartbeat keeps the lease alive during
/// long calls. If any write loses the ETag race, ownership is gone: <see cref="LeaseLost"/> fires and the
/// worker stops touching the article.
/// </summary>
public sealed class ArticleSession : IAsyncDisposable
{
    private readonly IArticleStore _store;
    private readonly TimeProvider _time;
    private readonly PipelineOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _heartbeat;
    private ArticleDocument _current;
    private DateTimeOffset _lastWrite;

    public ArticleSession(ArticleDocument claimed, IArticleStore store, TimeProvider time, PipelineOptions options, ILogger logger)
    {
        _current = claimed;
        _store = store;
        _time = time;
        _options = options;
        _logger = logger;
        _lastWrite = time.GetUtcNow();
        _heartbeat = Task.Run(HeartbeatLoopAsync);
    }

    /// <summary>The last successfully persisted state. Do not mutate; use <see cref="SaveAsync"/>.</summary>
    public ArticleDocument Current => _current;
    public CancellationToken LeaseLost => _lost.Token;

    public Task SaveAsync(Action<ArticleDocument> mutate, CancellationToken ct) =>
        WriteAsync(mutate, (doc, etag, token) => _store.ReplaceArticleAsync(doc, etag, token), ct);

    /// <summary>Heartbeat: extends the lease, unless the article was already released (finished, failed, retry scheduled).</summary>
    private Task RenewAsync(CancellationToken ct) =>
        WriteAsync(_ => { }, (doc, etag, token) => _store.ReplaceArticleAsync(doc, etag, token), ct, renewOnly: true);

    /// <summary>Persists a step output and the article change in one transaction.</summary>
    public Task CommitAsync<TOutput>(TOutput output, Action<ArticleDocument> mutate, CancellationToken ct) where TOutput : ContentDocument =>
        WriteAsync(mutate, (doc, etag, token) => _store.CommitStepAsync(doc, etag, output, token), ct);

    private async Task WriteAsync(Action<ArticleDocument> mutate, Func<ArticleDocument, string, CancellationToken, Task<string>> persist, CancellationToken ct, bool renewOnly = false)
    {
        if (_lost.IsCancellationRequested)
            throw new LeaseLostException($"Lease on {_current.Ref} already lost.");

        await _gate.WaitAsync(ct);
        try
        {
            if (renewOnly && _current.Lease is null)
                return;

            // Mutate a copy, so the in-memory state only changes if the write succeeds.
            var next = AppJson.DeepClone(_current);
            mutate(next);
            var now = _time.GetUtcNow();
            next.UpdatedAt = now;
            if (next.Lease is not null)
            {
                next.Lease.ExpiresAt = now + _options.LeaseDuration;
                next.DueAt = next.Lease.ExpiresAt; // if we die, the scheduler finds it once the lease expired
            }

            try
            {
                next.ETag = await persist(next, _current.ETag!, ct);
            }
            catch (ConcurrencyException ex)
            {
                _lost.Cancel();
                throw new LeaseLostException(ex.Message);
            }
            _current = next;
            _lastWrite = now;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        var interval = _options.LeaseDuration / 3;
        try
        {
            while (!_stop.IsCancellationRequested && _current.Lease is not null)
            {
                var wait = _lastWrite + interval - _time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, _stop.Token);
                    continue;
                }
                try
                {
                    await RenewAsync(_stop.Token);
                }
                catch (LeaseLostException)
                {
                    _logger.LogWarning("Lost the lease on article {Article}", _current.Ref);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Cosmos DB hiccup: try again soon; the lease still has 2/3 of its time left.
                    _logger.LogWarning(ex, "Lease heartbeat for {Article} failed", _current.Ref);
                    await Task.Delay(TimeSpan.FromSeconds(5), _time, _stop.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _heartbeat;
        _stop.Dispose();
        _lost.Dispose();
        _gate.Dispose();
    }
}
