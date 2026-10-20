using AudioBlog.Core.Domain;

namespace AudioBlog.Api;

/// <summary>What the UI needs to render one card. The internals (lease, dueAt, step bookkeeping) stay server-side.</summary>
public sealed record ArticleDto(
    string Id,
    string Url,
    string? Title,
    string? SiteName,
    string? Byline,
    /// <summary>queued | processing | ready | failed</summary>
    ArticleStatus Status,
    /// <summary>reading | generating | ready | failed – the three user-visible stages.</summary>
    string Phase,
    PipelineStep Step,
    string Voice,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? SpokenPreview,
    int? SpokenChars,
    bool HasSpokenText,
    ArticleDto.ProgressDto? Progress,
    ArticleDto.RetryDto? Retry,
    ArticleDto.FailureDto? Failure,
    ArticleDto.AudioDto? Audio)
{
    public sealed record ProgressDto(int Done, int Total);
    public sealed record RetryDto(int Attempt, int MaxAttempts, DateTimeOffset? NextAttemptAt, string? LastError);
    public sealed record FailureDto(PipelineStep Step, string Code, string Message, DateTimeOffset At);
    public sealed record AudioDto(string Voice, long SizeBytes, double? DurationSeconds, string Url, string DownloadUrl);

    public static ArticleDto From(ArticleDocument a, int maxAttempts)
    {
        var phase = a.Status switch
        {
            ArticleStatus.Ready => "ready",
            ArticleStatus.Failed => "failed",
            _ => a.Step == PipelineStep.Synthesize ? "generating" : "reading",
        };

        var stepState = a.Steps[a.Step];
        RetryDto? retry = !a.IsTerminal && stepState.LastError is { } last
            ? new RetryDto(stepState.Attempts + (a.Status == ArticleStatus.Queued ? 1 : 0), maxAttempts,
                a.Status == ArticleStatus.Queued ? a.DueAt : null, last.Message)
            : null;

        AudioDto? audio = a.Audio is { } au
            ? new AudioDto(au.Voice, au.SizeBytes, au.DurationSeconds,
                $"/api/articles/{a.Id}/audio?v={au.CreatedAt.ToUnixTimeSeconds()}",
                $"/api/articles/{a.Id}/audio?download=true&v={au.CreatedAt.ToUnixTimeSeconds()}")
            : null;

        return new ArticleDto(
            a.Id, a.Url, a.Title, a.SiteName, a.Byline,
            a.Status, phase, a.Step, a.Voice, a.CreatedAt, a.UpdatedAt,
            a.SpokenPreview, a.SpokenChars, a.Steps.Rewrite.Status == StepStatus.Succeeded,
            a.Progress is { } p ? new ProgressDto(p.Done, p.Total) : null,
            retry,
            a.Failure is { } f ? new FailureDto(f.Step, f.Code, f.Message, f.At) : null,
            audio);
    }
}
