using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Models;
using Farmer.Host.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Integration;

/// <summary>
/// Bridges the wire contract (InboxTrigger) to the persisted RunRequest.
/// Verifies prompts_inline survives the transcription and that
/// work_request_name is allowed to be absent when inline prompts are
/// supplied (phone client that just says "build me a thing" without a
/// canonical name).
/// </summary>
public class RunDirectoryFactory_InlinePromptsTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly string _runsDir;
    private readonly RunDirectoryFactory _factory;

    public RunDirectoryFactory_InlinePromptsTests()
    {
        _runsDir = Path.Combine(Path.GetTempPath(), "farmer-rdf-inline-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_runsDir);

        var settings = Options.Create(new FarmerSettings
        {
            Paths = new PathsSettings { Runs = _runsDir, Root = _runsDir },
        });
        _factory = new RunDirectoryFactory(settings);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_runsDir)) Directory.Delete(_runsDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task PromptsInline_persisted_to_request_json()
    {
        var triggerFile = Path.Combine(_runsDir, "trigger.json");
        var trigger = new
        {
            work_request_name = "live-demo",
            source = "phone",
            worker_mode = "fake",
            prompts_inline = new[]
            {
                new { filename = "1-Build.md", content = "hello world" },
                new { filename = "2-Review.md", content = "lint it" },
            },
        };
        await File.WriteAllTextAsync(triggerFile, JsonSerializer.Serialize(trigger, JsonOpts));

        var runDir = await _factory.CreateFromInboxFileAsync(triggerFile);

        var reqJson = await File.ReadAllTextAsync(Path.Combine(runDir, "request.json"));
        var req = JsonSerializer.Deserialize<RunRequest>(reqJson, JsonOpts)!;

        Assert.Equal("live-demo", req.WorkRequestName);
        Assert.NotNull(req.PromptsInline);
        Assert.Equal(2, req.PromptsInline!.Count);
        Assert.Equal("1-Build.md", req.PromptsInline[0].Filename);
        Assert.Equal("hello world", req.PromptsInline[0].Content);
    }

    [Fact]
    public async Task Missing_work_request_name_allowed_when_inline_prompts_supplied()
    {
        var triggerFile = Path.Combine(_runsDir, "trigger-no-name.json");
        var trigger = new
        {
            source = "phone",
            worker_mode = "fake",
            prompts_inline = new[]
            {
                new { filename = "1-Build.md", content = "hello" },
            },
        };
        await File.WriteAllTextAsync(triggerFile, JsonSerializer.Serialize(trigger, JsonOpts));

        var runDir = await _factory.CreateFromInboxFileAsync(triggerFile);

        var reqJson = await File.ReadAllTextAsync(Path.Combine(runDir, "request.json"));
        var req = JsonSerializer.Deserialize<RunRequest>(reqJson, JsonOpts)!;

        // Synthesized default so retrospective / worker logs have a name.
        Assert.Equal("inline-request", req.WorkRequestName);
        Assert.NotNull(req.PromptsInline);
    }

    [Fact]
    public async Task Absent_prompts_inline_keeps_legacy_shape()
    {
        // Backward compat: an old-style trigger with no prompts_inline must
        // produce a RunRequest with PromptsInline == null.
        var triggerFile = Path.Combine(_runsDir, "trigger-legacy.json");
        var trigger = new
        {
            work_request_name = "react-grid-component",
            source = "api",
        };
        await File.WriteAllTextAsync(triggerFile, JsonSerializer.Serialize(trigger, JsonOpts));

        var runDir = await _factory.CreateFromInboxFileAsync(triggerFile);

        var reqJson = await File.ReadAllTextAsync(Path.Combine(runDir, "request.json"));
        var req = JsonSerializer.Deserialize<RunRequest>(reqJson, JsonOpts)!;

        Assert.Equal("react-grid-component", req.WorkRequestName);
        Assert.Null(req.PromptsInline);
        // Phase Demo v2 Stream 3: back-compat asserts user_id stays null
        // when the caller doesn't set it.
        Assert.Null(req.UserId);
    }

    [Fact]
    public async Task UserId_on_trigger_body_is_persisted_to_request_json()
    {
        // Phase Demo v2 Stream 3: "user_id" on the JSON body is captured
        // verbatim into the persisted RunRequest so the UI can filter
        // run history per caller.
        var triggerFile = Path.Combine(_runsDir, "trigger-user.json");
        var trigger = new
        {
            work_request_name = "demo-per-user",
            source = "phone",
            user_id = "alice-b2c-oid",
        };
        await File.WriteAllTextAsync(triggerFile, JsonSerializer.Serialize(trigger, JsonOpts));

        var runDir = await _factory.CreateFromInboxFileAsync(triggerFile);

        var reqJson = await File.ReadAllTextAsync(Path.Combine(runDir, "request.json"));
        var req = JsonSerializer.Deserialize<RunRequest>(reqJson, JsonOpts)!;

        Assert.Equal("alice-b2c-oid", req.UserId);
        // Raw text assert too: snake_case on the wire is the stable contract.
        Assert.Contains("\"user_id\": \"alice-b2c-oid\"", reqJson);
    }
}
