using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Face.Models;
using Orrery.Observation;

namespace Face.Controls;

public sealed class WaterfallControl : Control {
    private const double CellW   = 16;  // pixels per cycle
    private const double RowH    = 22;
    private const double HeaderH = 26;
    private const double BarPad  = 2;   // inset on each edge of a bar
    private const int    MaxRows = 500;
    private const int    MaxCols = 800;
    private const double ColPad  = 12;

    private static readonly Typeface Mono = new("JetBrainsMono Nerd Font Mono, DejaVu Sans Mono, FreeMono");

    // ── Dark theme ─────────────────────────────────────────────────────────────
    private static readonly IBrush DarkHeaderBg   = new SolidColorBrush(Color.Parse("#2A2A3E"));
    private static readonly IBrush DarkRowBg0     = new SolidColorBrush(Color.Parse("#1C1C28"));
    private static readonly IBrush DarkRowBg1     = new SolidColorBrush(Color.Parse("#22222F"));
    private static readonly IBrush DarkLabelFg    = new SolidColorBrush(Color.Parse("#B0B0C8"));
    private static readonly IBrush DarkHeaderFg   = new SolidColorBrush(Color.Parse("#E0E0F0"));
    private static readonly IBrush DarkSpecPcFg   = new SolidColorBrush(Color.Parse("#8AAEC8"));
    private static readonly IBrush DarkStallHdrFg = new SolidColorBrush(Color.Parse("#606878"));
    private static readonly IBrush DarkDisasmFg   = new SolidColorBrush(Color.Parse("#C8C8E8"));
    private static readonly IBrush DarkSelRow     = new SolidColorBrush(Color.FromArgb(60, 100, 140, 255));
    private static readonly IPen   DarkGridPen    = new Pen(new SolidColorBrush(Color.Parse("#3A3A52")), 0.5);

    // ── Light theme ────────────────────────────────────────────────────────────
    private static readonly IBrush LightHeaderBg   = new SolidColorBrush(Color.Parse("#D8D8E8"));
    private static readonly IBrush LightRowBg0     = new SolidColorBrush(Color.Parse("#F5F5FC"));
    private static readonly IBrush LightRowBg1     = new SolidColorBrush(Color.Parse("#EDEDF8"));
    private static readonly IBrush LightLabelFg    = new SolidColorBrush(Color.Parse("#404060"));
    private static readonly IBrush LightHeaderFg   = new SolidColorBrush(Color.Parse("#1A1A30"));
    private static readonly IBrush LightSpecPcFg   = new SolidColorBrush(Color.Parse("#3A6080"));
    private static readonly IBrush LightStallHdrFg = new SolidColorBrush(Color.Parse("#888898"));
    private static readonly IBrush LightDisasmFg   = new SolidColorBrush(Color.Parse("#303050"));
    private static readonly IBrush LightSelRow     = new SolidColorBrush(Color.FromArgb(50, 60, 100, 220));
    private static readonly IPen   LightGridPen    = new Pen(new SolidColorBrush(Color.Parse("#C0C0D4")), 0.5);

    private static readonly IBrush FlushHdrBg  = new SolidColorBrush(Color.Parse("#6B2020"));
    private static readonly IBrush FlushColTint = new SolidColorBrush(Color.FromArgb(45, 200, 60, 60));

    private static readonly Dictionary<PEventKind, (IBrush Bg, string Label)> KindStyle = new() {
        [PEventKind.Fetch]    = (new SolidColorBrush(Color.Parse("#4A7EC7")), "F"),
        [PEventKind.Decode]   = (new SolidColorBrush(Color.Parse("#1A9490")), "Dc"),
        [PEventKind.Dispatch] = (new SolidColorBrush(Color.Parse("#2EA5A0")), "Ds"),
        [PEventKind.Issue]    = (new SolidColorBrush(Color.Parse("#8A6BD4")), "Is"),
        [PEventKind.Execute]  = (new SolidColorBrush(Color.Parse("#C87A2A")), "Ex"),
        [PEventKind.Retire]   = (new SolidColorBrush(Color.Parse("#4AB04A")), "Cm"),
        [PEventKind.Flush]    = (new SolidColorBrush(Color.Parse("#C45050")), "Fl"),
    };

