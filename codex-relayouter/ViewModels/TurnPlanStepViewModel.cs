// TurnPlanStepViewModel：用于“待办/计划”列表展示。
using System;

namespace codex_bridge.ViewModels;

public sealed class TurnPlanStepViewModel
{
    public TurnPlanStepViewModel(string step, string status)
    {
        Step = step ?? string.Empty;
        Status = status ?? string.Empty;
    }

    public string Step { get; }

    public string Status { get; }

    public bool IsCompleted => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);

    public bool IsInProgress => string.Equals(Status, "inProgress", StringComparison.OrdinalIgnoreCase);

    public string StatusLabel =>
        IsCompleted ? "已完成" :
        IsInProgress ? "进行中" :
        "待处理";
}
