using System;

namespace codex_bridge.ViewModels;

public enum DiffLineKind
{
    Context = 0,
    Added = 1,
    Removed = 2,
    Header = 3,
}

public sealed class DiffLineViewModel
{
    public DiffLineViewModel(string text, DiffLineKind kind)
    {
        Text = text ?? string.Empty;
        Kind = kind;
    }

    public string Text { get; }

    public string DisplayText => string.IsNullOrEmpty(Text) ? " " : Text;

    public DiffLineKind Kind { get; }

    public bool IsAdded => Kind == DiffLineKind.Added;

    public bool IsRemoved => Kind == DiffLineKind.Removed;

    public bool IsHeader => Kind == DiffLineKind.Header;

    public static DiffLineKind Classify(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return DiffLineKind.Context;
        }

        if (line.StartsWith("+++ ", StringComparison.Ordinal)
            || line.StartsWith("--- ", StringComparison.Ordinal)
            || line.StartsWith("@@ ", StringComparison.Ordinal)
            || line.StartsWith("diff ", StringComparison.Ordinal)
            || line.StartsWith("index ", StringComparison.Ordinal)
            || line.StartsWith("*** ", StringComparison.Ordinal))
        {
            return DiffLineKind.Header;
        }

        if (line.StartsWith('+'))
        {
            return DiffLineKind.Added;
        }

        if (line.StartsWith('-'))
        {
            return DiffLineKind.Removed;
        }

        return DiffLineKind.Context;
    }
}

