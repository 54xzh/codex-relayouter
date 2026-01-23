// ConnectionService：全局连接配置与 BridgeClient 管理服务。
using codex_bridge.Bridge;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace codex_bridge.State;

public sealed class ConnectionService : IAsyncDisposable
{
    private readonly BridgeClient _client = new();
    private readonly Timer _codexConfigWriteTimer;

    private string? _workingDirectory;
    private string? _model;
    private string? _effort;
    private int _isWritingCodexConfig;

    private const int CodexConfigWriteDebounceMilliseconds = 500;
    private const int RecentWorkingDirectoryLimit = 5;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly List<string> _recentWorkingDirectories = new();

    // 连接配置
    public string? ServerUrl { get; set; }
    public string? BearerToken { get; set; }
    public string? WorkingDirectory
    {
        get => _workingDirectory;
        set
        {
            var normalized = NormalizeWorkingDirectory(value);
            if (string.Equals(_workingDirectory, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _workingDirectory = normalized;
            TrackRecentWorkingDirectory(normalized);
        }
    }

    public IReadOnlyList<string> RecentWorkingDirectories => _recentWorkingDirectories;
    public string? Model
    {
        get => _model;
        set
        {
            if (string.Equals(_model, value, StringComparison.Ordinal))
            {
                return;
            }

            _model = value;
            ScheduleCodexConfigWrite();
        }
    }
    public string? Sandbox { get; set; }
    public string? ApprovalPolicy { get; set; }
    public string? Effort
    {
        get => _effort;
        set
        {
            if (string.Equals(_effort, value, StringComparison.Ordinal))
            {
                return;
            }

            _effort = value;
            ScheduleCodexConfigWrite();
        }
    }
    public bool SkipGitRepoCheck { get; set; } = true;

    // 客户端状态
    public BridgeClient Client => _client;
    public bool IsConnected => _client.IsConnected;

    public event EventHandler? ConnectionStateChanged;
    public event EventHandler<BridgeEnvelope>? EnvelopeReceived;
    public event EventHandler<string>? ConnectionClosed;

    public ConnectionService()
    {
        CodexCliConfig.TryLoadModelReasoningEffortApprovalPolicyAndSandboxMode(
            out _model,
            out _effort,
            out var approvalPolicy,
            out var sandboxMode);

        ApprovalPolicy = approvalPolicy;
        Sandbox = sandboxMode;
        LoadRecentWorkingDirectories();
        _codexConfigWriteTimer = new Timer(_ => PersistCodexConfigFromTimer(), null, Timeout.Infinite, Timeout.Infinite);

        _client.EnvelopeReceived += (_, e) => EnvelopeReceived?.Invoke(this, e);
        _client.ConnectionClosed += (_, msg) =>
        {
            ConnectionClosed?.Invoke(this, msg);
            ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(uri, BearerToken, cancellationToken);
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _client.DisconnectAsync(cancellationToken);
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task SendCommandAsync(string name, object data, CancellationToken cancellationToken) =>
        _client.SendCommandAsync(name, data, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            _codexConfigWriteTimer.Change(Timeout.Infinite, Timeout.Infinite);
            PersistCodexConfigNow();
        }
        catch
        {
        }

        _codexConfigWriteTimer.Dispose();
        await _client.DisposeAsync();
    }

    private void ScheduleCodexConfigWrite() =>
        _codexConfigWriteTimer.Change(CodexConfigWriteDebounceMilliseconds, Timeout.Infinite);

    private void PersistCodexConfigFromTimer()
    {
        if (Interlocked.Exchange(ref _isWritingCodexConfig, 1) == 1)
        {
            ScheduleCodexConfigWrite();
            return;
        }

        try
        {
            PersistCodexConfigNow();
        }
        finally
        {
            Interlocked.Exchange(ref _isWritingCodexConfig, 0);
        }
    }

    private void PersistCodexConfigNow()
    {
        if (!CodexCliConfig.TryUpdateModelAndReasoningEffort(_model, _effort, out var error))
        {
            Debug.WriteLine($"更新 Codex 配置失败: {error}");
        }
    }

    private static string? NormalizeWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        return Path.TrimEndingDirectorySeparator(workingDirectory.Trim());
    }

    private static bool IsUncPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();

        if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) && !trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string GetRecentWorkingDirectoriesPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty;
        }

        return Path.Combine(baseDir, "codex-relayouter", "recent-cwd.json");
    }

    private void TrackRecentWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return;
        }

        var normalized = NormalizeWorkingDirectory(workingDirectory);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!IsUncPath(normalized) && !Directory.Exists(normalized))
        {
            return;
        }

        _recentWorkingDirectories.RemoveAll(entry => string.Equals(entry, normalized, StringComparison.OrdinalIgnoreCase));
        _recentWorkingDirectories.Insert(0, normalized);

        if (_recentWorkingDirectories.Count > RecentWorkingDirectoryLimit)
        {
            _recentWorkingDirectories.RemoveRange(RecentWorkingDirectoryLimit, _recentWorkingDirectories.Count - RecentWorkingDirectoryLimit);
        }

        SaveRecentWorkingDirectories();
    }

    private void LoadRecentWorkingDirectories()
    {
        try
        {
            var path = GetRecentWorkingDirectoriesPath();
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path, Utf8NoBom);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var items = JsonSerializer.Deserialize<string[]>(json);
            if (items is null || items.Length == 0)
            {
                return;
            }

            foreach (var item in items)
            {
                var normalized = NormalizeWorkingDirectory(item);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (!IsUncPath(normalized) && !Directory.Exists(normalized))
                {
                    continue;
                }

                if (_recentWorkingDirectories.Exists(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _recentWorkingDirectories.Add(normalized);
                if (_recentWorkingDirectories.Count >= RecentWorkingDirectoryLimit)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取最近工作目录失败: {ex.Message}");
        }
    }

    private void SaveRecentWorkingDirectories()
    {
        try
        {
            var path = GetRecentWorkingDirectoriesPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_recentWorkingDirectories);
            File.WriteAllText(path, json, Utf8NoBom);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"保存最近工作目录失败: {ex.Message}");
        }
    }
}
