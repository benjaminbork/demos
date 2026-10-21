#!/usr/bin/env dotnet
// RU test: the three Cosmos DB queries the agent-kit review changed, before (round 1) vs after (round 2),
// on containers with 1, 5 and 10 PHYSICAL PARTITIONS.
//
//   list     ListArticlesAsync   ORDER BY c.createdAt DESC  →  ORDER BY c.userId ASC, c.type ASC, c.createdAt DESC
//   due      QueryDueAsync       no request options         →  MaxItemCount = maxItems
//   pending  CountPendingAsync   SELECT VALUE COUNT(1) ...  →  HasPendingAsync: SELECT TOP 1 VALUE 1 ... (MaxItemCount = 1)
//
// For every container (partition layout), data shape and size, each query pair runs several times against REAL Azure
// Cosmos DB. It records the request charge (RU), the number of physical partitions each query hit (from the per-partition
// query metrics), and checks that before and after return the same answer.
//
//   layouts  p1 / p5 / p10           containers with 1 / 5 / 10 physical partitions (infra/cosmos-partitions.bicep)
//   shapes   single                  all articles belong to one user (this app today: one user = one partition key prefix)
//            many                    articles spread over 1,000 users (multi-user future)
//   sizes    small / medium / large  1,000 / 10,000 / 100,000 articles
//   due      none, one, 10 %, all    articles with a dueAt in the past (due work, pending work)
//
// The before queries are the round-1 versions (as they were before the review); the after queries are copied verbatim from
// src/AudioBlog.Core/Data/CosmosArticleStore.cs. The only addition is reading the request charge and partition metrics.
//
// Setup:    az deployment group create -g rg-audioblog-partitions -f infra/cosmos-partitions.bicep -p developerPrincipalId=<you>
// Run:      dotnet run scripts/ru-test.cs -- --endpoint https://cosmos-audioblog-pt-xxx.documents.azure.com:443/
// Options:  --containers p1,p5,p10   --shapes single,many   --sizes small,medium,large   --queries list,due,pending
//           --repeat 5   --emulator (functional dry run against the local emulator; it reports no real RU)
// Auth: your `az login` (Entra ID). The account has keys disabled.
#:package Microsoft.Azure.Cosmos
#:package Azure.Identity
#:package Newtonsoft.Json

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

var opts = Options.Parse(args);
var sizes = new Dictionary<string, int> { ["small"] = 1_000, ["medium"] = 10_000, ["large"] = 100_000 };

using var client = opts.Emulator
    ? new CosmosClient(Options.EmulatorConnectionString, ClientOptions(gateway: true))
    : new CosmosClient(opts.Endpoint, new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = true }), ClientOptions(gateway: false));

if (opts.Emulator)
{
    Console.WriteLine("⚠ Emulator mode: functional dry run only. The emulator does not report real request charges.\n");
    var db = (await client.CreateDatabaseIfNotExistsAsync(Options.Database)).Database;
    foreach (var name in opts.Containers)
        await db.CreateContainerIfNotExistsAsync(new ContainerProperties(name, new List<string> { "/userId", "/articleId" }));
}

var results = new List<Result>();
var distribution = new List<Distribution>();
var errors = new List<string>();
var started = Stopwatch.StartNew();
var file = Path.Combine(Options.RepoRoot(), "docs", "ru-results", $"ru-test-partitions-{DateTime.UtcNow:yyyyMMdd-HHmmss}{(opts.Emulator ? "-emulator" : "")}.md");
Directory.CreateDirectory(Path.GetDirectoryName(file)!);
// The report is rewritten after every measurement, so a crash never loses results.
void Save() => File.WriteAllText(file, Report.Markdown(results, distribution, errors, opts, started.Elapsed));

