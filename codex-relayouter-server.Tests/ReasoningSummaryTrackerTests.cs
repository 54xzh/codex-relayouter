using codex_bridge_server.Bridge;

namespace codex_bridge_server.Tests;

public sealed class ReasoningSummaryTrackerTests
{
    [Fact]
    public void TryAppendDelta_emitsPreviousPartWhenIndexAdvances()
    {
        var tracker = new ReasoningSummaryTracker();

        Assert.False(tracker.TryAppendDelta("item_1", summaryIndex: 0, delta: "Hello ", out _));
        Assert.False(tracker.TryAppendDelta("item_1", summaryIndex: 0, delta: "world", out _));

        Assert.True(tracker.TryAppendDelta("item_1", summaryIndex: 1, delta: "Next", out var completed));
        Assert.Equal("item_1_summary_0", completed.PartId);
        Assert.Equal("Hello world", completed.Text);
    }

    [Fact]
    public void FinalizeFromSummary_emitsUnsentTailPart()
    {
        var tracker = new ReasoningSummaryTracker();

        Assert.False(tracker.TryAppendDelta("item_1", summaryIndex: 0, delta: "A", out _));
        Assert.True(tracker.TryAppendDelta("item_1", summaryIndex: 1, delta: "B", out _));
        Assert.False(tracker.TryAppendDelta("item_1", summaryIndex: 1, delta: "C", out _));

        var pending = tracker.FinalizeFromSummary("item_1", new[] { "A", "BC" });
        Assert.Single(pending);
        Assert.Equal("item_1_summary_1", pending[0].PartId);
        Assert.Equal("BC", pending[0].Text);
    }

    [Fact]
    public void FinalizeFromSummary_withoutSummary_flushesBufferedParts()
    {
        var tracker = new ReasoningSummaryTracker();

        Assert.False(tracker.TryAppendDelta("item_1", summaryIndex: 0, delta: "A", out _));
        Assert.True(tracker.TryAppendDelta("item_1", summaryIndex: 1, delta: "B", out _));
        Assert.False(tracker.TryAppendDelta("item_1", summaryIndex: 1, delta: "C", out _));

        var pending = tracker.FinalizeFromSummary("item_1", summaryParts: null);
        Assert.Single(pending);
        Assert.Equal("item_1_summary_1", pending[0].PartId);
        Assert.Equal("BC", pending[0].Text);
    }

    [Fact]
    public void FinalizeFromSummary_withoutDeltas_emitsAllParts()
    {
        var tracker = new ReasoningSummaryTracker();

        var pending = tracker.FinalizeFromSummary("item_1", new[] { "P0", "P1" });
        Assert.Equal(2, pending.Count);
        Assert.Equal("item_1_summary_0", pending[0].PartId);
        Assert.Equal("P0", pending[0].Text);
        Assert.Equal("item_1_summary_1", pending[1].PartId);
        Assert.Equal("P1", pending[1].Text);
    }
}

