using System.Collections.Concurrent;
using System.Threading.Channels;
using AudioBlog.Core.Domain;
using AudioBlog.Core.Pipeline;

namespace AudioBlog.Worker;

/// <summary>
/// In-memory hand-off between the triggers (change feed, scheduler sweep) and the processing slots.
/// It is only an optimization: nothing durable lives here. If the process dies, the articles are still
/// "due" in Cosmos DB and the sweep finds them again.
/// Each article is queued at most once at a time; slots process different articles in parallel,
/// so one slow or broken article never blocks the others.
/// </summary>
public sealed class WorkDispatcher(ArticleProcessor processor, PipelineOptions options, ILogger<WorkDispatcher> logger) : BackgroundService
{
    private readonly Channel<ArticleRef> _queue = Channel.CreateUnbounded<ArticleRef>(new UnboundedChannelOptions { SingleWriter = false });
    private readonly ConcurrentDictionary<ArticleRef, byte> _queuedOrRunning = new();

    public bool TryEnqueue(ArticleRef article)
    {
        if (!_queuedOrRunning.TryAdd(article, 0))
            return false; // already queued or in progress in this process
        if (_queue.Writer.TryWrite(article))
            return true;
        _queuedOrRunning.TryRemove(article, out _);
        return false;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, options.MaxConcurrency)).Select(_ => RunSlotAsync(stoppingToken)));

    private async Task RunSlotAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var article in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var result = await processor.ProcessAsync(article, ct);
                    logger.LogDebug("Article {Article}: {Result}", article, result);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Never let one article take down the slot. The article stays due and is retried by the sweep.
                    logger.LogError(ex, "Unhandled error while processing {Article}", article);
                }
                finally
                {
                    _queuedOrRunning.TryRemove(article, out _);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
