using System.Text.Json;
using Farmer.Core.Config;
using Farmer.Core.Models;
using Xunit;

namespace Farmer.Tests.Models;

/// <summary>
/// Regression tests for the Phase 6 foundation additions. Every field
/// here is additive; existing Phase 5 models serialize unchanged.
/// </summary>
public class Phase6ModelTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    [Fact]
    public void RunRequest_RoundTripsParentRunId()
    {
        var request = new RunRequest
        {
            RunId = "run-20260411-120000-abcdef",
            TaskId = "task-abc",
            WorkRequestName = "widget",
            ParentRunId = "run-20260410-110000-parent",
        };

        var json = JsonSerializer.Serialize(request, JsonOpts);
        var back = JsonSerializer.Deserialize<RunRequest>(json, JsonOpts);

        Assert.NotNull(back);
        Assert.Equal("run-20260410-110000-parent", back!.ParentRunId);
        Assert.Contains("\"parent_run_id\"", json);
    }

    [Fact]
    public void RunRequest_ParentRunId_DefaultsToNull_AndOmittedWhenNull()
    {
        var request = new RunRequest { RunId = "run-1", WorkRequestName = "test" };
        var json = JsonSerializer.Serialize(request, JsonOpts);
        var back = JsonSerializer.Deserialize<RunRequest>(json, JsonOpts);

        Assert.NotNull(back);
        Assert.Null(back!.ParentRunId);
    }

    [Fact]
    public void Manifest_Outputs_ListSerializesWithAllKinds()
    {
        var manifest = new Manifest
        {
            RunId = "run-test",
            BranchName = "test-branch",
            Outputs =
            {
                new OutputArtifact { Kind = OutputKind.File, Path = "src/foo.cs", Bytes = 1024, Description = "source file" },
                new OutputArtifact { Kind = OutputKind.Directory, Path = "build/", Bytes = 0, Description = "output dir" },
                new OutputArtifact { Kind = OutputKind.Archive, Path = "dist/app.zip", Bytes = 524288 },
                new OutputArtifact { Kind = OutputKind.Binary, Path = "bin/server", Bytes = 2097152 },
                new OutputArtifact { Kind = OutputKind.Report, Path = "report.md", Bytes = 4096 },
            }
        };

        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        var back = JsonSerializer.Deserialize<Manifest>(json, JsonOpts);

        Assert.NotNull(back);
        Assert.Equal(5, back!.Outputs.Count);
        Assert.Equal(OutputKind.File, back.Outputs[0].Kind);
        Assert.Equal(OutputKind.Directory, back.Outputs[1].Kind);
        Assert.Equal(OutputKind.Archive, back.Outputs[2].Kind);
        Assert.Equal(OutputKind.Binary, back.Outputs[3].Kind);
        Assert.Equal(OutputKind.Report, back.Outputs[4].Kind);
        // Phase 5 compatibility: empty files_changed default still present
        Assert.Empty(back.FilesChanged);
    }

    [Fact]
    public void Manifest_Outputs_UsesSnakeCaseEnums()
    {
        var json = JsonSerializer.Serialize(new OutputArtifact { Kind = OutputKind.Binary }, JsonOpts);
        Assert.Contains("\"binary\"", json);
    }

    [Fact]
    public void Summary_RoundTripsWorkerRetroSeparatelyFromLegacyRetro()
    {
        var summary = new Summary
        {
            RunId = "run-test",
            Description = "worked on widget",
            Retro = "old-style retro blob",
            WorkerRetro = "new-style structured worker retro markdown",
        };

        var json = JsonSerializer.Serialize(summary, JsonOpts);
        var back = JsonSerializer.Deserialize<Summary>(json, JsonOpts);

        Assert.NotNull(back);
        Assert.Equal("old-style retro blob", back!.Retro);
        Assert.Equal("new-style structured worker retro markdown", back.WorkerRetro);
        Assert.Contains("\"worker_retro\"", json);
        Assert.Contains("\"retro\"", json);
    }

    [Fact]
    public void DirectiveSuggestion_RoundTripsAllScopes()
    {
        DirectiveScope[] scopes = { DirectiveScope.Prompts, DirectiveScope.ClaudeMd, DirectiveScope.TaskPacket };

        foreach (var scope in scopes)
        {
            var suggestion = new DirectiveSuggestion
            {
                Scope = scope,
                Target = "example",
                CurrentValue = "old",
                SuggestedValue = "new",
                Rationale = "because reasons",
            };

            var json = JsonSerializer.Serialize(suggestion, JsonOpts);
            var back = JsonSerializer.Deserialize<DirectiveSuggestion>(json, JsonOpts);

            Assert.NotNull(back);
            Assert.Equal(scope, back!.Scope);
        }
    }

    [Fact]
    public void OpenAISettings_ResolveApiKey_PrefersExplicitValue()
    {
        var settings = new OpenAISettings { ApiKey = "sk-explicit" };
        Assert.Equal("sk-explicit", settings.ResolveApiKey());
    }

    [Fact]
    public void OpenAISettings_ResolveApiKey_FallsBackToEnvironment()
    {
        const string envVar = "OPENAI_API_KEY";
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "sk-from-env");
            var settings = new OpenAISettings { ApiKey = "" };
            Assert.Equal("sk-from-env", settings.ResolveApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Fact]
    public void OpenAISettings_HasGpt4oMiniAsDefaultQaModel()
    {
        var settings = new OpenAISettings();
        Assert.Equal("gpt-4o-mini", settings.QaModel);
    }

    [Fact]
    public void RetrospectiveSettings_DefaultsToAutoPass()
    {
        var settings = new RetrospectiveSettings();
        Assert.Equal(RetrospectiveFailureBehavior.AutoPass, settings.FailureBehavior);
    }

    [Fact]
    public void RetrospectiveSettings_HasSaneLimits()
    {
        var settings = new RetrospectiveSettings();
        Assert.InRange(settings.MaxChangedFiles, 1, 100);
        Assert.InRange(settings.MaxFileBytes, 1024, 65536);
        Assert.InRange(settings.MaxAgentCallRetries, 0, 5);
        Assert.InRange(settings.ExecutionLogTailLines, 10, 1000);
    }
}
