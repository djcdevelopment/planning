using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Tools;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<FarmerSettings>(builder.Configuration.GetSection(FarmerSettings.SectionName));

// Services
builder.Services.AddSingleton<ISshService, SshService>();
builder.Services.AddSingleton<IMappedDriveReader, MappedDriveReader>();
builder.Services.AddSingleton<IRunStore, FileRunStore>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.MapGet("/", () => Results.Ok(new
{
    service = "Farmer",
    version = "0.1.0",
    phase = "Phase 1 - Skeleton"
}));

app.Run();
