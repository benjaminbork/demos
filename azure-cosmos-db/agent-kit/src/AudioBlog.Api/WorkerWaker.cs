namespace AudioBlog.Api;

/// <summary>
/// Scale-to-zero mode only (Worker:WakeUrl set): pokes the worker after a write so Container Apps starts it.
/// Fire-and-forget – the API response never waits for it. This is a latency optimization, not the
/// scheduling mechanism: the article item in Cosmos DB already is the scheduled work, and the worker also
/// wakes up hourly (cron scale rule) and whenever the UI polls an unfinished article.
/// </summary>
public sealed class WorkerWaker(IHttpClientFactory httpClientFactory, IConfiguration config, TimeProvider time, ILogger<WorkerWaker> logger)
{
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(20);
    private readonly string? _url = config["Worker:WakeUrl"];
    private long _lastWakeTicks;

    public void Wake(bool force = true)
    {
        if (string.IsNullOrWhiteSpace(_url))
            return;

        var now = time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastWakeTicks);
        if (!force && now - last < Throttle.Ticks)
            return;
        Interlocked.Exchange(ref _lastWakeTicks, now);

        _ = Task.Run(async () =>
        {
            try
            {
                var http = httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(90); // a cold start can take a while
                using var response = await http.PostAsync(_url, null);
                logger.LogDebug("Woke worker: {Status}", response.StatusCode);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not wake the worker ({Message}); it will catch up on its next wake-up", ex.Message);
            }
        });
    }
}