foreach (var layout in opts.Containers)
{
    var container = client.GetContainer(Options.Database, layout);
    var ranges = await container.GetFeedRangesAsync();
    var partitions = ranges.Count;
    Console.WriteLine($"══ {layout}: {partitions} physical partition(s) ══════════════════════════");

    foreach (var shapeName in opts.Shapes)
    try
    {
        var shape = Shape.Get(shapeName);
        var sw = Stopwatch.StartNew();
        var cleared = await Data.ClearAsync(container);
        Console.WriteLine($"── {layout} / {shape.Name}: cleared {cleared:N0} items ({sw.Elapsed.TotalSeconds:N0} s)");

        foreach (var sizeName in opts.Sizes)
        {
            var total = sizes[sizeName];
            var ctx = new Context(layout, partitions, shape.Name, sizeName, total);

            sw.Restart();
            var loadRu = await Data.ResetAsync(container, shape, total);
            var withData = await Data.PartitionsWithDataAsync(container, ranges);
            distribution.Add(new Distribution(ctx, withData));
            Save();
            Console.WriteLine($"   {sizeName}: loaded {total:N0} articles ({loadRu:N0} RU, {sw.Elapsed.TotalSeconds:N0} s); data in {withData} of {partitions} physical partition(s)");

            // 1. List: per user, doesn't depend on dueAt, measured once per size.
            if (opts.Queries.Contains("list"))
            {
                var expected = Math.Min(shape.ArticlesOfListUser(total), Queries.ListPageSize);
                Print(await Measure(ctx, "list", $"first page (≤{Queries.ListPageSize})",
                    () => Queries.ListBeforeAsync(container, shape.ListUser), () => Queries.ListAfterAsync(container, shape.ListUser),
                    // Same first page, same order. (The emulator ignores the page size on ORDER BY queries, so compare the first page.)
                    (b, a) => b.Ids.Count >= expected && b.Ids.Take(expected).SequenceEqual(a.Ids.Take(expected)),
                    Calls.ListPerMonth));
            }

            // 2 + 3. Due sweep and pending check with a growing number of due articles.
            var dueNow = 0;
            foreach (var due in new[] { 0, 1, Math.Max(1, total / 10), total }.Distinct().Order())
            {
                if (due > dueNow)
                {
                    sw.Restart();
                    var patchRu = await Data.MakeDueAsync(container, shape, dueNow, due);
                    Console.WriteLine($"      made {due - dueNow:N0} more articles due ({patchRu:N0} RU, {sw.Elapsed.TotalSeconds:N0} s)");
                    dueNow = due;
                }

                if (opts.Queries.Contains("due"))
                    Print(await Measure(ctx, "due", $"{due:N0} due",
                        () => Queries.DueBeforeAsync(container), () => Queries.DueAfterAsync(container),
                        (b, a) => b.Value == Math.Min(due, Queries.SweepBatch) && a.Value == b.Value,
                        Calls.SweepPerMonth));

                if (opts.Queries.Contains("pending"))
                    Print(await Measure(ctx, "pending", $"{due:N0} pending",
                        () => Queries.CountPendingAsync(container), () => Queries.HasPendingAsync(container),
                        (b, a) => b.Value == due && (a.Value == 1) == (due > 0),
                        Calls.KeepAlivePerMonth));
            }
        }
    }
    catch (Exception ex)
    {
        // One failing layout/shape must not lose the others.
        var message = $"{layout} / {shapeName}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
        errors.Add(message);
        Console.WriteLine($"   ✗ {message} – continuing with the next one");
        Save();
    }
}

Save();
Console.WriteLine();
Console.WriteLine($"Done in {started.Elapsed.TotalMinutes:N1} min. Report saved to {file}");
return results.All(r => r.Correct) && errors.Count == 0 ? 0 : 1;

async Task<Result> Measure(Context ctx, string query, string scenario,
    Func<Task<Measurement>> before, Func<Task<Measurement>> after, Func<Measurement, Measurement, bool> sameAnswer, int callsPerMonth)
{
    var sw = Stopwatch.StartNew();
    // Warm-up (query plan, connections) so the first measured call isn't an outlier.
    await before();
    await after();

    var b = new List<Measurement>();
    var a = new List<Measurement>();
    for (var i = 0; i < opts.Repeat; i++)
    {
        b.Add(await before());
        a.Add(await after());
    }
    return new Result(ctx, query, scenario,
        Median(b.Select(m => m.Ru)), Median(a.Select(m => m.Ru)),
        b.Max(m => m.PartitionsHit), a.Max(m => m.PartitionsHit),
        b[0].Pages, a[0].Pages,
        b.Zip(a).All(pair => sameAnswer(pair.First, pair.Second)), callsPerMonth, sw.Elapsed);
}

