using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Middleware;
using Farmer.Core.Telemetry;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Farmer.Host.Services;
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

// --- Workflow stages (EXPLICIT ORDER — registration order is pipeline order) ---
builder.Services.AddSingleton<CreateRunStage>();
builder.Services.AddSingleton<LoadPromptsStage>();
builder.Services.AddSingleton<ReserveVmStage>();
builder.Services.AddSingleton<DeliverStage>();
builder.Services.AddSingleton<DispatchStage>();
builder.Services.AddSingleton<CollectStage>();
builder.Services.AddSingleton<ReviewStage>();

builder.Services.AddSingleton<IEnumerable<IWorkflowStage>>(sp => new IWorkflowStage[]
{
    sp.GetRequiredService<CreateRunStage>(),
    sp.GetRequiredService<LoadPromptsStage>(),
    sp.GetRequiredService<ReserveVmStage>(),
    sp.GetRequiredService<DeliverStage>(),
    sp.GetRequiredService<DispatchStage>(),
    sp.GetRequiredService<CollectStage>(),
    sp.GetRequiredService<ReviewStage>(),
});

// --- Middleware (registration order = execution order, outermost first) ---
builder.Services.AddSingleton<TelemetryMiddleware>();
builder.Services.AddSingleton<LoggingMiddleware>();
builder.Services.AddSingleton<EventingMiddleware>();
builder.Services.AddSingleton<CostTrackingMiddleware>();
builder.Services.AddSingleton<HeartbeatMiddleware>();

builder.Services.AddSingleton<IEnumerable<IWorkflowMiddleware>>(sp => new IWorkflowMiddleware[]
{
    sp.GetRequiredService<TelemetryMiddleware>(),
    sp.GetRequiredService<LoggingMiddleware>(),
    sp.GetRequiredService<EventingMiddleware>(),
    sp.GetRequiredService<CostTrackingMiddleware>(),
    sp.GetRequiredService<HeartbeatMiddleware>(),
});

// --- Workflow ---
builder.Services.AddSingleton<RunWorkflow>();

// --- Background services ---
builder.Services.AddSingleton<RunDirectoryFactory>();
builder.Services.AddHostedService<InboxWatcher>();

// --- OpenTelemetry ---
var otelResource = ResourceBuilder.CreateDefault()
    .AddService(telemetrySettings.ServiceName, serviceVersion: "0.1.0");

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.SetResourceBuilder(otelResource);
        tracing.AddSource(FarmerActivitySource.Name);
        tracing.AddAspNetCoreInstrumentation();
        if (telemetrySettings.EnableOtlpExporter)
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(telemetrySettings.OtlpEndpoint));
        if (telemetrySettings.EnableConsoleExporter)
            tracing.AddConsoleExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.SetResourceBuilder(otelResource);
        metrics.AddMeter(FarmerMetrics.MeterName);
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
    phase = "Phase 5 - Externalized Runtime + OTel"
}));

app.MapGet("/runs/{runId}", async (string runId, IRunStore store) =>
{
    var state = await store.GetRunStateAsync(runId);
    return state is not null ? Results.Ok(state) : Results.NotFound();
});

app.MapPost("/trigger", async (HttpContext ctx, RunDirectoryFactory factory, RunWorkflow workflow) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var tempFile = Path.GetTempFileName();
    await File.WriteAllTextAsync(tempFile, body);

    try
    {
        var runDir = await factory.CreateFromInboxFileAsync(tempFile);
        File.Delete(tempFile);
        var result = await workflow.ExecuteFromDirectoryAsync(runDir);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        File.Delete(tempFile);
        return Results.Problem(ex.Message);
    }
});

app.Run("http://localhost:5100");
