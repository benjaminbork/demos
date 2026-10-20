using System.Net.Http.Headers;
using System.Security;
using System.Text;
using Azure.Core;

namespace AudioBlog.Core.Pipeline.Speech;

public interface ISpeechSynthesizer
{
    /// <summary>Max characters per request (the service caps one request at 10 minutes of audio).</summary>
    int MaxCharsPerRequest { get; }
    /// <summary>Constant bit rate of the produced MP3, used to estimate the duration.</summary>
    int BitrateKbps { get; }
    Task<byte[]> SynthesizeMp3Async(string text, string voice, CancellationToken ct);
}

public sealed class SpeechOptions
{
    public const string Section = "Speech";

    /// <summary>Azure region of the Speech resource, e.g. "westeurope".</summary>
    public string? Region { get; set; }
    /// <summary>Full ARM resource id of the Speech resource (needed for Entra ID tokens).</summary>
    public string? ResourceId { get; set; }
    /// <summary>Optional override of the synthesis endpoint.</summary>
    public string? Endpoint { get; set; }
    public string OutputFormat { get; set; } = "audio-24khz-48kbitrate-mono-mp3";
    public int BitrateKbps { get; set; } = 48;
    /// <summary>~4,000 characters is roughly 4–5 minutes of speech, well below the 10 minute per request limit.</summary>
    public int MaxCharsPerRequest { get; set; } = 4_000;
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Azure AI Speech text to speech over REST with a Microsoft Entra token ("aad#{resourceId}#{token}").
/// REST instead of the Speech SDK keeps the container free of native dependencies.
/// </summary>
public sealed class AzureSpeechSynthesizer(HttpClient http, SpeechOptions options, TokenCredential credential) : ISpeechSynthesizer
{
    public const string HttpClientName = "azure-speech";
    private static readonly TokenRequestContext Scope = new(["https://cognitiveservices.azure.com/.default"]);

    public int MaxCharsPerRequest => options.MaxCharsPerRequest;
    public int BitrateKbps => options.BitrateKbps;

    private Uri Endpoint => new(options.Endpoint
        ?? $"https://{options.Region ?? throw new InvalidOperationException("Speech:Region must be configured.")}.tts.speech.microsoft.com/cognitiveservices/v1");

    public async Task<byte[]> SynthesizeMp3Async(string text, string voice, CancellationToken ct)
    {
        var resourceId = options.ResourceId ?? throw new InvalidOperationException("Speech:ResourceId must be configured.");
        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(Scope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw PipelineException.Retryable(FailureCodes.NetworkError, "Couldn't authenticate against the speech service", ex.Message, inner: ex);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(BuildSsml(text, voice), Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ssml+xml") { CharSet = "utf-8" };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"aad#{resourceId}#{token.Token}");
        request.Headers.Add("X-Microsoft-OutputFormat", options.OutputFormat);
        request.Headers.UserAgent.ParseAdd("AudioBlog/1.0");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                var audio = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                if (audio.Length == 0)
                    throw PipelineException.Retryable(FailureCodes.Unexpected, "The speech service returned no audio");
                return audio;
            }

            var status = (int)response.StatusCode;
            var detail = $"HTTP {status}: {Truncate(await response.Content.ReadAsStringAsync(timeout.Token))}";
            var retryAfter = response.Headers.RetryAfter?.Delta;
            throw status switch
            {
                429 => PipelineException.Retryable(FailureCodes.Throttled, "The speech service is busy (rate limit)", detail, retryAfter),
                _ when PipelineException.IsTransientStatus(status) => PipelineException.Retryable(FailureCodes.ServerError, "The speech service had a temporary problem", detail, retryAfter),
                _ => PipelineException.Permanent(FailureCodes.SpeechRequestRejected, "The speech service rejected the request", detail),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw PipelineException.Retryable(FailureCodes.Timeout, "The speech service took too long to respond");
        }
        catch (HttpRequestException ex)
        {
            throw PipelineException.Retryable(FailureCodes.NetworkError, "Couldn't reach the speech service", ex.Message, inner: ex);
        }
    }

    internal static string BuildSsml(string text, string voice)
    {
        var locale = LocaleOf(voice);
        var sb = new StringBuilder();
        sb.Append($"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{locale}\">");
        sb.Append($"<voice name=\"{SecurityElement.Escape(voice)}\">");
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            sb.Append("<p>").Append(SecurityElement.Escape(paragraph)).Append("</p>");
        sb.Append("</voice></speak>");
        return sb.ToString();
    }

    /// <summary>"en-US-AvaMultilingualNeural" → "en-US".</summary>
    internal static string LocaleOf(string voice)
    {
        var parts = voice.Split('-');
        return parts.Length >= 3 ? $"{parts[0]}-{parts[1]}" : "en-US";
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>
/// Offline stand-in for local development without Azure AI Speech: produces a valid, silent MP3
/// whose length matches what the narration would roughly take.
/// </summary>
public sealed class FakeSpeechSynthesizer : ISpeechSynthesizer
{
    public int MaxCharsPerRequest => 1_500;
    public int BitrateKbps => SilentMp3.BitrateKbps;

    public async Task<byte[]> SynthesizeMp3Async(string text, string voice, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(700), ct);
        // ~15 characters per second of speech.
        return SilentMp3.Create(TimeSpan.FromSeconds(Math.Max(1, text.Length / 15.0)));
    }
}

public static class SilentMp3
{
    public const int BitrateKbps = 32;
    // MPEG-1 Layer III, 32 kbit/s, 48 kHz, mono, no CRC: 96 bytes and 24 ms per frame.
    private static readonly byte[] Header = [0xFF, 0xFB, 0x14, 0xC0];
    private const int FrameBytes = 96;
    private const double FrameSeconds = 0.024;

    public static byte[] Create(TimeSpan duration)
    {
        var frames = (int)Math.Ceiling(duration.TotalSeconds / FrameSeconds);
        var bytes = new byte[frames * FrameBytes];
        for (var i = 0; i < frames; i++)
            Header.CopyTo(bytes, i * FrameBytes);
        return bytes;
    }
}
