// Bridge 协议消息封装：用于 WebSocket 命令/事件/响应的统一消息结构。
using System.Text.Json;
using System.Text.Json.Serialization;

namespace codex_bridge_server.Bridge;

public sealed class BridgeEnvelope
{
    public int ProtocolVersion { get; set; } = 1;

    public required string Type { get; set; }

    public required string Name { get; set; }

    public string? Id { get; set; }

    public DateTimeOffset Ts { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Data { get; set; }
}

