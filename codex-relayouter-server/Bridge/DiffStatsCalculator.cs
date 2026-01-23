using System;

namespace codex_bridge_server.Bridge;

public static class DiffStatsCalculator
{
    public static (int Added, int Removed) CountUnifiedDiffLines(string? diff)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            return (0, 0);
        }

        var added = 0;
        var removed = 0;
        var lines = diff.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("@@ ", StringComparison.Ordinal)
                || line.StartsWith("diff ", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('+'))
            {
                added++;
                continue;
            }

            if (line.StartsWith('-'))
            {
                removed++;
            }
        }

        return (added, removed);
    }
}
