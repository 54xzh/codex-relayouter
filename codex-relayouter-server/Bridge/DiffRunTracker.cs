using System;
using System.Collections.Generic;
using System.Linq;

namespace codex_bridge_server.Bridge;

public sealed class DiffRunTracker
{
    private readonly Dictionary<string, DiffFileSnapshot> _files = new(StringComparer.OrdinalIgnoreCase);

    public bool HasChanges => _files.Count > 0;

    public IReadOnlyList<DiffFileSnapshot> GetSnapshots()
    {
        if (_files.Count == 0)
        {
            return Array.Empty<DiffFileSnapshot>();
        }

        return _files.Values
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DiffFileSnapshot? Update(string path, string? diff, int? added, int? removed)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalizedPath = path.Trim();
        var diffText = string.IsNullOrWhiteSpace(diff) ? null : diff;
        var hasProvidedCounts = added.HasValue || removed.HasValue;
        var providedAdded = added ?? 0;
        var providedRemoved = removed ?? 0;
        var addedCount = providedAdded;
        var removedCount = providedRemoved;

        if (!string.IsNullOrWhiteSpace(diffText))
        {
            var (calcAdded, calcRemoved) = DiffStatsCalculator.CountUnifiedDiffLines(diffText);
            if (calcAdded == 0 && calcRemoved == 0 && hasProvidedCounts && (providedAdded != 0 || providedRemoved != 0))
            {
                // diff 文本不包含可统计的 +/- 行，但上游给了非零计数（例如二进制/元数据变更的摘要），保留上游计数。
                addedCount = providedAdded;
                removedCount = providedRemoved;
            }
            else
            {
                addedCount = calcAdded;
                removedCount = calcRemoved;
            }
        }

        var snapshot = new DiffFileSnapshot(normalizedPath, diffText, addedCount, removedCount);
        _files[normalizedPath] = snapshot;
        return snapshot;
    }

    public DiffSummarySnapshot BuildSummary()
    {
        var files = GetSnapshots();
        if (files.Count == 0)
        {
            return new DiffSummarySnapshot(Array.Empty<DiffFileSummary>(), 0, 0);
        }

        var totalAdded = 0;
        var totalRemoved = 0;
        var summaries = new DiffFileSummary[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            totalAdded += file.Added;
            totalRemoved += file.Removed;
            summaries[i] = new DiffFileSummary(file.Path, file.Added, file.Removed);
        }

        return new DiffSummarySnapshot(summaries, totalAdded, totalRemoved);
    }
}

public sealed record DiffFileSnapshot(string Path, string? Diff, int Added, int Removed);

public sealed record DiffFileSummary(string Path, int Added, int Removed);

public sealed record DiffSummarySnapshot(IReadOnlyList<DiffFileSummary> Files, int TotalAdded, int TotalRemoved);
