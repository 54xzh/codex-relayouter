// Codex 运行配置：定义如何调用本机 codex CLI（可执行文件名、可选参数等）。
namespace codex_bridge_server.Bridge;

public sealed class CodexOptions
{
    public string Executable { get; set; } = "codex";

    public bool SkipGitRepoCheck { get; set; } = false;
}

