using codex_bridge_server.Bridge;
using Xunit;

namespace codex_bridge_server.Tests;

public sealed class DiffStatsCalculatorTests
{
    [Fact]
    public void CountUnifiedDiffLines_IgnoresHeaders()
    {
        var diff = string.Join(
            "\n",
            "diff --git a/file.txt b/file.txt",
            "index 123..456 100644",
            "--- a/file.txt",
            "+++ b/file.txt",
            "@@ -1,2 +1,3 @@",
            "-old line",
            "+new line",
            "+added line",
            " unchanged");

        var (added, removed) = DiffStatsCalculator.CountUnifiedDiffLines(diff);

        Assert.Equal(2, added);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void CountUnifiedDiffLines_ReturnsZeroForEmpty()
    {
        var (added, removed) = DiffStatsCalculator.CountUnifiedDiffLines(" \n ");

        Assert.Equal(0, added);
        Assert.Equal(0, removed);
    }
}
