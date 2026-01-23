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
        var addedCount = added ?? 0;
        var removedCount = removed ?? 0;

        if (!string.IsNullOrWhiteSpace(diffText))
        {
            var (calcAdded, calcRemoved) = DiffStatsCalculator.CountUnifiedDiffLines(diffText);
            addedCount = calcAdded;
            removedCount = calcRemoved;
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
