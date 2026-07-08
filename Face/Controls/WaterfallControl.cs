using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Face.Models;
using Orrery.Observation;

namespace Face.Controls;

public sealed class WaterfallControl : Control {
    private const double DefaultCellW = 24;
    private const double MinCellW = 8;
    private const double MaxCellW = 96;
    private const double DefaultRowH = 22;
    private const double TextThreshold = 14; // below this cellW, hide all text
    private double _cellW = WaterfallControl.DefaultCellW;
    private double _rowH = WaterfallControl.DefaultRowH;
    private bool ShowText => _cellW >= WaterfallControl.TextThreshold;
    private const double HeaderH = 26;
    private const double BarPad = 2; // inset on each edge of a bar
    private const int MaxRows = 500;
    private const int MaxCols = 800;
    private const double ColPad = 12;

    private static readonly Typeface Mono = new("JetBrainsMono Nerd Font Mono, DejaVu Sans Mono, FreeMono");

    // ── Dark theme ─────────────────────────────────────────────────────────────
    private static readonly IBrush DarkHeaderBg = new SolidColorBrush(Color.Parse("#2A2A3E"));
    private static readonly IBrush DarkRowBg0 = new SolidColorBrush(Color.Parse("#1C1C28"));
    private static readonly IBrush DarkRowBg1 = new SolidColorBrush(Color.Parse("#22222F"));
    private static readonly IBrush DarkLabelFg = new SolidColorBrush(Color.Parse("#B0B0C8"));
    private static readonly IBrush DarkHeaderFg = new SolidColorBrush(Color.Parse("#E0E0F0"));
    private static readonly IBrush DarkSpecPcFg = new SolidColorBrush(Color.Parse("#8AAEC8"));
    private static readonly IBrush DarkStallHdrFg = new SolidColorBrush(Color.Parse("#606878"));
    private static readonly IBrush DarkDisasmFg = new SolidColorBrush(Color.Parse("#C8C8E8"));
    private static readonly IBrush DarkSelRow = new SolidColorBrush(Color.FromArgb(60, 100, 140, 255));
    private static readonly IPen DarkGridPen = new Pen(new SolidColorBrush(Color.Parse("#3A3A52")), 0.5);

    // ── Light theme ────────────────────────────────────────────────────────────
    private static readonly IBrush LightHeaderBg = new SolidColorBrush(Color.Parse("#D8D8E8"));
    private static readonly IBrush LightRowBg0 = new SolidColorBrush(Color.Parse("#F5F5FC"));
    private static readonly IBrush LightRowBg1 = new SolidColorBrush(Color.Parse("#EDEDF8"));
    private static readonly IBrush LightLabelFg = new SolidColorBrush(Color.Parse("#404060"));
    private static readonly IBrush LightHeaderFg = new SolidColorBrush(Color.Parse("#1A1A30"));
    private static readonly IBrush LightSpecPcFg = new SolidColorBrush(Color.Parse("#3A6080"));
    private static readonly IBrush LightStallHdrFg = new SolidColorBrush(Color.Parse("#888898"));
    private static readonly IBrush LightDisasmFg = new SolidColorBrush(Color.Parse("#303050"));
    private static readonly IBrush LightSelRow = new SolidColorBrush(Color.FromArgb(50, 60, 100, 220));
    private static readonly IPen LightGridPen = new Pen(new SolidColorBrush(Color.Parse("#C0C0D4")), 0.5);

    private static readonly IBrush FlushHdrBg = new SolidColorBrush(Color.Parse("#6B2020"));
    private static readonly IBrush FlushColTint = new SolidColorBrush(Color.FromArgb(45, 200, 60, 60));

