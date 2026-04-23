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
// IMappedDriveReader: SSH-based reader replaces the mapped-drive (WinFsp/SSHFS-Win)
// backend. Interface name kept for a clean one-shot commit; rename to
// IWorkerFileReader is a follow-up. See Phase 7 Stream D.
builder.Services.AddSingleton<IMappedDriveReader, SshWorkerFileReader>();
builder.Services.AddSingleton<IRunStore, FileRunStore>();
builder.Services.AddSingleton<IVmManager, VmManager>();

// --- Workflow stages (resolved by WorkflowPipelineFactory in pipeline order) ---
builder.Services.AddSingleton<CreateRunStage>();
builder.Services.AddSingleton<LoadPromptsStage>();
builder.Services.AddSingleton<ReserveVmStage>();
builder.Services.AddSingleton<DeliverStage>();
builder.Services.AddSingleton<DispatchStage>();
builder.Services.AddSingleton<CollectStage>();
builder.Services.AddSingleton<ArchiveStage>();
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
// IWorkflowRunner is the RetryDriver's seam against the workflow. Production wraps
// the factory + persists the cost report; tests can swap in a fake (see ADR / PR body).
builder.Services.AddSingleton<IWorkflowRunner, PipelineWorkflowRunner>();

// --- Background services ---
builder.Services.AddSingleton<RunDirectoryFactory>();
builder.Services.AddSingleton<RetryDriver>();
// InboxWatcher was the file-based ingress. Retired — NATS events + HTTP /trigger
// are the only supported paths now. See docs/adr/011-nats-cutover.md.

// --- CORS ---
// Permissive dev policy so browser clients hitting the cloudflared tunnel URL
// (e.g. desire-trace's Azure-hosted page) can POST to /trigger. For Phase Demo
// the trust model is "friend clicks a button; no PII, single-user". Tighten
// the allowed origins before exposing this to anything resembling a real user
// base. See docs/phase-demo-plan.md.
const string FarmerDevCorsPolicy = "FarmerDevCors";
builder.Services.AddCors(options =>
{
    options.AddPolicy(FarmerDevCorsPolicy, policy => policy
        .AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader());
});

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

// CORS must run before endpoint mapping so preflight (OPTIONS) and response
// headers apply to every Map* below.
app.UseCors(FarmerDevCorsPolicy);

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
    RetryDriver driver,
    IRunStore runStore) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var tempFile = Path.GetTempFileName();
    await File.WriteAllTextAsync(tempFile, body);

    try
    {
        var attempts = await driver.RunAsync(tempFile, ctx.RequestAborted);

        // Cost reports use the final attempt's runId; per-attempt cost is handled
        // inside each workflow execution via CostTrackingMiddleware.
        var final = attempts[^1];

        // Backward-compat response: when there's only one attempt (retry disabled or
        // short-circuited), return the single WorkflowResult directly so existing
        // callers (curl, smoke scripts, Farmer.SmokeTrace.ps1) keep working. When a
        // retry chain ran, return the whole list plus the final result on top-level.
        if (attempts.Count == 1)
            return Results.Ok(final);
        return Results.Ok(new { attempts, final });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
    finally
    {
        if (File.Exists(tempFile)) File.Delete(tempFile);
    }
});

app.Run("http://localhost:5100");
