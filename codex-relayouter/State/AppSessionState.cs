// AppSessionState：在 WinUI 侧维护当前选中的会话（用于 chat.send 绑定 sessionId）。
using System;

namespace codex_bridge.State;

public sealed class AppSessionState
{
    private string? _currentSessionId;
    private string? _currentSessionCwd;

    public event EventHandler? CurrentSessionChanged;

    public string? CurrentSessionId
    {
        get => _currentSessionId;
        set
        {
            if (string.Equals(_currentSessionId, value, StringComparison.Ordinal))
            {
                return;
            }

            _currentSessionId = value;
            CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? CurrentSessionCwd
    {
        get => _currentSessionCwd;
        set
        {
            if (string.Equals(_currentSessionCwd, value, StringComparison.Ordinal))
            {
                return;
            }

            _currentSessionCwd = value;
            CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

