// SessionMessage：与 Bridge Server `/api/v1/sessions/{sessionId}/messages` 对齐的会话消息模型。
namespace codex_bridge.Models;

public sealed class SessionMessage
{
    public required string Role { get; init; }

    public required string Text { get; init; }

    public string[]? Images { get; init; }

    public string? Kind { get; init; }

    public SessionTraceEntry[]? Trace { get; init; }
}
