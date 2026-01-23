namespace codex_bridge_server.Bridge;

public sealed class CodexAppServerApprovalDecision
{
    public required string Decision { get; init; }

    public string[]? ExecpolicyAmendment { get; init; }
}

