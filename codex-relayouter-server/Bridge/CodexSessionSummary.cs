// CodexSessionSummary：会话列表返回的最小元数据模型（来源于 ~/.codex/sessions 的 session_meta）。
using System.Text.Json.Serialization;

namespace codex_bridge_server.Bridge;

public sealed class CodexSessionSummary
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cwd { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Originator { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CliVersion { get; init; }
}
