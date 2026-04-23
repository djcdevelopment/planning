using Farmer.Core.Config;
using Farmer.Core.Models;
using Farmer.Core.Workflow;
using Farmer.Core.Workflow.Stages;
using Farmer.Tests.TestHelpers;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farmer.Tests.Workflow;

/// <summary>
/// prompts_inline fast path: clients without a shared filesystem (phone /
/// browser / tunnel) can submit prompt bodies directly on the /trigger body.
/// LoadPromptsStage must use them instead of scanning the sample-plans
/// directory, preserving work_request_name for display / metadata.
/// See docs/phase-demo-plan.md.
/// </summary>
public class LoadPromptsStage_InlineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _samplePlansDir;
    private readonly InMemoryRunStore _store = new();

    public LoadPromptsStage_InlineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "farmer-test-" + Guid.NewGuid().ToString("N")[..8]);
        _samplePlansDir = Path.Combine(_tempDir, "sample-plans");
        Directory.CreateDirectory(_samplePlansDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private LoadPromptsStage MakeStage()
    {
        var settings = Options.Create(new FarmerSettings { Paths = new PathsSettings { Data = _tempDir } });
        return new LoadPromptsStage(settings, _store);
    }

    [Fact]
    public async Task Inline_prompts_bypass_disk_when_no_plan_dir_exists()
    {
        // No sample-plans/live-demo/ on disk at all. Happy path for the
        // friend-on-phone case: a totally fresh idea nobody pre-staged.
        var stage = MakeStage();
        var state = new RunFlowState
        {
            RunId = "run-1",
            WorkRequestName = "live-demo",
            RunRequest = new RunRequest
            {
                RunId = "run-1",
                WorkRequestName = "live-demo",
                PromptsInline =
                [
                    new InlinePrompt { Filename = "1-Build.md", Content = "Write a Python function that prints hello world" },
                    new InlinePrompt { Filename = "2-Review.md", Content = "Lint it" },
                ],
            },
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.NotNull(state.TaskPacket);
        Assert.Equal(2, state.TaskPacket!.Prompts.Count);
        Assert.Equal("1-Build.md", state.TaskPacket.Prompts[0].Filename);
        Assert.Equal("Write a Python function that prints hello world", state.TaskPacket.Prompts[0].Content);
        Assert.Equal(1, state.TaskPacket.Prompts[0].Order);
        Assert.Equal("2-Review.md", state.TaskPacket.Prompts[1].Filename);
        Assert.Equal(2, state.TaskPacket.Prompts[1].Order);
    }

    [Fact]
    public async Task Inline_prompts_win_over_disk_when_both_exist()
    {
        // A sample-plans dir with the same work_request_name is present, but
        // inline wins. Display name (work_request_name) still flows through.
        var dir = Path.Combine(_samplePlansDir, "live-demo");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "1-FromDisk.md"), "# should NOT be used");

        var stage = MakeStage();
        var state = new RunFlowState
        {
            RunId = "run-2",
            WorkRequestName = "live-demo",
            RunRequest = new RunRequest
            {
                RunId = "run-2",
                WorkRequestName = "live-demo",
                PromptsInline =
                [
                    new InlinePrompt { Filename = "1-Inline.md", Content = "inline wins" },
                ],
            },
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Single(state.TaskPacket!.Prompts);
        Assert.Equal("1-Inline.md", state.TaskPacket.Prompts[0].Filename);
        Assert.Equal("inline wins", state.TaskPacket.Prompts[0].Content);
        // display / metadata name is still the one the client submitted
        Assert.Equal("live-demo", state.TaskPacket.WorkRequestName);
    }

    [Fact]
    public async Task Empty_inline_list_falls_through_to_disk()
    {
        // Defensive: a caller sending `"prompts_inline": []` (or a client
        // that always serializes the field) should behave identically to
        // the disk-only case.
        var dir = Path.Combine(_samplePlansDir, "disk-only");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "1-Setup.md"), "# from disk");

        var stage = MakeStage();
        var state = new RunFlowState
        {
            RunId = "run-3",
            WorkRequestName = "disk-only",
            RunRequest = new RunRequest
            {
                RunId = "run-3",
                WorkRequestName = "disk-only",
                PromptsInline = new List<InlinePrompt>(), // empty, not null
            },
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Single(state.TaskPacket!.Prompts);
        Assert.Equal("1-Setup.md", state.TaskPacket.Prompts[0].Filename);
        Assert.Contains("from disk", state.TaskPacket.Prompts[0].Content);
    }

    [Fact]
    public async Task Null_inline_falls_through_to_disk()
    {
        // Explicit baseline: null prompts_inline is the pre-Phase-Demo shape
        // and must keep working.
        var dir = Path.Combine(_samplePlansDir, "backcompat");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "1-Setup.md"), "# from disk");

        var stage = MakeStage();
        var state = new RunFlowState
        {
            RunId = "run-4",
            WorkRequestName = "backcompat",
            RunRequest = new RunRequest
            {
                RunId = "run-4",
                WorkRequestName = "backcompat",
                PromptsInline = null,
            },
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Single(state.TaskPacket!.Prompts);
        Assert.Equal("1-Setup.md", state.TaskPacket.Prompts[0].Filename);
    }

    [Fact]
    public async Task Feedback_still_prepended_when_using_inline_prompts()
    {
        // Retry-driver feedback + inline-prompts must compose cleanly: the
        // 0-feedback.md still lands at index 0, inline prompts follow.
        var stage = MakeStage();
        var feedback = "# Reviewer feedback\nTry again, gentler.";
        var state = new RunFlowState
        {
            RunId = "run-5",
            WorkRequestName = "live-demo",
            RunRequest = new RunRequest
            {
                RunId = "run-5",
                WorkRequestName = "live-demo",
                Feedback = feedback,
                PromptsInline =
                [
                    new InlinePrompt { Filename = "1-Build.md", Content = "build it" },
                ],
            },
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Equal(2, state.TaskPacket!.Prompts.Count);
        Assert.Equal("0-feedback.md", state.TaskPacket.Prompts[0].Filename);
        Assert.Equal(0, state.TaskPacket.Prompts[0].Order);
        Assert.Equal(feedback, state.TaskPacket.Prompts[0].Content);
        Assert.Equal("1-Build.md", state.TaskPacket.Prompts[1].Filename);
    }

    [Fact]
    public async Task Inline_prompt_missing_filename_gets_synthesized_name()
    {
        // Be forgiving: a phone client that only sends `content` shouldn't
        // blow up the pipeline. Synthesized filenames keep worker-side logs
        // meaningful.
        var stage = MakeStage();
        var state = new RunFlowState
        {
            RunId = "run-6",
            WorkRequestName = "live-demo",
            RunRequest = new RunRequest
            {
                RunId = "run-6",
                WorkRequestName = "live-demo",
                PromptsInline =
                [
                    new InlinePrompt { Filename = "", Content = "first" },
                    new InlinePrompt { Filename = "   ", Content = "second" },
                ],
            },
        };

        var result = await stage.ExecuteAsync(state);

        Assert.Equal(StageOutcome.Success, result.Outcome);
        Assert.Equal(2, state.TaskPacket!.Prompts.Count);
        Assert.Equal("1-inline.md", state.TaskPacket.Prompts[0].Filename);
        Assert.Equal("2-inline.md", state.TaskPacket.Prompts[1].Filename);
    }

    [Fact]
    public async Task Inline_prompt_count_persisted_to_RunRequest()
    {
        // PromptCount is surfaced in retrospective metadata; inline path must
        // set it the same way disk path does.
        var stage = MakeStage();
        var state = new RunFlowState
        {
            RunId = "run-7",
            WorkRequestName = "live-demo",
            RunRequest = new RunRequest
            {
                RunId = "run-7",
                WorkRequestName = "live-demo",
                PromptsInline =
                [
                    new InlinePrompt { Filename = "1.md", Content = "a" },
                    new InlinePrompt { Filename = "2.md", Content = "b" },
                    new InlinePrompt { Filename = "3.md", Content = "c" },
                ],
            },
        };

        await stage.ExecuteAsync(state);

        Assert.Equal(3, state.RunRequest!.PromptCount);
    }

    [Fact]
    public async Task TaskPacket_persisted_for_inline_path()
    {
        // Parity with the disk path: TaskPacket lands in the run store so
        // downstream stages and observability see it.
        var stage = MakeStage();
        var state = new RunFlowState
        {
            RunId = "run-8",
            WorkRequestName = "live-demo",
            RunRequest = new RunRequest
            {
                RunId = "run-8",
                WorkRequestName = "live-demo",
                PromptsInline = [new InlinePrompt { Filename = "1.md", Content = "x" }],
            },
        };

        await stage.ExecuteAsync(state);

        var saved = await _store.GetTaskPacketAsync("run-8");
        Assert.NotNull(saved);
        Assert.Single(saved!.Prompts);
        Assert.Equal("1.md", saved.Prompts[0].Filename);
    }
}
