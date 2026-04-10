namespace Farmer.Core.Config;

public sealed class PathsSettings
{
    public string Root { get; set; } = @"D:\work\planning-runtime";
    public string Data { get; set; } = @"D:\work\planning-runtime\data";
    public string Runs { get; set; } = @"D:\work\planning-runtime\runs";
    public string Inbox { get; set; } = @"D:\work\planning-runtime\inbox";
    public string Outbox { get; set; } = @"D:\work\planning-runtime\outbox";
    public string Qa { get; set; } = @"D:\work\planning-runtime\qa";

    public string SamplePlansPath => Path.Combine(Data, "sample-plans");
}
