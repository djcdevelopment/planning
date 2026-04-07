namespace Farmer.Core.Contracts;

public interface IMappedDriveReader
{
    Task<string> ReadFileAsync(string vmName, string relativePath, CancellationToken ct = default);

    Task<bool> FileExistsAsync(string vmName, string relativePath, CancellationToken ct = default);

    Task<string> WaitForFileAsync(string vmName, string relativePath, TimeSpan timeout, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListFilesAsync(string vmName, string relativePath, string pattern = "*", CancellationToken ct = default);
}
