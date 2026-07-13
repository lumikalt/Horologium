using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Face.Controls;

/// <summary>
///     Renders a Ripes-style address decomposition strip showing how a 32-bit cache
///     address splits into Tag / Index / Offset bit fields, with colored bands,
///     bit-range annotations, decoded values, and arrows pointing to field labels.
/// </summary>
public sealed class CacheAddressDecoder : Control {
    // ── Layout constants ────────────────────────────────────────────────────

    private const double AddrH = 22; // address header + badge row height
    private const double BarH = 32;  // colored bar height

    private const double StemH = 16; // vertical connector height
    // ── Styled properties ────────────────────────────────────────────────────

    public static readonly StyledProperty<ulong?> AddressProperty =
        AvaloniaProperty.Register<CacheAddressDecoder, ulong?>(nameof(Address));

    public static readonly StyledProperty<bool> IsHitProperty =
        AvaloniaProperty.Register<CacheAddressDecoder, bool>(nameof(IsHit));

    public static readonly StyledProperty<int> TagBitsProperty =
        AvaloniaProperty.Register<CacheAddressDecoder, int>(nameof(TagBits), 22);

    public static readonly StyledProperty<int> IndexBitsProperty =
        AvaloniaProperty.Register<CacheAddressDecoder, int>(nameof(IndexBits), 5);

    public static readonly StyledProperty<int> OffsetBitsProperty =
        AvaloniaProperty.Register<CacheAddressDecoder, int>(nameof(OffsetBits), 5);

    // ── Static brushes/pens (allocated once) ────────────────────────────────

