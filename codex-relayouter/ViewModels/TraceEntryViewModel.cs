// TraceEntryViewModel：聊天页 trace 条目（命令/思考等）。
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace codex_bridge.ViewModels;

public sealed class TraceEntryViewModel : INotifyPropertyChanged
{
    private string _status;
    private int? _exitCode;
    private string? _output;
    private string _rawReasoningText = string.Empty;
    private bool _isExpanded;

    private TraceEntryViewModel(string id, string kind)
    {
        Id = id;
        Kind = kind;
        _status = string.Empty;
    }

    public string Id { get; }

    public string Kind { get; }

    public bool IsCommand => string.Equals(Kind, "command", StringComparison.OrdinalIgnoreCase);

    public bool IsReasoning => string.Equals(Kind, "reasoning", StringComparison.OrdinalIgnoreCase);

    public string? Title { get; private set; }

    public string? Text { get; private set; }

    public string? Tool { get; private set; }

    public string? Command { get; private set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (string.Equals(_status, value, StringComparison.Ordinal))
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(StatusBadge));
            OnPropertyChanged(nameof(HasStatusBadge));
        }
    }

    public int? ExitCode
    {
        get => _exitCode;
        set
        {
            if (_exitCode == value)
            {
                return;
            }

            _exitCode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(StatusBadge));
            OnPropertyChanged(nameof(HasStatusBadge));
        }
    }

    public string? Output
    {
        get => _output;
        set
        {
            if (string.Equals(_output, value, StringComparison.Ordinal))
            {
                return;
            }

            _output = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasOutput));
        }
    }

    public bool HasOutput => !string.IsNullOrWhiteSpace(Output);

    public bool HasStatusBadge => !string.IsNullOrWhiteSpace(StatusBadge);

    public string? StatusBadge
    {
        get
        {
            var status = string.IsNullOrWhiteSpace(Status) ? "completed" : Status.Trim();
            var exitCode = ExitCode;

            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                && (!exitCode.HasValue || exitCode.Value == 0))
            {
                return null;
            }

            if (!exitCode.HasValue)
            {
                return status;
            }

            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return $"exitCode={exitCode.Value}";
            }

            return $"{status} exitCode={exitCode.Value}".Trim();
        }
    }

    public string StatusLine
    {
        get
        {
            var status = string.IsNullOrWhiteSpace(Status) ? "completed" : Status.Trim();
            var exitCodeText = ExitCode.HasValue ? $" exitCode={ExitCode.Value}" : string.Empty;
            return $"{status}{exitCodeText}".Trim();
        }
    }

    public static TraceEntryViewModel CreateReasoning(string id, string rawText)
    {
        var (title, text) = SplitReasoningTitle(rawText);
        var vm = new TraceEntryViewModel(id, "reasoning")
        {
            Title = title,
            Text = text,
        };
        vm._rawReasoningText = rawText ?? string.Empty;
        return vm;
    }

    public static TraceEntryViewModel CreateReasoning(string id, string? title, string? text)
    {
        var resolvedTitle = string.IsNullOrWhiteSpace(title) ? "思考摘要" : title.Trim();
        var resolvedText = text?.Trim() ?? string.Empty;

        var vm = new TraceEntryViewModel(id, "reasoning")
        {
            Title = resolvedTitle,
            Text = resolvedText,
        };
        vm._rawReasoningText = string.IsNullOrWhiteSpace(title)
            ? resolvedText
            : $"**{resolvedTitle}**\n\n{resolvedText}";
        return vm;
    }

    public static TraceEntryViewModel CreateCommand(string id, string? tool, string command, string? status)
    {
        var vm = new TraceEntryViewModel(id, "command")
        {
            Tool = string.IsNullOrWhiteSpace(tool) ? null : tool.Trim(),
            Command = command,
        };
        vm.Status = string.IsNullOrWhiteSpace(status) ? "completed" : status;
        return vm;
    }

    public void UpdateCommand(string? status, int? exitCode, string? output)
    {
        if (status is not null)
        {
            Status = status;
        }

        if (exitCode.HasValue)
        {
            ExitCode = exitCode;
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            Output = output;
        }
    }

    public void AppendOutputDelta(string delta)
    {
        if (!IsCommand || string.IsNullOrEmpty(delta))
        {
            return;
        }

        Output = string.IsNullOrEmpty(Output) ? delta : string.Concat(Output, delta);
    }

    public void AppendReasoningDelta(string delta)
    {
        if (!IsReasoning || string.IsNullOrEmpty(delta))
        {
            return;
        }

        _rawReasoningText = string.Concat(_rawReasoningText, delta);
        UpdateReasoningFromRawText(_rawReasoningText);
    }

    public void SetReasoningText(string rawText)
    {
        if (!IsReasoning)
        {
            return;
        }

        _rawReasoningText = rawText ?? string.Empty;
        UpdateReasoningFromRawText(_rawReasoningText);
    }

    private void UpdateReasoningFromRawText(string rawText)
    {
        var (title, text) = SplitReasoningTitle(rawText);

        if (!string.Equals(Title, title, StringComparison.Ordinal))
        {
            Title = title;
            OnPropertyChanged(nameof(Title));
        }

        if (!string.Equals(Text, text, StringComparison.Ordinal))
        {
            Text = text;
            OnPropertyChanged(nameof(Text));
        }
    }

    private static (string title, string text) SplitReasoningTitle(string? raw)
    {
        var detail = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(detail))
        {
            return ("思考摘要", string.Empty);
        }

        if (detail.StartsWith("**", StringComparison.Ordinal))
        {
            var end = detail.IndexOf("**", startIndex: 2, StringComparison.Ordinal);
            if (end > 2)
            {
                var extractedTitle = detail.Substring(2, end - 2).Trim();
                var rest = detail.Substring(end + 2).Trim();
                var title = string.IsNullOrWhiteSpace(extractedTitle) ? "思考摘要" : extractedTitle;
                var text = string.IsNullOrWhiteSpace(rest) ? detail : rest;
                return (title, text);
            }
        }

        using var reader = new StringReader(detail);
        var firstLine = reader.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return ("思考摘要", detail);
        }

        var titleLine = firstLine.Length <= 80 ? firstLine : string.Concat(firstLine.AsSpan(0, 79), "…");
        return (titleLine, detail);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
