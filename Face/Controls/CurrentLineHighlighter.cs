using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace Face.Controls;

public class CurrentLineHighlighter : IBackgroundRenderer {
    private static readonly ISolidColorBrush Highlight =
        new SolidColorBrush(Color.FromArgb(48, 255, 215, 0));

    public int Line { get; set; } // 1-based; 0 = disabled

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext) {
        if (Line <= 0 || Line > textView.Document.LineCount) return;
        var docLine = textView.Document.GetLineByNumber(Line);
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, docLine))
            drawingContext.FillRectangle(Highlight, rect);
    }
}