Result Print(Result r)
{
    results.Add(r);
    Save();
    Console.WriteLine($"      {r.Query,-8} {r.Scenario,-20} before {r.BeforeRu,8:N2} RU [{r.BeforeHit,2} pp, {r.BeforePages} pg]  after {r.AfterRu,8:N2} RU [{r.AfterHit,2} pp, {r.AfterPages} pg]  saved {r.SavedPercent,6:N1} %  ({r.Elapsed.TotalSeconds:N1} s){(r.Correct ? "" : "  ✗ DIFFERENT RESULT")}");
    return r;
}

static CosmosClientOptions ClientOptions(bool gateway) => new()
{
    ConnectionMode = gateway ? ConnectionMode.Gateway : ConnectionMode.Direct,
    AllowBulkExecution = true, // only used for loading / clearing the test data
    MaxRetryAttemptsOnRateLimitedRequests = 50,
    MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromMinutes(2),
};

static double Median(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
}

record Context(string Layout, int Partitions, string Shape, string Size, int Total);

record Distribution(Context Ctx, int PartitionsWithData);

/// <summary>One query execution: request charge of all pages, physical partitions hit, and what it returned.</summary>
record Measurement(double Ru, int Pages, int PartitionsHit, int Value, IReadOnlyList<string> Ids);

record Result(Context Ctx, string Query, string Scenario, double BeforeRu, double AfterRu, int BeforeHit, int AfterHit,
    int BeforePages, int AfterPages, bool Correct, int CallsPerMonth, TimeSpan Elapsed)
{
    public double SavedRu => BeforeRu - AfterRu;
    public double SavedPercent => BeforeRu == 0 ? 0 : SavedRu / BeforeRu * 100;
}

/// <summary>How often the app runs each query (used for the RU / month columns).</summary>
static class Calls
{
    const int Minutes = 60 * 24 * 30;
    public const int KeepAlivePerMonth = Minutes;        // keep-alive: once a minute while the worker is awake
    public const int SweepPerMonth = Minutes * 4;        // due-work sweep: every 15 s
    public const int ListPerMonth = 0;                   // list: on page load and while polling; depends on usage
}

/// <summary>Collects request charge, pages and physical partitions over all pages of one query execution.</summary>
sealed class Pages
{
    readonly HashSet<string> _partitions = [];
    public double Ru { get; private set; }
    public int Count { get; private set; }

    public void Add<T>(FeedResponse<T> page)
    {
        Ru += page.RequestCharge;
        Count++;
        // Per-partition server-side query metrics: one entry per physical partition this page touched.
        foreach (var p in page.Diagnostics.GetQueryMetrics()?.PartitionedMetrics ?? [])
            _partitions.Add(p.FeedRange ?? p.PartitionKeyRangeId?.ToString() ?? "?");
    }

    public Measurement Result(int value, IReadOnlyList<string>? ids = null) => new(Ru, Count, _partitions.Count, value, ids ?? []);
}

/// <summary>Before/after versions of the three queries, verbatim apart from recording the request charge and partitions.</summary>
static class Queries
{
    public const int ListPageSize = 100; // the UI requests /api/articles?pageSize=100
    public const int SweepBatch = 100;   // DueWorkSweeper calls QueryDueAsync(now, 100, ct)
    static PartitionKey UserPrefix(string userId) => new PartitionKeyBuilder().Add(userId).Build();

    // ---------------------------------------------------------------- 1. ListArticlesAsync

    // BEFORE – round 1
    public static async Task<Measurement> ListBeforeAsync(Container articles, string userId)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.userId = @userId AND c.type = @type ORDER BY c.createdAt DESC")
            .WithParameter("@userId", userId)
            .WithParameter("@type", "article");

        using var iterator = articles.GetItemQueryIterator<ListRow>(query, null, new QueryRequestOptions
        {
            PartitionKey = UserPrefix(userId),
            MaxItemCount = ListPageSize,
        });

