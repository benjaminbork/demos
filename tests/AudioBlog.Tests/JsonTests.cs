using System.Text.Json;
using System.Text.Json.Nodes;
using AudioBlog.Core.Domain;
using AudioBlog.Core.Json;

namespace AudioBlog.Tests;

public class JsonTests
{
    [Fact]
    public void Article_item_has_the_documented_shape()
    {
        var article = new ArticleDocument
        {
            Id = "a1",
            UserId = "me",
            ArticleId = "a1",
            Url = "https://example.com",
            Voice = "en-US-AvaMultilingualNeural",
            CreatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            DueAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 500, TimeSpan.FromHours(2)),
        };

        var json = JsonNode.Parse(JsonSerializer.Serialize(article, AppJson.Options))!.AsObject();

        Assert.Equal("article", (string?)json["type"]);
        Assert.Equal("queued", (string?)json["status"]);
        Assert.Equal("extract", (string?)json["step"]);
        // Computed helpers must not be persisted.
        Assert.False(json.ContainsKey("ref"));
        Assert.False(json.ContainsKey("isTerminal"));
        // Nulls are omitted: a finished article has no dueAt at all, so the scheduler query skips it.
        Assert.False(json.ContainsKey("lease"));
        // Fixed-width UTC timestamps compare correctly as strings inside Cosmos DB queries.
        Assert.Equal("2026-01-02T03:04:05.0000000Z", (string?)json["createdAt"]);
        Assert.Equal("2026-01-02T01:04:05.5000000Z", (string?)json["dueAt"]);
        Assert.Equal(ArticleDocument.CurrentSchemaVersion, (int?)json["schemaVersion"]);
    }

    [Fact]
    public void Items_written_before_schema_versioning_read_as_version_1()
    {
        const string legacy = """{"id":"a1","userId":"me","articleId":"a1","type":"article","url":"https://example.com","voice":"v"}""";

        Assert.Equal(1, JsonSerializer.Deserialize<ArticleDocument>(legacy, AppJson.Options)!.SchemaVersion);

        var script = new ScriptDocument { Id = "a1:script", UserId = "me", ArticleId = "a1", Text = "t", SourceHash = "s", ScriptHash = "h" };
        var json = JsonNode.Parse(JsonSerializer.Serialize(script, AppJson.Options))!.AsObject();
        Assert.Equal(ContentDocument.CurrentSchemaVersion, (int?)json["schemaVersion"]);
    }
}
