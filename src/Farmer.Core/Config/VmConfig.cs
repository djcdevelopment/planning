namespace Farmer.Core.Config;

public sealed class VmConfig
{
    public string Name { get; set; } = string.Empty;
    public string SshHost { get; set; } = string.Empty;
    public string SshUser { get; set; } = "claude";
    public string MappedDriveLetter { get; set; } = string.Empty;
    public string RemoteProjectPath { get; set; } = "~/projects";

    public string MappedDrivePath => $@"{MappedDriveLetter}:\projects";
    public string CommsPath => ".comms";
    public string PlansPath => "plans";
    public string OutputPath => "output";
}
