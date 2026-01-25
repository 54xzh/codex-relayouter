// ReasoningSummaryTracker：跟踪 app-server 的 reasoning summaryTextDelta，并在 summaryIndex 前进时产出“已完成段落”的文本快照。
using System.Text;

namespace codex_bridge_server.Bridge;

internal sealed class ReasoningSummaryTracker
{
    private readonly Dictionary<string, ReasoningItemState> _states = new(StringComparer.Ordinal);

    public void Clear(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        _states.Remove(itemId.Trim());
    }

    public bool TryAppendDelta(string itemId, long summaryIndex, string delta, out ReasoningSummaryPart completedPart)
    {
        completedPart = default;

        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrEmpty(delta))
        {
            return false;
        }

        var key = itemId.Trim();
        var state = GetOrCreateState(key);

        var hasCompletedPart = false;
        if (summaryIndex > state.CurrentIndex && state.CurrentIndex >= 0)
        {
            var prevIndex = state.CurrentIndex;
            if (!state.EmittedIndices.Contains(prevIndex)
                && state.Buffers.TryGetValue(prevIndex, out var prevBuffer))
            {
                var completedText = prevBuffer.ToString();
                if (!string.IsNullOrWhiteSpace(completedText))
                {
                    completedPart = new ReasoningSummaryPart(BuildPartId(key, prevIndex), completedText);
                    state.EmittedIndices.Add(prevIndex);
                    hasCompletedPart = true;
                }
            }
        }

        if (summaryIndex > state.CurrentIndex)
        {
            state.CurrentIndex = summaryIndex;
        }

        if (!state.Buffers.TryGetValue(summaryIndex, out var buffer))
        {
            buffer = new StringBuilder();
            state.Buffers[summaryIndex] = buffer;
        }

        buffer.Append(delta);
        return hasCompletedPart;
    }

    public IReadOnlyList<ReasoningSummaryPart> FinalizeFromSummary(string itemId, IReadOnlyList<string>? summaryParts)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return Array.Empty<ReasoningSummaryPart>();
        }

        var key = itemId.Trim();
        _states.TryGetValue(key, out var state);

        var parts = new List<ReasoningSummaryPart>(capacity: summaryParts?.Count ?? 0);

        if (summaryParts is not null && summaryParts.Count > 0)
        {
            for (var i = 0; i < summaryParts.Count; i++)
            {
                var text = summaryParts[i];
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var index = (long)i;
                var partId = BuildPartId(key, index);

                var shouldEmit = true;
                if (state is not null && state.EmittedIndices.Contains(index))
                {
                    if (state.Buffers.TryGetValue(index, out var existingBuffer))
                    {
                        shouldEmit = !string.Equals(existingBuffer.ToString().Trim(), text.Trim(), StringComparison.Ordinal);
                    }
                    else
                    {
                        shouldEmit = false;
                    }
                }

                if (shouldEmit)
                {
                    parts.Add(new ReasoningSummaryPart(partId, text));
                }

                state?.EmittedIndices.Add(index);
                if (state is not null)
                {
                    state.Buffers[index] = new StringBuilder(text);
                }
            }
        }
        else if (state is not null)
        {
            foreach (var (index, buffer) in state.Buffers.OrderBy(pair => pair.Key))
            {
                if (state.EmittedIndices.Contains(index))
                {
                    continue;
                }

                var text = buffer.ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                parts.Add(new ReasoningSummaryPart(BuildPartId(key, index), text));
                state.EmittedIndices.Add(index);
            }
        }

        if (state is not null)
        {
            _states.Remove(key);
        }

        return parts.Count == 0 ? Array.Empty<ReasoningSummaryPart>() : parts.ToArray();
    }

    private static string BuildPartId(string itemId, long summaryIndex) => $"{itemId}_summary_{summaryIndex}";

    private ReasoningItemState GetOrCreateState(string itemId)
    {
        if (_states.TryGetValue(itemId, out var existing))
        {
            return existing;
        }

        var created = new ReasoningItemState();
        _states[itemId] = created;
        return created;
    }

    private sealed class ReasoningItemState
    {
        public long CurrentIndex { get; set; } = -1;

        public Dictionary<long, StringBuilder> Buffers { get; } = new();

        public HashSet<long> EmittedIndices { get; } = new();
    }
}

internal readonly record struct ReasoningSummaryPart(string PartId, string Text);
