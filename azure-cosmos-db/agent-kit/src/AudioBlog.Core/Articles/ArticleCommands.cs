using AudioBlog.Core.Data;
using AudioBlog.Core.Domain;
using AudioBlog.Core.Json;

namespace AudioBlog.Core.Articles;

public sealed class VoiceOptions
{
    public const string Section = "Voices";

    public string Default { get; set; } = "en-US-AvaMultilingualNeural";
    public List<VoiceInfo> Available { get; set; } = [];

    public bool IsKnown(string voice) => voice == Default || Available.Any(v => v.Id == voice);
}

public sealed class VoiceInfo
{
    public required string Id { get; set; }
    public required string Label { get; set; }
}

public enum CommandStatus { Ok, Created, NotFound, Conflict, Invalid }

public sealed record CommandResult(CommandStatus Status, ArticleDocument? Article = null, string? Error = null);

/// <summary>
/// The user-facing state changes. Each is one ETag-conditional write of the article item; that single write
/// is also what schedules the work (it sets dueAt and appears on the change feed). No second system to keep in sync.
/// </summary>
public sealed class ArticleCommands(IArticleStore store, VoiceOptions voices, TimeProvider time)
{
    public async Task<CommandResult> CreateAsync(string userId, string? clientId, string url, string? voice, CancellationToken ct)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || url!.Length > 2048)
            return new CommandResult(CommandStatus.Invalid, Error: "Please enter a valid http(s) URL.");
        if (uri.IsLoopback || !uri.Host.Contains('.'))
            return new CommandResult(CommandStatus.Invalid, Error: "Please enter a public web address.");

        voice = string.IsNullOrWhiteSpace(voice) ? voices.Default : voice;
        if (!voices.IsKnown(voice))
            return new CommandResult(CommandStatus.Invalid, Error: $"Unknown voice '{voice}'.");

        // The client may send its own id (a UUID) so that a retried POST is idempotent.
        string id;
        if (clientId is null)
            id = Guid.NewGuid().ToString();
        else if (Guid.TryParse(clientId, out var parsed))
            id = parsed.ToString();
        else
            return new CommandResult(CommandStatus.Invalid, Error: "The id must be a UUID.");

        var now = time.GetUtcNow();
        var article = new ArticleDocument
        {
            Id = id,
            UserId = userId,
            ArticleId = id,
            Url = uri.ToString(),
            SiteName = uri.Host,
            Voice = voice,
            Status = ArticleStatus.Queued,
            Step = PipelineStep.Extract,
            DueAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var (stored, created) = await store.CreateArticleAsync(article, ct);
        if (!created && stored.Url != article.Url)
            return new CommandResult(CommandStatus.Conflict, stored, "An article with this id already exists for a different URL.");
        return new CommandResult(created ? CommandStatus.Created : CommandStatus.Ok, stored);
    }

    /// <summary>Retry a failed article from the step that failed (earlier step outputs are reused).</summary>
    public Task<CommandResult> RetryAsync(ArticleRef reference, CancellationToken ct) =>
        UpdateAsync(reference, article =>
        {
            if (article.Status != ArticleStatus.Failed)
                return "Only failed articles can be retried.";
            var now = time.GetUtcNow();
            article.Steps[article.Step].Reset();
            article.Failure = null;
            article.Status = ArticleStatus.Queued;
            article.DueAt = now;
            return null;
        }, ct);

    /// <summary>Re-generate the audio with another voice. Re-uses the stored spoken text: no fetch, no rewrite.</summary>
    public Task<CommandResult> ChangeVoiceAsync(ArticleRef reference, string voice, CancellationToken ct)
    {
        if (!voices.IsKnown(voice))
            return Task.FromResult(new CommandResult(CommandStatus.Invalid, Error: $"Unknown voice '{voice}'."));

        return UpdateAsync(reference, article =>
        {
            if (article.Steps.Rewrite.Status != StepStatus.Succeeded)
                return "The spoken text isn't ready yet.";
            if (!article.IsTerminal)
                return "The article is still being processed.";
            if (article.Status == ArticleStatus.Ready && article.Voice == voice)
                return null; // nothing to do

            var now = time.GetUtcNow();
            article.Voice = voice;
            article.Step = PipelineStep.Synthesize;
            article.Steps.Synthesize.Reset();
            article.Failure = null;
            article.Progress = null;
            article.Status = ArticleStatus.Queued;
            article.DueAt = now;
            // article.Audio is kept, so the old recording stays playable until the new one replaces it.
            return null;
        }, ct);
    }

    private async Task<CommandResult> UpdateAsync(ArticleRef reference, Func<ArticleDocument, string?> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var article = await store.GetArticleAsync(reference, ct);
            if (article is null)
                return new CommandResult(CommandStatus.NotFound);

            var updated = AppJson.DeepClone(article);
            var error = change(updated);
            if (error is not null)
                return new CommandResult(CommandStatus.Conflict, article, error);

            updated.UpdatedAt = time.GetUtcNow();
            try
            {
                updated.ETag = await store.ReplaceArticleAsync(updated, article.ETag!, ct);
                return new CommandResult(CommandStatus.Ok, updated);
            }
            catch (ConcurrencyException)
            {
                // Someone else (the worker) changed it in between: re-read and re-validate.
            }
        }
        return new CommandResult(CommandStatus.Conflict, Error: "The article is changing too quickly, please try again.");
    }
}