        var pages = new Pages();
        var page = await iterator.ReadNextAsync();
        pages.Add(page);
        return pages.Result(page.Count, page.Select(r => r.Id).ToList());
    }

    // AFTER – round 2
    public static async Task<Measurement> ListAfterAsync(Container articles, string userId)
    {
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.userId = @userId AND c.type = @type ORDER BY c.userId ASC, c.type ASC, c.createdAt DESC")
            .WithParameter("@userId", userId)
            .WithParameter("@type", "article");

        using var iterator = articles.GetItemQueryIterator<ListRow>(query, null, new QueryRequestOptions
        {
            PartitionKey = UserPrefix(userId),
            MaxItemCount = ListPageSize,
        });

        var pages = new Pages();
        var page = await iterator.ReadNextAsync();
        pages.Add(page);
        return pages.Result(page.Count, page.Select(r => r.Id).ToList());
    }

    // ---------------------------------------------------------------- 2. QueryDueAsync

    static QueryDefinition DueQuery(DateTimeOffset now, int maxItems) => new QueryDefinition(
            "SELECT TOP @max c.userId, c.articleId FROM c WHERE c.type = @type AND c.dueAt <= @now")
        .WithParameter("@max", maxItems)
        .WithParameter("@type", "article")
        .WithParameter("@now", Data.Ts(now.UtcDateTime));

    // BEFORE – round 1
    public static async Task<Measurement> DueBeforeAsync(Container articles)
    {
        var maxItems = SweepBatch;
        var query = DueQuery(DateTimeOffset.UtcNow, maxItems);

        var result = new List<DueRow>();
        var pages = new Pages();
        using var iterator = articles.GetItemQueryIterator<DueRow>(query);
        while (iterator.HasMoreResults && result.Count < maxItems)
        {
            var page = await iterator.ReadNextAsync();
            pages.Add(page);
            foreach (var row in page)
                result.Add(row);
        }
        return pages.Result(result.Count);
    }

    // AFTER – round 2
    public static async Task<Measurement> DueAfterAsync(Container articles)
    {
        var maxItems = SweepBatch;
        var query = DueQuery(DateTimeOffset.UtcNow, maxItems);

        var result = new List<DueRow>();
        var pages = new Pages();
        using var iterator = articles.GetItemQueryIterator<DueRow>(query, requestOptions: new QueryRequestOptions { MaxItemCount = maxItems });
        while (iterator.HasMoreResults && result.Count < maxItems)
        {
            var page = await iterator.ReadNextAsync();
            pages.Add(page);
            result.AddRange(page.Take(maxItems - result.Count));
        }
        return pages.Result(result.Count);
    }

    // ---------------------------------------------------------------- 3. CountPendingAsync → HasPendingAsync

    // BEFORE – round 1
    public static async Task<Measurement> CountPendingAsync(Container articles)
    {
        var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.type = @type AND IS_DEFINED(c.dueAt)")
            .WithParameter("@type", "article");
        using var iterator = articles.GetItemQueryIterator<int>(query);
        var total = 0;
        var pages = new Pages();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            pages.Add(page);
            total += page.Sum();
        }
        return pages.Result(total);
    }

    // AFTER – round 2
    public static async Task<Measurement> HasPendingAsync(Container articles)
    {
        var query = new QueryDefinition("SELECT TOP 1 VALUE 1 FROM c WHERE c.type = @type AND IS_DEFINED(c.dueAt)")
            .WithParameter("@type", "article");
        using var iterator = articles.GetItemQueryIterator<int>(query, requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
        var pages = new Pages();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            pages.Add(page);
            if (page.Count > 0)
                return pages.Result(1);
        }
        return pages.Result(0);
    }

    record ListRow(string Id, string CreatedAt);
    record DueRow(string UserId, string ArticleId);
}

/// <summary>How the test articles are spread over partition key prefixes (users).</summary>
sealed record Shape(string Name, int Users)
{
    public static Shape Get(string name) => name switch
    {
        "single" => new Shape("single", 1),     // this app today: one user
        "many" => new Shape("many", 1_000),     // multi-user: 1,000 users
        _ => throw new ArgumentException($"Unknown shape {name}"),
    };

