// CodexSessionCreateRequest：创建会话的最小请求体（MVP 仅支持 cwd）。
namespace codex_bridge_server.Bridge;

public sealed class CodexSessionCreateRequest
{
    public string? Cwd { get; init; }
}
