using System.Security.Cryptography;
using System.Text;

namespace AudioBlog.Core.Pipeline;

public sealed class PipelineOptions
{
    public const string Section = "Pipeline";

    /// <summary>Attempts per step before the article is marked Failed (transient errors only; permanent errors fail at once).</summary>
    public int MaxAttempts { get; set; } = 3;
    /// <summary>Delay before attempt 2, 3, ... (a 429 Retry-After header can make it longer).</summary>
    public int[] RetryDelaysSeconds { get; set; } = [20, 60];
    /// <summary>How long a worker owns an article without renewing. A crashed worker's work is picked up after this.</summary>
    public int LeaseSeconds { get; set; } = 120;
    /// <summary>Articles processed in parallel by one worker instance.</summary>
    public int MaxConcurrency { get; set; } = 2;
    /// <summary>How often the scheduler looks for due work (retries, expired leases, missed change feed events).</summary>
    public int SweepIntervalSeconds { get; set; } = 15;
    /// <summary>Longer articles are cut at a paragraph boundary (cost cap).</summary>
    public int MaxSourceChars { get; set; } = 60_000;
    /// <summary>Characters of spoken text stored on the article item for the UI preview.</summary>
    public int PreviewChars { get; set; } = 700;

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseSeconds);

    public TimeSpan RetryDelay(int failedAttempt, TimeSpan? retryAfter)
    {
        var configured = RetryDelaysSeconds.Length == 0
            ? 30
            : RetryDelaysSeconds[Math.Clamp(failedAttempt - 1, 0, RetryDelaysSeconds.Length - 1)];
        var delay = TimeSpan.FromSeconds(configured * (0.8 + Random.Shared.NextDouble() * 0.4)); // ±20 % jitter
        if (retryAfter is { } ra && ra > delay)
            delay = ra > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : ra;
        return delay;
    }
}

public static class Hashing
{
    public static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
