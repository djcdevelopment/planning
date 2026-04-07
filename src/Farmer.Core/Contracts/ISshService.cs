namespace Farmer.Core.Contracts;

public sealed class SshResult
{
    public int ExitCode { get; set; }
    public string StdOut { get; set; } = string.Empty;
    public string StdErr { get; set; } = string.Empty;
    public bool Success => ExitCode == 0;
}

public interface ISshService
{
    Task<SshResult> ExecuteAsync(string vmName, string command, TimeSpan? timeout = null, CancellationToken ct = default);

    Task ScpUploadAsync(string vmName, string localPath, string remotePath, CancellationToken ct = default);

    Task ScpUploadContentAsync(string vmName, string content, string remotePath, CancellationToken ct = default);
}
