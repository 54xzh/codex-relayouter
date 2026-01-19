// ChatMessageViewModel：用于聊天消息展示（支持流式追加，通知 UI 刷新）。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace codex_bridge.ViewModels;

public sealed class ChatMessageViewModel : INotifyPropertyChanged
{
    private string _text;
    private bool _isTraceExpanded;
    private bool _renderMarkdown;
    private readonly Dictionary<string, TraceEntryViewModel> _traceById = new(StringComparer.Ordinal);

    public ChatMessageViewModel(string role, string text, string? runId = null, bool renderMarkdown = true)
    {
        Role = role;
        _text = text;
        _renderMarkdown = renderMarkdown;
        RunId = runId;
        CreatedAt = DateTimeOffset.Now;
        Trace.CollectionChanged += TraceOnCollectionChanged;
        Images.CollectionChanged += ImagesOnCollectionChanged;
    }

    public string Role { get; }

    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);

    public bool IsAssistant => !IsUser;

    public string? RunId { get; }

    public DateTimeOffset CreatedAt { get; }

    public ObservableCollection<TraceEntryViewModel> Trace { get; } = new();

    public int TraceCount => Trace.Count;

    public bool HasTrace => Trace.Count > 0;

    public string TraceHeader => $"执行过程（{TraceCount}）";

    public bool IsTraceExpanded
    {
        get => _isTraceExpanded;
        set
        {
            if (_isTraceExpanded == value)
            {
                return;
            }

            _isTraceExpanded = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<ChatImageViewModel> Images { get; } = new();

    public int ImageCount => Images.Count;

    public bool HasImages => Images.Count > 0;

    public bool RenderMarkdown
    {
        get => _renderMarkdown;
        set
        {
            if (_renderMarkdown == value)
            {
                return;
            }

            _renderMarkdown = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowMarkdown));
            OnPropertyChanged(nameof(ShowPlainText));
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (string.Equals(_text, value, StringComparison.Ordinal))
            {
                return;
            }

            _text = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasText));
            OnPropertyChanged(nameof(MarkdownText));
            OnPropertyChanged(nameof(ShowMarkdown));
            OnPropertyChanged(nameof(ShowPlainText));
        }
    }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// Text with underscores escaped to prevent Markdown from interpreting them as italic markers.
    /// Only asterisks (*) will be used for emphasis.
    /// </summary>
    public string MarkdownText => NormalizeMarkdownForRendering(EscapeUnderscores(Text));

    private static string NormalizeMarkdownForRendering(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // MarkdownTextBlock 的解析在某些情况下对“带缩进的列表”不够稳定。
        // 这里做轻量规范化：
        // 1) 列表行（最多 3 个前导空格）去缩进，避免被当作普通段落/代码块。
        // 2) “标签行(:/：) + 下一行是列表”时补一个空行，兼容更严格的解析器。
        // 3) 不在 fenced code block（```/~~~）内改写内容。
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var builder = new StringBuilder(normalized.Length + 16);
        var insideFence = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.AsSpan().TrimStart();

            if (IsFenceMarker(trimmed))
            {
                insideFence = !insideFence;
            }

            if (!insideFence)
            {
                if (IsBoxRuleLine(trimmed) && index + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[index + 1]))
                {
                    builder.Append(line);
                    builder.Append('\n');
                    builder.Append('\n');
                    continue;
                }

                if (IsLabelLine(line) && index + 1 < lines.Length && IsListLine(lines[index + 1]))
                {
                    builder.Append(line);
                    builder.Append('\n');
                    builder.Append('\n');
                    continue;
                }

                line = DedentListLine(line);
                line = ConvertSoftBreaksToHardBreaks(line);
            }

            builder.Append(line);
            if (index < lines.Length - 1)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    private static bool IsFenceMarker(ReadOnlySpan<char> trimmedLine) =>
        trimmedLine.StartsWith("```".AsSpan(), StringComparison.Ordinal)
        || trimmedLine.StartsWith("~~~".AsSpan(), StringComparison.Ordinal);

    private static bool IsBoxRuleLine(ReadOnlySpan<char> trimmedLine)
    {
        if (trimmedLine.Length < 3)
        {
            return false;
        }

        foreach (var ch in trimmedLine)
        {
            if (ch != '─')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLabelLine(string line) =>
        line.EndsWith(":", StringComparison.Ordinal) || line.EndsWith("：", StringComparison.Ordinal);

    private static string ConvertSoftBreaksToHardBreaks(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line;
        }

        // 期望表现：只要文本里包含 \n，就显示换行（而不是被 Markdown 合并成空格）。
        // CommonMark: 行尾添加两个空格可强制硬换行（Hard Line Break）。
        // - 不处理空行
        // - 不处理可能属于“缩进代码块”的行（4 空格或 Tab 开头）以避免改写代码内容
        // - 已经是硬换行（行尾已有两个空格）或显式硬换行（行尾反斜杠）则不重复添加
        if (line.Length >= 1 && (line[0] == '\t' || (line.Length >= 4 && line.StartsWith("    ", StringComparison.Ordinal))))
        {
            return line;
        }

        if (line.EndsWith("  ", StringComparison.Ordinal) || line.EndsWith("\\", StringComparison.Ordinal))
        {
            return line;
        }

        return $"{line}  ";
    }

    private static bool IsListLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var index = 0;
        while (index < line.Length && index < 3 && line[index] == ' ')
        {
            index++;
        }

        if (index >= line.Length)
        {
            return false;
        }

        var marker = line[index];
        if ((marker == '-' || marker == '*' || marker == '+') && index + 1 < line.Length && line[index + 1] == ' ')
        {
            return true;
        }

        var digitIndex = index;
        while (digitIndex < line.Length && char.IsDigit(line[digitIndex]))
        {
            digitIndex++;
        }

        if (digitIndex == index || digitIndex + 1 >= line.Length)
        {
            return false;
        }

        var delimiter = line[digitIndex];
        return (delimiter == '.' || delimiter == ')') && line[digitIndex + 1] == ' ';
    }

    private static string DedentListLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line;
        }

        var index = 0;
        while (index < line.Length && index < 3 && line[index] == ' ')
        {
            index++;
        }

        if (index == 0)
        {
            return line;
        }

        return IsListLine(line) ? line[index..] : line;
    }

    private static string EscapeUnderscores(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Escape underscores that could be interpreted as emphasis markers.
        // This prevents _text_ from becoming italic, while preserving *text* for italic.
        return text.Replace("_", @"\_");
    }

    public bool ShowMarkdown => IsAssistant && HasText && RenderMarkdown;

    public bool ShowPlainText => IsAssistant && HasText && !RenderMarkdown;

    public void AppendLine(string line)
    {
        Text = string.IsNullOrEmpty(Text) ? line : $"{Text}\n{line}";
    }

    public void AppendTextDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        if (string.Equals(Text, "思考中…", StringComparison.Ordinal))
        {
            Text = delta;
            return;
        }

        Text = string.Concat(Text, delta);
    }

    public void UpsertCommandTrace(string id, string? tool, string command, string? status, int? exitCode, string? output)
    {
        if (_traceById.TryGetValue(id, out var existing) && existing.IsCommand)
        {
            existing.UpdateCommand(status, exitCode, output);
            return;
        }

        var created = TraceEntryViewModel.CreateCommand(id, tool, command, status);
        created.UpdateCommand(status, exitCode, output);
        _traceById[id] = created;
        Trace.Add(created);
    }

    public void AddReasoningTrace(string id, string text)
    {
        UpsertReasoningTrace(id, text);
    }

    public void UpsertReasoningTrace(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (_traceById.TryGetValue(id, out var existing) && existing.IsReasoning)
        {
            existing.SetReasoningText(text);
            ExpandLatestReasoning(existing);
            return;
        }

        var entry = TraceEntryViewModel.CreateReasoning(id, text);
        _traceById[id] = entry;
        Trace.Add(entry);
        ExpandLatestReasoning(entry);
    }

    public void AppendReasoningDelta(string id, string textDelta)
    {
        if (string.IsNullOrEmpty(textDelta))
        {
            return;
        }

        if (_traceById.TryGetValue(id, out var existing) && existing.IsReasoning)
        {
            existing.AppendReasoningDelta(textDelta);
            ExpandLatestReasoning(existing);
            return;
        }

        var entry = TraceEntryViewModel.CreateReasoning(id, textDelta);
        _traceById[id] = entry;
        Trace.Add(entry);
        ExpandLatestReasoning(entry);
    }

    private void ExpandLatestReasoning(TraceEntryViewModel latest)
    {
        if (!latest.IsReasoning)
        {
            return;
        }

        foreach (var entry in Trace)
        {
            if (!entry.IsReasoning || ReferenceEquals(entry, latest))
            {
                continue;
            }

            entry.IsExpanded = false;
        }

        latest.IsExpanded = true;
    }

    public void AppendCommandOutputDelta(string id, string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        if (_traceById.TryGetValue(id, out var existing) && existing.IsCommand)
        {
            existing.AppendOutputDelta(delta);
            return;
        }

        var created = TraceEntryViewModel.CreateCommand(id, tool: null, command: "command", status: "inProgress");
        _traceById[id] = created;
        Trace.Add(created);
        created.AppendOutputDelta(delta);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void TraceOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(TraceCount));
        OnPropertyChanged(nameof(HasTrace));
        OnPropertyChanged(nameof(TraceHeader));
    }

    private void ImagesOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ImageCount));
        OnPropertyChanged(nameof(HasImages));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
