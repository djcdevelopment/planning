namespace Farmer.Core.Config;

public sealed class FarmerSettings
{
    public const string SectionName = "Farmer";

    public List<VmConfig> Vms { get; set; } = [];
    public string DataPath { get; set; } = @"D:\work\start\farmer\data";
    public string SamplePlansPath { get; set; } = @"D:\work\start\farmer\data\sample-plans";
    public string RunStorePath { get; set; } = @"D:\work\start\farmer\runs";
    public int MaxRetries { get; set; } = 2;
    public int SshCommandTimeoutSeconds { get; set; } = 30;
    public int SshDispatchTimeoutMinutes { get; set; } = 30;
    public int SshfsCacheLagMs { get; set; } = 500;
    public int ProgressPollIntervalMs { get; set; } = 2000;
}