    private static readonly Dictionary<(string, double, IBrush), FormattedText> FtCache = new();

    // ── Styled properties ───────────────────────────────────────────────────────
    public static readonly StyledProperty<WaterfallData?> DataProperty =
        AvaloniaProperty.Register<WaterfallControl, WaterfallData?>(nameof(Data));

    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<WaterfallControl, bool>(nameof(IsDark), true);

    public static readonly StyledProperty<WaterfallRow?> SelectedRowProperty =
        AvaloniaProperty.Register<WaterfallControl, WaterfallRow?>(nameof(SelectedRow));

    public static readonly RoutedEvent<WaterfallRowSelectedEventArgs> RowSelectedEvent =
        RoutedEvent.Register<WaterfallControl, WaterfallRowSelectedEventArgs>(
            nameof(RowSelected), RoutingStrategies.Bubble);

    public WaterfallData? Data {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public bool IsDark {
        get => GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    public WaterfallRow? SelectedRow {
        get => GetValue(SelectedRowProperty);
        set => SetValue(SelectedRowProperty, value);
    }

    public event EventHandler<WaterfallRowSelectedEventArgs> RowSelected {
        add    => AddHandler(RowSelectedEvent, value);
        remove => RemoveHandler(RowSelectedEvent, value);
    }

    // Gutter column widths — computed in MeasureOverride.
    private double _instrIdColW = 48;
    private double _pcColW      = 80;
    private double _disasmColW  = 160;
    private double GutterW => _instrIdColW + _pcColW + _disasmColW;

    static WaterfallControl() {
        DataProperty.Changed.AddClassHandler<WaterfallControl>((c, _) => {
            c.InvalidateMeasure();
            c.InvalidateVisual();
        });
        IsDarkProperty.Changed.AddClassHandler<WaterfallControl>((c, _) => c.InvalidateVisual());
        SelectedRowProperty.Changed.AddClassHandler<WaterfallControl>((c, _) => c.InvalidateVisual());
    }

    public WaterfallControl() {
        Focusable = false;
    }

    protected override Size MeasureOverride(Size availableSize) {
        WaterfallData? data = Data;
        if (data is null || data.Rows.Count == 0)
            return new Size(200, HeaderH + RowH);
        ComputeColumnWidths(data);
        int  rows = Math.Min(data.Rows.Count, MaxRows);
        long cols = Math.Min(data.MaxCycle - data.MinCycle + 2, MaxCols);
        return new Size(GutterW + cols * CellW, HeaderH + rows * RowH);
    }

    private void ComputeColumnWidths(WaterfallData data) {
        string widestId  = "ID";
        double widestPcW = MeasureFtWidth("PC", 10.5);
        double widestDsW = MeasureFtWidth("Instruction", 10.5);
        ulong  basePc    = data.BasePc;
        int    limit     = Math.Min(data.Rows.Count, MaxRows);

        for (var i = 0; i < limit; i++) {
            WaterfallRow row = data.Rows[i];
            var id = row.InstrId.ToString();
            if (id.Length > widestId.Length) widestId = id;

            double pcW = MeasureFtWidth($"{row.Pc - basePc:X}", 10);
            if (row.SpecPc != row.Pc) pcW += MeasureFtWidth($"/{row.SpecPc - basePc:X}", 10);
            if (pcW > widestPcW) widestPcW = pcW;

            double dsW = MeasureFtWidth(row.Disassembly, 10);
            if (dsW > widestDsW) widestDsW = dsW;
        }

        _instrIdColW = MeasureFtWidth(widestId, widestId == "ID" ? 10.5 : 10) + ColPad;
        _pcColW      = widestPcW + ColPad;
        _disasmColW  = Math.Min(widestDsW + ColPad, 280); // cap at 280 px
    }

    private static double MeasureFtWidth(string text, double size) {
        (string, double, IBrush) key = (text, size, DarkLabelFg);
        if (!FtCache.TryGetValue(key, out FormattedText? ft)) {
            ft = new FormattedText(text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Mono, size, DarkLabelFg);
            FtCache[key] = ft;
        }
        return ft.Width;
    }

    // ── Input ─────────────────────────────────────────────────────────────────
    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        WaterfallData? data = Data;
        if (data is null) return;

        Point pos = e.GetPosition(this);
        if (pos.Y < HeaderH) { SelectedRow = null; return; }

        int r = (int)((pos.Y - HeaderH) / RowH);
        if (r < 0 || r >= Math.Min(data.Rows.Count, MaxRows)) { SelectedRow = null; return; }

        WaterfallRow row = data.Rows[r];
        if (SelectedRow == row) {
            // Second click on same row — deselect.
            SelectedRow = null;
        }
        else {
            SelectedRow = row;
            PixelPoint screenPx = this.PointToScreen(new Point(0, HeaderH + r * RowH + RowH));
            var screenPt = new Point(screenPx.X, screenPx.Y);
            RaiseEvent(new WaterfallRowSelectedEventArgs(RowSelectedEvent, this, row, screenPt));
        }

        e.Handled = true;
    }

    // ── Rendering ──────────────────────────────────────────────────────────────
    public override void Render(DrawingContext ctx) {
        WaterfallData? data = Data;
        if (data is null || data.Rows.Count == 0) return;
        RenderCore(ctx, data);
    }

    private void RenderCore(DrawingContext ctx, WaterfallData data) {
        bool   dark      = IsDark;
        IBrush headerBg  = dark ? DarkHeaderBg   : LightHeaderBg;
        IBrush rowBg0    = dark ? DarkRowBg0     : LightRowBg0;
        IBrush rowBg1    = dark ? DarkRowBg1     : LightRowBg1;
        IBrush labelFg   = dark ? DarkLabelFg    : LightLabelFg;
        IBrush headerFg  = dark ? DarkHeaderFg   : LightHeaderFg;
        IBrush specPcFg  = dark ? DarkSpecPcFg   : LightSpecPcFg;
        IBrush stallHdrFg= dark ? DarkStallHdrFg : LightStallHdrFg;
        IBrush disasmFg  = dark ? DarkDisasmFg   : LightDisasmFg;
        IBrush selRowBg  = dark ? DarkSelRow      : LightSelRow;
        IPen   gridPen   = dark ? DarkGridPen     : LightGridPen;

        double gutterW  = GutterW;
        int    numRows  = Math.Min(data.Rows.Count, MaxRows);
        long   numCols  = Math.Min(data.MaxCycle - data.MinCycle + 2, MaxCols);
        double totalW   = gutterW + numCols * CellW;
        double totalH   = HeaderH + numRows * RowH;
        long   minCy    = data.MinCycle;

        ScrollViewer? sv = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        double sx = sv?.Offset.X ?? 0;
        double sy = sv?.Offset.Y ?? 0;
        double vw = sv?.Viewport.Width  ?? totalW;
        double vh = sv?.Viewport.Height ?? totalH;

        int  rFirst = Math.Max(0, (int)((sy - HeaderH) / RowH));
        int  rLast  = Math.Min(numRows - 1, (int)((sy + vh - HeaderH) / RowH) + 1);
        long cFirst = Math.Max(0L, (long)((sx - gutterW) / CellW) - 1);
        long cLast  = Math.Min(numCols - 1, (long)((sx + vw - gutterW) / CellW) + 1);

        // ── Row backgrounds ────────────────────────────────────────────────────
        for (int r = rFirst; r <= rLast; r++) {
            double rowY = HeaderH + r * RowH;
            IBrush bg   = r % 2 == 0 ? rowBg0 : rowBg1;
            ctx.DrawRectangle(bg, null, new Rect(0, rowY, totalW, RowH));
            if (SelectedRow is { } sel && data.Rows[r] == sel)
                ctx.DrawRectangle(selRowBg, null, new Rect(0, rowY, totalW, RowH));
        }

        // ── Flush column tint ──────────────────────────────────────────────────
        double colTintTop = HeaderH + rFirst * RowH;
        double colTintH   = (rLast - rFirst + 1) * RowH;
        for (long c = cFirst; c <= cLast; c++)
            if (data.FlushCycles.Contains(minCy + c)) {
                double cx = gutterW + c * CellW;
                ctx.DrawRectangle(FlushColTint, null, new Rect(cx, colTintTop, CellW, colTintH));
            }

        // ── Stage bars ────────────────────────────────────────────────────────
        for (int r = rFirst; r <= rLast; r++) {
            WaterfallRow row  = data.Rows[r];
            double       rowY = HeaderH + r * RowH;
            foreach (PSpan span in row.Spans) {
                if (!KindStyle.TryGetValue(span.Stage, out var style)) continue;

                long spanCStart = span.Start - minCy;
                long spanCEnd   = span.End   - minCy; // exclusive
                if (spanCEnd <= cFirst || spanCStart > cLast) continue;

                double barX  = gutterW + spanCStart * CellW;
                double barW  = (spanCEnd - spanCStart) * CellW;
                double barXc = Math.Max(barX, gutterW + cFirst * CellW); // clip to viewport
                double barWc = barX + barW - barXc;
                if (barWc <= 0) continue;

                ctx.DrawRectangle(
                    style.Bg, null,
                    new Rect(barXc + BarPad, rowY + BarPad, barWc - BarPad * 2, RowH - BarPad * 2),
                    3, 3);

                // Label — only if bar is wide enough and starts within the visible region.
                double labelX = Math.Max(barX + BarPad + 3, barXc + BarPad + 3);
                double avail  = barX + barW - BarPad - labelX;
                if (avail > 8) {
                    DrawFt(ctx, style.Label, Brushes.White, 9.5, new Point(labelX, rowY + BarPad + 3));
                    // Duration number to the right of the label if there's room.
                    if (span.Duration > 1 && avail > 32) {
                        double labelW = MeasureFtWidth(style.Label, 9.5);
                        DrawFt(ctx, span.Duration.ToString(), Brushes.White, 9, new Point(labelX + labelW + 3, rowY + BarPad + 4));
                    }
                }
            }
        }

        // ── Grid lines ────────────────────────────────────────────────────────
        for (int r = rFirst + 1; r <= rLast + 1; r++) {
            double y = HeaderH + r * RowH;
            ctx.DrawLine(gridPen, new Point(gutterW, y), new Point(totalW, y));
        }
        if (cLast - cFirst <= 200)
            for (long c = cFirst; c <= cLast + 1; c++) {
                double x = gutterW + c * CellW;
                ctx.DrawLine(gridPen, new Point(x, 0), new Point(x, totalH));
            }

        // ── Sticky header ─────────────────────────────────────────────────────
        ctx.DrawRectangle(headerBg, null, new Rect(sx, sy, vw, HeaderH));
        for (long c = cFirst; c <= cLast; c++) {
            long   cycle = minCy + c;
            double cx    = gutterW + c * CellW;
            if (cx + CellW <= sx + gutterW) continue;
            bool isFlush = data.FlushCycles.Contains(cycle);
            bool isStall = data.FetchStallCycles.Contains(cycle);
            if (isFlush)
                ctx.DrawRectangle(FlushHdrBg, null, new Rect(cx, sy, CellW, HeaderH));
            IBrush cycleFg = isFlush ? Brushes.White : isStall ? stallHdrFg : headerFg;
            DrawFt(ctx, $"C{cycle}", cycleFg, 8.5, new Point(cx + 2, sy + 7));
        }
        ctx.DrawLine(gridPen, new Point(sx, sy + HeaderH), new Point(sx + vw, sy + HeaderH));

        // ── Sticky gutter ─────────────────────────────────────────────────────
        ulong basePc = data.BasePc;
        double disasmX = _instrIdColW + _pcColW;
        for (int r = rFirst; r <= rLast; r++) {
            WaterfallRow row  = data.Rows[r];
            double       rowY = HeaderH + r * RowH;
            ctx.DrawRectangle(r % 2 == 0 ? rowBg0 : rowBg1, null, new Rect(sx, rowY, gutterW, RowH));
            if (SelectedRow is { } sel && data.Rows[r] == sel)
                ctx.DrawRectangle(selRowBg, null, new Rect(sx, rowY, gutterW, RowH));

            DrawFt(ctx, row.InstrId.ToString(), labelFg, 10, new Point(sx + 4, rowY + 4));

            var    pcStr = $"{row.Pc - basePc:X}";
            double pcX   = sx + _instrIdColW + 4;
            DrawFt(ctx, pcStr, labelFg, 10, new Point(pcX, rowY + 4));
            if (row.SpecPc != row.Pc) {
                double slashX = pcX + MeasureFtWidth(pcStr, 10);
                DrawFt(ctx, $"/{row.SpecPc - basePc:X}", specPcFg, 10, new Point(slashX, rowY + 4));
            }

            // Disassembly — clipped to column width.
            using (ctx.PushClip(new Rect(sx + disasmX, rowY, _disasmColW, RowH)))
                DrawFt(ctx, row.Disassembly, disasmFg, 10, new Point(sx + disasmX + 4, rowY + 4));
        }
        ctx.DrawLine(gridPen, new Point(sx + gutterW, sy), new Point(sx + gutterW, sy + vh));

        // ── Sticky corner ─────────────────────────────────────────────────────
        ctx.DrawRectangle(headerBg, null, new Rect(sx, sy, gutterW, HeaderH));
        DrawFt(ctx, "ID",   headerFg, 10.5, new Point(sx + 4, sy + 6));
        DrawFt(ctx, "PC",   headerFg, 10.5, new Point(sx + _instrIdColW + 6, sy + 6));
        DrawFt(ctx, "Instruction", headerFg, 10.5, new Point(sx + disasmX + 4, sy + 6));
        ctx.DrawLine(gridPen, new Point(sx + _instrIdColW, sy), new Point(sx + _instrIdColW, sy + HeaderH));
        ctx.DrawLine(gridPen, new Point(sx + disasmX, sy),      new Point(sx + disasmX, sy + HeaderH));
        ctx.DrawLine(gridPen, new Point(sx + gutterW, sy),      new Point(sx + gutterW, sy + HeaderH));
        ctx.DrawLine(gridPen, new Point(sx, sy + HeaderH),      new Point(sx + gutterW, sy + HeaderH));
    }

    // ── Headless PNG export ────────────────────────────────────────────────────
    public static void RenderToFile(WaterfallData data, bool isDark, string path) {
        var ctrl = new WaterfallControl { Data = data, IsDark = isDark };
        ctrl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ctrl.Arrange(new Rect(ctrl.DesiredSize));
        Size sz = ctrl.DesiredSize;
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(sz.Width), (int)Math.Ceiling(sz.Height)),
            new Vector(96, 96));
        bitmap.Render(ctrl);
        bitmap.Save(path);
    }

    private static void DrawFt(DrawingContext ctx, string text, IBrush fg, double size, Point origin) {
        (string text, double size, IBrush fg) key = (text, size, fg);
        if (!FtCache.TryGetValue(key, out FormattedText? ft)) {
            ft = new FormattedText(text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Mono, size, fg);
            FtCache[key] = ft;
        }
        ctx.DrawText(ft, origin);
    }
}

public sealed class WaterfallRowSelectedEventArgs(
    RoutedEvent routedEvent,
    object source,
    WaterfallRow row,
    Point screenPoint
) : RoutedEventArgs(routedEvent, source) {
    public WaterfallRow Row { get; } = row;
    /// <summary>Screen point near the bottom-left of the clicked row — popup anchor.</summary>
    public Point ScreenPoint { get; } = screenPoint;
}
