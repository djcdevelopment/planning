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
        var planDir = Path.Combine(_settings.SamplePlansPath, state.WorkRequestName);

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

        var prompts = new List<PromptFile>();
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

        var taskPacket = new TaskPacket
        {
            RunId = state.RunId,
            WorkRequestName = state.WorkRequestName,
            BranchName = $"{state.Vm?.Name ?? "local"}-{state.WorkRequestName}",
            Prompts = prompts
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
