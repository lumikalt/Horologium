using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Face.Controls;

public sealed class StageBrushConverter : IValueConverter {
    public static readonly StageBrushConverter Instance = new();

    // Colors mirror WaterfallControl.KindStyle, rendered at reduced alpha for row highlighting
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.FromArgb(55, 255, 215, 0));
    private static readonly IBrush IFBrush = new SolidColorBrush(Color.FromArgb(70, 74, 126, 199));
    private static readonly IBrush IDBrush = new SolidColorBrush(Color.FromArgb(70, 26, 148, 144));
    private static readonly IBrush EXBrush = new SolidColorBrush(Color.FromArgb(70, 200, 122, 42));
    private static readonly IBrush MEMBrush = new SolidColorBrush(Color.FromArgb(45, 200, 122, 42));
    private static readonly IBrush WBBrush = new SolidColorBrush(Color.FromArgb(70, 74, 176, 74));
    private static readonly IBrush DisBrush = new SolidColorBrush(Color.FromArgb(70, 46, 165, 160));
    private static readonly IBrush IssBrush = new SolidColorBrush(Color.FromArgb(70, 138, 107, 212));
    private static readonly IBrush FlBrush = new SolidColorBrush(Color.FromArgb(70, 196, 80, 80));

    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value as string) switch {
            "PC"          => StageBrushConverter.CurrentBrush,
            "IF"          => StageBrushConverter.IFBrush,
            "ID"          => StageBrushConverter.IDBrush,
            "EX" or "Ex"  => StageBrushConverter.EXBrush,
            "MEM"         => StageBrushConverter.MEMBrush,
            "WB" or "Ret" => StageBrushConverter.WBBrush,
            "Dis"         => StageBrushConverter.DisBrush,
            "Iss"         => StageBrushConverter.IssBrush,
            "~~"          => StageBrushConverter.FlBrush,
            _             => Brushes.Transparent,
        };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}