    public string UserOf(int i) => Users == 1 ? "rutest" : $"u{i % Users:D4}";
    /// <summary>The user whose articles the list query reads.</summary>
    public string ListUser => UserOf(0);
    public int ArticlesOfListUser(int total) => Users == 1 ? total : (total + Users - 1) / Users;
}

/// <summary>Test data: article items shaped like the app's (same fields that are indexed and queried).</summary>
static class Data
{
    static string ArticleId(int i) => $"a{i:D6}";
    static PartitionKey Pk(string userId, string articleId) => new PartitionKeyBuilder().Add(userId).Add(articleId).Build();
    static readonly string Preview = string.Concat(Enumerable.Repeat("This is the spoken preview of a test article. ", 15));

    /// <summary>Deletes everything in the container (switching data shape). Returns the number of deleted items.</summary>
    public static async Task<int> ClearAsync(Container c)
    {
        var keys = new List<(string Id, string UserId, string ArticleId)>();
        using var iterator = c.GetItemQueryIterator<KeyRow>(new QueryDefinition("SELECT c.id, c.userId, c.articleId FROM c"),
            requestOptions: new QueryRequestOptions { MaxItemCount = 10_000 });
        while (iterator.HasMoreResults)
            foreach (var row in await iterator.ReadNextAsync())
                keys.Add((row.Id, row.UserId, row.ArticleId));

        await Bulk.RunAsync(keys, async k =>
        {
            try
            {
                return (await c.DeleteItemAsync<object>(k.Id, Pk(k.UserId, k.ArticleId))).RequestCharge;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return 0;
            }
        });
        return keys.Count;
    }

    /// <summary>Upserts articles 0..total-1 as finished (no dueAt). Sizes grow, so this also resets the previous size's dueAt.</summary>
    public static async Task<double> ResetAsync(Container c, Shape shape, int total)
    {
        var created = DateTime.UtcNow.AddDays(-30);
        return await Bulk.RunAsync(Enumerable.Range(0, total).ToList(), async i => (await c.UpsertItemAsync(new Dictionary<string, object>
        {
            ["id"] = ArticleId(i),
            ["userId"] = shape.UserOf(i),
            ["articleId"] = ArticleId(i),
            ["type"] = "article",
            ["schemaVersion"] = 1,
            ["url"] = $"https://example.com/articles/{i}",
            ["title"] = $"Test article {i}",
            ["status"] = "ready",
            ["step"] = "synthesize",
            ["voice"] = "en-US-AvaMultilingualNeural",
            ["spokenPreview"] = Preview,
            ["createdAt"] = Ts(created.AddMinutes(i)),
            ["updatedAt"] = Ts(created.AddMinutes(i)),
        }, Pk(shape.UserOf(i), ArticleId(i)), new ItemRequestOptions { EnableContentResponseOnWrite = false })).RequestCharge);
    }

    /// <summary>Gives articles [from, to) a dueAt in the past, i.e. makes them due (and pending) work.</summary>
    public static async Task<double> MakeDueAsync(Container c, Shape shape, int from, int to)
    {
        var due = Ts(DateTime.UtcNow.AddMinutes(-1));
        return await Bulk.RunAsync(Enumerable.Range(from, to - from).ToList(), async i => (await c.PatchItemAsync<object>(ArticleId(i), Pk(shape.UserOf(i), ArticleId(i)),
            [PatchOperation.Set("/dueAt", due), PatchOperation.Set("/status", "queued")],
            new PatchItemRequestOptions { EnableContentResponseOnWrite = false })).RequestCharge);
    }

    /// <summary>How many physical partitions (feed ranges) hold at least one item.</summary>
    public static async Task<int> PartitionsWithDataAsync(Container c, IReadOnlyList<FeedRange> ranges)
    {
        var withData = 0;
        foreach (var range in ranges)
        {
            using var iterator = c.GetItemQueryIterator<int>(range, new QueryDefinition("SELECT VALUE COUNT(1) FROM c"));
            var count = 0;
            while (iterator.HasMoreResults)
                count += (await iterator.ReadNextAsync()).Sum();
            if (count > 0)
                withData++;
        }
        return withData;
    }

