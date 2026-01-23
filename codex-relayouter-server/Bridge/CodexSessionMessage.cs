// CodexSessionMessage：从 ~/.codex/sessions 的 JSONL 中解析出的会话消息（用于历史回放）。
using System.Text.Json.Serialization;

namespace codex_bridge_server.Bridge;

public sealed class CodexSessionMessage
{
    public required string Role { get; init; }

    public required string Text { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Images { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CodexSessionTraceEntry>? Trace { get; init; }
}
