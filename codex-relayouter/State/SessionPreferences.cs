// SessionPreferences：本地会话偏好设置存储（隐藏/置顶状态），不影响 Codex CLI 原始文件。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace codex_bridge.State;

public sealed class SessionPreferencesData
{
    public List<string> Hidden { get; set; } = new();
    public List<string> Pinned { get; set; } = new();
}

public sealed class SessionPreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private SessionPreferencesData _data = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loaded;

    public SessionPreferences()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDir = Path.Combine(localAppData, "codex-bridge");
        _filePath = Path.Combine(appDir, "session_preferences.json");
    }

    public async Task LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_loaded)
            {
                return;
            }

            if (File.Exists(_filePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_filePath);
                    _data = JsonSerializer.Deserialize<SessionPreferencesData>(json, JsonOptions) ?? new();
                }
                catch
                {
                    _data = new();
                }
            }

            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_data, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool IsHidden(string sessionId)
    {
        return _data.Hidden.Contains(sessionId);
    }

    public bool IsPinned(string sessionId)
    {
        return _data.Pinned.Contains(sessionId);
    }

    public async Task SetHiddenAsync(string sessionId, bool hidden)
    {
        if (hidden)
        {
            if (!_data.Hidden.Contains(sessionId))
            {
                _data.Hidden.Add(sessionId);
            }
        }
        else
        {
            _data.Hidden.Remove(sessionId);
        }

        await SaveAsync();
    }

    public async Task SetPinnedAsync(string sessionId, bool pinned)
    {
        if (pinned)
        {
            if (!_data.Pinned.Contains(sessionId))
            {
                _data.Pinned.Add(sessionId);
            }
        }
        else
        {
            _data.Pinned.Remove(sessionId);
        }

        await SaveAsync();
    }

    public async Task ToggleHiddenAsync(string sessionId)
    {
        await SetHiddenAsync(sessionId, !IsHidden(sessionId));
    }

    public async Task TogglePinnedAsync(string sessionId)
    {
        await SetPinnedAsync(sessionId, !IsPinned(sessionId));
    }

    public async Task RemoveSessionAsync(string sessionId)
    {
        _data.Hidden.Remove(sessionId);
        _data.Pinned.Remove(sessionId);
        await SaveAsync();
    }

    public IReadOnlyList<string> GetHiddenIds() => _data.Hidden;

    public IReadOnlyList<string> GetPinnedIds() => _data.Pinned;
}
