// JSON 序列化配置：保持与后端一致（camelCase 等默认设置）。
using System.Text.Json;

namespace codex_bridge.Bridge;

internal static class BridgeJson
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

