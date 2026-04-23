using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Farmer.Core.Models;
using Microsoft.Extensions.Options;

namespace Farmer.Core.Workflow.Stages;

public sealed class LoadPromptsStage : IWorkflowStage
{
    private readonly FarmerSettings _settings;
    private readonly IRunStore _runStore;

    public string Name => "LoadPrompts";
    public RunPhase Phase => RunPhase.Loading;

    public LoadPromptsStage(IOptions<FarmerSettings> settings, IRunStore runStore)
    {
        _settings = settings.Value;
        _runStore = runStore;
    }

    public async Task<StageResult> ExecuteAsync(RunFlowState state, CancellationToken ct = default)
    {
        // Inline-prompts fast path: when the caller supplied prompts on the
        // /trigger body, skip the disk scan entirely. This path exists so
        // clients that have no shared filesystem with Farmer.Host (phone /
        // browser / tunnel) can still drive a run. state.WorkRequestName is
        // still used for the run's display / metadata; only the prompt
        // *source* changes. See docs/phase-demo-plan.md.
        var inline = state.RunRequest?.PromptsInline;
        List<PromptFile> prompts;

        if (inline is { Count: > 0 })
        {
            prompts = new List<PromptFile>(inline.Count);
            for (var i = 0; i < inline.Count; i++)
            {
                var item = inline[i];
                var filename = string.IsNullOrWhiteSpace(item.Filename)
                    ? $"{i + 1}-inline.md"
                    : item.Filename;
                prompts.Add(new PromptFile
                {
                    // 1-indexed by list position. Inline callers choose order
                    // by array position, not by filename numeric prefix.
                    Order = i + 1,
                    Filename = filename,
                    Content = item.Content ?? string.Empty,
                });
            }
        }
        else
        {
            var planDir = Path.Combine(_settings.Paths.SamplePlansPath, state.WorkRequestName);

            if (!Directory.Exists(planDir))
                return StageResult.Failed(Name, $"Work request directory not found: {planDir}");

            var mdFiles = Directory.GetFiles(planDir, "*.md")
                .Select(Path.GetFileName)
                .Where(f => f is not null)
                .Select(f => f!)
                .Where(f => char.IsDigit(f[0]))
                .OrderBy(f => ParseNumericPrefix(f))
                .ToList();

            if (mdFiles.Count == 0)
                return StageResult.Failed(Name, $"No numbered prompt files found in {planDir}");

            prompts = new List<PromptFile>();
            foreach (var filename in mdFiles)
            {
                var content = await File.ReadAllTextAsync(Path.Combine(planDir, filename), ct);
                prompts.Add(new PromptFile
                {
                    Order = ParseNumericPrefix(filename),
                    Filename = filename,
                    Content = content
                });
            }
        }

        // Retry feedback injection: when the caller populated RunRequest.Feedback
        // (the retry driver does this on attempt > 1), prepend it as a synthetic
        // prompt #0. The VM's CLAUDE.md tells Claude that a 0-indexed prompt is
        // reviewer feedback; worker.sh picks it up because its find pattern
        // `[0-9]*-*.md` includes 0-prefixed files, sorted numerically.
        if (!string.IsNullOrWhiteSpace(state.RunRequest?.Feedback))
        {
            prompts.Insert(0, new PromptFile
            {
                Order = 0,
                Filename = "0-feedback.md",
                Content = state.RunRequest.Feedback,
            });
        }

        // Worker mode precedence: per-request override > config default > "real" as the
        // final fallback. worker.sh on the VM reads `worker_mode` from task-packet.json.
        var workerMode = state.RunRequest?.WorkerMode
            ?? (string.IsNullOrWhiteSpace(_settings.DefaultWorkerMode) ? "real" : _settings.DefaultWorkerMode);

        var taskPacket = new TaskPacket
        {
            RunId = state.RunId,
            WorkRequestName = state.WorkRequestName,
            BranchName = $"{state.Vm?.Name ?? "local"}-{state.WorkRequestName}",
            Prompts = prompts,
            WorkerMode = workerMode,
            // Mirror the feedback onto the packet for observability; worker.sh ignores
            // this field today (it reads feedback as prompt #0), but having it here
            // makes "was this a retry?" obvious from task-packet.json alone.
            Feedback = state.RunRequest?.Feedback,
        };

        state.TaskPacket = taskPacket;

        if (state.RunRequest is not null)
            state.RunRequest.PromptCount = prompts.Count;

        await _runStore.SaveTaskPacketAsync(taskPacket, ct);

        return StageResult.Succeeded(Name);
    }

    private static int ParseNumericPrefix(string filename)
    {
        var digits = new string(filename.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }
}
