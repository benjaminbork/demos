using AudioBlog.Core.Articles;
using AudioBlog.Core.Domain;
using AudioBlog.Core.Json;
using AudioBlog.Core.Pipeline;
using AudioBlog.Core.Pipeline.Extraction;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioBlog.Tests;

public class ProcessorTests
{
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryArticleStore _store = new();
    private readonly InMemoryAudioStore _audio = new();
    private readonly ScriptedFetcher _fetcher = new() { Default = () => Pages.Page(Pages.ArticleHtml()) };
    private readonly CountingRewriter _rewriter = new();
    private readonly CountingSpeech _speech = new();
    private readonly PipelineOptions _options = new() { MaxAttempts = 3, RetryDelaysSeconds = [20, 60], LeaseSeconds = 120 };
    private readonly VoiceOptions _voices = new()
    {
        Default = "en-US-AvaMultilingualNeural",
        Available = [new() { Id = "en-US-AvaMultilingualNeural", Label = "Ava" }, new() { Id = "en-GB-RyanNeural", Label = "Ryan" }],
    };
    private static readonly CancellationToken Ct = CancellationToken.None;

    private ArticleProcessor Processor() =>
        new(_store, _audio, _fetcher, new ArticleExtractor(_options), _rewriter, _speech, _options, _time, NullLogger<ArticleProcessor>.Instance);

    private ArticleCommands Commands() => new(_store, _voices, _time);

    private async Task<ArticleRef> CreateAsync()
    {
        var result = await Commands().CreateAsync("me", null, "https://example.com/a", null, Ct);
        Assert.Equal(CommandStatus.Created, result.Status);
        return result.Article!.Ref;
    }

    private Task<ArticleDocument> GetAsync(ArticleRef r) => _store.GetArticleAsync(r, Ct)!;

    [Fact]
    public async Task Happy_path_produces_exactly_one_mp3()
    {
        var r = await CreateAsync();

        Assert.Equal(ProcessResult.Completed, await Processor().ProcessAsync(r, Ct));

        var article = await GetAsync(r);
        Assert.Equal(ArticleStatus.Ready, article.Status);
        Assert.Null(article.DueAt);
        Assert.Null(article.Lease);
        Assert.NotNull(article.SpokenPreview);
        Assert.Single(_audio.Committed);
        Assert.Equal(article.Audio!.BlobName, _audio.Committed.Keys.Single());
        Assert.Empty(_audio.Staged);
        Assert.Equal(1, _fetcher.Calls);
        Assert.Equal(1, _rewriter.Calls);
    }

