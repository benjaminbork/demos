using AudioBlog.Core.Data;
using AudioBlog.Core.Domain;
using AudioBlog.Core.Pipeline;
using Microsoft.Azure.Cosmos;

namespace AudioBlog.Worker;

public sealed class WorkerOptions
{
    public const string Section = "Worker";
    public int ChangeFeedPollSeconds { get; set; } = 5;
    /// <summary>
    /// Scale-to-zero mode only: this worker's own ingress URL. While articles are pending, the worker
    /// calls it once a minute so Container Apps' HTTP scaler keeps the replica alive.
    /// </summary>
    public string? SelfUrl { get; set; }
}

/// <summary>
/// Low-latency trigger: the Cosmos DB change feed of the articles container. Creating an article (or pressing
/// Retry / changing the voice) is a write, every write appears on the change feed, so "saving" and
/// "scheduling" are the same operation. Checkpoints (leases) live in the "leases" container, so after a
/// restart the feed continues where it left off.
/// </summary>
public sealed class ChangeFeedListener(
    CosmosContainers cosmos,
    WorkDispatcher dispatcher,
    ArticleProcessor processor,
    WorkerOptions options,
    TimeProvider time,
    ILogger<ChangeFeedListener> logger) : IHostedService
{
    private ChangeFeedProcessor? _processor;

    public async Task StartAsync(CancellationToken ct)
    {
        _processor = cosmos.Articles
            .GetChangeFeedProcessorBuilder<ArticleChange>("article-dispatcher", HandleChangesAsync)
            .WithInstanceName(processor.WorkerId)
            .WithLeaseContainer(cosmos.Leases)
            .WithPollInterval(TimeSpan.FromSeconds(options.ChangeFeedPollSeconds))
            .WithErrorNotification((lease, ex) =>
            {
                logger.LogWarning(ex, "Change feed error on lease {Lease}", lease);
                return Task.CompletedTask;
            })
            .Build();
        await _processor.StartAsync();
        logger.LogInformation("Change feed processor started as {Instance}", processor.WorkerId);
    }

    private Task HandleChangesAsync(ChangeFeedProcessorContext context, IReadOnlyCollection<ArticleChange> changes, CancellationToken ct)
    {
        // Must be fast and must not throw: a throwing handler would redeliver (and stall) the whole batch.
        // Only enqueue; the real work happens in the dispatcher slots.
        var horizon = time.GetUtcNow().AddSeconds(1);
        foreach (var change in changes)
        {
            if (change.Type == ArticleDocument.ItemType && change.DueAt is { } due && due <= horizon)
                dispatcher.TryEnqueue(new ArticleRef(change.UserId, change.ArticleId));
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_processor is not null)
            await _processor.StopAsync();
    }

    /// <summary>Just the fields needed to decide whether an item is due; ignores large text items' bodies.</summary>
    public sealed class ArticleChange
    {
        public string Id { get; set; } = "";
        public string UserId { get; set; } = "";
        public string ArticleId { get; set; } = "";
        public string Type { get; set; } = "";
        public DateTimeOffset? DueAt { get; set; }
    }
}

/// <summary>
/// Safety net and timer: periodically queries for articles whose dueAt has passed. This covers
/// delayed retries (dueAt in the future), crashed workers (lease expired → dueAt passed),
/// and anything the change feed delivered while no slot was free or before the first worker ever ran.
/// </summary>
public sealed class DueWorkSweeper(
    IArticleStore store,
    WorkDispatcher dispatcher,
    PipelineOptions options,
    TimeProvider time,
    ILogger<DueWorkSweeper> logger) : BackgroundService
{
    private readonly SemaphoreSlim _sweepNow = new(0, 1);

    /// <summary>Run the next sweep immediately (e.g. when the API wakes us up).</summary>
    public void SweepNow()
    {
        if (_sweepNow.CurrentCount == 0)
            try { _sweepNow.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(options.SweepIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var due = await store.QueryDueAsync(time.GetUtcNow(), 100, ct);
                var queued = due.Count(dispatcher.TryEnqueue);
                if (queued > 0)
                    logger.LogInformation("Sweep queued {Count} due article(s)", queued);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sweep for due articles failed");
            }

            try
            {
                await _sweepNow.WaitAsync(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>
/// Scale-to-zero mode: keeps the replica alive while there is work, by sending itself an HTTP request
/// through the Container Apps ingress once a minute (the HTTP scaler counts it). Without pending work it
/// stays quiet and Container Apps scales the worker to zero after the cool-down period.
/// </summary>
public sealed class KeepAliveService(
    IArticleStore store,
    WorkerOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger<KeepAliveService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.SelfUrl))
            return;

        var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                if (!await store.HasPendingAsync(ct))
                    continue;
                await http.GetAsync(new Uri(new Uri(options.SelfUrl), "/healthz"), ct);
                logger.LogDebug("Keep-alive ping (articles pending)");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Keep-alive ping failed: {Message}", ex.Message);
            }
        }
    }
}
