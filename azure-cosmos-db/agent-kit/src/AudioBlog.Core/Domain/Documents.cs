using System.Text.Json.Serialization;

namespace AudioBlog.Core.Domain;

/// <summary>Identifies one article. Together the two values form the hierarchical partition key.</summary>
public readonly record struct ArticleRef(string UserId, string ArticleId)
{
    public override string ToString() => $"{UserId}/{ArticleId}";
}

public enum ArticleStatus
{
    /// <summary>Waiting for a worker (new, retry scheduled, or released on shutdown). <c>dueAt</c> says from when.</summary>
    Queued,
    /// <summary>Claimed by a worker that holds a time-limited lease.</summary>
    Processing,
    Ready,
    Failed,
}

/// <summary>The resumable steps of the pipeline, in order.</summary>
public enum PipelineStep
{
    /// <summary>Fetch the HTML and extract the article text (output: the "source" item).</summary>
    Extract,
    /// <summary>Rewrite the text into spoken prose with Azure OpenAI (output: the "script" item).</summary>
    Rewrite,
    /// <summary>Text to speech and store the MP3 in Blob Storage (output: the blob + <see cref="AudioInfo"/>).</summary>
    Synthesize,
}

public enum StepStatus { Pending, Running, Succeeded, Failed }

/// <summary>
/// The "article" item: small, frequently updated status document. It is both what the UI shows
/// and the durable work item the worker schedules from (see README, "the article is the job").
/// </summary>
public sealed class ArticleDocument
{
    public const string ItemType = "article";
    /// <summary>Bump when the stored shape changes in a way that needs a migration.</summary>
    public const int CurrentSchemaVersion = 1;

    public required string Id { get; set; }
    public required string UserId { get; set; }
    public required string ArticleId { get; set; }
    public string Type { get; set; } = ItemType;
    /// <summary>Shape of this stored item. Items written before versioning existed read as 1.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public required string Url { get; set; }
    public string? Title { get; set; }
    public string? SiteName { get; set; }
    public string? Byline { get; set; }

    public ArticleStatus Status { get; set; }
    /// <summary>The step that is running or will run next. Retry resumes here.</summary>
    public PipelineStep Step { get; set; }
    public required string Voice { get; set; }
    public StepStates Steps { get; set; } = new();
    /// <summary>Set only when <see cref="Status"/> is Failed.</summary>
    public FailureInfo? Failure { get; set; }
    public SynthesisProgress? Progress { get; set; }

    public string? SpokenPreview { get; set; }
    public int? SourceChars { get; set; }
    public int? SpokenChars { get; set; }
    public AudioInfo? Audio { get; set; }

    /// <summary>
    /// When a worker should look at this article next. Present only while work is pending:
    /// "now" for new work, a future time for a scheduled retry, the lease expiry while processing.
    /// Removed when Ready/Failed, so the scheduler query never sees finished articles.
    /// </summary>
    public DateTimeOffset? DueAt { get; set; }
    public LeaseInfo? Lease { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    [JsonIgnore] public ArticleRef Ref => new(UserId, ArticleId);
    [JsonIgnore] public bool IsTerminal => Status is ArticleStatus.Ready or ArticleStatus.Failed;
    public bool IsDue(DateTimeOffset now) => !IsTerminal && DueAt is { } due && due <= now;
}

public sealed class StepStates
{
    public StepState Extract { get; set; } = new();
    public StepState Rewrite { get; set; } = new();
    public StepState Synthesize { get; set; } = new();

    public StepState this[PipelineStep step] => step switch
    {
        PipelineStep.Extract => Extract,
        PipelineStep.Rewrite => Rewrite,
        PipelineStep.Synthesize => Synthesize,
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };
}

public sealed class StepState
{
    public StepStatus Status { get; set; } = StepStatus.Pending;
    /// <summary>Attempts started. Counted when a worker claims the step, so a crash loop is bounded too.</summary>
    public int Attempts { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Most recent error of this step, kept while a retry is scheduled.</summary>
    public FailureInfo? LastError { get; set; }

    public void Reset()
    {
        Status = StepStatus.Pending;
        Attempts = 0;
        StartedAt = null;
        CompletedAt = null;
        LastError = null;
    }
}

public sealed class FailureInfo
{
    public PipelineStep Step { get; set; }
    /// <summary>Machine-readable code, see <c>FailureCodes</c>.</summary>
    public required string Code { get; set; }
    /// <summary>Human-readable reason shown in the UI.</summary>
    public required string Message { get; set; }
    /// <summary>Technical detail for debugging (truncated).</summary>
    public string? Detail { get; set; }
    public bool Transient { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class SynthesisProgress
{
    public int Done { get; set; }
    public int Total { get; set; }
}

public sealed class AudioInfo
{
    public required string BlobName { get; set; }
    public required string Voice { get; set; }
    public required string ScriptHash { get; set; }
    public long SizeBytes { get; set; }
    public double? DurationSeconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class LeaseInfo
{
    public required string Owner { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Large, write-once step outputs. They live in the same logical partition as their article
/// (same userId + articleId), so a step output and the article's state transition are committed
/// together in one transactional batch.
/// </summary>
public abstract class ContentDocument
{
    /// <summary>Bump when the stored shape changes in a way that needs a migration.</summary>
    public const int CurrentSchemaVersion = 1;

    public required string Id { get; set; }
    public required string UserId { get; set; }
    public required string ArticleId { get; set; }
    public abstract string Type { get; set; }
    /// <summary>Shape of this stored item. Items written before versioning existed read as 1.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Output of <see cref="PipelineStep.Extract"/>: the readable article text.</summary>
public sealed class SourceDocument : ContentDocument
{
    public const string ItemType = "source";
    public static string IdFor(string articleId) => $"{articleId}:source";

    public override string Type { get; set; } = ItemType;
    public string? Title { get; set; }
    public string? Byline { get; set; }
    public string? SiteName { get; set; }
    public string? Language { get; set; }
    public string? FinalUrl { get; set; }
    /// <summary>Article text with light structure markers (headings, lists, tables, code, images) for the rewrite.</summary>
    public required string Text { get; set; }
    public bool Truncated { get; set; }
    public required string ContentHash { get; set; }
}

/// <summary>Output of <see cref="PipelineStep.Rewrite"/>: the text that is read aloud.</summary>
public sealed class ScriptDocument : ContentDocument
{
    public const string ItemType = "script";
    public static string IdFor(string articleId) => $"{articleId}:script";

    public override string Type { get; set; } = ItemType;
    public required string Text { get; set; }
    /// <summary>Hash of the source text this script was produced from.</summary>
    public required string SourceHash { get; set; }
    /// <summary>Hash of <see cref="Text"/>; part of the audio blob name.</summary>
    public required string ScriptHash { get; set; }
    public string? Model { get; set; }
}
