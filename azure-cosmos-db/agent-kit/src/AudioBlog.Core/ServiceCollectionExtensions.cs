using Azure.Core;
using Azure.Identity;
using AudioBlog.Core.Articles;
using AudioBlog.Core.Data;
using AudioBlog.Core.Pipeline;
using AudioBlog.Core.Pipeline.Extraction;
using AudioBlog.Core.Pipeline.Fetching;
using AudioBlog.Core.Pipeline.Rewriting;
using AudioBlog.Core.Pipeline.Speech;
using AudioBlog.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AudioBlog.Core;

public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>Single-user app: every article belongs to this user id (the first level of the partition key).</summary>
    public string UserId { get; set; } = "me";
    /// <summary>"Azure" uses Azure OpenAI + Azure AI Speech; "Fake" runs fully offline for local development.</summary>
    public string AiMode { get; set; } = "Azure";
}

public static class ServiceCollectionExtensions
{
    private static T Bind<T>(IConfiguration config, string section) where T : new()
    {
        var value = new T();
        config.GetSection(section).Bind(value);
        return value;
    }

    /// <summary>Shared by API and worker: credentials, Cosmos DB, Blob Storage, commands.</summary>
    public static IServiceCollection AddAudioBlogCore(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(Bind<AppOptions>(config, AppOptions.Section));
        services.AddSingleton(Bind<CosmosOptions>(config, CosmosOptions.Section));
        services.AddSingleton(Bind<StorageOptions>(config, StorageOptions.Section));
        services.AddSingleton(Bind<PipelineOptions>(config, PipelineOptions.Section));
        services.AddSingleton(Bind<VoiceOptions>(config, VoiceOptions.Section));
        services.AddSingleton(TimeProvider.System);

        // Managed identity in Azure (AZURE_CLIENT_ID selects the user-assigned identity),
        // az login / Visual Studio locally. No keys anywhere. Without AZURE_CLIENT_ID (local dev) the
        // managed identity probe is skipped: it would otherwise fail instead of falling through to az login.
        services.AddSingleton<TokenCredential>(_ =>
        {
            var clientId = config["AZURE_CLIENT_ID"];
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = clientId,
                ExcludeManagedIdentityCredential = string.IsNullOrWhiteSpace(clientId),
                ExcludeWorkloadIdentityCredential = string.IsNullOrWhiteSpace(clientId),
            });
        });

        services.AddSingleton(sp => new CosmosContainers(sp.GetRequiredService<CosmosOptions>(), sp.GetRequiredService<TokenCredential>()));
        services.AddSingleton<IArticleStore, CosmosArticleStore>();
        services.AddSingleton(sp => new BlobAudioStore(sp.GetRequiredService<StorageOptions>(), sp.GetRequiredService<TokenCredential>()));
        services.AddSingleton<IAudioStore>(sp => sp.GetRequiredService<BlobAudioStore>());
        services.AddSingleton<ArticleCommands>();
        services.AddHostedService<LocalBootstrapper>();
        return services;
    }

    /// <summary>Worker only: the processing pipeline and its external services.</summary>
    public static IServiceCollection AddAudioBlogPipeline(this IServiceCollection services, IConfiguration config)
    {
        var app = Bind<AppOptions>(config, AppOptions.Section);
        var fetch = Bind<FetchOptions>(config, FetchOptions.Section);
        services.AddSingleton(fetch);
        services.AddSingleton(Bind<AzureOpenAIOptions>(config, AzureOpenAIOptions.Section));
        services.AddSingleton(Bind<SpeechOptions>(config, SpeechOptions.Section));

        services.AddHttpClient(ArticleFetcher.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => SafeSocketsHandler.Create(fetch));
        services.AddSingleton<IPageFetcher>(sp => new ArticleFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ArticleFetcher.HttpClientName), fetch));
        services.AddSingleton<ArticleExtractor>();

        if (string.Equals(app.AiMode, "Fake", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ITextRewriter, FakeRewriter>();
            services.AddSingleton<ISpeechSynthesizer, FakeSpeechSynthesizer>();
        }
        else
        {
            services.AddSingleton<ITextRewriter>(sp => new AzureOpenAIRewriter(
                sp.GetRequiredService<AzureOpenAIOptions>(), sp.GetRequiredService<TokenCredential>()));
            services.AddHttpClient(AzureSpeechSynthesizer.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
            services.AddSingleton<ISpeechSynthesizer>(sp => new AzureSpeechSynthesizer(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(AzureSpeechSynthesizer.HttpClientName),
                sp.GetRequiredService<SpeechOptions>(),
                sp.GetRequiredService<TokenCredential>()));
        }

        services.AddSingleton<ArticleProcessor>();
        return services;
    }
}

/// <summary>Creates the Cosmos DB database/containers and the blob container when running against local emulators.</summary>
internal sealed class LocalBootstrapper(CosmosContainers cosmos, BlobAudioStore blobs, StorageOptions storage, ILogger<LocalBootstrapper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (cosmos.Options.EnsureCreated)
            await cosmos.EnsureCreatedAsync(logger, ct);
        if (storage.EnsureCreated)
            await blobs.EnsureCreatedAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