    private static readonly Dictionary<PEventKind, (IBrush Bg, string Label)> KindStyle = new() {
        [PEventKind.Fetch]    = (new SolidColorBrush(Color.Parse("#4A7EC7")), "F"),
        [PEventKind.Decode]   = (new SolidColorBrush(Color.Parse("#1A9490")), "Dc"),
        [PEventKind.Rename]   = (new SolidColorBrush(Color.Parse("#1E8A86")), "Rn"),
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

    public WaterfallData? Data {
        get => GetValue(WaterfallControl.DataProperty);
        set => SetValue(WaterfallControl.DataProperty, value);
    }

    public bool IsDark {
        get => GetValue(WaterfallControl.IsDarkProperty);
        set => SetValue(WaterfallControl.IsDarkProperty, value);
    }

    public WaterfallRow? SelectedRow {
        get => GetValue(WaterfallControl.SelectedRowProperty);
        set => SetValue(WaterfallControl.SelectedRowProperty, value);
    }

    // Gutter column widths — computed in MeasureOverride. Collapsed when text is hidden.
    private double _instrIdColW = 48;
    private double _pcColW = 80;
    private double _disasmColW = 160;
    private double GutterW => ShowText ? _instrIdColW + _pcColW + _disasmColW : 0;

    static WaterfallControl() {
        WaterfallControl.DataProperty.Changed.AddClassHandler<WaterfallControl>((c, _) => {
                c.InvalidateMeasure();
                c.InvalidateVisual();
            }
        );
        WaterfallControl.IsDarkProperty.Changed.AddClassHandler<WaterfallControl>((c, _) => c.InvalidateVisual());
        WaterfallControl.SelectedRowProperty.Changed.AddClassHandler<WaterfallControl>((c, _) => c.InvalidateVisual());
    }

    public WaterfallControl() => Focusable = false;

    protected override Size MeasureOverride(Size availableSize) {
        WaterfallData? data = Data;
        if (data is null || data.Rows.Count == 0) return new Size(200, WaterfallControl.HeaderH + _rowH);
        ComputeColumnWidths(data);
        int rows = Math.Min(data.Rows.Count, WaterfallControl.MaxRows);
        long cols = Math.Min(data.MaxCycle - data.MinCycle + 2, WaterfallControl.MaxCols);
        return new Size(GutterW + cols * _cellW, WaterfallControl.HeaderH + rows * _rowH);
    }

    private void ComputeColumnWidths(WaterfallData data) {
        var widestId = "ID";
        double widestPcW = MeasureFtWidth("PC", 10.5);
        double widestDsW = MeasureFtWidth("Instruction", 10.5);
        ulong basePc = data.BasePc;
        int limit = Math.Min(data.Rows.Count, WaterfallControl.MaxRows);

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

        _instrIdColW = MeasureFtWidth(widestId, widestId == "ID" ? 10.5 : 10) + WaterfallControl.ColPad;
        _pcColW = widestPcW + WaterfallControl.ColPad;
        _disasmColW = Math.Min(widestDsW + WaterfallControl.ColPad, 280); // cap at 280 px
    }

    private static double MeasureFtWidth(string text, double size) {
        (string, double, IBrush) key = (text, size, WaterfallControl.DarkLabelFg);
        if (!WaterfallControl.FtCache.TryGetValue(key, out FormattedText? ft)) {
            ft = new FormattedText(
                text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, WaterfallControl.Mono, size, WaterfallControl.DarkLabelFg
            );
            WaterfallControl.FtCache[key] = ft;
        }

        return ft.Width;
    }

    public void ZoomIn() => SetCellW(_cellW * 1.25);
    public void ZoomOut() => SetCellW(_cellW / 1.25);
    public void ZoomReset() => SetCellW(WaterfallControl.DefaultCellW);

    private void SetCellW(double newW) {
        _cellW = Math.Clamp(newW, WaterfallControl.MinCellW, WaterfallControl.MaxCellW);
        _rowH = WaterfallControl.DefaultRowH * _cellW / WaterfallControl.DefaultCellW;
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ── Input ─────────────────────────────────────────────────────────────────
    public WaterfallRow? GetRowAt(Point localPt) {
        WaterfallData? data = Data;
        if (data is null || localPt.Y < WaterfallControl.HeaderH) return null;
        var r = (int)((localPt.Y - WaterfallControl.HeaderH) / _rowH);
        if (r < 0 || r >= Math.Min(data.Rows.Count, WaterfallControl.MaxRows)) return null;
        return data.Rows[r];
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        WaterfallData? data = Data;
        if (data is null) return;
        Point pos = e.GetPosition(this);
        WaterfallRow? row = GetRowAt(pos);
        SelectedRow = SelectedRow == row ? null : row;
        e.Handled = true;
    }

    // ── Rendering ──────────────────────────────────────────────────────────────
    public override void Render(DrawingContext ctx) {
        WaterfallData? data = Data;
        if (data is null || data.Rows.Count == 0) return;
        RenderCore(ctx, data);
    }

    private void RenderCore(DrawingContext ctx, WaterfallData data) {
        bool dark = IsDark;
        IBrush headerBg = dark ? WaterfallControl.DarkHeaderBg : WaterfallControl.LightHeaderBg;
        IBrush rowBg0 = dark ? WaterfallControl.DarkRowBg0 : WaterfallControl.LightRowBg0;
        IBrush rowBg1 = dark ? WaterfallControl.DarkRowBg1 : WaterfallControl.LightRowBg1;
        IBrush labelFg = dark ? WaterfallControl.DarkLabelFg : WaterfallControl.LightLabelFg;
        IBrush headerFg = dark ? WaterfallControl.DarkHeaderFg : WaterfallControl.LightHeaderFg;
        IBrush specPcFg = dark ? WaterfallControl.DarkSpecPcFg : WaterfallControl.LightSpecPcFg;
        IBrush stallHdrFg = dark ? WaterfallControl.DarkStallHdrFg : WaterfallControl.LightStallHdrFg;
        IBrush disasmFg = dark ? WaterfallControl.DarkDisasmFg : WaterfallControl.LightDisasmFg;
        IBrush selRowBg = dark ? WaterfallControl.DarkSelRow : WaterfallControl.LightSelRow;
        IPen gridPen = dark ? WaterfallControl.DarkGridPen : WaterfallControl.LightGridPen;

        double gutterW = GutterW;
        int numRows = Math.Min(data.Rows.Count, WaterfallControl.MaxRows);
        long numCols = Math.Min(data.MaxCycle - data.MinCycle + 2, WaterfallControl.MaxCols);
        double totalW = gutterW + numCols * _cellW;
        double totalH = WaterfallControl.HeaderH + numRows * _rowH;
        long minCy = data.MinCycle;

        ScrollViewer? sv = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        double sx = sv?.Offset.X ?? 0;
        double sy = sv?.Offset.Y ?? 0;
        double vw = sv?.Viewport.Width ?? totalW;
        double vh = sv?.Viewport.Height ?? totalH;

        int rFirst = Math.Max(0, (int)((sy - WaterfallControl.HeaderH) / _rowH));
        int rLast = Math.Min(numRows - 1, (int)((sy + vh - WaterfallControl.HeaderH) / _rowH) + 1);
        long cFirst = Math.Max(0L, (long)((sx - gutterW) / _cellW) - 1);
        long cLast = Math.Min(numCols - 1, (long)((sx + vw - gutterW) / _cellW) + 1);

        // ── Row backgrounds ────────────────────────────────────────────────────
        for (int r = rFirst; r <= rLast; r++) {
            double rowY = WaterfallControl.HeaderH + r * _rowH;
            IBrush bg = r % 2 == 0 ? rowBg0 : rowBg1;
            ctx.DrawRectangle(bg, null, new Rect(0, rowY, totalW, _rowH));
            if (SelectedRow is { } sel && data.Rows[r] == sel)
                ctx.DrawRectangle(selRowBg, null, new Rect(0, rowY, totalW, _rowH));
        }

        // ── Flush column tint ──────────────────────────────────────────────────
        double colTintTop = WaterfallControl.HeaderH + rFirst * _rowH;
        double colTintH = (rLast - rFirst + 1) * _rowH;
        for (long c = cFirst; c <= cLast; c++)
            if (data.FlushCycles.Contains(minCy + c)) {
                double cx = gutterW + c * _cellW;
                ctx.DrawRectangle(WaterfallControl.FlushColTint, null, new Rect(cx, colTintTop, _cellW, colTintH));
            }

        // ── Stage bars ────────────────────────────────────────────────────────
        bool showText = ShowText;
        double barPad = showText ? WaterfallControl.BarPad : Math.Min(WaterfallControl.BarPad, _rowH / 5);
        for (int r = rFirst; r <= rLast; r++) {
            WaterfallRow row = data.Rows[r];
            double rowY = WaterfallControl.HeaderH + r * _rowH;
            bool flushed = row.Spans.Count > 0 && row.Spans[^1].Stage == PEventKind.Flush;

            if (flushed) {
                using DrawingContext.PushedState dim = ctx.PushOpacity(0.38);
                DrawSpans();
            }
            else { DrawSpans(); }

            void DrawSpans() {
                foreach (PSpan span in row.Spans) {
                    if (!WaterfallControl.KindStyle.TryGetValue(span.Stage, out (IBrush Bg, string Label) style))
                        continue;

                    long spanCStart = span.Start - minCy;
                    long spanCEnd = span.End - minCy; // exclusive
                    if (spanCEnd <= cFirst || spanCStart > cLast) continue;

                    double barX = gutterW + spanCStart * _cellW;
                    double barW = (spanCEnd - spanCStart) * _cellW;
                    double barXc = Math.Max(barX, gutterW + cFirst * _cellW); // clip to viewport
                    double barWc = barX + barW - barXc;
                    if (barWc <= 0) continue;

                    double barH = _rowH - barPad * 2;
                    double radius = Math.Min(3.0, barH / 3.0);
                    ctx.DrawRectangle(
                        style.Bg, null,
                        new Rect(barXc + barPad, rowY + barPad, barWc - barPad * 2, barH),
                        radius, radius
                    );

                    // Label — only if text is enabled and the bar's starting cell is visible.
                    // avail is capped to one cell so the number never bleeds into the next cell.
                    if (showText && barXc <= barX + WaterfallControl.BarPad) {
                        double labelX = barX + WaterfallControl.BarPad + 3;
                        double cellAvail = _cellW - 2 * WaterfallControl.BarPad - 3;
                        if (cellAvail > 6) {
                            string abbr = style.Label;
                            string full = span.Duration > 1 ? $"{abbr} {span.Duration}" : abbr;
                            string lbl = cellAvail >= MeasureFtWidth(full, 9.5) ? full :
                                cellAvail >= MeasureFtWidth(abbr, 9.5)          ? abbr : "";
                            if (lbl.Length > 0)
                                DrawFt(
                                    ctx, lbl, Brushes.White, 9.5, new Point(labelX, rowY + WaterfallControl.BarPad + 3)
                                );
                        }
                    }
                }
            }
        }

        // ── Grid lines ────────────────────────────────────────────────────────
        for (int r = rFirst + 1; r <= rLast + 1; r++) {
            double y = WaterfallControl.HeaderH + r * _rowH;
            ctx.DrawLine(gridPen, new Point(gutterW, y), new Point(totalW, y));
        }

        if (cLast - cFirst <= 200)
            for (long c = cFirst; c <= cLast + 1; c++) {
                double x = gutterW + c * _cellW;
                ctx.DrawLine(gridPen, new Point(x, 0), new Point(x, totalH));
            }

        // ── Sticky header ─────────────────────────────────────────────────────
        ctx.DrawRectangle(headerBg, null, new Rect(sx, sy, vw, WaterfallControl.HeaderH));
        for (long c = cFirst; c <= cLast; c++) {
            long cycle = minCy + c;
            double cx = gutterW + c * _cellW;
            if (cx + _cellW <= sx + gutterW) continue;
            bool isFlush = data.FlushCycles.Contains(cycle);
            bool isStall = data.FetchStallCycles.Contains(cycle);
            if (isFlush)
                ctx.DrawRectangle(
                    WaterfallControl.FlushHdrBg, null, new Rect(cx, sy, _cellW, WaterfallControl.HeaderH)
                );
            if (showText) {
                IBrush cycleFg = isFlush ? Brushes.White : isStall ? stallHdrFg : headerFg;
                DrawFt(ctx, $"C{cycle}", cycleFg, 8.5, new Point(cx + 2, sy + 7));
            }
        }

        ctx.DrawLine(
            gridPen, new Point(sx, sy + WaterfallControl.HeaderH), new Point(sx + vw, sy + WaterfallControl.HeaderH)
        );

        // ── Sticky gutter (hidden when text is suppressed) ────────────────────
        if (showText) {
            ulong basePc = data.BasePc;
            double disasmX = _instrIdColW + _pcColW;
            for (int r = rFirst; r <= rLast; r++) {
                WaterfallRow row = data.Rows[r];
                double rowY = WaterfallControl.HeaderH + r * _rowH;
                bool flushed = row.Spans.Count > 0 && row.Spans[^1].Stage == PEventKind.Flush;
                ctx.DrawRectangle(r % 2 == 0 ? rowBg0 : rowBg1, null, new Rect(sx, rowY, gutterW, _rowH));
                if (SelectedRow is { } sel && data.Rows[r] == sel)
                    ctx.DrawRectangle(selRowBg, null, new Rect(sx, rowY, gutterW, _rowH));

                if (flushed) {
                    using DrawingContext.PushedState dim = ctx.PushOpacity(0.38);
                    DrawGutterText();
                }
                else { DrawGutterText(); }

                void DrawGutterText() {
                    DrawFt(ctx, row.InstrId.ToString(), labelFg, 10, new Point(sx + 4, rowY + 4));

                    var pcStr = $"{row.Pc - basePc:X}";
                    double pcX = sx + _instrIdColW + 4;
                    DrawFt(ctx, pcStr, labelFg, 10, new Point(pcX, rowY + 4));
                    if (row.SpecPc != row.Pc) {
                        double slashX = pcX + MeasureFtWidth(pcStr, 10);
                        DrawFt(ctx, $"/{row.SpecPc - basePc:X}", specPcFg, 10, new Point(slashX, rowY + 4));
                    }

                    // Disassembly — clipped to column width.
                    using (ctx.PushClip(new Rect(sx + disasmX, rowY, _disasmColW, _rowH))) {
                        DrawFt(ctx, row.Disassembly, disasmFg, 10, new Point(sx + disasmX + 4, rowY + 4));
                    }
                }
            }

            ctx.DrawLine(gridPen, new Point(sx + gutterW, sy), new Point(sx + gutterW, sy + vh));

            // ── Sticky corner ─────────────────────────────────────────────────
            ctx.DrawRectangle(headerBg, null, new Rect(sx, sy, gutterW, WaterfallControl.HeaderH));
            DrawFt(ctx, "ID", headerFg, 10.5, new Point(sx + 4, sy + 6));
            DrawFt(ctx, "PC", headerFg, 10.5, new Point(sx + _instrIdColW + 6, sy + 6));
            DrawFt(ctx, "Instruction", headerFg, 10.5, new Point(sx + disasmX + 4, sy + 6));
            ctx.DrawLine(
                gridPen, new Point(sx + _instrIdColW, sy), new Point(sx + _instrIdColW, sy + WaterfallControl.HeaderH)
            );
            ctx.DrawLine(gridPen, new Point(sx + disasmX, sy), new Point(sx + disasmX, sy + WaterfallControl.HeaderH));
            ctx.DrawLine(gridPen, new Point(sx + gutterW, sy), new Point(sx + gutterW, sy + WaterfallControl.HeaderH));
            ctx.DrawLine(
                gridPen, new Point(sx, sy + WaterfallControl.HeaderH),
                new Point(sx + gutterW, sy + WaterfallControl.HeaderH)
            );
        }
    }

    // ── Headless PNG export ────────────────────────────────────────────────────
    public static void RenderToFile(WaterfallData data, bool isDark, string path) {
        var ctrl = new WaterfallControl { Data = data, IsDark = isDark, };
        ctrl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ctrl.Arrange(new Rect(ctrl.DesiredSize));
        Size sz = ctrl.DesiredSize;
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(sz.Width), (int)Math.Ceiling(sz.Height)),
            new Vector(96, 96)
        );
        bitmap.Render(ctrl);
        bitmap.Save(path);
    }

    private static void DrawFt(DrawingContext ctx, string text, IBrush fg, double size, Point origin) {
        (string text, double size, IBrush fg) key = (text, size, fg);
        if (!WaterfallControl.FtCache.TryGetValue(key, out FormattedText? ft)) {
            ft = new FormattedText(
                text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, WaterfallControl.Mono, size, fg
            );
            WaterfallControl.FtCache[key] = ft;
        }

        ctx.DrawText(ft, origin);
    }
}