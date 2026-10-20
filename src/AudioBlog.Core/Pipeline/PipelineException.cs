using AudioBlog.Core.Domain;

namespace AudioBlog.Core.Pipeline;

/// <summary>Machine-readable failure codes stored on the article (failure.code).</summary>
public static class FailureCodes
{
    // Permanent: retrying without a change will not help.
    public const string InvalidUrl = "invalidUrl";
    public const string NotFound = "notFound";
    public const string AccessDenied = "accessDenied";
    public const string Paywall = "paywall";
    public const string JavaScriptRequired = "javaScriptRequired";
    public const string BotProtection = "botProtection";
    public const string EmptyContent = "emptyContent";
    public const string NotHtml = "notHtml";
    public const string TooLarge = "tooLarge";
    public const string HostNotFound = "hostNotFound";
    public const string BlockedAddress = "blockedAddress";
    public const string HttpError = "httpError";
    public const string ContentFiltered = "contentFiltered";
    public const string AiRequestRejected = "aiRequestRejected";
    public const string SpeechRequestRejected = "speechRequestRejected";
    public const string Interrupted = "interrupted";

    // Transient: retried automatically (max attempts per step).
    public const string Timeout = "timeout";
    public const string Throttled = "throttled";
    public const string ServerError = "serverError";
    public const string NetworkError = "networkError";
    public const string Unexpected = "unexpected";
}

/// <summary>
/// A classified step failure. Steps throw this; the processor decides between
/// "schedule a retry" (transient and attempts left) and "fail now" (permanent or out of attempts).
/// </summary>
public sealed class PipelineException : Exception
{
    public PipelineException(string code, string userMessage, bool transient, string? detail = null, TimeSpan? retryAfter = null, Exception? inner = null)
        : base($"{code}: {userMessage}{(detail is null ? "" : $" ({detail})")}", inner)
    {
        Code = code;
        UserMessage = userMessage;
        Transient = transient;
        Detail = detail;
        RetryAfter = retryAfter;
    }

    public string Code { get; }
    public string UserMessage { get; }
    public bool Transient { get; }
    public string? Detail { get; }
    public TimeSpan? RetryAfter { get; }

    public static PipelineException Permanent(string code, string userMessage, string? detail = null, Exception? inner = null) =>
        new(code, userMessage, transient: false, detail, inner: inner);

    public static PipelineException Retryable(string code, string userMessage, string? detail = null, TimeSpan? retryAfter = null, Exception? inner = null) =>
        new(code, userMessage, transient: true, detail, retryAfter, inner);

    /// <summary>Maps a status code from a remote service onto transient/permanent.</summary>
    public static bool IsTransientStatus(int status) =>
        status is 408 or 425 or 429 || status >= 500;

    public static PipelineException FromUnknown(Exception ex) => ex switch
    {
        PipelineException pe => pe,
        TimeoutException or TaskCanceledException => Retryable(FailureCodes.Timeout, "The operation timed out", ex.Message, inner: ex),
        HttpRequestException => Retryable(FailureCodes.NetworkError, "A network error occurred", ex.Message, inner: ex),
        _ => Retryable(FailureCodes.Unexpected, "An unexpected error occurred", $"{ex.GetType().Name}: {ex.Message}", inner: ex),
    };

    public FailureInfo ToFailureInfo(PipelineStep step, DateTimeOffset at) => new()
    {
        Step = step,
        Code = Code,
        Message = UserMessage,
        Detail = Detail is { Length: > 500 } d ? d[..500] : Detail,
        Transient = Transient,
        At = at,
    };
}
