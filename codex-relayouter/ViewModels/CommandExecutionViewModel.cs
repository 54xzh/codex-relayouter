using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace codex_bridge.ViewModels;

public sealed class CommandExecutionViewModel : INotifyPropertyChanged
{
    private string _status;
    private int? _exitCode;
    private string? _output;

    public CommandExecutionViewModel(string itemId, string command, string status)
    {
        ItemId = itemId;
        Command = command;
        _status = status;
    }

    public string ItemId { get; }

    public string Command { get; }

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
            OnPropertyChanged(nameof(Summary));
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
            OnPropertyChanged(nameof(Summary));
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

    public string Summary
    {
        get
        {
            var exitCodeText = ExitCode.HasValue ? $" exitCode={ExitCode.Value}" : string.Empty;
            return $"{Status}{exitCodeText}".Trim();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

