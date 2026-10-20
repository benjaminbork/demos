using System.Collections.ObjectModel;
using Azure.Core;
using AudioBlog.Core.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace AudioBlog.Core.Data;

public sealed class CosmosOptions
{
    public const string Section = "Cosmos";

    /// <summary>Account endpoint for Entra ID (managed identity) auth, e.g. https://myacct.documents.azure.com:443/.</summary>
    public string? Endpoint { get; set; }
    /// <summary>Only for the local emulator (its well-known key is not a secret). Leave empty in Azure.</summary>
    public string? ConnectionString { get; set; }
    public string Database { get; set; } = "audioblog";
    public string ArticlesContainer { get; set; } = "articles";
    public string LeasesContainer { get; set; } = "leases";
    /// <summary>The emulator only supports gateway mode.</summary>
    public bool UseGatewayMode { get; set; }
    /// <summary>Create database and containers on startup (local dev). In Azure, Bicep owns them.</summary>
    public bool EnsureCreated { get; set; }

    /// <summary>Point reads/writes slower than this are logged with full diagnostics.</summary>
    public TimeSpan SlowPointOperationThreshold { get; set; } = TimeSpan.FromMilliseconds(100);
    /// <summary>Queries and batches slower than this are logged with full diagnostics.</summary>
    public TimeSpan SlowQueryThreshold { get; set; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Requests that cost more RU than this are logged with full diagnostics.</summary>
    public double HighRequestChargeThreshold { get; set; } = 50;
}

/// <summary>Singleton holder for the CosmosClient and its containers (one client per process, as recommended).</summary>
public sealed class CosmosContainers : IDisposable
{
    public CosmosContainers(CosmosOptions options, TokenCredential credential)
    {
        Options = options;
        var clientOptions = new CosmosClientOptions
        {
            ApplicationName = "audioblog",
            UseSystemTextJsonSerializerWithOptions = AppJson.Options,
            ConnectionMode = options.UseGatewayMode ? ConnectionMode.Gateway : ConnectionMode.Direct,
            // The SDK retries 429s itself (with the server-provided back-off); give it a sane ceiling.
            MaxRetryAttemptsOnRateLimitedRequests = 9,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30),
            // Distributed tracing (Azure.Cosmos.Operation activity source) with the same thresholds the
            // store uses for its own slow/expensive request log, so both point at the same requests.
            CosmosClientTelemetryOptions = new CosmosClientTelemetryOptions
            {
                DisableDistributedTracing = false,
                CosmosThresholdOptions = new CosmosThresholdOptions
                {
                    PointOperationLatencyThreshold = options.SlowPointOperationThreshold,
                    NonPointOperationLatencyThreshold = options.SlowQueryThreshold,
                    RequestChargeThreshold = options.HighRequestChargeThreshold,
                },
            },
        };

        Client = !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new CosmosClient(options.ConnectionString, clientOptions)
            : new CosmosClient(options.Endpoint ?? throw new InvalidOperationException("Cosmos:Endpoint or Cosmos:ConnectionString must be configured."), credential, clientOptions);

        Database = Client.GetDatabase(options.Database);
        Articles = Database.GetContainer(options.ArticlesContainer);
        Leases = Database.GetContainer(options.LeasesContainer);
    }

    public CosmosOptions Options { get; }
    public CosmosClient Client { get; }
    public Database Database { get; }
    public Container Articles { get; }
    public Container Leases { get; }

    public void Dispose() => Client.Dispose();

