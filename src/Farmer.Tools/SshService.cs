using System.Collections.Concurrent;
using System.Text;
using Farmer.Core.Config;
using Farmer.Core.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;

namespace Farmer.Tools;

public sealed class SshService : ISshService, IDisposable
{
    private readonly FarmerSettings _settings;
    private readonly ILogger<SshService> _logger;
    private readonly ConcurrentDictionary<string, SshClient> _clients = new();
    private readonly ConcurrentDictionary<string, ScpClient> _scpClients = new();

    public SshService(IOptions<FarmerSettings> settings, ILogger<SshService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<SshResult> ExecuteAsync(string vmName, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var vm = GetVmConfig(vmName);
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(_settings.SshCommandTimeoutSeconds);

        _logger.LogInformation("SSH [{Vm}] Executing: {Command}", vmName, command);

        var client = GetOrCreateSshClient(vm);

        return await Task.Run(() =>
        {
            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = effectiveTimeout;

            var result = new SshResult();
            try
            {
                result.StdOut = cmd.Execute();
                result.StdErr = cmd.Error;
                result.ExitCode = cmd.ExitStatus ?? -1;
            }
            catch (Exception ex)
            {
                result.ExitCode = -1;
                result.StdErr = ex.Message;
            }

            _logger.LogInformation("SSH [{Vm}] Exit: {ExitCode}", vmName, result.ExitCode);
            return result;
        }, ct);
    }

    public async Task ScpUploadAsync(string vmName, string localPath, string remotePath, CancellationToken ct = default)
    {
        var vm = GetVmConfig(vmName);
        _logger.LogInformation("SCP [{Vm}] Upload: {Local} -> {Remote}", vmName, localPath, remotePath);

        var client = GetOrCreateScpClient(vm);

        await Task.Run(() =>
        {
            using var fs = File.OpenRead(localPath);
            client.Upload(fs, remotePath);
        }, ct);
    }

    public async Task ScpUploadContentAsync(string vmName, string content, string remotePath, CancellationToken ct = default)
    {
        var vm = GetVmConfig(vmName);
        _logger.LogInformation("SCP [{Vm}] Upload content -> {Remote} ({Bytes} bytes)", vmName, remotePath, content.Length);

        var client = GetOrCreateScpClient(vm);

        await Task.Run(() =>
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
            client.Upload(ms, remotePath);
        }, ct);
    }

    private VmConfig GetVmConfig(string vmName)
    {
        return _settings.Vms.FirstOrDefault(v => v.Name == vmName)
            ?? throw new ArgumentException($"VM '{vmName}' not found in configuration");
    }

    private SshClient GetOrCreateSshClient(VmConfig vm)
    {
        return _clients.GetOrAdd(vm.Name, _ =>
        {
            var client = new SshClient(vm.SshHost, vm.SshUser, new PrivateKeyFile(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_rsa")));
            client.Connect();
            return client;
        });
    }

    private ScpClient GetOrCreateScpClient(VmConfig vm)
    {
        return _scpClients.GetOrAdd(vm.Name, _ =>
        {
            var client = new ScpClient(vm.SshHost, vm.SshUser, new PrivateKeyFile(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_rsa")));
            client.Connect();
            return client;
        });
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            try { client.Disconnect(); client.Dispose(); } catch { }
        }
        foreach (var client in _scpClients.Values)
        {
            try { client.Disconnect(); client.Dispose(); } catch { }
        }
        _clients.Clear();
        _scpClients.Clear();
    }
}
