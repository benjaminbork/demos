using System.Net;
using AudioBlog.Core.Pipeline;
using AudioBlog.Core.Pipeline.Extraction;
using AudioBlog.Core.Pipeline.Fetching;

namespace AudioBlog.Tests;

public class ExtractionAndTextTests
{
    private static readonly ArticleExtractor Extractor = new(new PipelineOptions());

    private static PipelineException ExtractFails(string html) =>
        Assert.Throws<PipelineException>(() => Extractor.Extract(Pages.Page(html)));

    [Fact]
    public void Readable_article_keeps_structure_markers_for_the_rewrite()
    {
        var article = Extractor.Extract(Pages.Page(Pages.ArticleHtml()));

        Assert.Contains("paragraph number 1", article.Text);
        Assert.Contains("[Table]", article.Text);
        Assert.Contains("| Free | 0 |", article.Text);
    }

    [Fact]
    public void Empty_spa_shell_is_reported_as_needing_javascript()
    {
        var error = ExtractFails("<html><body><div id=\"__next\"></div><script></script><script></script><script></script></body></html>");
        Assert.Equal(FailureCodes.JavaScriptRequired, error.Code);
        Assert.False(error.Transient);
    }

    [Fact]
    public void Paywalled_teaser_is_reported_as_paywall()
    {
        var html = """
            <html><head><script type="application/ld+json">{"@type":"NewsArticle","isAccessibleForFree":"False"}</script></head>
            <body><article><h1>Big story</h1><p>The first paragraph of a long investigative story that you can read for free before the wall.
            It is long enough to look like an article, with a few more words to pass the threshold for readable content here.</p>
            <div class="paywall">Subscribe to continue reading.</div></article></body></html>
            """;
        Assert.Equal(FailureCodes.Paywall, ExtractFails(html).Code);
    }

    [Fact]
    public void Page_without_text_is_reported_as_empty()
    {
        Assert.Equal(FailureCodes.EmptyContent, ExtractFails("<html><body><p>Hi.</p></body></html>").Code);
    }

    [Fact]
    public void Bot_challenge_is_detected()
    {
        Assert.Equal(FailureCodes.BotProtection, ExtractFails("<html><head><title>Just a moment...</title></head><body></body></html>").Code);
    }

    [Fact]
    public void Structured_text_marks_lists_code_and_images()
    {
        var text = StructuredText.FromHtml("""
            <h2>Setup</h2><ul><li>One</li><li>Two<ul><li>Nested</li></ul></li></ul>
            <pre><code>var x = 1;</code></pre><figure><img src="a.png" alt="A cat on a desk"><figcaption>Our office cat</figcaption></figure>
            """);

        Assert.Contains("## Setup", text);
        Assert.Contains("- One", text);
        Assert.Contains("  - Nested", text);
        Assert.Contains("[Code]\nvar x = 1;\n[/Code]", text);
        Assert.Contains("[Image: A cat on a desk – Our office cat]", text);
    }

    [Fact]
    public void Chunking_is_deterministic_and_respects_the_limit()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 40).Select(i =>
            string.Join(' ', Enumerable.Range(0, 30).Select(j => $"Sentence {i}.{j} has words."))));

        var a = TextChunker.Split(text, 1000);
        var b = TextChunker.Split(text, 1000);

        Assert.Equal(a, b);
        Assert.All(a, chunk => Assert.True(chunk.Length <= 1000));
        Assert.Equal(Words(text), a.SelectMany(Words));

        static IEnumerable<string> Words(string s) => s.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Ssrf_guard_only_allows_public_addresses(string ip, bool expected) =>
        Assert.Equal(expected, SafeSocketsHandler.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public void Ssml_is_escaped_and_uses_the_voice_locale()
    {
        var ssml = AudioBlog.Core.Pipeline.Speech.AzureSpeechSynthesizer.BuildSsml("Tom & Jerry <3\n\nSecond", "de-DE-SeraphinaMultilingualNeural");

        Assert.Contains("xml:lang=\"de-DE\"", ssml);
        Assert.Contains("<p>Tom &amp; Jerry &lt;3</p><p>Second</p>", ssml);
    }
}
