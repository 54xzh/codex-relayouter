// ChatImageViewModel：用于聊天图片展示（从 data URL 解码为 BitmapImage）。
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace codex_bridge.ViewModels;

public sealed class ChatImageViewModel : INotifyPropertyChanged
{
    private BitmapImage? _bitmap;
    private bool _isLoading;
    private string? _error;

    public ChatImageViewModel(string dataUrl)
    {
        DataUrl = dataUrl ?? string.Empty;
    }

    public string DataUrl { get; }

    public BitmapImage? Bitmap
    {
        get => _bitmap;
        private set
        {
            if (ReferenceEquals(_bitmap, value))
            {
                return;
            }

            _bitmap = value;
            OnPropertyChanged();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value)
            {
                return;
            }

            _isLoading = value;
            OnPropertyChanged();
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (string.Equals(_error, value, StringComparison.Ordinal))
            {
                return;
            }

            _error = value;
            OnPropertyChanged();
        }
    }

    public async Task LoadAsync()
    {
        if (Bitmap is not null || IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            if (!TryDecodeDataUrl(DataUrl, out var bytes))
            {
                Error = "无效图片数据";
                return;
            }

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            Bitmap = bitmap;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static bool TryDecodeDataUrl(string dataUrl, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return false;
        }

        var trimmed = dataUrl.Trim();
        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = trimmed.IndexOf(',');
        if (commaIndex < 0)
        {
            return false;
        }

        var meta = trimmed.Substring(5, commaIndex - 5);
        if (!meta.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var payload = trimmed.Substring(commaIndex + 1);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(payload);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

