// SessionTraceEntry：与 Bridge Server message.trace 对齐的 trace 条目模型。
namespace codex_bridge.Models;

public sealed class SessionTraceEntry
{
    public required string Kind { get; init; }

    public string? Title { get; init; }

    public string? Text { get; init; }

    public string? Tool { get; init; }

    public string? Command { get; init; }

    public string? Status { get; init; }

    public int? ExitCode { get; init; }

    public string? Output { get; init; }
}

