// BackendServerManager：负责在 WinUI 启动时拉起本机 Bridge Server，并提供自动连接所需的地址信息。
using System;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace codex_bridge.Backend;

public sealed class BackendServerManager : IAsyncDisposable
{
    private readonly object _gate = new();
    private Task? _startTask;
    private CancellationTokenSource? _lifetimeCts;
    private Process? _process;
    private bool _lanEnabled;
    private int? _port;

    public Uri? HttpBaseUri { get; private set; }

    public Uri? WebSocketUri { get; private set; }

    public bool IsLanEnabled
    {
        get
        {
            lock (_gate)
            {
                return _lanEnabled;
            }
        }
    }

    public Task EnsureStartedAsync()
    {
        lock (_gate)
        {
            _startTask ??= EnsureStartedCoreAsync();
            return _startTask;
        }
    }

    public async Task SetLanEnabledAsync(bool enabled)
    {
        bool shouldRestart;
        lock (_gate)
        {
            shouldRestart = _lanEnabled != enabled;
            _lanEnabled = enabled;
        }

        if (!shouldRestart)
        {
            return;
        }

        await StopAsync();
        await EnsureStartedAsync();
    }

    private async Task EnsureStartedCoreAsync()
    {
        var lifetimeCts = new CancellationTokenSource();
        lock (_gate)
        {
            _lifetimeCts = lifetimeCts;
        }

        var port = _port ??= GetFreeTcpPort();
        HttpBaseUri = new Uri($"http://127.0.0.1:{port}/");
        WebSocketUri = new Uri($"ws://127.0.0.1:{port}/ws");

        try
        {
            var exePath = LocateServerExecutable();
            var serverDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;

            var listenHost = IsLanEnabled ? "0.0.0.0" : "127.0.0.1";

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--urls http://{listenHost}:{port} --Bridge:Security:RemoteEnabled={IsLanEnabled.ToString().ToLowerInvariant()}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = serverDir,
            };
            startInfo.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Production";

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };

            process.Start();

            lock (_gate)
            {
                _process = process;
            }

            await WaitForHealthyAsync(HttpBaseUri, process, lifetimeCts.Token);
        }
        catch
        {
            lock (_gate)
            {
                _startTask = null;
                _port = null;
            }

            throw;
        }
    }

    private static string LocateServerExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "bridge-server", "codex-bridge-server.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException($"未找到后端可执行文件：{candidate}");
    }

    private static int GetFreeTcpPort()
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

    private static async Task WaitForHealthyAsync(Uri httpBaseUri, Process process, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        var healthUri = new Uri(httpBaseUri, "api/v1/health");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException($"后端进程已退出，ExitCode={process.ExitCode}");
            }

            try
            {
                using var response = await httpClient.GetAsync(healthUri, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException("等待后端健康检查超时。");
    }

    public async Task StopAsync()
    {
        Task? startTask;
        CancellationTokenSource? lifetimeCts;
        Process? process;

        lock (_gate)
        {
            startTask = _startTask;
            lifetimeCts = _lifetimeCts;
            process = _process;

            _startTask = null;
            _lifetimeCts = null;
            _process = null;
        }

        try
        {
            lifetimeCts?.Cancel();
        }
        catch
        {
        }
        finally
        {
            lifetimeCts?.Dispose();
        }

        if (startTask is not null)
        {
            try
            {
                await startTask;
            }
            catch
            {
            }
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