    /// <summary>
    /// The container definitions. Bicep (infra/modules/cosmos.bicep) declares the same thing for Azure;
    /// this is used to bootstrap the local emulator. Keep the two identical.
    /// </summary>
    public static ContainerProperties ArticlesContainerProperties(string name)
    {
        var props = new ContainerProperties(name, partitionKeyPaths: new Collection<string> { "/userId", "/articleId" });

        // Index only what we query on. Big text fields (source/script text) are never indexed,
        // which keeps write RU charges low for the large items.
        props.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        props.IndexingPolicy.Automatic = true;
        props.IndexingPolicy.IncludedPaths.Clear();
        foreach (var path in new[] { "/userId/?", "/type/?", "/createdAt/?", "/dueAt/?" })
            props.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = path });
        props.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });
        props.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/\"_etag\"/?" });

        // List: WHERE userId = @u AND type = 'article' ORDER BY userId, type, createdAt DESC
        props.IndexingPolicy.CompositeIndexes.Add(new Collection<CompositePath>
        {
            new() { Path = "/userId", Order = CompositePathSortOrder.Ascending },
            new() { Path = "/type", Order = CompositePathSortOrder.Ascending },
            new() { Path = "/createdAt", Order = CompositePathSortOrder.Descending },
        });
        // Scheduler: WHERE type = 'article' AND dueAt <= @now
        props.IndexingPolicy.CompositeIndexes.Add(new Collection<CompositePath>
        {
            new() { Path = "/type", Order = CompositePathSortOrder.Ascending },
            new() { Path = "/dueAt", Order = CompositePathSortOrder.Ascending },
        });
        return props;
    }

    public async Task EnsureCreatedAsync(ILogger logger, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var db = (await Client.CreateDatabaseIfNotExistsAsync(Options.Database, cancellationToken: ct)).Database;
                var wanted = ArticlesContainerProperties(Options.ArticlesContainer);
                var articles = await db.CreateContainerIfNotExistsAsync(wanted, cancellationToken: ct);
                await SyncIndexingPolicyAsync(articles, wanted, logger, ct);
                await db.CreateContainerIfNotExistsAsync(new ContainerProperties(Options.LeasesContainer, "/id"), cancellationToken: ct);
                logger.LogInformation("Cosmos DB database '{Database}' and containers are ready", Options.Database);
                return;
            }
            catch (Exception ex) when (attempt < 20 && !ct.IsCancellationRequested)
            {
                // The emulator can take a while to accept requests after `docker compose up`.
                logger.LogWarning("Cosmos DB not reachable yet (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    /// <summary>
    /// CreateContainerIfNotExists ignores the indexing policy of an existing container, so a container
    /// created by an older version would silently keep its old indexes. Replace the policy when it differs.
    /// </summary>
    private static async Task SyncIndexingPolicyAsync(ContainerResponse existing, ContainerProperties wanted, ILogger logger, CancellationToken ct)
    {
        if (existing.StatusCode == System.Net.HttpStatusCode.Created)
            return;

        var current = existing.Resource;
        var currentSignature = IndexingSignature(current.IndexingPolicy);
        var wantedSignature = IndexingSignature(wanted.IndexingPolicy);
        if (currentSignature == wantedSignature)
            return;

        logger.LogInformation("Indexing policy of container '{Container}' is outdated; replacing it. Current: {Current}. Wanted: {Wanted}",
            current.Id, currentSignature, wantedSignature);
        current.IndexingPolicy = wanted.IndexingPolicy;
        await existing.Container.ReplaceContainerAsync(current, cancellationToken: ct);
    }

    /// <summary>System paths (/"_ts"/?, /"_etag"/?) are skipped: the service adds them on its own.</summary>
    private static string IndexingSignature(IndexingPolicy policy) => string.Join('|',
        policy.IndexingMode,
        string.Join(',', policy.IncludedPaths.Select(p => p.Path).Where(p => !p.StartsWith("/\"_", StringComparison.Ordinal)).Order(StringComparer.Ordinal)),
        string.Join(',', policy.ExcludedPaths.Select(p => p.Path).Where(p => !p.StartsWith("/\"_", StringComparison.Ordinal)).Order(StringComparer.Ordinal)),
        string.Join(';', policy.CompositeIndexes.Select(c => string.Join(',', c.Select(p => $"{p.Path} {p.Order}"))).Order(StringComparer.Ordinal)));
}
