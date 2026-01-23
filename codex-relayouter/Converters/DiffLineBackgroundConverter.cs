using System;
using codex_bridge.ViewModels;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace codex_bridge.Converters;

public sealed class DiffLineBackgroundConverter : IValueConverter
{
    private static readonly Brush TransparentBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    private static readonly Brush AddedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x00, 0xA8, 0x00));
    private static readonly Brush RemovedBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xD4, 0x30, 0x30));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DiffLineKind kind)
        {
            return kind switch
            {
                DiffLineKind.Added => AddedBrush,
                DiffLineKind.Removed => RemovedBrush,
                _ => TransparentBrush,
            };
        }

        if (value is string text && Enum.TryParse<DiffLineKind>(text, ignoreCase: true, out var parsed))
        {
            return parsed switch
            {
                DiffLineKind.Added => AddedBrush,
                DiffLineKind.Removed => RemovedBrush,
                _ => TransparentBrush,
            };
        }

        return TransparentBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

