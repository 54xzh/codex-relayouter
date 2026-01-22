// ChatNavigationRequest：用于在 Frame.Navigate 时携带会话切换目标。
namespace codex_bridge.Models;

public sealed record ChatNavigationRequest(string? SessionId, string? Cwd);