    private static readonly IBrush STagBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x66, 0xB8));    // blue
    private static readonly IBrush SIndexBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x7F, 0x40));  // green
    private static readonly IBrush SOffsetBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0x00)); // amber
    private static readonly IBrush SHitBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x9A, 0x3E));    // green badge
    private static readonly IBrush SMissBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x3A, 0x2A));   // red badge
    private static readonly IBrush SWhiteBrush = Brushes.White;
    private static readonly IPen SDivPen = new Pen(Brushes.White, 1.5, lineCap: PenLineCap.Round);

    private static readonly Typeface SMono = new("Consolas,Cascadia Code,Courier New,monospace");
    private static readonly Typeface SSans = new("Inter,Segoe UI,Arial,sans-serif");

    static CacheAddressDecoder() {
        AffectsRender<CacheAddressDecoder>(
            CacheAddressDecoder.AddressProperty, CacheAddressDecoder.IsHitProperty,
            CacheAddressDecoder.TagBitsProperty, CacheAddressDecoder.IndexBitsProperty,
            CacheAddressDecoder.OffsetBitsProperty
        );
    }

    public ulong? Address {
        get => GetValue(CacheAddressDecoder.AddressProperty);
        set => SetValue(CacheAddressDecoder.AddressProperty, value);
    }

    public bool IsHit {
        get => GetValue(CacheAddressDecoder.IsHitProperty);
        set => SetValue(CacheAddressDecoder.IsHitProperty, value);
    }

    public int TagBits {
        get => GetValue(CacheAddressDecoder.TagBitsProperty);
        set => SetValue(CacheAddressDecoder.TagBitsProperty, value);
    }

    public int IndexBits {
        get => GetValue(CacheAddressDecoder.IndexBitsProperty);
        set => SetValue(CacheAddressDecoder.IndexBitsProperty, value);
    }

    public int OffsetBits {
        get => GetValue(CacheAddressDecoder.OffsetBitsProperty);
        set => SetValue(CacheAddressDecoder.OffsetBitsProperty, value);
    }

    // ── Theme awareness ──────────────────────────────────────────────────────

    private static IBrush FgBrush => Application.Current?.ActualThemeVariant == ThemeVariant.Light
        ? Brushes.Black
        : Brushes.White;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        if (Application.Current is { } app) app.ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnDetachedFromVisualTree(e);
        if (Application.Current is { } app) app.ActualThemeVariantChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();
    // barY = AddrH + 4, stemY = barY + BarH, labelY = stemY + StemH

    // ── Rendering ────────────────────────────────────────────────────────────

    public override void Render(DrawingContext dc) {
        ulong? addr = Address;
        if (addr == null) {
            DrawPlaceholder(dc);
            return;
        }

        int tagBits = Math.Max(1, TagBits);
        int indexBits = Math.Max(0, IndexBits);
        int offsetBits = Math.Max(1, OffsetBits);
        int totalBits = tagBits + indexBits + offsetBits;

        double w = Bounds.Width;
        const double barY = CacheAddressDecoder.AddrH + 4;
        const double stemY = barY + CacheAddressDecoder.BarH;
        const double labelY = stemY + CacheAddressDecoder.StemH;

        double tagW = w * tagBits / totalBits;
        double indexW = w * indexBits / totalBits;
        double offsetW = w - tagW - indexW;

        const double tagX = 0;
        double offsetX = tagW + indexW;

        // ── Decoded field values ─────────────────────────────────────────────
        ulong a32 = addr.Value & 0xFFFF_FFFF;
        ulong offsetVal = a32 & (((ulong)1 << offsetBits) - 1);
        ulong indexVal = indexBits > 0 ? (a32 >> offsetBits) & (((ulong)1 << indexBits) - 1) : 0;
        ulong tagVal = a32 >> (offsetBits + indexBits);

        // ── Address header ───────────────────────────────────────────────────
        var addrText = $"0x{(uint)a32:X8}";
        DrawText(dc, addrText, CacheAddressDecoder.SMono, 12, FgBrush, new Point(0, 2), FontWeight.SemiBold);

        // HIT / MISS badge
        bool isHit = IsHit;
        string badge = isHit ? "HIT" : "MISS";
        IBrush badgeBg = isHit ? CacheAddressDecoder.SHitBrush : CacheAddressDecoder.SMissBrush;
        FormattedText badgeFt = MakeFormattedText(
            badge, CacheAddressDecoder.SSans, 10, CacheAddressDecoder.SWhiteBrush, FontWeight.Bold
        );
        double badgeW = badgeFt.Width + 10;
        double badgeX = w - badgeW;
        dc.DrawRectangle(badgeBg, null, new RoundedRect(new Rect(badgeX, 2, badgeW, 17), 3));
        dc.DrawText(badgeFt, new Point(badgeX + 5, 3.5));

        // ── Colored bar ──────────────────────────────────────────────────────
        dc.DrawRectangle(CacheAddressDecoder.STagBrush, null, new Rect(tagX, barY, tagW, CacheAddressDecoder.BarH));
        dc.DrawRectangle(
            CacheAddressDecoder.SIndexBrush, null, new Rect(tagW, barY, indexW, CacheAddressDecoder.BarH)
        );
        dc.DrawRectangle(
            CacheAddressDecoder.SOffsetBrush, null, new Rect(offsetX, barY, offsetW, CacheAddressDecoder.BarH)
        );

        // Divider lines at boundaries
        if (indexBits > 0) {
            dc.DrawLine(
                CacheAddressDecoder.SDivPen, new Point(tagW, barY),
                new Point(tagW, barY + CacheAddressDecoder.BarH)
            );
            dc.DrawLine(
                CacheAddressDecoder.SDivPen, new Point(offsetX, barY),
                new Point(offsetX, barY + CacheAddressDecoder.BarH)
            );
        }

        // ── Bit-range labels (top half of bar, white mono) ──────────────────
        const double midBar = barY + CacheAddressDecoder.BarH * 0.28;
        DrawCenteredText(
            dc, BitRange(totalBits - 1, totalBits - tagBits), CacheAddressDecoder.SMono, 9.5,
            CacheAddressDecoder.SWhiteBrush, tagX, tagW, midBar
        );
        if (indexBits > 0)
            DrawCenteredText(
                dc, BitRange(offsetBits + indexBits - 1, offsetBits), CacheAddressDecoder.SMono, 9.5,
                CacheAddressDecoder.SWhiteBrush, tagW, indexW, midBar
            );
        DrawCenteredText(
            dc, BitRange(offsetBits - 1, 0), CacheAddressDecoder.SMono, 9.5, CacheAddressDecoder.SWhiteBrush, offsetX,
            offsetW, midBar
        );

        // ── Decoded value labels (bottom half of bar, white mono) ────────────
        const double valY = barY + CacheAddressDecoder.BarH * 0.56;
        DrawCenteredText(
            dc, $"0x{tagVal:X}", CacheAddressDecoder.SMono, 10, CacheAddressDecoder.SWhiteBrush, tagX, tagW, valY,
            FontWeight.SemiBold
        );
        if (indexBits > 0)
            DrawCenteredText(
                dc, $"{indexVal}", CacheAddressDecoder.SMono, 10, CacheAddressDecoder.SWhiteBrush, tagW, indexW,
                valY, FontWeight.SemiBold
            );
        DrawCenteredText(
            dc, $"+{offsetVal}", CacheAddressDecoder.SMono, 10, CacheAddressDecoder.SWhiteBrush, offsetX, offsetW,
            valY, FontWeight.SemiBold
        );

        // ── Vertical stems ───────────────────────────────────────────────────
        double tagCx = tagX + tagW / 2;
        double indexCx = tagW + indexW / 2;
        double offsetCx = offsetX + offsetW / 2;
        var stemPen = new Pen(CacheAddressDecoder.STagBrush, 1.5);
        var idxPen = new Pen(CacheAddressDecoder.SIndexBrush, 1.5);
        var offPen = new Pen(CacheAddressDecoder.SOffsetBrush, 1.5);

        dc.DrawLine(stemPen, new Point(tagCx, stemY), new Point(tagCx, stemY + CacheAddressDecoder.StemH - 6));
        if (indexBits > 0)
            dc.DrawLine(idxPen, new Point(indexCx, stemY), new Point(indexCx, stemY + CacheAddressDecoder.StemH - 6));
        dc.DrawLine(offPen, new Point(offsetCx, stemY), new Point(offsetCx, stemY + CacheAddressDecoder.StemH - 6));

        // ── Arrowhead triangles ──────────────────────────────────────────────
        Arrow(dc, tagCx, stemY + CacheAddressDecoder.StemH - 5, CacheAddressDecoder.STagBrush);
        if (indexBits > 0) Arrow(dc, indexCx, stemY + CacheAddressDecoder.StemH - 5, CacheAddressDecoder.SIndexBrush);
        Arrow(dc, offsetCx, stemY + CacheAddressDecoder.StemH - 5, CacheAddressDecoder.SOffsetBrush);

        // ── Field name labels ─────────────────────────────────────────────────
        DrawCenteredText(dc, "TAG", CacheAddressDecoder.SSans, 10, FgBrush, tagX, tagW, labelY, FontWeight.SemiBold);
        if (indexBits > 0)
            DrawCenteredText(
                dc, "INDEX", CacheAddressDecoder.SSans, 10, FgBrush, tagW, indexW, labelY, FontWeight.SemiBold
            );
        DrawCenteredText(
            dc, "OFFSET", CacheAddressDecoder.SSans, 10, FgBrush, offsetX, offsetW, labelY, FontWeight.SemiBold
        );
    }

    private void DrawPlaceholder(DrawingContext dc) {
        FormattedText ft = MakeFormattedText("No cache access yet", CacheAddressDecoder.SSans, 11, FgBrush);
        dc.DrawText(ft, new Point((Bounds.Width - ft.Width) / 2, (Bounds.Height - ft.Height) / 2));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string BitRange(int hi, int lo) => hi == lo ? $"[{hi}]" : $"[{hi}:{lo}]";

    private static void Arrow(DrawingContext dc, double cx, double tipY, IBrush brush) {
        const double aw = 5.5, ah = 5.0;
        var sg = new StreamGeometry();
        using (StreamGeometryContext c = sg.Open()) {
            c.BeginFigure(new Point(cx, tipY));
            c.LineTo(new Point(cx - aw, tipY - ah));
            c.LineTo(new Point(cx + aw, tipY - ah));
            c.EndFigure(true);
        }

        dc.DrawGeometry(brush, null, sg);
    }

    private static void DrawCenteredText(
        DrawingContext dc,
        string text,
        Typeface face,
        double size,
        IBrush fg,
        double sectionX,
        double sectionW,
        double y,
        FontWeight weight = FontWeight.Normal
    ) {
        FormattedText ft = MakeFormattedText(text, face, size, fg, weight);
        double x = sectionX + (sectionW - ft.Width) / 2;
        dc.DrawText(ft, new Point(x, y));
    }

    private static void DrawText(
        DrawingContext dc,
        string text,
        Typeface face,
        double size,
        IBrush fg,
        Point origin,
        FontWeight weight = FontWeight.Normal
    ) {
        FormattedText ft = MakeFormattedText(text, face, size, fg, weight);
        dc.DrawText(ft, origin);
    }

    private static FormattedText MakeFormattedText(
        string text,
        Typeface face,
        double size,
        IBrush fg,
        FontWeight weight = FontWeight.Normal
    ) => new(
        text,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        weight == FontWeight.Normal ? face : new Typeface(face.FontFamily, face.Style, weight),
        size,
        fg
    );
}