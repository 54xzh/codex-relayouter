using codex_bridge.ViewModels;

namespace codex_bridge.Tests;

public sealed class TraceExpansionTests
{
    [Fact]
    public void ManualExpandedReasoningIsNotAutoCollapsed()
    {
        var message = new ChatMessageViewModel(role: "assistant", text: string.Empty);

        message.UpsertReasoningTrace(id: "r1", text: "First reasoning");
        message.UpsertReasoningTrace(id: "r2", text: "Second reasoning");

        var r1 = message.Trace.Single(entry => entry.Id == "r1");
        var r2 = message.Trace.Single(entry => entry.Id == "r2");

        Assert.False(r1.IsExpanded);
        Assert.True(r2.IsExpanded);

        r1.IsExpanded = true;

        message.UpsertReasoningTrace(id: "r3", text: "Third reasoning");

        var r3 = message.Trace.Single(entry => entry.Id == "r3");
        Assert.True(r1.IsExpanded);
        Assert.False(r2.IsExpanded);
        Assert.True(r3.IsExpanded);
    }

    [Fact]
    public void ManualCollapsedLatestReasoningIsNotAutoReexpanded()
    {
        var message = new ChatMessageViewModel(role: "assistant", text: string.Empty);

        message.UpsertReasoningTrace(id: "r1", text: "First reasoning");

        var r1 = message.Trace.Single(entry => entry.Id == "r1");
        Assert.True(r1.IsExpanded);

        r1.IsExpanded = false;

        message.AppendReasoningDelta(id: "r1", textDelta: " more");

        Assert.False(r1.IsExpanded);
    }
}
