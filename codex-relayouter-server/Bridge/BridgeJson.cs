// JSON 序列化配置：对齐 Web/跨端默认设置，避免协议字段大小写不一致。
using System.Text.Json;

namespace codex_bridge_server.Bridge;

internal static class BridgeJson
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

