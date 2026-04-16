using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Farmer.Tests.Integration;

/// <summary>
/// xUnit class fixture: spawns a fresh nats-server.exe --jetstream on a random port
/// per test class, with an isolated temp store_dir. Torn down in DisposeAsync so
/// there's no shared state between classes.
///
/// The nats-server.exe binary is copied into the test's output directory by the
/// csproj's &lt;None Include="..\..\tools\nats-server.exe" CopyToOutputDirectory="PreserveNewest" /&gt;.
/// </summary>
public sealed class NatsServerFixture : IAsyncLifetime
{
    private Process? _process;
    private string? _storeDir;

    public int Port { get; private set; }
    public string Url => $"nats://127.0.0.1:{Port}";

    public Task InitializeAsync()
    {
        Port = FindFreePort();
        _storeDir = Path.Combine(Path.GetTempPath(), "farmer-nats-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_storeDir);

        var exe = Path.Combine(AppContext.BaseDirectory, "nats-server.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"nats-server.exe not found next to the test DLL at {exe}. " +
                "Check Farmer.Tests.Integration.csproj's <None Include ...CopyToOutputDirectory /> rule.");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--jetstream --store_dir \"{_storeDir}\" --addr 127.0.0.1 --port {Port}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start nats-server.exe");

        return WaitUntilListeningAsync(_process, Port, TimeSpan.FromSeconds(15));
    }

    public Task DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
            _process?.Dispose();
        }
        catch { /* best-effort teardown */ }

        try
        {
            if (_storeDir is not null && Directory.Exists(_storeDir))
                Directory.Delete(_storeDir, recursive: true);
        }
        catch { /* Windows occasionally holds file handles briefly; safe to leak a temp dir in tests */ }

        return Task.CompletedTask;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WaitUntilListeningAsync(Process process, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                var stdout = await process.StandardOutput.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"nats-server exited with code {process.ExitCode} before listening on :{port}.\n" +
                    $"stdout:\n{stdout}\nstderr:\n{stderr}");
            }
            try
            {
                using var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
                return;
            }
            catch
            {
                await Task.Delay(150);
            }
        }
        throw new TimeoutException($"nats-server did not listen on :{port} within {timeout}");
    }
}
