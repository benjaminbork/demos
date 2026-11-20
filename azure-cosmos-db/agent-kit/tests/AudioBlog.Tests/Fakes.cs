using System.Collections.Concurrent;
using AudioBlog.Core.Data;
using AudioBlog.Core.Domain;
using AudioBlog.Core.Json;
using AudioBlog.Core.Pipeline;
using AudioBlog.Core.Pipeline.Fetching;
using AudioBlog.Core.Pipeline.Rewriting;
using AudioBlog.Core.Pipeline.Speech;
using AudioBlog.Core.Storage;

namespace AudioBlog.Tests;

/// <summary>Mimics the Cosmos DB semantics the pipeline relies on: ETags, conflict on create, atomic batch.</summary>
internal sealed class InMemoryArticleStore : IArticleStore
{
    private readonly ConcurrentDictionary<string, (string Json, string ETag)> _items = new();
    private readonly object _lock = new();

    public int ArticleWrites { get; private set; }

    private static string Key(ArticleRef r, string id) => $"{r.UserId}|{r.ArticleId}|{id}";

    private T? Read<T>(ArticleRef r, string id) where T : class
    {
        if (!_items.TryGetValue(Key(r, id), out var entry)) return null;
        var doc = System.Text.Json.JsonSerializer.Deserialize<T>(entry.Json, AppJson.Options)!;
        if (doc is ArticleDocument a) a.ETag = entry.ETag;
        return doc;
    }

    private static string Serialize<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, AppJson.Options);

    public Task<ArticleDocument?> GetArticleAsync(ArticleRef article, CancellationToken ct) =>
        Task.FromResult(Read<ArticleDocument>(article, article.ArticleId));

    public Task<(ArticleDocument Article, bool Created)> CreateArticleAsync(ArticleDocument article, CancellationToken ct)
    {
        lock (_lock)
        {
            var key = Key(article.Ref, article.Id);
            if (_items.ContainsKey(key))
                return Task.FromResult((Read<ArticleDocument>(article.Ref, article.Id)!, false));
            _items[key] = (Serialize(article), Guid.NewGuid().ToString());
            ArticleWrites++;
            return Task.FromResult((Read<ArticleDocument>(article.Ref, article.Id)!, true));
        }
    }

    public Task<string> ReplaceArticleAsync(ArticleDocument article, string ifMatchETag, CancellationToken ct)
    {
        lock (_lock)
        {
            var key = Key(article.Ref, article.Id);
            if (!_items.TryGetValue(key, out var current) || current.ETag != ifMatchETag)
                throw new ConcurrencyException("etag mismatch");
            var etag = Guid.NewGuid().ToString();
            _items[key] = (Serialize(article), etag);
            ArticleWrites++;
            return Task.FromResult(etag);
        }
    }

    public Task<string> CommitStepAsync<TOutput>(ArticleDocument article, string ifMatchETag, TOutput output, CancellationToken ct) where TOutput : ContentDocument
    {
        lock (_lock)
        {
            var key = Key(article.Ref, article.Id);
            if (!_items.TryGetValue(key, out var current) || current.ETag != ifMatchETag)
                throw new ConcurrencyException("etag mismatch"); // nothing written: atomic
            _items[Key(article.Ref, output.Id)] = (Serialize(output), Guid.NewGuid().ToString());
            var etag = Guid.NewGuid().ToString();
            _items[key] = (Serialize(article), etag);
            ArticleWrites++;
            return Task.FromResult(etag);
        }
    }

    public Task<SourceDocument?> GetSourceAsync(ArticleRef article, CancellationToken ct) =>
        Task.FromResult(Read<SourceDocument>(article, SourceDocument.IdFor(article.ArticleId)));

    public Task<ScriptDocument?> GetScriptAsync(ArticleRef article, CancellationToken ct) =>
        Task.FromResult(Read<ScriptDocument>(article, ScriptDocument.IdFor(article.ArticleId)));

    public Task<ArticlePage> ListArticlesAsync(string userId, int pageSize, string? continuationToken, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<bool> HasPendingAsync(CancellationToken ct) =>
        Task.FromResult(_items.Keys.Select(k => k.Split('|')).Where(p => p[1] == p[2])
            .Any(p => Read<ArticleDocument>(new ArticleRef(p[0], p[1]), p[1])!.DueAt is not null));

    public Task<IReadOnlyList<ArticleRef>> QueryDueAsync(DateTimeOffset now, int maxItems, CancellationToken ct)
    {
        var due = _items.Keys
            .Select(k => k.Split('|'))
            .Where(p => p[1] == p[2])
            .Select(p => Read<ArticleDocument>(new ArticleRef(p[0], p[1]), p[1])!)
            .Where(a => a.DueAt is { } d && d <= now)
            .Select(a => a.Ref)
            .Take(maxItems)
            .ToList();
        return Task.FromResult<IReadOnlyList<ArticleRef>>(due);
    }
}

