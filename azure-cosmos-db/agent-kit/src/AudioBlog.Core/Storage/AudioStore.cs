using System.Text;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using AudioBlog.Core.Domain;

namespace AudioBlog.Core.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Blob endpoint for Entra ID auth, e.g. https://myacct.blob.core.windows.net.</summary>
    public string? BlobEndpoint { get; set; }
    /// <summary>Only for Azurite locally ("UseDevelopmentStorage=true"). Leave empty in Azure.</summary>
    public string? ConnectionString { get; set; }
    public string AudioContainer { get; set; } = "audio";
    public bool EnsureCreated { get; set; }
}

public interface IAudioStore
{
    /// <summary>True if the final (committed) MP3 exists.</summary>
    Task<bool> ExistsAsync(string blobName, CancellationToken ct);
    /// <summary>Indexes of chunks already uploaded but not yet committed (resume after a crash).</summary>
    Task<IReadOnlySet<int>> GetStagedChunksAsync(string blobName, CancellationToken ct);
    Task StageChunkAsync(string blobName, int index, ReadOnlyMemory<byte> mp3, CancellationToken ct);
    /// <summary>Atomically assembles chunks 0..count-1 into the final blob.</summary>
    Task CommitAsync(string blobName, int chunkCount, CancellationToken ct);
    Task<long> GetSizeAsync(string blobName, CancellationToken ct);
    Task DeleteIfExistsAsync(string blobName, CancellationToken ct);
    /// <summary>Short-lived read-only SAS URL. With a file name, the browser downloads instead of playing.</summary>
    Task<Uri> GetReadUrlAsync(string blobName, TimeSpan lifetime, string? downloadFileName, CancellationToken ct);
}

public static class AudioNaming
{
    /// <summary>
    /// Deterministic name: the same script read by the same voice always maps to the same blob.
    /// Re-running the step overwrites instead of duplicating; a different voice is a different blob.
    /// </summary>
    public static string BlobNameFor(ArticleRef article, string scriptHash, string voice) =>
        $"{article.UserId}/{article.ArticleId}/{scriptHash[..16]}-{voice}.mp3";
}

/// <summary>
/// Stores the MP3 as a block blob. Each TTS chunk is staged as a block with a deterministic block id;
/// the blob only becomes visible when the block list is committed. Uncommitted blocks survive a worker
/// crash (up to 7 days), so synthesis resumes at the first missing chunk, and readers never see a partial file.
/// </summary>
public sealed class BlobAudioStore : IAudioStore
{
    private readonly BlobServiceClient _service;
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private UserDelegationKey? _delegationKey;

    public BlobAudioStore(StorageOptions options, TokenCredential credential)
    {
        _service = !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new BlobServiceClient(options.ConnectionString)
            : new BlobServiceClient(new Uri(options.BlobEndpoint ?? throw new InvalidOperationException("Storage:BlobEndpoint or Storage:ConnectionString must be configured.")), credential);
        _container = _service.GetBlobContainerClient(options.AudioContainer);
    }

    public Task EnsureCreatedAsync(CancellationToken ct) =>
        _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);

    private BlockBlobClient Blob(string name) => _container.GetBlockBlobClient(name);

    private static string BlockId(int index) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"chunk-{index:D6}"));

    public async Task<bool> ExistsAsync(string blobName, CancellationToken ct) =>
        (await Blob(blobName).ExistsAsync(ct)).Value;

    public async Task<IReadOnlySet<int>> GetStagedChunksAsync(string blobName, CancellationToken ct)
    {
        try
        {
            var list = await Blob(blobName).GetBlockListAsync(BlockListTypes.Uncommitted, cancellationToken: ct);
            var result = new HashSet<int>();
            foreach (var block in list.Value.UncommittedBlocks)
            {
                var name = Encoding.ASCII.GetString(Convert.FromBase64String(block.Name));
                if (name.StartsWith("chunk-", StringComparison.Ordinal) && int.TryParse(name.AsSpan(6), out var index))
                    result.Add(index);
            }
            return result;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new HashSet<int>();
        }
    }

    public async Task StageChunkAsync(string blobName, int index, ReadOnlyMemory<byte> mp3, CancellationToken ct)
    {
        using var stream = new MemoryStream(mp3.ToArray(), writable: false);
        await Blob(blobName).StageBlockAsync(BlockId(index), stream, cancellationToken: ct);
    }

    public async Task CommitAsync(string blobName, int chunkCount, CancellationToken ct)
    {
        var ids = Enumerable.Range(0, chunkCount).Select(BlockId).ToList();
        await Blob(blobName).CommitBlockListAsync(ids, new CommitBlockListOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "audio/mpeg", CacheControl = "private, max-age=3600" },
        }, ct);
    }

    public async Task<long> GetSizeAsync(string blobName, CancellationToken ct) =>
        (await Blob(blobName).GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength;

    public Task DeleteIfExistsAsync(string blobName, CancellationToken ct) =>
        Blob(blobName).DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);

    public async Task<Uri> GetReadUrlAsync(string blobName, TimeSpan lifetime, string? downloadFileName, CancellationToken ct)
    {
        var blob = Blob(blobName);
        var builder = new BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(lifetime))
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5), // clock skew
            Protocol = blob.Uri.Scheme == "https" ? SasProtocol.Https : SasProtocol.HttpsAndHttp,
            ContentType = "audio/mpeg",
        };
        if (downloadFileName is not null)
            builder.ContentDisposition = ContentDisposition(downloadFileName);

        // Azurite / account key: service SAS. In Azure shared keys are disabled, so we sign with a
        // user delegation key obtained via the managed identity (no secrets anywhere).
        if (blob.CanGenerateSasUri)
            return blob.GenerateSasUri(builder);

        var key = await GetDelegationKeyAsync(ct);
        var sas = builder.ToSasQueryParameters(key, _service.AccountName);
        return new BlobUriBuilder(blob.Uri) { Sas = sas }.ToUri();
    }

    private async Task<UserDelegationKey> GetDelegationKeyAsync(CancellationToken ct)
    {
        if (_delegationKey is { } cached && cached.SignedExpiresOn > DateTimeOffset.UtcNow.AddHours(2))
            return cached;

        await _keyLock.WaitAsync(ct);
        try
        {
            if (_delegationKey is { } again && again.SignedExpiresOn > DateTimeOffset.UtcNow.AddHours(2))
                return again;
            var response = await _service.GetUserDelegationKeyAsync(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(12), ct);
            return _delegationKey = response.Value;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static string ContentDisposition(string fileName)
    {
        var ascii = new string(fileName.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_').ToArray());
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }
}
