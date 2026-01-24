// Bridge 翻译配置：用于自动翻译 Trace（思考摘要）等非主链路文本。
namespace codex_bridge_server.Bridge;

public sealed class BridgeTranslationOptions
{
    public bool Enabled { get; set; } = false;

    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    public string TargetLocale { get; set; } = "zh-CN";

    public string Model { get; set; } = "gpt-4.1-mini";

    public int MaxRequestsPerSecond { get; set; } = 1;

    public int MaxConcurrency { get; set; } = 2;

    public int TimeoutMs { get; set; } = 15000;

    public int MaxInputChars { get; set; } = 8000;
}

