using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Middleware;
using Farmer.Core.Telemetry;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Farmer.Agents;
using Farmer.Host.Services;
using Farmer.Messaging;
using Farmer.Tools;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
builder.Services.Configure<FarmerSettings>(builder.Configuration.GetSection(FarmerSettings.SectionName));

var telemetrySettings = builder.Configuration
    .GetSection("Farmer:Telemetry")
    .Get<TelemetrySettings>() ?? new TelemetrySettings();

// --- Infrastructure services ---
builder.Services.AddSingleton<ISshService, SshService>();
builder.Services.AddSingleton<IMappedDriveReader, MappedDriveReader>();
builder.Services.AddSingleton<IRunStore, FileRunStore>();
builder.Services.AddSingleton<IVmManager, VmManager>();

// --- Workflow stages (resolved by WorkflowPipelineFactory in pipeline order) ---
builder.Services.AddSingleton<CreateRunStage>();
builder.Services.AddSingleton<LoadPromptsStage>();
builder.Services.AddSingleton<ReserveVmStage>();
builder.Services.AddSingleton<DeliverStage>();
builder.Services.AddSingleton<DispatchStage>();
builder.Services.AddSingleton<CollectStage>();
builder.Services.AddSingleton<RetrospectiveStage>();

// --- Stateless middleware (resolved by WorkflowPipelineFactory) ---
// CostTrackingMiddleware is NOT registered here — the factory creates a
// fresh instance per run so per-run cost state never bleeds across runs.
builder.Services.AddSingleton<TelemetryMiddleware>();
builder.Services.AddSingleton<LoggingMiddleware>();
builder.Services.AddSingleton<EventingMiddleware>();
builder.Services.AddSingleton<HeartbeatMiddleware>();

// --- Retrospective agent (MAF + OpenAI, via Farmer.Agents) ---
builder.Services.AddFarmerAgents(builder.Configuration);

// --- NATS messaging: JetStream events + ObjectStore artifacts ---
builder.Services.AddFarmerMessaging(builder.Configuration);

// --- Workflow factory ---
// Builds (RunWorkflow, CostTrackingMiddleware) per call. RunWorkflow is no
// longer a singleton because the cost tracker it composes must be per-run.
builder.Services.AddSingleton<WorkflowPipelineFactory>();

// --- Background services ---
builder.Services.AddSingleton<RunDirectoryFactory>();
// InboxWatcher was the file-based ingress. Retired — NATS events + HTTP /trigger
// are the only supported paths now. See docs/adr/011-nats-cutover.md.

// --- OpenTelemetry ---
var otelResource = ResourceBuilder.CreateDefault()
    .AddService(telemetrySettings.ServiceName, serviceVersion: "0.1.0");

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.SetResourceBuilder(otelResource);
        tracing.AddSource(FarmerActivitySource.Name);
        tracing.AddSource("Experimental.Microsoft.Agents.AI"); // MAF agent spans
        tracing.AddSource("NATS.Net");                          // NATS client publish/subscribe spans
        tracing.AddAspNetCoreInstrumentation();
        tracing.AddHttpClientInstrumentation(); // outbound OpenAI API calls
        if (telemetrySettings.EnableOtlpExporter)
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(telemetrySettings.OtlpEndpoint));
        if (telemetrySettings.EnableConsoleExporter)
            tracing.AddConsoleExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.SetResourceBuilder(otelResource);
        metrics.AddMeter(FarmerMetrics.MeterName);
        metrics.AddMeter("Experimental.Microsoft.Agents.AI"); // MAF agent metrics
        if (telemetrySettings.EnableOtlpExporter)
            metrics.AddOtlpExporter(o => o.Endpoint = new Uri(telemetrySettings.OtlpEndpoint));
        if (telemetrySettings.EnableConsoleExporter)
            metrics.AddConsoleExporter();
    });

var app = builder.Build();

// --- Endpoints ---
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.MapGet("/", () => Results.Ok(new
{
    service = "Farmer",
    version = "0.1.0",
    phase = "Phase 6 - Retrospective Loop"
}));

app.MapGet("/runs/{runId}", async (string runId, IRunStore store) =>
{
    var state = await store.GetRunStateAsync(runId);
    return state is not null ? Results.Ok(state) : Results.NotFound();
});

app.MapPost("/trigger", async (
    HttpContext ctx,
    RunDirectoryFactory dirFactory,
    WorkflowPipelineFactory pipelineFactory,
    IRunStore runStore,
    IRunArtifactStore artifactStore) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var tempFile = Path.GetTempFileName();
    await File.WriteAllTextAsync(tempFile, body);

    string? runDir = null;
    try
    {
        runDir = await dirFactory.CreateFromInboxFileAsync(tempFile);
        File.Delete(tempFile);

        var (workflow, costTracker) = pipelineFactory.Create();
        var result = await workflow.ExecuteFromDirectoryAsync(runDir);

        var costReport = costTracker.GetReport(result.RunId);
        await runStore.SaveCostReportAsync(costReport);

        // Mirror the run directory to NATS ObjectStore so artifacts are accessible
        // over the bus without requiring filesystem access. Best-effort; NATS outage
        // never fails a run. Key layout: {runId}/{filename}.
        await UploadRunArtifactsAsync(artifactStore, result.RunId, runDir, ctx.RequestAborted);

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        if (File.Exists(tempFile)) File.Delete(tempFile);
        return Results.Problem(ex.Message);
    }
});

static async Task UploadRunArtifactsAsync(IRunArtifactStore store, string runId, string runDir, CancellationToken ct)
{
    if (!Directory.Exists(runDir)) return;
    foreach (var file in Directory.EnumerateFiles(runDir))
    {
        var name = Path.GetFileName(file);
        try
        {
            var bytes = await File.ReadAllBytesAsync(file, ct);
            await store.PutAsync(runId, name, bytes, ct);
        }
        catch { /* already logged inside the store; continuing */ }
    }
}

app.Run("http://localhost:5100");
