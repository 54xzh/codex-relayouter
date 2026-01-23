// TurnPlanSnapshot：与 Bridge Server `/api/v1/sessions/{sessionId}/plan` 对齐的会话计划模型。
using System;

namespace codex_bridge.Models;

public sealed class TurnPlanSnapshot
{
    public required string SessionId { get; init; }

    public required string TurnId { get; init; }

    public string? Explanation { get; init; }

    public required TurnPlanStep[] Plan { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class TurnPlanStep
{
    public required string Step { get; init; }

    public required string Status { get; init; }
}