    [Fact]
    public async Task Processing_a_finished_article_again_does_nothing()
    {
        var r = await CreateAsync();
        await Processor().ProcessAsync(r, Ct);
        var speechCalls = _speech.Calls;

        Assert.Equal(ProcessResult.NotDue, await Processor().ProcessAsync(r, Ct));
        Assert.Equal(ProcessResult.NotDue, await Processor().ProcessAsync(r, Ct));

        Assert.Equal(1, _fetcher.Calls);
        Assert.Equal(speechCalls, _speech.Calls);
        Assert.Single(_audio.Committed);
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_backoff_then_fail_after_three_attempts()
    {
        _fetcher.Default = () => throw PipelineException.Retryable(FailureCodes.Timeout, "The website took too long to respond");
        var r = await CreateAsync();

        Assert.Equal(ProcessResult.RetryScheduled, await Processor().ProcessAsync(r, Ct));
        var article = await GetAsync(r);
        Assert.Equal(ArticleStatus.Queued, article.Status);
        Assert.True(article.DueAt > _time.Now.AddSeconds(10));
        Assert.Equal(FailureCodes.Timeout, article.Steps.Extract.LastError!.Code);

        // Not due yet: nothing happens before the back-off has passed.
        Assert.Equal(ProcessResult.NotDue, await Processor().ProcessAsync(r, Ct));
        Assert.Equal(1, _fetcher.Calls);

        _time.Advance(TimeSpan.FromSeconds(25));
        Assert.Equal(ProcessResult.RetryScheduled, await Processor().ProcessAsync(r, Ct));
        _time.Advance(TimeSpan.FromSeconds(75));
        Assert.Equal(ProcessResult.Failed, await Processor().ProcessAsync(r, Ct));

        article = await GetAsync(r);
        Assert.Equal(3, _fetcher.Calls);
        Assert.Equal(ArticleStatus.Failed, article.Status);
        Assert.Equal(PipelineStep.Extract, article.Failure!.Step);
        Assert.Equal("The website took too long to respond", article.Failure.Message);
        Assert.Null(article.DueAt);
    }

    [Fact]
    public async Task Rate_limit_retry_after_is_respected()
    {
        _fetcher.Default = () => throw PipelineException.Retryable(FailureCodes.Throttled, "busy", retryAfter: TimeSpan.FromSeconds(300));
        var r = await CreateAsync();

        await Processor().ProcessAsync(r, Ct);

        Assert.True((await GetAsync(r)).DueAt >= _time.Now.AddSeconds(300));
    }

    [Fact]
    public async Task Permanent_failures_fail_immediately()
    {
        _fetcher.Default = () => throw PipelineException.Permanent(FailureCodes.NotFound, "The page doesn't exist (404)");
        var r = await CreateAsync();

        Assert.Equal(ProcessResult.Failed, await Processor().ProcessAsync(r, Ct));

        var article = await GetAsync(r);
        Assert.Equal(1, _fetcher.Calls);
        Assert.Equal(FailureCodes.NotFound, article.Failure!.Code);
        Assert.Equal(PipelineStep.Extract, article.Failure.Step);
    }

    [Fact]
    public async Task Javascript_only_pages_fail_with_a_readable_reason()
    {
        _fetcher.Default = () => Pages.Page("<html><body><div id=\"root\"></div><noscript>You need to enable JavaScript to run this app.</noscript><script src=a.js></script></body></html>");
        var r = await CreateAsync();

        Assert.Equal(ProcessResult.Failed, await Processor().ProcessAsync(r, Ct));
        Assert.Equal("Page needs JavaScript", (await GetAsync(r)).Failure!.Message);
    }

    [Fact]
    public async Task Retry_resumes_at_the_failed_step_without_fetching_again()
    {
        _rewriter.FailWith = PipelineException.Permanent(FailureCodes.AiRequestRejected, "The AI service rejected the request");
        var r = await CreateAsync();
        Assert.Equal(ProcessResult.Failed, await Processor().ProcessAsync(r, Ct));
        Assert.Equal(PipelineStep.Rewrite, (await GetAsync(r)).Failure!.Step);

        _rewriter.FailWith = null;
        Assert.Equal(CommandStatus.Ok, (await Commands().RetryAsync(r, Ct)).Status);
        Assert.Equal(ProcessResult.Completed, await Processor().ProcessAsync(r, Ct));

        Assert.Equal(ArticleStatus.Ready, (await GetAsync(r)).Status);
        Assert.Equal(1, _fetcher.Calls); // the extracted text was reused
    }

    [Fact]
    public async Task Changing_the_voice_reuses_the_spoken_text_and_replaces_the_audio()
    {
        var r = await CreateAsync();
        await Processor().ProcessAsync(r, Ct);
        var oldBlob = (await GetAsync(r)).Audio!.BlobName;

        var change = await Commands().ChangeVoiceAsync(r, "en-GB-RyanNeural", Ct);
        Assert.Equal(CommandStatus.Ok, change.Status);
        Assert.Equal(oldBlob, change.Article!.Audio!.BlobName); // old audio stays playable meanwhile
        Assert.Equal(ProcessResult.Completed, await Processor().ProcessAsync(r, Ct));

        var article = await GetAsync(r);
        Assert.Equal("en-GB-RyanNeural", article.Audio!.Voice);
        Assert.NotEqual(oldBlob, article.Audio.BlobName);
        Assert.Equal(1, _fetcher.Calls);
        Assert.Equal(1, _rewriter.Calls);
        Assert.Single(_audio.Committed); // old voice's file was removed
        Assert.Contains("en-GB-RyanNeural", _speech.Voices);
    }

    [Fact]
    public async Task Synthesis_resumes_at_the_first_missing_chunk()
    {
        _speech.CrashAfter = 2;
        var r = await CreateAsync();
        Assert.Equal(ProcessResult.RetryScheduled, await Processor().ProcessAsync(r, Ct));
        Assert.Equal(2, _audio.Staged.Count);
        Assert.Empty(_audio.Committed);

        _speech.CrashAfter = null;
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(ProcessResult.Completed, await Processor().ProcessAsync(r, Ct));

        var article = await GetAsync(r);
        var chunks = TextChunker.Split((await _store.GetScriptAsync(r, Ct))!.Text, _speech.MaxCharsPerRequest).Count;
        Assert.Equal(chunks, _speech.Calls); // no chunk was synthesized twice
        Assert.Equal(chunks * 3, _audio.Committed[article.Audio!.BlobName].Length);
        Assert.Equal(1, _rewriter.Calls);
    }

    [Fact]
    public async Task A_crashed_workers_article_is_picked_up_after_its_lease_expires()
    {
        var r = await CreateAsync();
        await SimulateCrashedClaimAsync(r);

        Assert.Equal(ProcessResult.NotDue, await Processor().ProcessAsync(r, Ct)); // lease still held
        Assert.Empty(await _store.QueryDueAsync(_time.Now, 10, Ct));

        _time.Advance(TimeSpan.FromSeconds(121));
        Assert.Contains(r, await _store.QueryDueAsync(_time.Now, 10, Ct));
        Assert.Equal(ProcessResult.Completed, await Processor().ProcessAsync(r, Ct));
        Assert.Equal(2, (await GetAsync(r)).Steps.Extract.Attempts);
    }

    [Fact]
    public async Task A_crash_loop_is_bounded_by_max_attempts()
    {
        var r = await CreateAsync();
        for (var i = 0; i < 3; i++)
        {
            await SimulateCrashedClaimAsync(r);
            _time.Advance(TimeSpan.FromSeconds(121));
        }

        Assert.Equal(ProcessResult.Failed, await Processor().ProcessAsync(r, Ct));
        var article = await GetAsync(r);
        Assert.Equal(FailureCodes.Interrupted, article.Failure!.Code);
        Assert.Equal(0, _fetcher.Calls);
    }

    [Fact]
    public async Task Losing_ownership_mid_step_writes_nothing()
    {
        var r = await CreateAsync();
        _fetcher.Default = () =>
        {
            // While we fetch, someone else (a second worker after a lease expiry) updates the article.
            var current = _store.GetArticleAsync(r, Ct).Result!;
            var other = AppJson.DeepClone(current);
            other.Lease = new LeaseInfo { Owner = "other-worker", ExpiresAt = _time.Now.AddMinutes(2) };
            _store.ReplaceArticleAsync(other, current.ETag!, Ct).Wait();
            return Pages.Page(Pages.ArticleHtml());
        };

        Assert.Equal(ProcessResult.LeaseLost, await Processor().ProcessAsync(r, Ct));
        Assert.Null(await _store.GetSourceAsync(r, Ct)); // step output not committed
        Assert.Equal("other-worker", (await GetAsync(r)).Lease!.Owner);
    }

    [Fact]
    public async Task Creating_with_the_same_client_id_is_idempotent()
    {
        var id = Guid.NewGuid().ToString();
        var first = await Commands().CreateAsync("me", id, "https://example.com/a", null, Ct);
        var second = await Commands().CreateAsync("me", id, "https://example.com/a", null, Ct);
        var different = await Commands().CreateAsync("me", id, "https://example.com/b", null, Ct);

        Assert.Equal(CommandStatus.Created, first.Status);
        Assert.Equal(CommandStatus.Ok, second.Status);
        Assert.Equal(CommandStatus.Conflict, different.Status);
    }

    [Fact]
    public async Task Retry_is_only_allowed_for_failed_articles()
    {
        var r = await CreateAsync();
        Assert.Equal(CommandStatus.Conflict, (await Commands().RetryAsync(r, Ct)).Status);
    }

    private async Task SimulateCrashedClaimAsync(ArticleRef r)
    {
        // What a worker writes when it claims an article – and then dies without writing anything else.
        var doc = await GetAsync(r);
        var claimed = AppJson.DeepClone(doc);
        claimed.Status = ArticleStatus.Processing;
        claimed.Lease = new LeaseInfo { Owner = "dead-worker", ExpiresAt = _time.Now.AddSeconds(120) };
        claimed.DueAt = claimed.Lease.ExpiresAt;
        claimed.Steps[claimed.Step].Attempts++;
        claimed.Steps[claimed.Step].Status = StepStatus.Running;
        await _store.ReplaceArticleAsync(claimed, doc.ETag!, Ct);
    }
}
