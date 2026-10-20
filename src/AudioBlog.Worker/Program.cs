using AudioBlog.Core;
using AudioBlog.Worker;

var builder = WebApplication.CreateBuilder(args);

// Optional, git-ignored overrides for local runs, e.g. real Azure AI instead of the fakes
// (written by scripts/setup-local-ai.ps1). Environment variables still win.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

builder.Services
    .AddAudioBlogCore(builder.Configuration)
    .AddAudioBlogPipeline(builder.Configuration);

var workerOptions = new WorkerOptions();
builder.Configuration.GetSection(WorkerOptions.Section).Bind(workerOptions);
builder.Services.AddSingleton(workerOptions);

// Order matters: the dispatcher must exist before the triggers start feeding it.
builder.Services.AddSingleton<WorkDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkDispatcher>());
builder.Services.AddHostedService<ChangeFeedListener>();
builder.Services.AddSingleton<DueWorkSweeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DueWorkSweeper>());
builder.Services.AddHostedService<KeepAliveService>();

// Container Apps sends SIGTERM and waits 30 s: leave time to hand in-flight articles back.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Scale-to-zero mode: the API calls this after every write. The request itself is what makes Container Apps
// start a replica; once running, the sweep looks for due work right away. It carries no data on purpose –
// the work is in Cosmos DB, so a lost wake-up call only delays processing, it never loses anything.
app.MapMethods("/wake", ["GET", "POST"], (DueWorkSweeper sweeper) =>
{
    sweeper.SweepNow();
    return Results.Accepted();
});

app.Run();