    public static string Ts(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    record KeyRow(string Id, string UserId, string ArticleId);
}

/// <summary>
/// Runs data operations in chunks (instead of 100,000 requests at once) and retries transient errors
/// (408 timeout, 410, 449, 503). The SDK already retries 429 throttling itself.
/// </summary>
static class Bulk
{
    const int Chunk = 2_000;

    public static async Task<double> RunAsync<T>(IReadOnlyList<T> items, Func<T, Task<double>> operation)
    {
        double ru = 0;
        for (var i = 0; i < items.Count; i += Chunk)
            ru += (await Task.WhenAll(items.Skip(i).Take(Chunk).Select(item => WithRetry(() => operation(item))))).Sum();
        return ru;
    }

    static async Task<double> WithRetry(Func<Task<double>> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (CosmosException ex) when (attempt < 6 && (int)ex.StatusCode is 408 or 410 or 449 or 503)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
            }
        }
    }
}

static class Report
{
    static readonly (string Key, string Title, string Before, string After, string Notes)[] Sections =
    [
        ("list", "1. List articles, newest first (`ListArticlesAsync`)",
            "`... ORDER BY c.createdAt DESC`",
            "`... ORDER BY c.userId ASC, c.type ASC, c.createdAt DESC`",
            "Scoped to one user (partition key prefix). Runs on page load and every 2 s while an article is in progress. *Same result* = identical items in identical order."),
        ("due", "2. Due-work sweep (`QueryDueAsync`)",
            "`SELECT TOP @max c.userId, c.articleId FROM c WHERE c.type = @type AND c.dueAt <= @now`, no request options",
            "Same SQL, with `MaxItemCount = maxItems`",
            "Cross-partition. Runs every 15 s in the worker (172,800 calls / month) with `maxItems = 100`. *Same result* = same number of due articles."),
        ("pending", "3. Pending check for the scale-to-zero keep-alive (`CountPendingAsync` → `HasPendingAsync`)",
            "`SELECT VALUE COUNT(1) FROM c WHERE c.type = @type AND IS_DEFINED(c.dueAt)`",
            "`SELECT TOP 1 VALUE 1 FROM c WHERE c.type = @type AND IS_DEFINED(c.dueAt)` with `MaxItemCount = 1`",
            "Cross-partition. Runs once a minute while the worker is awake (43,200 calls / month). *Same result* = both agree whether anything is pending."),
    ];

    public static string Markdown(List<Result> results, List<Distribution> distribution, List<string> errors, Options opts, TimeSpan duration)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# RU test: Cosmos DB queries before and after the agent-kit review, with multiple physical partitions");
        sb.AppendLine();
        sb.AppendLine($"- Run: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC against {(opts.Emulator ? "the **emulator** (RU values not meaningful)" : "Azure Cosmos DB (provisioned throughput, `infra/cosmos-partitions.bicep`)")}, {duration.TotalMinutes:N0} min");
        sb.AppendLine($"- Each RU value is the median of {opts.Repeat} runs after one warm-up run; the RU of all pages of a query are summed.");
        sb.AppendLine("- *Partitions hit* = physical partitions the query touched (from the per-partition server-side query metrics), before → after.");
        sb.AppendLine("- Data shapes: **single** = all articles of one user (this app today), **many** = articles spread over 1,000 users.");
        sb.AppendLine("- Test data: articles shaped like the app's, in containers with the production partition key and indexing policy (`infra/modules/articles-container.bicep`).");
        sb.AppendLine("- Script: `scripts/ru-test.cs`");
        sb.AppendLine();
        if (errors.Count > 0)
        {
            sb.AppendLine("## Errors (these runs are incomplete)");
            sb.AppendLine();
            foreach (var e in errors)
                sb.AppendLine($"- {e}");
            sb.AppendLine();
        }
        sb.AppendLine("## Physical partitions holding data");
        sb.AppendLine();
        sb.AppendLine("| Container | Physical partitions | Shape | Articles | Partitions with data |");
        sb.AppendLine("|---|---:|---|---:|---:|");
        foreach (var d in distribution)
            sb.AppendLine($"| {d.Ctx.Layout} | {d.Ctx.Partitions} | {d.Ctx.Shape} | {d.Ctx.Total:N0} | {d.PartitionsWithData} |");

