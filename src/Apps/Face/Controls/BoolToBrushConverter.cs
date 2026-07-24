#region

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

#endregion

namespace Face.Controls;

public sealed class BoolToBrushConverter : IValueConverter {
    public static readonly BoolToBrushConverter CacheLastAccess = new() {
        TrueColor = Color.FromArgb(50, 0, 180, 255),
        FalseColor = Colors.Transparent,
    };

    private Color TrueColor { get; init; }
    private Color FalseColor { get; init; }

    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        new SolidColorBrush(value is true ? TrueColor : FalseColor);

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}