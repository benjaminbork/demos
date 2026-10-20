using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AudioBlog.Api;
using AudioBlog.Core;
using AudioBlog.Core.Json;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAudioBlogCore(builder.Configuration);
builder.Services.AddSingleton<PipelineSettings>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<WorkerWaker>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Audio").Get<AudioSettings>() ?? new AudioSettings());
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    foreach (var converter in AppJson.Options.Converters)
        o.SerializerOptions.Converters.Add(converter);
});

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().WithMethods("GET", "POST")));

// Every new article costs AI money; a simple global cap protects a public hobby endpoint from abuse.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("writes", _ => RateLimitPartition.GetFixedWindowLimiter("global", _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 20,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear(); // Container Apps ingress is the only way in
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseCors();
app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
app.MapArticleEndpoints();

app.Run();
