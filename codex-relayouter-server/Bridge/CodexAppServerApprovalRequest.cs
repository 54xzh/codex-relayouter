namespace codex_bridge_server.Bridge;

public sealed class CodexAppServerApprovalRequest
{
    public required string RequestId { get; init; }

    public required string Kind { get; init; }

    public required string ThreadId { get; init; }

    public required string TurnId { get; init; }

    public string? ItemId { get; init; }

    public string? Reason { get; init; }

    public string[]? ProposedExecpolicyAmendment { get; init; }

    public string? GrantRoot { get; init; }
}

