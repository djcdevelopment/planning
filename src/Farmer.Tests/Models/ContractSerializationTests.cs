using System.Text.Json;
using Farmer.Core.Models;
using Xunit;

namespace Farmer.Tests.Models;

public class ContractSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public void RunRequest_RoundTrips()
    {
        var original = new RunRequest
        {
            RunId = "run-001",
            TaskId = "task-001",
            AttemptId = 1,
            WorkRequestName = "react-grid-component",
            PromptCount = 3,
            Source = "api"
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<RunRequest>(json, Options)!;

        Assert.Equal(original.RunId, deserialized.RunId);
        Assert.Equal(original.TaskId, deserialized.TaskId);
        Assert.Equal(original.WorkRequestName, deserialized.WorkRequestName);
        Assert.Equal(original.PromptCount, deserialized.PromptCount);
        Assert.Contains("run_id", json);
        Assert.Contains("work_request_name", json);
    }

    [Fact]
    public void RunRequest_Deserializes_WithoutPromptsInline()
    {
        // Back-compat: existing /trigger payloads that don't carry prompts_inline
        // must keep parsing cleanly, with the property null.
        var json = """{"run_id":"run-1","work_request_name":"demo","source":"api"}""";
        var req = JsonSerializer.Deserialize<RunRequest>(json, Options)!;

        Assert.Equal("demo", req.WorkRequestName);
        Assert.Null(req.PromptsInline);
    }

    [Fact]
    public void RunRequest_Deserializes_WithPromptsInline()
    {
        var json = """
        {
          "run_id": "run-1",
          "work_request_name": "live-demo",
          "prompts_inline": [
            { "filename": "1-Build.md", "content": "Write a Python function that prints hello world" },
            { "filename": "2-Review.md", "content": "Lint the script" }
          ]
        }
        """;

        var req = JsonSerializer.Deserialize<RunRequest>(json, Options)!;

        Assert.NotNull(req.PromptsInline);
        Assert.Equal(2, req.PromptsInline!.Count);
        Assert.Equal("1-Build.md", req.PromptsInline[0].Filename);
        Assert.Equal("Write a Python function that prints hello world", req.PromptsInline[0].Content);
        Assert.Equal("live-demo", req.WorkRequestName);
    }

    [Fact]
    public void RunRequest_PromptsInline_RoundTrips()
    {
        var original = new RunRequest
        {
            RunId = "run-1",
            WorkRequestName = "live-demo",
            PromptsInline =
            [
                new InlinePrompt { Filename = "1-Build.md", Content = "do the thing" },
            ],
        };

        var json = JsonSerializer.Serialize(original, Options);
        var roundTripped = JsonSerializer.Deserialize<RunRequest>(json, Options)!;

        Assert.Contains("prompts_inline", json);
        Assert.NotNull(roundTripped.PromptsInline);
        Assert.Single(roundTripped.PromptsInline!);
        Assert.Equal("1-Build.md", roundTripped.PromptsInline![0].Filename);
        Assert.Equal("do the thing", roundTripped.PromptsInline[0].Content);
    }

    [Fact]
    public void InlinePrompt_UsesSnakeCaseJsonNames()
    {
        // Both fields are already lowercase so the check is mostly a
        // "these names stay stable" guard, but make the wire contract explicit.
        var p = new InlinePrompt { Filename = "x.md", Content = "y" };
        var json = JsonSerializer.Serialize(p, Options);

        Assert.Contains("\"filename\"", json);
        Assert.Contains("\"content\"", json);
    }

    [Fact]
    public void TaskPacket_RoundTrips_WithPrompts()
    {
        var original = new TaskPacket
        {
            RunId = "run-001",
            WorkRequestName = "react-grid-component",
            BranchName = "claudefarm1-react-grid-component",
            Prompts =
            [
                new PromptFile { Order = 1, Filename = "1-SetupProject.md", Content = "Set up the React project" },
                new PromptFile { Order = 2, Filename = "2-BuildGrid.md", Content = "Build the grid component" }
            ]
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<TaskPacket>(json, Options)!;

        Assert.Equal(original.RunId, deserialized.RunId);
        Assert.Equal(original.BranchName, deserialized.BranchName);
        Assert.Equal(2, deserialized.Prompts.Count);
        Assert.Equal("1-SetupProject.md", deserialized.Prompts[0].Filename);
        Assert.Equal(1, deserialized.Prompts[0].Order);
    }

    [Fact]
    public void RunStatus_Serializes_EnumAsString()
    {
        var status = new RunStatus
        {
            RunId = "run-001",
            Phase = RunPhase.Executing,
            ProgressPct = 50,
            VmId = "claudefarm1",
            CurrentPrompt = 2,
            TotalPrompts = 4
        };

        var json = JsonSerializer.Serialize(status, Options);

        Assert.Contains("\"Executing\"", json);
        Assert.Contains("progress_pct", json);
        Assert.Contains("current_prompt", json);
    }

    [Fact]
    public void RunStatus_Deserializes_EnumFromString()
    {
        var json = """{"run_id":"run-001","phase":"Dispatching","progress_pct":25}""";
        var status = JsonSerializer.Deserialize<RunStatus>(json, Options)!;

        Assert.Equal(RunPhase.Dispatching, status.Phase);
    }

    [Fact]
    public void CostReport_RoundTrips_WithStages()
    {
        var original = new CostReport
        {
            RunId = "run-001",
            TotalDurationSeconds = 120.5,
            Stages =
            [
                new StageCost
                {
                    StageName = "deliver",
                    DurationSeconds = 2.3,
                    StartedAt = DateTimeOffset.UtcNow.AddSeconds(-120),
                    CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-118)
                }
            ]
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<CostReport>(json, Options)!;

        Assert.Equal(120.5, deserialized.TotalDurationSeconds);
        Assert.Single(deserialized.Stages);
        Assert.Equal("deliver", deserialized.Stages[0].StageName);
    }

    [Fact]
    public void ReviewVerdict_RoundTrips()
    {
        var original = new ReviewVerdict
        {
            RunId = "run-001",
            Verdict = Verdict.Retry,
            RiskScore = 45,
            Findings = ["Missing error handling", "No tests"],
            Suggestions = ["Add try-catch blocks", "Write unit tests"]
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<ReviewVerdict>(json, Options)!;

        Assert.Equal(Verdict.Retry, deserialized.Verdict);
        Assert.Equal(45, deserialized.RiskScore);
        Assert.Equal(2, deserialized.Findings.Count);
        Assert.Contains("\"Retry\"", json);
    }

    [Fact]
    public void Manifest_RoundTrips()
    {
        var original = new Manifest
        {
            RunId = "run-001",
            BranchName = "claudefarm1-react-grid",
            CommitSha = "abc123",
            FilesChanged = ["src/Grid.tsx", "src/Grid.test.tsx", "package.json"]
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<Manifest>(json, Options)!;

        Assert.Equal(3, deserialized.FilesChanged.Count);
        Assert.Equal("abc123", deserialized.CommitSha);
        Assert.Contains("branch_name", json);
    }

    [Fact]
    public void Summary_RoundTrips_WithRetro()
    {
        var original = new Summary
        {
            RunId = "run-001",
            Description = "Built a React grid component with sorting and filtering",
            Issues = ["TypeScript strict mode warnings"],
            Retro = "Overall good. Could improve test coverage."
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<Summary>(json, Options)!;

        Assert.Equal(original.Description, deserialized.Description);
        Assert.Single(deserialized.Issues);
        Assert.NotNull(deserialized.Retro);
    }
}
