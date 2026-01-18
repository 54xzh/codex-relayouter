// Codex 运行请求：最小参数集合（prompt + 可选工作区目录）。
namespace codex_bridge_server.Bridge;

public sealed class CodexRunRequest
{
    public required string Prompt { get; init; }

    public IReadOnlyList<string>? Images { get; init; }

    public string? SessionId { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? Model { get; init; }

    public string? Sandbox { get; init; }

    public bool SkipGitRepoCheck { get; init; }

    public string? ApprovalPolicy { get; init; }

    public string? Effort { get; init; }
}