internal sealed class InMemoryAudioStore : IAudioStore
{
    public readonly ConcurrentDictionary<string, byte[]> Committed = new();
    public readonly ConcurrentDictionary<(string Blob, int Index), byte[]> Staged = new();

    public Task<bool> ExistsAsync(string blobName, CancellationToken ct) => Task.FromResult(Committed.ContainsKey(blobName));

    public Task<IReadOnlySet<int>> GetStagedChunksAsync(string blobName, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<int>>(Staged.Keys.Where(k => k.Blob == blobName).Select(k => k.Index).ToHashSet());

    public Task StageChunkAsync(string blobName, int index, ReadOnlyMemory<byte> mp3, CancellationToken ct)
    {
        Staged[(blobName, index)] = mp3.ToArray();
        return Task.CompletedTask;
    }

    public Task CommitAsync(string blobName, int chunkCount, CancellationToken ct)
    {
        Committed[blobName] = Enumerable.Range(0, chunkCount).SelectMany(i => Staged[(blobName, i)]).ToArray();
        foreach (var key in Staged.Keys.Where(k => k.Blob == blobName).ToList())
            Staged.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<long> GetSizeAsync(string blobName, CancellationToken ct) => Task.FromResult((long)Committed[blobName].Length);

    public Task DeleteIfExistsAsync(string blobName, CancellationToken ct)
    {
        Committed.TryRemove(blobName, out _);
        return Task.CompletedTask;
    }

    public Task<Uri> GetReadUrlAsync(string blobName, TimeSpan lifetime, string? downloadFileName, CancellationToken ct) =>
        Task.FromResult(new Uri($"https://example.test/{blobName}"));
}

internal sealed class ScriptedFetcher : IPageFetcher
{
    public int Calls { get; private set; }
    public Queue<Func<FetchedPage>> Responses { get; } = new();
    public Func<FetchedPage>? Default { get; set; }

    public Task<FetchedPage> FetchAsync(string url, CancellationToken ct)
    {
        Calls++;
        var next = Responses.Count > 0 ? Responses.Dequeue() : Default ?? throw new InvalidOperationException("no response scripted");
        return Task.FromResult(next());
    }
}

internal sealed class CountingRewriter : ITextRewriter
{
    public int Calls { get; private set; }
    public Exception? FailWith { get; set; }
    public string ModelName => "test";

    public Task<string> RewriteAsync(SourceDocument source, CancellationToken ct)
    {
        Calls++;
        if (FailWith is not null) throw FailWith;
        return Task.FromResult("Spoken: " + source.Text);
    }
}

internal sealed class CountingSpeech : ISpeechSynthesizer
{
    public int Calls { get; private set; }
    /// <summary>Throw after this many successful chunk syntheses (simulates a crash mid-synthesis).</summary>
    public int? CrashAfter { get; set; }
    public List<string> Voices { get; } = [];
    public int MaxCharsPerRequest => 200;
    public int BitrateKbps => 32;

    public Task<byte[]> SynthesizeMp3Async(string text, string voice, CancellationToken ct)
    {
        if (CrashAfter is { } n && Calls >= n)
            throw new InvalidOperationException("simulated crash");
        Calls++;
        Voices.Add(voice);
        return Task.FromResult(new byte[] { 1, 2, 3 });
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

internal static class Pages
{
    public static string ArticleHtml(int paragraphs = 8) =>
        "<html><head><title>Test article</title></head><body><article><h1>Test article</h1>" +
        string.Concat(Enumerable.Range(1, paragraphs).Select(i =>
            $"<p>This is paragraph number {i} of the test article. It has enough words to be considered readable content by the extractor, " +
            "and it talks about Cosmos DB, change feeds and reliable background processing in some detail.</p>")) +
        "<table><tr><th>Plan</th><th>Price</th></tr><tr><td>Free</td><td>0</td></tr></table>" +
        "</article></body></html>";

    public static FetchedPage Page(string html) => new(new Uri("https://example.com/a"), new Uri("https://example.com/a"), html);
}
