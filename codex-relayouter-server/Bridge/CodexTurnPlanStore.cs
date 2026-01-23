// CodexTurnPlanStore：缓存每个会话(threadId/sessionId)的最新 turn plan（用于 WS 推送与 HTTP 回填）。
using System.Collections.Concurrent;

namespace codex_bridge_server.Bridge;

public sealed class CodexTurnPlanStore
{
    private readonly ConcurrentDictionary<string, TurnPlanSnapshot> _snapshots = new(StringComparer.Ordinal);

    public void Upsert(TurnPlanSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (string.IsNullOrWhiteSpace(snapshot.SessionId))
        {
            throw new ArgumentException("SessionId 不能为空", nameof(snapshot));
        }

        _snapshots[snapshot.SessionId] = snapshot;
    }

    public bool TryGet(string sessionId, out TurnPlanSnapshot snapshot) =>
        _snapshots.TryGetValue(sessionId, out snapshot!);
}

public sealed record TurnPlanSnapshot(
    string SessionId,
    string TurnId,
    string? Explanation,
    TurnPlanStep[] Plan,
    DateTimeOffset UpdatedAt);

public sealed record TurnPlanStep(
    string Step,
    string Status);

