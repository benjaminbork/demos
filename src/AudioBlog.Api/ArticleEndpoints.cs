using AudioBlog.Core;
using AudioBlog.Core.Articles;
using AudioBlog.Core.Data;
using AudioBlog.Core.Domain;
using AudioBlog.Core.Storage;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AudioBlog.Api;

public static class ArticleEndpoints
{
    public static void MapArticleEndpoints(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api");

        api.MapGet("/voices", (VoiceOptions voices) => Results.Ok(new
        {
            voices.Default,
            Voices = voices.Available.Count > 0 ? voices.Available : [new VoiceInfo { Id = voices.Default, Label = voices.Default }],
        }));

        var articles = api.MapGroup("/articles");

        articles.MapGet("/", async (IArticleStore store, AppOptions app, PipelineSettings settings, WorkerWaker waker, int? pageSize, string? continuationToken, CancellationToken ct) =>
        {
            var page = await store.ListArticlesAsync(app.UserId, Math.Clamp(pageSize ?? 50, 1, 100), continuationToken, ct);
            if (page.Items.Any(a => !a.IsTerminal))
                waker.Wake(force: false); // someone is watching unfinished work: make sure a worker runs
            return Results.Ok(new { Items = page.Items.Select(a => ArticleDto.From(a, settings.MaxAttempts)), page.ContinuationToken });
        });

        articles.MapPost("/", async (CreateArticleRequest request, ArticleCommands commands, AppOptions app, PipelineSettings settings, WorkerWaker waker, CancellationToken ct) =>
        {
            // Responds as soon as the article item is written. That write *is* the scheduling: the worker
            // picks it up from the Cosmos DB change feed. No processing happens in the request.
            var result = await commands.CreateAsync(app.UserId, request.Id, request.Url, request.Voice, ct);
            if (result.Status == CommandStatus.Created)
                waker.Wake();
            return result.Status switch
            {
                CommandStatus.Created => Results.Created($"/api/articles/{result.Article!.Id}", ArticleDto.From(result.Article, settings.MaxAttempts)),
                CommandStatus.Ok => Results.Ok(ArticleDto.From(result.Article!, settings.MaxAttempts)),
                _ => ToProblem(result),
            };
        }).RequireRateLimiting("writes");

        articles.MapGet("/{id}", async (string id, IArticleStore store, AppOptions app, PipelineSettings settings, CancellationToken ct) =>
            await store.GetArticleAsync(new ArticleRef(app.UserId, id), ct) is { } article
                ? Results.Ok(ArticleDto.From(article, settings.MaxAttempts))
                : Results.NotFound());

        articles.MapGet("/{id}/text", async (string id, IArticleStore store, AppOptions app, CancellationToken ct) =>
            await store.GetScriptAsync(new ArticleRef(app.UserId, id), ct) is { } script
                ? Results.Ok(new { script.Text, script.Model, script.CreatedAt })
                : Results.NotFound());

        articles.MapPost("/{id}/retry", async (string id, ArticleCommands commands, AppOptions app, PipelineSettings settings, WorkerWaker waker, CancellationToken ct) =>
            ToResult(await commands.RetryAsync(new ArticleRef(app.UserId, id), ct), settings, waker))
            .RequireRateLimiting("writes");

        articles.MapPost("/{id}/voice", async (string id, ChangeVoiceRequest request, ArticleCommands commands, AppOptions app, PipelineSettings settings, WorkerWaker waker, CancellationToken ct) =>
            ToResult(await commands.ChangeVoiceAsync(new ArticleRef(app.UserId, id), request.Voice, ct), settings, waker))
            .RequireRateLimiting("writes");

        // The blob is private. This endpoint hands out a short-lived, read-only SAS URL via redirect, so the
        // <audio> element and the download link always get a fresh one.
        articles.MapGet("/{id}/audio", async (string id, bool? download, IArticleStore store, IAudioStore audio, AppOptions app, AudioSettings settings, CancellationToken ct) =>
        {
            var article = await store.GetArticleAsync(new ArticleRef(app.UserId, id), ct);
            if (article?.Audio is null)
                return Results.NotFound();
            var fileName = download == true ? FileNameFor(article) : null;
            var url = await audio.GetReadUrlAsync(article.Audio.BlobName, TimeSpan.FromMinutes(settings.SasMinutes), fileName, ct);
            return Results.Redirect(url.ToString(), permanent: false);
        });
    }

    private static IResult ToResult(CommandResult result, PipelineSettings settings, WorkerWaker waker)
    {
        if (result.Status is not (CommandStatus.Ok or CommandStatus.Created))
            return ToProblem(result);
        if (!result.Article!.IsTerminal)
            waker.Wake();
        return Results.Ok(ArticleDto.From(result.Article, settings.MaxAttempts));
    }

    private static IResult ToProblem(CommandResult result) => result.Status switch
    {
        CommandStatus.NotFound => Results.NotFound(),
        CommandStatus.Invalid => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"),
        _ => Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict, title: "Conflict"),
    };

    private static string FileNameFor(ArticleDocument article)
    {
        var name = string.IsNullOrWhiteSpace(article.Title) ? article.SiteName ?? "article" : article.Title;
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim();
        if (clean.Length > 80) clean = clean[..80].Trim();
        return $"{clean}.mp3";
    }
}

public sealed record CreateArticleRequest(string Url, string? Id, string? Voice);
public sealed record ChangeVoiceRequest(string Voice);

public sealed class PipelineSettings(AudioBlog.Core.Pipeline.PipelineOptions options)
{
    public int MaxAttempts => options.MaxAttempts;
}

public sealed class AudioSettings
{
    public int SasMinutes { get; set; } = 60;
}
