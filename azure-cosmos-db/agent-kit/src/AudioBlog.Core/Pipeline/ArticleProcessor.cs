using System.Net;
using Azure;
using AudioBlog.Core.Data;
using AudioBlog.Core.Domain;
using AudioBlog.Core.Json;
using AudioBlog.Core.Pipeline.Extraction;
using AudioBlog.Core.Pipeline.Fetching;
using AudioBlog.Core.Pipeline.Rewriting;
using AudioBlog.Core.Pipeline.Speech;
using AudioBlog.Core.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace AudioBlog.Core.Pipeline;

public enum ProcessResult { NotFound, NotDue, LostRace, Completed, RetryScheduled, Failed, Released, LeaseLost }

public interface IPageFetcher
{
    Task<FetchedPage> FetchAsync(string url, CancellationToken ct);
}

/// <summary>
/// Runs an article through Extract → Rewrite → Synthesize, resuming at <see cref="ArticleDocument.Step"/>.
/// Safe to call any number of times, from any number of workers, for the same article:
/// only the worker that wins the ETag-conditional claim does work, and every step is idempotent.
/// </summary>
public sealed class ArticleProcessor(
    IArticleStore store,
    IAudioStore audio,
    IPageFetcher fetcher,
    ArticleExtractor extractor,
    ITextRewriter rewriter,
    ISpeechSynthesizer speech,
    PipelineOptions options,
    TimeProvider time,
    ILogger<ArticleProcessor> logger)
{
    public string WorkerId { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid().ToString("N")[..8]}";

    public async Task<ProcessResult> ProcessAsync(ArticleRef reference, CancellationToken ct)
    {
        var article = await store.GetArticleAsync(reference, ct);
        if (article is null)
            return ProcessResult.NotFound;

        var now = time.GetUtcNow();
        if (!article.IsDue(now))
            return ProcessResult.NotDue;

        if (article.Steps[article.Step].Attempts >= options.MaxAttempts)
            return await FailInterruptedAsync(article, now, ct);

        // Claim: take a lease and count the attempt, conditional on the ETag we read.
        var claimed = AppJson.DeepClone(article);
        claimed.Status = ArticleStatus.Processing;
        claimed.Lease = new LeaseInfo { Owner = WorkerId, ExpiresAt = now + options.LeaseDuration };
        claimed.DueAt = claimed.Lease.ExpiresAt;
        claimed.UpdatedAt = now;
        StartStep(claimed, claimed.Step, now);
        try
        {
            claimed.ETag = await store.ReplaceArticleAsync(claimed, article.ETag!, ct);
        }
        catch (ConcurrencyException)
        {
            return ProcessResult.LostRace; // another worker (or the user) got there first
        }

        logger.LogInformation("Processing {Article} at step {Step} (attempt {Attempt})", reference, claimed.Step, claimed.Steps[claimed.Step].Attempts);

        await using var session = new ArticleSession(claimed, store, time, options, logger);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.LeaseLost);
        try
        {
            while (session.Current.Status == ArticleStatus.Processing)
            {
                switch (session.Current.Step)
                {
                    case PipelineStep.Extract: await ExtractAsync(session, linked.Token); break;
                    case PipelineStep.Rewrite: await RewriteAsync(session, linked.Token); break;
                    case PipelineStep.Synthesize: await SynthesizeAsync(session, linked.Token); break;
                }
            }
            logger.LogInformation("Article {Article} is ready", reference);
            return ProcessResult.Completed;
        }
        catch (Exception ex) when (session.LeaseLost.IsCancellationRequested || ex is LeaseLostException)
        {
            logger.LogWarning("Stopped processing {Article}: ownership lost", reference);
            return ProcessResult.LeaseLost;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await ReleaseAsync(session);
        }
        catch (Exception ex)
        {
            return await HandleFailureAsync(session, Classify(ex), ct);
        }
    }

    // ---------------------------------------------------------------- steps

    private async Task ExtractAsync(ArticleSession session, CancellationToken ct)
    {
        var article = session.Current;
        var page = await fetcher.FetchAsync(article.Url, ct);
        var extracted = extractor.Extract(page);
        var now = time.GetUtcNow();

        var source = new SourceDocument
        {
            Id = SourceDocument.IdFor(article.ArticleId),
            UserId = article.UserId,
            ArticleId = article.ArticleId,
            CreatedAt = now,
            Title = extracted.Title,
            Byline = extracted.Byline,
            SiteName = extracted.SiteName,
            Language = extracted.Language,
            FinalUrl = page.FinalUrl.ToString(),
            Text = extracted.Text,
            Truncated = extracted.Truncated,
            ContentHash = Hashing.Sha256Hex(extracted.Text),
        };

        await session.CommitAsync(source, d =>
        {
            d.Title = extracted.Title ?? d.Title;
            d.SiteName = extracted.SiteName ?? page.FinalUrl.Host;
            d.Byline = extracted.Byline;
            d.SourceChars = extracted.Text.Length;
            Advance(d, PipelineStep.Extract, PipelineStep.Rewrite, now);
        }, ct);
    }

    private async Task RewriteAsync(ArticleSession session, CancellationToken ct)
    {
        var article = session.Current;
        var source = await store.GetSourceAsync(article.Ref, ct);
        if (source is null)
        {
            await GoBackAsync(session, PipelineStep.Extract, ct);
            return;
        }

        var text = await rewriter.RewriteAsync(source, ct);
        var now = time.GetUtcNow();
        var script = new ScriptDocument
        {
            Id = ScriptDocument.IdFor(article.ArticleId),
            UserId = article.UserId,
            ArticleId = article.ArticleId,
            CreatedAt = now,
            Text = text,
            SourceHash = source.ContentHash,
            ScriptHash = Hashing.Sha256Hex(text),
            Model = rewriter.ModelName,
        };

        await session.CommitAsync(script, d =>
        {
            d.SpokenPreview = Preview(text);
            d.SpokenChars = text.Length;
            Advance(d, PipelineStep.Rewrite, PipelineStep.Synthesize, now);
        }, ct);
    }

    private async Task SynthesizeAsync(ArticleSession session, CancellationToken ct)
    {
        var article = session.Current;
        var script = await store.GetScriptAsync(article.Ref, ct);
        if (script is null)
        {
            await GoBackAsync(session, PipelineStep.Rewrite, ct);
            return;
        }

        var voice = article.Voice;
        var blobName = AudioNaming.BlobNameFor(article.Ref, script.ScriptHash, voice);

        // The final blob only exists after a successful commit, so "exists" means "done" (e.g. we crashed
        // after committing the blob but before updating the article).
        if (!await audio.ExistsAsync(blobName, ct))
        {
            var chunks = TextChunker.Split(script.Text, speech.MaxCharsPerRequest);
            var done = new HashSet<int>((await audio.GetStagedChunksAsync(blobName, ct)).Where(i => i < chunks.Count));
            if (done.Count > 0)
                logger.LogInformation("Resuming synthesis of {Article}: {Done}/{Total} chunks already uploaded", article.Ref, done.Count, chunks.Count);
            await session.SaveAsync(d => d.Progress = new SynthesisProgress { Done = done.Count, Total = chunks.Count }, ct);

            for (var i = 0; i < chunks.Count; i++)
            {
                if (done.Contains(i))
                    continue; // already uploaded by an earlier, interrupted attempt
                var mp3 = await speech.SynthesizeMp3Async(chunks[i], voice, ct);
                await audio.StageChunkAsync(blobName, i, mp3, ct);
                done.Add(i);
                await session.SaveAsync(d => d.Progress = new SynthesisProgress { Done = done.Count, Total = chunks.Count }, ct);
            }
            await audio.CommitAsync(blobName, chunks.Count, ct);
        }

        var size = await audio.GetSizeAsync(blobName, ct);
        var previous = article.Audio?.BlobName;
        var now = time.GetUtcNow();
        await session.SaveAsync(d =>
        {
            d.Audio = new AudioInfo
            {
                BlobName = blobName,
                Voice = voice,
                ScriptHash = script.ScriptHash,
                SizeBytes = size,
                DurationSeconds = Math.Round(size * 8.0 / (speech.BitrateKbps * 1000.0), 1),
                CreatedAt = now,
            };
            var state = d.Steps.Synthesize;
            state.Status = StepStatus.Succeeded;
            state.CompletedAt = now;
            state.LastError = null;
            d.Status = ArticleStatus.Ready;
            d.Progress = null;
            d.Failure = null;
            d.Lease = null;
            d.DueAt = null;
        }, ct);

        // Audio for the previous voice is no longer referenced. Best effort: an orphan only costs a few cents.
        if (previous is not null && previous != blobName)
        {
            try { await audio.DeleteIfExistsAsync(previous, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not delete old audio {Blob}", previous); }
        }
    }

    // ---------------------------------------------------------------- state transitions

    private static void StartStep(ArticleDocument d, PipelineStep step, DateTimeOffset now)
    {
        var state = d.Steps[step];
        state.Status = StepStatus.Running;
        state.Attempts++;
        state.StartedAt = now;
        state.CompletedAt = null;
    }

    private static void Advance(ArticleDocument d, PipelineStep from, PipelineStep to, DateTimeOffset now)
    {
        var done = d.Steps[from];
        done.Status = StepStatus.Succeeded;
        done.CompletedAt = now;
        done.LastError = null;
        d.Step = to;
        d.Steps[to].Reset();
        StartStep(d, to, now);
    }

    /// <summary>A step's input is missing (should not happen, outputs are committed atomically) – redo the earlier step.</summary>
    private Task GoBackAsync(ArticleSession session, PipelineStep step, CancellationToken ct)
    {
        logger.LogWarning("Input of step {Step} missing for {Article}; re-running {Earlier}", session.Current.Step, session.Current.Ref, step);
        var now = time.GetUtcNow();
        return session.SaveAsync(d =>
        {
            d.Steps[d.Step].Reset();
            d.Step = step;
            d.Steps[step].Reset();
            StartStep(d, step, now);
        }, ct);
    }

    private async Task<ProcessResult> HandleFailureAsync(ArticleSession session, PipelineException error, CancellationToken ct)
    {
        var article = session.Current;
        var step = article.Step;
        var attempts = article.Steps[step].Attempts;
        var now = time.GetUtcNow();
        var info = error.ToFailureInfo(step, now);

        try
        {
            if (error.Transient && attempts < options.MaxAttempts)
            {
                var delay = options.RetryDelay(attempts, error.RetryAfter);
                logger.LogWarning("Step {Step} of {Article} failed transiently (attempt {Attempt}/{Max}), retrying in {Delay}: {Error}",
                    step, article.Ref, attempts, options.MaxAttempts, delay, error.Message);
                await session.SaveAsync(d =>
                {
                    var state = d.Steps[step];
                    state.Status = StepStatus.Pending;
                    state.LastError = info;
                    d.Status = ArticleStatus.Queued;
                    d.Lease = null;
                    d.DueAt = now + delay;
                }, CancellationToken.None);
                return ProcessResult.RetryScheduled;
            }

            logger.LogWarning("Step {Step} of {Article} failed permanently after {Attempt} attempt(s): {Error}", step, article.Ref, attempts, error.Message);
            await session.SaveAsync(d => MarkFailed(d, info), CancellationToken.None);
            return ProcessResult.Failed;
        }
        catch (LeaseLostException)
        {
            return ProcessResult.LeaseLost;
        }
        catch (Exception ex)
        {
            // Could not record the failure (e.g. Cosmos DB unavailable). The lease will expire and the
            // scheduler will pick the article up again; the attempt has already been counted.
            logger.LogError(ex, "Could not record failure of {Article}", article.Ref);
            return ProcessResult.LeaseLost;
        }
    }

    private async Task<ProcessResult> FailInterruptedAsync(ArticleDocument article, DateTimeOffset now, CancellationToken ct)
    {
        // Every allowed attempt was started but none reported back: the worker died mid-step each time.
        // Stop here instead of crash-looping forever.
        var failed = AppJson.DeepClone(article);
        var info = failed.Steps[failed.Step].LastError is { } last
            ? new FailureInfo { Step = failed.Step, Code = last.Code, Message = last.Message, Detail = last.Detail, Transient = last.Transient, At = now }
            : new FailureInfo { Step = failed.Step, Code = FailureCodes.Interrupted, Message = "Processing was interrupted repeatedly", Transient = true, At = now };
        MarkFailed(failed, info);
        failed.UpdatedAt = now;
        try
        {
            await store.ReplaceArticleAsync(failed, article.ETag!, ct);
            return ProcessResult.Failed;
        }
        catch (ConcurrencyException)
        {
            return ProcessResult.LostRace;
        }
    }

    private static void MarkFailed(ArticleDocument d, FailureInfo info)
    {
        var state = d.Steps[info.Step];
        state.Status = StepStatus.Failed;
        state.LastError = info;
        d.Failure = info;
        d.Status = ArticleStatus.Failed;
        d.Progress = null;
        d.Lease = null;
        d.DueAt = null;
    }

    private async Task<ProcessResult> ReleaseAsync(ArticleSession session)
    {
        // Graceful shutdown: hand the article back immediately and don't count the interrupted attempt.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var now = time.GetUtcNow();
            await session.SaveAsync(d =>
            {
                var state = d.Steps[d.Step];
                state.Attempts = Math.Max(0, state.Attempts - 1);
                state.Status = StepStatus.Pending;
                d.Status = ArticleStatus.Queued;
                d.Lease = null;
                d.DueAt = now;
            }, timeout.Token);
            logger.LogInformation("Released {Article} on shutdown", session.Current.Ref);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not release {Article}; it will be picked up when the lease expires", session.Current.Ref);
        }
        return ProcessResult.Released;
    }

    private string Preview(string text)
    {
        if (text.Length <= options.PreviewChars)
            return text;
        var cut = text.LastIndexOf(' ', options.PreviewChars);
        return text[..(cut > options.PreviewChars / 2 ? cut : options.PreviewChars)] + " …";
    }

    private static PipelineException Classify(Exception ex) => ex switch
    {
        PipelineException pe => pe,
        CosmosException { StatusCode: HttpStatusCode.TooManyRequests } ce =>
            PipelineException.Retryable(FailureCodes.Throttled, "The database is busy", ce.Message, ce.RetryAfter, ce),
        CosmosException ce =>
            PipelineException.Retryable(FailureCodes.ServerError, "A temporary database problem occurred", $"{(int)ce.StatusCode}: {ce.Message}", inner: ce),
        Azure.Identity.AuthenticationFailedException or Azure.Identity.CredentialUnavailableException =>
            PipelineException.Retryable(FailureCodes.NetworkError, "Couldn't sign in to Azure (check az login or the managed identity)", ex.Message, inner: ex),
        RequestFailedException rfe =>
            PipelineException.Retryable(FailureCodes.ServerError, "A temporary storage problem occurred", $"{rfe.Status}: {rfe.ErrorCode}", inner: rfe),
        _ => PipelineException.FromUnknown(ex),
    };
}
