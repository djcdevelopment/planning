using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Host.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Integration;

/// <summary>
/// Stream J (Phase Demo) — direct coverage for the <see cref="RunsBrowserService"/>
/// that backs <c>GET /runs</c>, <c>GET /runs/{id}</c>, and
/// <c>GET /runs/{id}/file/{**path}</c>. These are the three reveal endpoints;
/// exercising the service is equivalent to exercising the handlers since each
/// handler delegates exactly one call and maps result-is-null to 404.
///
/// Path-traversal rejection is tested here (<see cref="TryResolveRunFile_RejectsTraversal"/>),
/// covering the security assertion in the Stream J spec.
/// </summary>
public class RunsBrowserServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private readonly string _runsDir;
    private readonly RunsBrowserService _svc;

    public RunsBrowserServiceTests()
    {
        _runsDir = Path.Combine(Path.GetTempPath(), "farmer-runs-browser-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_runsDir);

        var settings = new FarmerSettings { Paths = new PathsSettings { Runs = _runsDir } };
        _svc = new RunsBrowserService(Options.Create(settings));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_runsDir)) Directory.Delete(_runsDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void ListRecent_EmptyRoot_ReturnsEmpty()
    {
        Assert.Empty(_svc.ListRecent());
    }

    [Fact]
    public void ListRecent_SkipsRunsMissingResultJson()
    {
        var runA = CreateRun("run-A", withResult: true, withReview: true);
        var runB = CreateRun("run-B", withResult: false, withReview: false); // still running / crashed

        var list = _svc.ListRecent();

        Assert.Single(list);
        Assert.Equal("run-A", list[0].RunId);
    }

    [Fact]
    public void ListRecent_SortsNewestFirst_ByDirMtime()
    {
        CreateRun("run-old", withResult: true, withReview: true);
        // Force an earlier mtime on run-old so run-new is clearly newer.
        Directory.SetLastWriteTimeUtc(Path.Combine(_runsDir, "run-old"), DateTime.UtcNow.AddHours(-2));

        CreateRun("run-new", withResult: true, withReview: true);

        var list = _svc.ListRecent();

        Assert.Equal(2, list.Count);
        Assert.Equal("run-new", list[0].RunId);
        Assert.Equal("run-old", list[1].RunId);
    }

    [Fact]
    public void ListRecent_PopulatesVerdictFromReviewJson()
    {
        CreateRun("run-accept", withResult: true, withReview: true, verdict: "Accept", riskScore: 12);

        var list = _svc.ListRecent();

        var s = Assert.Single(list);
        Assert.Equal("Accept", s.Verdict);
        Assert.Equal(12, s.RiskScore);
    }

    [Fact]
    public void TryLoadDetail_ReturnsRetroAndDirectiveMarkdown()
    {
        var runDir = CreateRun("run-full", withResult: true, withReview: true);
        File.WriteAllText(Path.Combine(runDir, "qa-retro.md"), "retro body");
        File.WriteAllText(Path.Combine(runDir, "directive-suggestions.md"), "# 1. do a thing");

        var detail = _svc.TryLoadDetail("run-full");

        Assert.NotNull(detail);
        Assert.Equal("retro body", detail!.QaRetroMarkdown);
        Assert.Equal("# 1. do a thing", detail.DirectivesMarkdown);
    }

    [Fact]
    public void TryLoadDetail_UnknownRunId_ReturnsNull()
    {
        Assert.Null(_svc.TryLoadDetail("run-nonexistent"));
    }

    [Fact]
    public void ListArtifacts_PrefersIndexOverFilesystem()
    {
        var runDir = CreateRun("run-idx", withResult: true, withReview: true);
        Directory.CreateDirectory(Path.Combine(runDir, "artifacts"));
        File.WriteAllText(Path.Combine(runDir, "artifacts", "ignored.txt"), "disk only");

        var index = new
        {
            run_id = "run-idx",
            entries = new[]
            {
                new { path = "from-index.md", status = "captured", bytes = (long?)42, reason = (string?)null, detail = (string?)null }
            }
        };
        File.WriteAllText(Path.Combine(runDir, "artifacts-index.json"), JsonSerializer.Serialize(index, JsonOpts));

        var info = _svc.ListArtifacts("run-idx");

        Assert.Equal("index", info.Source);
        var e = Assert.Single(info.Entries);
        Assert.Equal("from-index.md", e.Path);
    }

    [Fact]
    public void ListArtifacts_FallsBackToFilesystem_WhenNoIndex()
    {
        var runDir = CreateRun("run-fs", withResult: true, withReview: true);
        var art = Path.Combine(runDir, "artifacts");
        Directory.CreateDirectory(art);
        File.WriteAllText(Path.Combine(art, "a.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(art, "sub"));
        File.WriteAllText(Path.Combine(art, "sub", "b.md"), "world");

        var info = _svc.ListArtifacts("run-fs");

        Assert.Equal("filesystem", info.Source);
        Assert.Equal(2, info.Entries.Count);
        Assert.Contains(info.Entries, e => e.Path == "a.txt");
        Assert.Contains(info.Entries, e => e.Path == "sub/b.md");
    }

    [Fact]
    public void TryResolveRunFile_ResolvesFileInRunDir()
    {
        var runDir = CreateRun("run-file", withResult: true, withReview: true);
        File.WriteAllText(Path.Combine(runDir, "result.json"), "{}");

        Assert.True(_svc.TryResolveRunFile("run-file", "result.json", out var fullPath));
        Assert.Equal(Path.Combine(runDir, "result.json"), fullPath);
    }

    [Fact]
    public void TryResolveRunFile_ResolvesNestedArtifact()
    {
        var runDir = CreateRun("run-nested", withResult: true, withReview: true);
        var art = Path.Combine(runDir, "artifacts", "sub");
        Directory.CreateDirectory(art);
        File.WriteAllText(Path.Combine(art, "nested.txt"), "hi");

        Assert.True(_svc.TryResolveRunFile("run-nested", "artifacts/sub/nested.txt", out var fullPath));
        Assert.EndsWith("nested.txt", fullPath);
    }

    [Fact]
    public void TryResolveRunFile_RejectsTraversal()
    {
        // Put a victim file OUTSIDE the run dir but inside the runs root --
        // a naive Path.Combine would resolve "../run-sibling/secret" to it.
        CreateRun("run-attacker", withResult: true, withReview: true);
        var sibling = Path.Combine(_runsDir, "run-sibling");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "secret.txt"), "very secret");

        Assert.False(_svc.TryResolveRunFile("run-attacker", "../run-sibling/secret.txt", out var p1));
        Assert.Equal(string.Empty, p1);

        // Forward and back slash variants.
        Assert.False(_svc.TryResolveRunFile("run-attacker", "..\\run-sibling\\secret.txt", out _));

        // Embedded traversal after a valid segment.
        Assert.False(_svc.TryResolveRunFile("run-attacker", "artifacts/../../run-sibling/secret.txt", out _));
    }

    [Fact]
    public void TryResolveRunFile_RejectsRootedPath()
    {
        CreateRun("run-root", withResult: true, withReview: true);
        Assert.False(_svc.TryResolveRunFile("run-root", @"C:\Windows\System32\drivers\etc\hosts", out _));
        Assert.False(_svc.TryResolveRunFile("run-root", "/etc/passwd", out _));
    }

    [Fact]
    public void TryResolveRunFile_RejectsMaliciousRunId()
    {
        Assert.False(_svc.TryResolveRunFile("../run-sibling", "result.json", out _));
        Assert.False(_svc.TryResolveRunFile("run-with/slash", "result.json", out _));
        Assert.False(_svc.TryResolveRunFile("", "result.json", out _));
    }

    [Fact]
    public void TryResolveRunFile_ReturnsFalseForDirectory()
    {
        var runDir = CreateRun("run-dir", withResult: true, withReview: true);
        Directory.CreateDirectory(Path.Combine(runDir, "artifacts"));

        // Directory listings are not served via /file/ — only existing files.
        Assert.False(_svc.TryResolveRunFile("run-dir", "artifacts", out _));
    }

    // --- helpers ---

    private string CreateRun(string runId, bool withResult, bool withReview, string verdict = "Accept", int riskScore = 5)
    {
        var runDir = Path.Combine(_runsDir, runId);
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(Path.Combine(runDir, "logs"));
        Directory.CreateDirectory(Path.Combine(runDir, "artifacts"));

        File.WriteAllText(Path.Combine(runDir, "request.json"), JsonSerializer.Serialize(new
        {
            run_id = runId,
            work_request_name = "demo-" + runId,
            created_at = DateTimeOffset.UtcNow,
        }, JsonOpts));

        if (withResult)
        {
            File.WriteAllText(Path.Combine(runDir, "result.json"), JsonSerializer.Serialize(new
            {
                run_id = runId,
                success = true,
                final_phase = "Complete",
                started_at = DateTimeOffset.UtcNow.AddMinutes(-5),
                completed_at = DateTimeOffset.UtcNow,
                duration_seconds = 300.0,
                stages_completed = new[] { "CreateRun", "Complete" },
            }, JsonOpts));
        }

        if (withReview)
        {
            File.WriteAllText(Path.Combine(runDir, "review.json"), JsonSerializer.Serialize(new
            {
                run_id = runId,
                verdict,
                risk_score = riskScore,
                findings = new[] { "finding-1" },
                suggestions = new[] { "suggestion-1" },
                reviewed_at = DateTimeOffset.UtcNow,
            }, JsonOpts));
        }

        return runDir;
    }
}
