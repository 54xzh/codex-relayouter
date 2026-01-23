// SessionSettingsSnapshot：与 Bridge Server `/api/v1/sessions/{sessionId}/settings` 对齐的会话设置快照。
namespace codex_bridge.Models;

public sealed class SessionSettingsSnapshot
{
    public string? Sandbox { get; init; }

    public string? ApprovalPolicy { get; init; }
}

