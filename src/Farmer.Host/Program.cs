using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Middleware;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Farmer.Host.Models;
using Farmer.Host.Services;
using Farmer.Host.Telemetry;
using Farmer.Tools;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<FarmerSettings>(builder.Configuration.GetSection(FarmerSettings.SectionName));

// Infrastructure services (Singleton — long-lived, thread-safe)
builder.Services.AddSingleton<ISshService, SshService>();
builder.Services.AddSingleton<IMappedDriveReader, MappedDriveReader>();
builder.Services.AddSingleton<IRunStore, FileRunStore>();
builder.Services.AddSingleton<IVmManager, VmManager>();

// Workflow stages (Transient — factory creates fresh per run)
builder.Services.AddTransient<CreateRunStage>();
builder.Services.AddTransient<LoadPromptsStage>();
builder.Services.AddTransient<ReserveVmStage>();
builder.Services.AddTransient<DeliverStage>();
builder.Services.AddTransient<DispatchStage>();
builder.Services.AddTransient<CollectStage>();
builder.Services.AddTransient<ReviewStage>();

// Stateless middleware (Singleton — no per-run state)
builder.Services.AddSingleton<LoggingMiddleware>();
builder.Services.AddSingleton<TelemetryMiddleware>();
builder.Services.AddSingleton<HeartbeatMiddleware>();
// CostTrackingMiddleware is NOT registered — factory creates fresh per run

// Workflow orchestration
builder.Services.AddSingleton<WorkflowPipelineFactory>();
builder.Services.AddSingleton<BackgroundWorkflowRunner>();

// OpenTelemetry
builder.Services.AddFarmerTelemetry();

var app = builder.Build();

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.MapGet("/", () => Results.Ok(new
{
    service = "Farmer",
    version = "0.5.0",
    phase = "Phase 5 - OTel + API"
}));

// OpenAI-compatible completions endpoint
app.MapPost("/v1/chat/completions", (ChatCompletionRequest request, BackgroundWorkflowRunner runner) =>
{
    var lastMessage = request.Messages.LastOrDefault();
    if (lastMessage is null || string.IsNullOrWhiteSpace(lastMessage.Content))
    {
        return Results.BadRequest(new { error = "No message content provided" });
    }

    // Extract work request name from content (e.g., "load:react-grid-component")
    var content = lastMessage.Content.Trim();
    var workRequestName = content.StartsWith("load:", StringComparison.OrdinalIgnoreCase)
        ? content["load:".Length..].Trim()
        : content;

    var runId = runner.StartRun(workRequestName);
    return Results.Accepted($"/v1/runs/{runId}/status", ChatCompletionResponse.ForRun(runId));
});

// Run status endpoint
app.MapGet("/v1/runs/{runId}/status", async (string runId, IRunStore store) =>
{
    var status = await store.GetRunStatusAsync(runId);
    return status is null
        ? Results.NotFound(new { error = $"Run {runId} not found" })
        : Results.Ok(status);
});

// Run result endpoint
app.MapGet("/v1/runs/{runId}/result", async (string runId, IRunStore store) =>
{
    var status = await store.GetRunStatusAsync(runId);
    if (status is null)
    {
        return Results.NotFound(new { error = $"Run {runId} not found" });
    }

    if (status.Phase != Farmer.Core.Models.RunPhase.Complete &&
        status.Phase != Farmer.Core.Models.RunPhase.Failed)
    {
        return Results.Conflict(new { error = "Run still in progress", phase = status.Phase.ToString() });
    }

    return Results.Ok(new { status });
});

app.Run();
