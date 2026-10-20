using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace AudioBlog.Core.Pipeline.Fetching;

public sealed class FetchOptions
{
    public const string Section = "Fetch";

    public int TimeoutSeconds { get; set; } = 30;
    public long MaxBytes { get; set; } = 5 * 1024 * 1024;
    /// <summary>Allow fetching from private/loopback addresses. Only for local testing.</summary>
    public bool AllowPrivateNetworks { get; set; }
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36 AudioBlog/1.0";
}

public sealed record FetchedPage(Uri RequestedUrl, Uri FinalUrl, string Html);

/// <summary>
/// Downloads the article HTML in our own code (the model never browses). Classifies every failure as
/// transient (timeouts, 408/429/5xx, connection resets) or permanent (404, 401/403, non-HTML, ...).
/// </summary>
public sealed class ArticleFetcher(HttpClient http, FetchOptions options) : IPageFetcher
{
    public const string HttpClientName = "article-fetcher";

    static ArticleFetcher() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // windows-1252 & co.

    public async Task<FetchedPage> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw PipelineException.Permanent(FailureCodes.InvalidUrl, "This is not a valid web address");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(options.UserAgent);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");
        request.Headers.AcceptLanguage.ParseAdd("en;q=0.9,*;q=0.5");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw PipelineException.Retryable(FailureCodes.Timeout, "The website took too long to respond");
        }
        catch (HttpRequestException ex)
        {
            throw Classify(ex);
        }

        using (response)
        {
            ThrowForStatus(response);

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
                throw PipelineException.Permanent(FailureCodes.NotHtml, $"This link is not a web page ({mediaType})");

            if (response.Content.Headers.ContentLength > options.MaxBytes)
                throw PipelineException.Permanent(FailureCodes.TooLarge, "The page is too large to process");

            byte[] body;
            try
            {
                body = await ReadLimitedAsync(response.Content, options.MaxBytes, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw PipelineException.Retryable(FailureCodes.Timeout, "The website took too long to respond");
            }
            catch (IOException ex)
            {
                throw PipelineException.Retryable(FailureCodes.NetworkError, "The connection to the website was interrupted", ex.Message, inner: ex);
            }

            var html = Decode(body, response.Content.Headers.ContentType);
            return new FetchedPage(uri, response.RequestMessage?.RequestUri ?? uri, html);
        }
    }

    private static void ThrowForStatus(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
            return;

        var detail = $"HTTP {status} {response.ReasonPhrase}";
        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

        throw status switch
        {
            404 or 410 => PipelineException.Permanent(FailureCodes.NotFound, "The page doesn't exist (404)", detail),
            402 => PipelineException.Permanent(FailureCodes.Paywall, "Article is behind a paywall", detail),
            401 or 403 => PipelineException.Permanent(FailureCodes.AccessDenied, "The website refused access – it may need a login or block automated readers", detail),
            451 => PipelineException.Permanent(FailureCodes.AccessDenied, "The page is unavailable for legal reasons", detail),
            429 => PipelineException.Retryable(FailureCodes.Throttled, "The website is rate limiting us", detail, retryAfter),
            _ when PipelineException.IsTransientStatus(status) => PipelineException.Retryable(FailureCodes.ServerError, "The website had a temporary problem", detail, retryAfter),
            _ => PipelineException.Permanent(FailureCodes.HttpError, $"The website answered with an error ({status})", detail),
        };
    }

    private static PipelineException Classify(HttpRequestException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case BlockedAddressException blocked:
                    return PipelineException.Permanent(FailureCodes.BlockedAddress, "This address points to a private network and can't be read", blocked.Message);
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }:
                    return PipelineException.Permanent(FailureCodes.HostNotFound, "The website could not be found", e.Message);
            }
        }
        if (ex.HttpRequestError == HttpRequestError.NameResolutionError)
            return PipelineException.Permanent(FailureCodes.HostNotFound, "The website could not be found", ex.Message);
        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            return PipelineException.Permanent(FailureCodes.HttpError, "The website's secure connection (TLS certificate) is invalid", ex.Message);
        return PipelineException.Retryable(FailureCodes.NetworkError, "Couldn't connect to the website", ex.Message, inner: ex);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw PipelineException.Permanent(FailureCodes.TooLarge, "The page is too large to process");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string Decode(byte[] body, MediaTypeHeaderValue? contentType)
    {
        Encoding encoding = Encoding.UTF8;
        var charset = contentType?.CharSet?.Trim('"');
        if (!string.IsNullOrEmpty(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); } catch (ArgumentException) { }
        }
        else if (SniffMetaCharset(body) is { } meta)
        {
            try { encoding = Encoding.GetEncoding(meta); } catch (ArgumentException) { }
        }
        return encoding.GetString(body);
    }

    private static string? SniffMetaCharset(byte[] body)
    {
        var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 4096));
        var match = System.Text.RegularExpressions.Regex.Match(head, "charset\\s*=\\s*[\"']?([A-Za-z0-9_\\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}

public sealed class BlockedAddressException(string message) : IOException(message);

/// <summary>
/// SSRF protection: resolve DNS ourselves and refuse to connect to loopback, private, link-local
/// (including the cloud metadata endpoint) or other non-public addresses. Done in ConnectCallback so it
/// also covers redirects and DNS rebinding.
/// </summary>
public static class SafeSocketsHandler
{
    public static SocketsHttpHandler Create(FetchOptions options) => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        UseCookies = false,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = options.AllowPrivateNetworks ? addresses : addresses.Where(IsPublic).ToArray();
            if (allowed.Length == 0)
                throw new BlockedAddressException($"{context.DnsEndPoint.Host} resolves to a non-public address");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0
                || b[0] == 10
                || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                  // link-local, cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))  // benchmarking
                || b[0] >= 224);                                 // multicast, reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC);                       // unique local fc00::/7
        }

        return false;
    }
}
