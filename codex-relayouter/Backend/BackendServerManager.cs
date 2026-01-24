// BackendServerManager：负责在 WinUI 启动时拉起本机 Bridge Server，并提供自动连接所需的地址信息。
using System;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace codex_bridge.Backend;

public sealed class BackendServerManager : IAsyncDisposable
{
    private sealed class BackendProcessLogCapture : IDisposable
    {
        private readonly object _writeGate = new();
        private readonly Process _process;
        private readonly StreamWriter _writer;
        private readonly DataReceivedEventHandler _stdoutHandler;
        private readonly DataReceivedEventHandler _stderrHandler;
        private bool _disposed;

        public BackendProcessLogCapture(Process process, string logFilePath, Encoding encoding)
        {
            _process = process;
            var dir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var stream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, encoding)
            {
                AutoFlush = true,
            };

            _writer.WriteLine($"===== bridge-server start {DateTimeOffset.Now:O} pid={process.Id} =====");

            _stdoutHandler = (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Write("OUT", e.Data);
                }
            };
            _stderrHandler = (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Write("ERR", e.Data);
                }
            };

            process.OutputDataReceived += _stdoutHandler;
            process.ErrorDataReceived += _stderrHandler;

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        private void Write(string channel, string message)
        {
            lock (_writeGate)
            {
                if (_disposed)
                {
                    return;
                }

                _writer.WriteLine($"{DateTimeOffset.Now:O} [{channel}] {message}");
            }
        }

        public void Dispose()
        {
            lock (_writeGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                try
                {
                    _writer.WriteLine($"===== bridge-server stop {DateTimeOffset.Now:O} =====");
                }
                catch
                {
                }
            }

            try
            {
                _process.OutputDataReceived -= _stdoutHandler;
                _process.ErrorDataReceived -= _stderrHandler;
            }
            catch
            {
            }

            try
            {
                _process.CancelOutputRead();
            }
            catch
            {
            }

            try
            {
                _process.CancelErrorRead();
            }
            catch
            {
            }

            try
            {
                _writer.Dispose();
            }
            catch
            {
            }
        }
    }

    private sealed class BackendServerPreferences
    {
        public bool LanEnabled { get; set; }
        public int? Port { get; set; }

        public bool TranslationEnabled { get; set; }
        public string? TranslationBaseUrl { get; set; }
        public string? TranslationApiKey { get; set; }
        public string? TranslationModel { get; set; }
        public int? TranslationMaxRequestsPerSecond { get; set; }
        public int? TranslationMaxConcurrency { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _appDir;
    private readonly string _preferencesPath;
    private readonly string _logsDir;
    private readonly string _logFilePath;
    private Task? _startTask;
    private CancellationTokenSource? _lifetimeCts;
    private Process? _process;
    private BackendProcessLogCapture? _logCapture;
    private bool _lanEnabled;
    private int? _port;
    private bool _translationEnabled;
    private string? _translationBaseUrl;
    private string? _translationApiKey;
    private string? _translationModel;
    private int _translationMaxRequestsPerSecond = 1;
    private int _translationMaxConcurrency = 2;

    public Uri? HttpBaseUri { get; private set; }

    public Uri? WebSocketUri { get; private set; }

    public string LogFilePath => _logFilePath;

    public string LogsDirectoryPath => _logsDir;

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

    public BackendTranslationSettings TranslationSettings
    {
        get
        {
            lock (_gate)
            {
                return new BackendTranslationSettings(
                    Enabled: _translationEnabled,
                    BaseUrl: _translationBaseUrl,
                    ApiKey: _translationApiKey,
                    Model: _translationModel,
                    MaxRequestsPerSecond: _translationMaxRequestsPerSecond,
                    MaxConcurrency: _translationMaxConcurrency);
            }
        }
    }

    public BackendServerManager()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _appDir = Path.Combine(localAppData, "codex-relayouter");
        _preferencesPath = Path.Combine(_appDir, "connection_preferences.json");
        _logsDir = Path.Combine(_appDir, "logs");
        _logFilePath = Path.Combine(_logsDir, "bridge-server.log");

        TryLoadPreferences();
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

        SavePreferences();
        await StopAsync();
        await EnsureStartedAsync();
    }

    public async Task SetTranslationSettingsAsync(BackendTranslationSettings settings)
    {
        bool shouldRestart;
        lock (_gate)
        {
            shouldRestart =
                _translationEnabled != settings.Enabled
                || !string.Equals(_translationBaseUrl, settings.BaseUrl, StringComparison.Ordinal)
                || !string.Equals(_translationApiKey, settings.ApiKey, StringComparison.Ordinal)
                || !string.Equals(_translationModel, settings.Model, StringComparison.Ordinal)
                || _translationMaxRequestsPerSecond != settings.MaxRequestsPerSecond
                || _translationMaxConcurrency != settings.MaxConcurrency;

            _translationEnabled = settings.Enabled;
            _translationBaseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl) ? null : settings.BaseUrl.Trim();
            _translationApiKey = string.IsNullOrWhiteSpace(settings.ApiKey) ? null : settings.ApiKey.Trim();
            _translationModel = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim();
            _translationMaxRequestsPerSecond = settings.MaxRequestsPerSecond <= 0 ? 1 : settings.MaxRequestsPerSecond;
            _translationMaxConcurrency = settings.MaxConcurrency <= 0 ? 1 : settings.MaxConcurrency;
        }

        if (!shouldRestart)
        {
            return;
        }

        SavePreferences();
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

        try
        {
            PrepareLogFile();

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var port = GetOrCreatePort(attempt == 1);
                HttpBaseUri = new Uri($"http://127.0.0.1:{port}/");
                WebSocketUri = new Uri($"ws://127.0.0.1:{port}/ws");

                var exePath = LocateServerExecutable();
                var serverDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;

                var listenHost = IsLanEnabled ? "0.0.0.0" : "127.0.0.1";

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"--urls http://{listenHost}:{port} --Bridge:Security:RemoteEnabled={IsLanEnabled.ToString().ToLowerInvariant()}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Utf8NoBom,
                    StandardErrorEncoding = Utf8NoBom,
                    WorkingDirectory = serverDir,
                };
                startInfo.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Production";

                ApplyTranslationEnv(startInfo);

                var process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true,
                };

                process.Start();

                BackendProcessLogCapture? logCapture = null;
                try
                {
                    logCapture = new BackendProcessLogCapture(process, _logFilePath, Utf8NoBom);
                }
                catch
                {
                }

                lock (_gate)
                {
                    _process = process;
                    _logCapture?.Dispose();
                    _logCapture = logCapture;
                }

                try
                {
                    await WaitForHealthyAsync(HttpBaseUri, process, lifetimeCts.Token);
                    SavePreferences();
                    return;
                }
                catch
                {
                    logCapture?.Dispose();

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

                    lock (_gate)
                    {
                        if (_process == process)
                        {
                            _process = null;
                        }
                    }

                    if (attempt == 1)
                    {
                        throw;
                    }
                }
            }
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

    private void PrepareLogFile()
    {
        try
        {
            if (!Directory.Exists(_logsDir))
            {
                Directory.CreateDirectory(_logsDir);
            }

            if (!File.Exists(_logFilePath))
            {
                return;
            }

            var info = new FileInfo(_logFilePath);
            const long maxBytes = 10 * 1024 * 1024;
            if (info.Length <= maxBytes)
            {
                return;
            }

            var rotated = Path.Combine(_logsDir, $"bridge-server_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.log");
            File.Move(_logFilePath, rotated, overwrite: true);
        }
        catch
        {
        }
    }

    private int GetOrCreatePort(bool forceNew)
    {
        int? existing;
        lock (_gate)
        {
            existing = _port;
        }

        var port = forceNew ? null : existing;
        if (port is null || port <= 0 || port > 65535)
        {
            port = GetFreeTcpPort();
            lock (_gate)
            {
                _port = port;
            }
        }

        return port.Value;
    }

    private static string LocateServerExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "bridge-server", "codex-relayouter-server.exe");
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

    private void TryLoadPreferences()
    {
        try
        {
            if (!File.Exists(_preferencesPath))
            {
                return;
            }

            var json = File.ReadAllText(_preferencesPath, Utf8NoBom);
            var prefs = JsonSerializer.Deserialize<BackendServerPreferences>(json, JsonOptions);
            if (prefs is null)
            {
                return;
            }

            lock (_gate)
            {
                _lanEnabled = prefs.LanEnabled;
                if (prefs.Port is > 0 and <= 65535)
                {
                    _port = prefs.Port;
                }

                _translationEnabled = prefs.TranslationEnabled;
                _translationBaseUrl = string.IsNullOrWhiteSpace(prefs.TranslationBaseUrl) ? null : prefs.TranslationBaseUrl.Trim();
                _translationApiKey = string.IsNullOrWhiteSpace(prefs.TranslationApiKey) ? null : prefs.TranslationApiKey.Trim();
                _translationModel = string.IsNullOrWhiteSpace(prefs.TranslationModel) ? null : prefs.TranslationModel.Trim();
                if (prefs.TranslationMaxRequestsPerSecond is > 0)
                {
                    _translationMaxRequestsPerSecond = prefs.TranslationMaxRequestsPerSecond.Value;
                }
                if (prefs.TranslationMaxConcurrency is > 0)
                {
                    _translationMaxConcurrency = prefs.TranslationMaxConcurrency.Value;
                }
            }
        }
        catch
        {
        }
    }

    private void SavePreferences()
    {
        BackendServerPreferences snapshot;
        lock (_gate)
        {
            snapshot = new BackendServerPreferences
            {
                LanEnabled = _lanEnabled,
                Port = _port,
                TranslationEnabled = _translationEnabled,
                TranslationBaseUrl = _translationBaseUrl,
                TranslationApiKey = _translationApiKey,
                TranslationModel = _translationModel,
                TranslationMaxRequestsPerSecond = _translationMaxRequestsPerSecond,
                TranslationMaxConcurrency = _translationMaxConcurrency,
            };
        }

        try
        {
            var dir = Path.GetDirectoryName(_preferencesPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(_preferencesPath, json, Utf8NoBom);
        }
        catch
        {
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

    private void ApplyTranslationEnv(ProcessStartInfo startInfo)
    {
        BackendTranslationSettings settings;
        lock (_gate)
        {
            settings = new BackendTranslationSettings(
                Enabled: _translationEnabled,
                BaseUrl: _translationBaseUrl,
                ApiKey: _translationApiKey,
                Model: _translationModel,
                MaxRequestsPerSecond: _translationMaxRequestsPerSecond,
                MaxConcurrency: _translationMaxConcurrency);
        }

        startInfo.EnvironmentVariables["Bridge__Translation__Enabled"] = settings.Enabled.ToString().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            startInfo.EnvironmentVariables["Bridge__Translation__BaseUrl"] = settings.BaseUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            startInfo.EnvironmentVariables["Bridge__Translation__ApiKey"] = settings.ApiKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            startInfo.EnvironmentVariables["Bridge__Translation__Model"] = settings.Model.Trim();
        }

        startInfo.EnvironmentVariables["Bridge__Translation__TargetLocale"] = "zh-CN";
        startInfo.EnvironmentVariables["Bridge__Translation__MaxRequestsPerSecond"] = settings.MaxRequestsPerSecond <= 0 ? "1" : settings.MaxRequestsPerSecond.ToString();
        startInfo.EnvironmentVariables["Bridge__Translation__MaxConcurrency"] = settings.MaxConcurrency <= 0 ? "1" : settings.MaxConcurrency.ToString();
    }

    public async Task StopAsync()
    {
        Task? startTask;
        CancellationTokenSource? lifetimeCts;
        Process? process;
        BackendProcessLogCapture? logCapture;

        lock (_gate)
        {
            startTask = _startTask;
            lifetimeCts = _lifetimeCts;
            process = _process;
            logCapture = _logCapture;

            _startTask = null;
            _lifetimeCts = null;
            _process = null;
            _logCapture = null;
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
            logCapture?.Dispose();
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
            logCapture?.Dispose();
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

public sealed record BackendTranslationSettings(
    bool Enabled,
    string? BaseUrl,
    string? ApiKey,
    string? Model,
    int MaxRequestsPerSecond,
    int MaxConcurrency);
