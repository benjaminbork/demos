using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using AudioBlog.Core.Pipeline.Fetching;
using SmartReader;

namespace AudioBlog.Core.Pipeline.Extraction;

public sealed record ExtractedArticle(string? Title, string? Byline, string? SiteName, string? Language, string Text, bool Truncated);

/// <summary>
/// Extracts the main article with SmartReader and turns its cleaned HTML into lightly structured text
/// (headings, lists, tables, code and images are marked so the rewrite can describe them in sentences).
/// When nothing readable is found, it explains why: JavaScript-only page, paywall, bot protection, or empty.
/// </summary>
public sealed partial class ArticleExtractor(PipelineOptions options)
{
    private const int MinReadableChars = 250;
    private const int PaywallTeaserChars = 2500;

    public ExtractedArticle Extract(FetchedPage page)
    {
        var signals = PageSignals.Analyze(page.Html);
        if (signals.BotChallenge)
            throw PipelineException.Permanent(FailureCodes.BotProtection, "The website blocks automated readers (bot protection)");

        Article article;
        try
        {
            // Parse the HTML we fetched ourselves. (Careful: Reader.ParseArticle(string, string) would download the URL.)
            using var reader = new Reader(page.FinalUrl.ToString(), page.Html);
            article = reader.GetArticle();
        }
        catch (Exception ex)
        {
            throw PipelineException.Permanent(FailureCodes.EmptyContent, "The page could not be parsed", ex.Message, ex);
        }

        var text = article.IsReadable && !string.IsNullOrWhiteSpace(article.Content)
            ? StructuredText.FromHtml(article.Content)
            : "";

        if (text.Length < MinReadableChars)
        {
            if (signals.JavaScriptRequired)
                throw PipelineException.Permanent(FailureCodes.JavaScriptRequired, "Page needs JavaScript", signals.Reason);
            if (signals.Paywall)
                throw PipelineException.Permanent(FailureCodes.Paywall, "Article is behind a paywall", signals.Reason);
            throw PipelineException.Permanent(FailureCodes.EmptyContent, "No readable article text was found on this page",
                article.Errors.FirstOrDefault()?.Message);
        }

        if (signals.Paywall && text.Length < PaywallTeaserChars)
            throw PipelineException.Permanent(FailureCodes.Paywall, "Article is behind a paywall (only a preview is visible)", signals.Reason);

        var truncated = false;
        if (text.Length > options.MaxSourceChars)
        {
            var cut = text.LastIndexOf("\n\n", options.MaxSourceChars, StringComparison.Ordinal);
            text = text[..(cut > options.MaxSourceChars / 2 ? cut : options.MaxSourceChars)].TrimEnd();
            truncated = true;
        }

        return new ExtractedArticle(
            Clean(article.Title),
            Clean(article.Byline ?? article.Author),
            Clean(article.SiteName),
            Clean(article.Language),
            text,
            truncated);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : WhitespaceRegex().Replace(value, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>Cheap heuristics on the raw HTML that explain why a page has no readable text.</summary>
public sealed partial record PageSignals(bool JavaScriptRequired, bool Paywall, bool BotChallenge, string? Reason)
{
    public static PageSignals Analyze(string html)
    {
        var lower = html.Length > 400_000 ? html[..400_000].ToLowerInvariant() : html.ToLowerInvariant();

        string? botReason = BotMarkers.FirstOrDefault(lower.Contains);

        string? paywallReason = PaywallJsonLd().IsMatch(lower)
            ? "isAccessibleForFree: false"
            : PaywallMarkers.FirstOrDefault(lower.Contains);

        string? jsReason = null;
        if (NoScriptMessage().IsMatch(lower))
            jsReason = "noscript asks to enable JavaScript";
        else if (EmptySpaRoot().IsMatch(lower))
            jsReason = "empty single-page-app root element";
        else
        {
            var document = new HtmlParser().ParseDocument(html);
            var scripts = document.Scripts.Length;
            foreach (var junk in document.QuerySelectorAll("script, style, noscript, template").ToList())
                junk.Remove();
            var visible = document.Body?.TextContent.Trim().Length ?? 0;
            if (scripts >= 3 && visible < 400)
                jsReason = $"{scripts} scripts but only {visible} characters of visible text";
        }

        return new PageSignals(jsReason is not null, paywallReason is not null, botReason is not null,
            botReason ?? paywallReason ?? jsReason);
    }

    private static readonly string[] BotMarkers =
    [
        "<title>just a moment...</title>", "cf-browser-verification", "challenges.cloudflare.com/turnstile",
        "attention required! | cloudflare", "px-captcha", "captcha-delivery.com", "please verify you are a human",
        "are you a robot", "enable javascript and cookies to continue",
    ];

    private static readonly string[] PaywallMarkers =
    [
        "subscribe to continue reading", "subscribe to read", "subscribe to unlock", "already a subscriber",
        "this article is for subscribers", "this content is for subscribers", "become a member to read",
        "member-only story", "to continue reading, subscribe", "create a free account to continue",
        "class=\"paywall", "id=\"paywall", "data-paywall", "piano-paywall", "tp-modal",
        "mit abo weiterlesen", "jetzt abonnieren und weiterlesen", "plus-artikel",
    ];

    [GeneratedRegex("\"isaccessibleforfree\"\\s*:\\s*\"?false")]
    private static partial Regex PaywallJsonLd();

    [GeneratedRegex(@"<noscript[^>]*>[^<]{0,300}(enable javascript|javascript is required|javascript enabled|turn on javascript|activate javascript|javascript aktivieren)", RegexOptions.Singleline)]
    private static partial Regex NoScriptMessage();

    [GeneratedRegex("<div[^>]+id=\"(root|app|__next|__nuxt|svelte)\"[^>]*>\\s*</div>")]
    private static partial Regex EmptySpaRoot();
}
