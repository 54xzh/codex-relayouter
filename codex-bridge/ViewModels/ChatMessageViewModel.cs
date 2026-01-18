// ChatMessageViewModel：用于聊天消息展示（支持流式追加，通知 UI 刷新）。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace codex_bridge.ViewModels;

public sealed class ChatMessageViewModel : INotifyPropertyChanged
{
    private string _text;
    private readonly Dictionary<string, TraceEntryViewModel> _traceById = new(StringComparer.Ordinal);

    public ChatMessageViewModel(string role, string text, string? runId = null)
    {
        Role = role;
        _text = text;
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

    public ObservableCollection<ChatImageViewModel> Images { get; } = new();

    public int ImageCount => Images.Count;

    public bool HasImages => Images.Count > 0;

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
        }
    }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

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
            return;
        }

        var entry = TraceEntryViewModel.CreateReasoning(id, text);
        _traceById[id] = entry;
        Trace.Add(entry);
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
            return;
        }

        var entry = TraceEntryViewModel.CreateReasoning(id, textDelta);
        _traceById[id] = entry;
        Trace.Add(entry);
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
