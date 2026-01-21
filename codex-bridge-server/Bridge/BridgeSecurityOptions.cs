// Bridge 安全配置：默认仅本机回环访问；开启远程后支持设备令牌（可逐设备撤销）。
namespace codex_bridge_server.Bridge;

public sealed class BridgeSecurityOptions
{
    public bool RemoteEnabled { get; set; } = false;

    public string? BearerToken { get; set; }
}