        foreach (var s in Sections.Where(s => results.Any(r => r.Query == s.Key)))
        {
            var rows = results.Where(r => r.Query == s.Key).ToList();
            var monthly = rows[0].CallsPerMonth > 0;
            sb.AppendLine();
            sb.AppendLine($"## {s.Title}");
            sb.AppendLine();
            sb.AppendLine($"- **Before:** {s.Before}");
            sb.AppendLine($"- **After:** {s.After}");
            sb.AppendLine($"- {s.Notes}");
            sb.AppendLine();
            sb.AppendLine("| Partitions | Shape | Articles | Scenario | Before RU | After RU | Saved RU | Saved % | Partitions hit | "
                + (monthly ? "Before RU / month | After RU / month | " : "") + "Same result |");
            sb.AppendLine("|---:|---|---:|---|---:|---:|---:|---:|:---:|" + (monthly ? "---:|---:|" : "") + ":---:|");
            foreach (var r in rows)
            {
                sb.Append($"| {r.Ctx.Partitions} | {r.Ctx.Shape} | {r.Ctx.Total:N0} | {r.Scenario} | {r.BeforeRu:N2} | {r.AfterRu:N2} | {r.SavedRu:N2} | {r.SavedPercent:N1} % | {r.BeforeHit} → {r.AfterHit} | ");
                if (monthly)
                    sb.Append($"{r.BeforeRu * r.CallsPerMonth:N0} | {r.AfterRu * r.CallsPerMonth:N0} | ");
                sb.AppendLine($"{(r.Correct ? "✓" : "✗")} |");
            }
            sb.AppendLine();
            sb.AppendLine("### Analysis");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

sealed class Options
{
    public const string Database = "rutest";
    public const string EmulatorConnectionString =
        "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;DisableServerCertificateValidation=True";
    static readonly string[] AllQueries = ["list", "due", "pending"];
    static readonly string[] AllSizes = ["small", "medium", "large"];
    static readonly string[] AllShapes = ["single", "many"];

    public string Endpoint { get; private set; } = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? "";
    public string[] Containers { get; private set; } = ["p1", "p5", "p10"];
    public string[] Shapes { get; private set; } = AllShapes;
    public string[] Queries { get; private set; } = AllQueries;
    public string[] Sizes { get; private set; } = AllSizes;
    public int Repeat { get; private set; } = 5;
    public bool Emulator { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--endpoint": o.Endpoint = args[++i]; break;
                case "--containers": o.Containers = List(args[++i]); break;
                case "--shapes": o.Shapes = List(args[++i]); break;
                case "--queries": o.Queries = List(args[++i]); break;
                case "--sizes": o.Sizes = List(args[++i]); break;
                case "--repeat": o.Repeat = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--emulator": o.Emulator = true; break;
                default: throw new ArgumentException($"Unknown argument {args[i]}");
            }
        }
        if (!o.Emulator && string.IsNullOrWhiteSpace(o.Endpoint))
            throw new ArgumentException("Pass --endpoint <cosmos endpoint> (output 'cosmosEndpoint' of infra/cosmos-partitions.bicep), set COSMOS_ENDPOINT, or use --emulator.");
        if (o.Sizes.Except(AllSizes).Any())
            throw new ArgumentException("--sizes accepts small, medium, large");
        if (o.Shapes.Except(AllShapes).Any())
            throw new ArgumentException("--shapes accepts single, many");
        if (o.Queries.Except(AllQueries).Any())
            throw new ArgumentException("--queries accepts list, due, pending");
        // Sizes must run in ascending order: each reset upserts a superset of the previous size's items.
        o.Sizes = AllSizes.Where(o.Sizes.Contains).ToArray();
        return o;
    }

    static string[] List(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AudioBlog.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Environment.CurrentDirectory;
    }
}
