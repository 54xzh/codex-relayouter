// SessionSummary：与 Bridge Server `/api/v1/sessions` 对齐的会话摘要模型。
using System;

namespace codex_bridge.Models;

public sealed class SessionSummary
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public string? Cwd { get; init; }

    public string? Originator { get; init; }

    public string? CliVersion { get; init; }
}
