// Codex 输出行：用于统一处理 stdout/stderr 的流式输出。
namespace codex_bridge_server.Bridge;

public sealed record CodexOutputLine(string Stream, string Line);

