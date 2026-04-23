namespace Farmer.Core.Config;

public sealed class PathsSettings
{
    public string Root { get; set; } = @"C:\work\iso\planning-runtime";
    public string Data { get; set; } = @"C:\work\iso\planning-runtime\data";
    public string Runs { get; set; } = @"C:\work\iso\planning-runtime\runs";
    public string Inbox { get; set; } = @"C:\work\iso\planning-runtime\inbox";
    public string Outbox { get; set; } = @"C:\work\iso\planning-runtime\outbox";
    public string Qa { get; set; } = @"C:\work\iso\planning-runtime\qa";

    public string SamplePlansPath => Path.Combine(Data, "sample-plans");
}
