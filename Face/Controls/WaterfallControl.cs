using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Face.Models;
using Orrery.Observation;

namespace Face.Controls;

public sealed class WaterfallControl : Control {
    private const double CellW   = 44;
    private const double RowH    = 20;
    private const double HeaderH = 26;
    private const int MaxRows = 500;
    private const int MaxCols = 300;
    private const double ColPad  = 12; // horizontal padding added to each gutter column

    private static readonly Typeface Mono = new("JetBrainsMono Nerd Font Mono, DejaVu Sans Mono, FreeMono");

    private static readonly IBrush DarkHeaderBg    = new SolidColorBrush(Color.Parse("#2A2A3E"));
    private static readonly IBrush DarkRowBg0      = new SolidColorBrush(Color.Parse("#1C1C28"));
    private static readonly IBrush DarkRowBg1      = new SolidColorBrush(Color.Parse("#22222F"));
    private static readonly IBrush DarkLabelFg     = new SolidColorBrush(Color.Parse("#B0B0C8"));
    private static readonly IBrush DarkHeaderFg    = new SolidColorBrush(Color.Parse("#E0E0F0"));
    private static readonly IBrush DarkSpecPcFg    = new SolidColorBrush(Color.Parse("#8AAEC8"));
    private static readonly IBrush DarkStallHdrFg  = new SolidColorBrush(Color.Parse("#606878"));
    private static readonly IPen   DarkGridPen     = new Pen(new SolidColorBrush(Color.Parse("#3A3A52")), 0.5);

    private static readonly IBrush LightHeaderBg   = new SolidColorBrush(Color.Parse("#D8D8E8"));
    private static readonly IBrush LightRowBg0     = new SolidColorBrush(Color.Parse("#F5F5FC"));
    private static readonly IBrush LightRowBg1     = new SolidColorBrush(Color.Parse("#EDEDF8"));
    private static readonly IBrush LightLabelFg    = new SolidColorBrush(Color.Parse("#404060"));
    private static readonly IBrush LightHeaderFg   = new SolidColorBrush(Color.Parse("#1A1A30"));
    private static readonly IBrush LightSpecPcFg   = new SolidColorBrush(Color.Parse("#3A6080"));
    private static readonly IBrush LightStallHdrFg = new SolidColorBrush(Color.Parse("#888898"));
    private static readonly IPen   LightGridPen    = new Pen(new SolidColorBrush(Color.Parse("#C0C0D4")), 0.5);

    private static readonly IBrush FlushHdrBg  = new SolidColorBrush(Color.Parse("#6B2020"));
    private static readonly IBrush FlushColTint = new SolidColorBrush(Color.FromArgb(45, 200, 60, 60));

    private static readonly Dictionary<PEventKind, (IBrush Bg, string Label)> KindStyle = new() {
        [PEventKind.Fetch]    = (new SolidColorBrush(Color.Parse("#4A7EC7")), "F"),
        [PEventKind.Decode]   = (new SolidColorBrush(Color.Parse("#1A9490")), "DC"),
        [PEventKind.Dispatch] = (new SolidColorBrush(Color.Parse("#2EA5A0")), "D"),
        [PEventKind.Issue]    = (new SolidColorBrush(Color.Parse("#8A6BD4")), "IS"),
        [PEventKind.Execute]  = (new SolidColorBrush(Color.Parse("#C87A2A")), "EX"),
        [PEventKind.Retire]   = (new SolidColorBrush(Color.Parse("#4AB04A")), "RT"),
        [PEventKind.Flush]    = (new SolidColorBrush(Color.Parse("#C45050")), "FL"),
    };

    // Keyed by (text, fontSize, brush) — brush reference equality (all brushes are static singletons).
    // Font family resolution via SKFontManager.MatchFamily is expensive; caching ensures it runs once
    // per unique string rather than once per frame.
    private static readonly Dictionary<(string, double, IBrush), FormattedText> FtCache = new();

    public static readonly StyledProperty<WaterfallData?> DataProperty =
        AvaloniaProperty.Register<WaterfallControl, WaterfallData?>(nameof(Data));

    public static readonly StyledProperty<bool> IsDarkProperty =
        AvaloniaProperty.Register<WaterfallControl, bool>(nameof(IsDark), true);

    public WaterfallData? Data {
        get => GetValue(WaterfallControl.DataProperty);
        set => SetValue(WaterfallControl.DataProperty, value);
    }

    public bool IsDark {
        get => GetValue(WaterfallControl.IsDarkProperty);
        set => SetValue(WaterfallControl.IsDarkProperty, value);
    }

    // Computed per-data in MeasureOverride; sized to fit actual content + ColPad.
    private double _instrIdColW = 64;
    private double _pcColW = 100;
    private double GutterW => _instrIdColW + _pcColW;

    static WaterfallControl() {
        WaterfallControl.DataProperty.Changed.AddClassHandler<WaterfallControl>((c, _) => {
                c.InvalidateMeasure();
                c.InvalidateVisual();
            }
        );
        WaterfallControl.IsDarkProperty.Changed.AddClassHandler<WaterfallControl>((c, _) => c.InvalidateVisual());
    }

    protected override Size MeasureOverride(Size availableSize) {
        WaterfallData? data = Data;
        if (data is null || data.Rows.Count == 0)
            return new Size(100, WaterfallControl.HeaderH + WaterfallControl.RowH);
        ComputeColumnWidths(data);
        int rows = Math.Min(data.Rows.Count, WaterfallControl.MaxRows);
        long cols = Math.Min(data.MaxCycle - data.MinCycle + 1, WaterfallControl.MaxCols);
        return new Size(
            GutterW + cols * WaterfallControl.CellW,
            WaterfallControl.HeaderH + rows * WaterfallControl.RowH
        );
    }

    // Sets _instrIdColW and _pcColW wide enough to display the widest value in each column.
    private void ComputeColumnWidths(WaterfallData data) {
        string widestId  = "InstrId"; // header is the minimum width reference
        string widestPc  = "PC";
        string widestSPc = "";

        int limit = Math.Min(data.Rows.Count, WaterfallControl.MaxRows);
        for (int i = 0; i < limit; i++) {
            WaterfallRow row = data.Rows[i];
            string id = row.InstrId.ToString();
            if (id.Length > widestId.Length) widestId = id;
            string pc = $"{row.Pc:X}";
            if (pc.Length > widestPc.Length) widestPc = pc;
            if (row.SpecPc != row.Pc) {
                string spc = $"~{row.SpecPc:X}";
                if (spc.Length > widestSPc.Length) widestSPc = spc;
            }
        }

        double idW  = MeasureFtWidth(widestId,  widestId == "InstrId" ? 10.5 : 10);
        double pcW  = Math.Max(MeasureFtWidth("PC", 10.5), MeasureFtWidth(widestPc, widestPc == "PC" ? 10.5 : 10));
        if (widestSPc.Length > 0)
            pcW = Math.Max(pcW, MeasureFtWidth(widestSPc, 7.5));

        _instrIdColW = idW + WaterfallControl.ColPad;
        _pcColW      = pcW + WaterfallControl.ColPad;
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

    public override void Render(DrawingContext ctx) {
        WaterfallData? data = Data;
        if (data is null || data.Rows.Count == 0) return;
        RenderCore(ctx, data);
    }

    private void RenderCore(DrawingContext ctx, WaterfallData data) {
        bool dark = IsDark;
        IBrush headerBg   = dark ? WaterfallControl.DarkHeaderBg   : WaterfallControl.LightHeaderBg;
        IBrush rowBg0     = dark ? WaterfallControl.DarkRowBg0     : WaterfallControl.LightRowBg0;
        IBrush rowBg1     = dark ? WaterfallControl.DarkRowBg1     : WaterfallControl.LightRowBg1;
        IBrush labelFg    = dark ? WaterfallControl.DarkLabelFg    : WaterfallControl.LightLabelFg;
        IBrush headerFg   = dark ? WaterfallControl.DarkHeaderFg   : WaterfallControl.LightHeaderFg;
        IBrush specPcFg   = dark ? WaterfallControl.DarkSpecPcFg   : WaterfallControl.LightSpecPcFg;
        IBrush stallHdrFg = dark ? WaterfallControl.DarkStallHdrFg : WaterfallControl.LightStallHdrFg;
        IPen gridPen      = dark ? WaterfallControl.DarkGridPen    : WaterfallControl.LightGridPen;

        double gutterW = GutterW;
        int numRows = Math.Min(data.Rows.Count, WaterfallControl.MaxRows);
        long numCols = Math.Min(data.MaxCycle - data.MinCycle + 1, WaterfallControl.MaxCols);
        double totalW = gutterW + numCols * WaterfallControl.CellW;
        double totalH = WaterfallControl.HeaderH + numRows * WaterfallControl.RowH;
        long minCy = data.MinCycle;

        ScrollViewer? sv = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        double sx = sv?.Offset.X ?? 0;
        double sy = sv?.Offset.Y ?? 0;
        double vw = sv?.Viewport.Width ?? totalW;
        double vh = sv?.Viewport.Height ?? totalH;

        int rFirst = Math.Max(0, (int)((sy - WaterfallControl.HeaderH) / WaterfallControl.RowH));
        int rLast  = Math.Min(numRows - 1, (int)((sy + vh - WaterfallControl.HeaderH) / WaterfallControl.RowH) + 1);

        long cFirst = Math.Max(0L, (long)((sx - gutterW) / WaterfallControl.CellW) - 1);
        long cLast  = Math.Min(numCols - 1, (long)((sx + vw - gutterW) / WaterfallControl.CellW) + 1);

        // ── Row backgrounds (full-width stripe, gutter included) ─────────────────
        for (int r = rFirst; r <= rLast; r++) {
            double rowY = WaterfallControl.HeaderH + r * WaterfallControl.RowH;
            ctx.DrawRectangle(
                r % 2 == 0 ? rowBg0 : rowBg1, null,
                new Rect(0, rowY, totalW, WaterfallControl.RowH)
            );
        }

        // ── Flush column tint ─────────────────────────────────────────────────────
        double colTintTop = WaterfallControl.HeaderH + rFirst * WaterfallControl.RowH;
        double colTintH = (rLast - rFirst + 1) * WaterfallControl.RowH;
        for (long c = cFirst; c <= cLast; c++)
            if (data.FlushCycles.Contains(minCy + c)) {
                double cx = gutterW + c * WaterfallControl.CellW;
                ctx.DrawRectangle(
                    WaterfallControl.FlushColTint, null,
                    new Rect(cx, colTintTop, WaterfallControl.CellW, colTintH)
                );
            }

        // ── PEvent cells ─────────────────────────────────────────────────────────
        for (int r = rFirst; r <= rLast; r++) {
            WaterfallRow row = data.Rows[r];
            double rowY = WaterfallControl.HeaderH + r * WaterfallControl.RowH;
            foreach ((long cycle, PEventKind kind) in row.Events) {
                if (!WaterfallControl.KindStyle.TryGetValue(kind, out (IBrush Bg, string Label) style)) continue;
                long ci = cycle - minCy;
                if (ci < cFirst || ci > cLast) continue;
                double cx = gutterW + ci * WaterfallControl.CellW;
                ctx.DrawRectangle(
                    style.Bg, null,
                    new Rect(cx + 1, rowY + 1, WaterfallControl.CellW - 2, WaterfallControl.RowH - 2), 2, 2
                );
                DrawFt(ctx, style.Label, Brushes.White, 10, new Point(cx + 5, rowY + 3));
            }
        }

        // ── Grid lines (cycle columns + horizontal row lines) ────────────────────
        for (int r = rFirst + 1; r <= rLast + 1; r++) {
            double y = WaterfallControl.HeaderH + r * WaterfallControl.RowH;
            ctx.DrawLine(gridPen, new Point(gutterW, y), new Point(totalW, y));
        }
        if (cLast - cFirst <= 100)
            for (long c = cFirst; c <= cLast + 1; c++) {
                double x = gutterW + c * WaterfallControl.CellW;
                ctx.DrawLine(gridPen, new Point(x, 0), new Point(x, totalH));
            }

        // ── Sticky header row (redrawn at viewport-top y = sy) ───────────────────
        ctx.DrawRectangle(headerBg, null, new Rect(sx, sy, vw, WaterfallControl.HeaderH));
        for (long c = cFirst; c <= cLast; c++) {
            long cycle = minCy + c;
            double cx = gutterW + c * WaterfallControl.CellW;
            if (cx + WaterfallControl.CellW <= sx + gutterW) continue; // behind sticky gutter
            bool isFlush = data.FlushCycles.Contains(cycle);
            bool isStall = data.FetchStallCycles.Contains(cycle);
            if (isFlush)
                ctx.DrawRectangle(
                    WaterfallControl.FlushHdrBg, null,
                    new Rect(cx, sy, WaterfallControl.CellW, WaterfallControl.HeaderH)
                );
            IBrush cycleFg = isFlush ? Brushes.White : isStall ? stallHdrFg : headerFg;
            DrawFt(ctx, $"C{cycle}", cycleFg, 9, new Point(cx + 3, sy + 7));
        }
        ctx.DrawLine(gridPen, new Point(sx, sy + WaterfallControl.HeaderH), new Point(sx + vw, sy + WaterfallControl.HeaderH));

        // ── Sticky gutter columns (redrawn at viewport-left x = sx) ─────────────
        for (int r = rFirst; r <= rLast; r++) {
            WaterfallRow row = data.Rows[r];
            double rowY = WaterfallControl.HeaderH + r * WaterfallControl.RowH;
            ctx.DrawRectangle(
                r % 2 == 0 ? rowBg0 : rowBg1, null,
                new Rect(sx, rowY, gutterW, WaterfallControl.RowH)
            );
            DrawFt(ctx, row.InstrId.ToString(), labelFg, 10, new Point(sx + 4, rowY + 3));
            bool twoLine = row.SpecPc != row.Pc;
            DrawFt(
                ctx, $"{row.Pc:X}", labelFg, twoLine ? 8.5 : 10,
                new Point(sx + _instrIdColW + 4, rowY + (twoLine ? 2 : 3))
            );
            if (twoLine)
                DrawFt(
                    ctx, $"~{row.SpecPc:X}", specPcFg, 7.5,
                    new Point(sx + _instrIdColW + 4, rowY + 11)
                );
        }
        ctx.DrawLine(
            gridPen,
            new Point(sx + gutterW, sy),
            new Point(sx + gutterW, sy + vh)
        );

        // ── Sticky corner (header × gutter, drawn last so it sits on top) ────────
        ctx.DrawRectangle(headerBg, null, new Rect(sx, sy, gutterW, WaterfallControl.HeaderH));
        DrawFt(ctx, "InstrId", headerFg, 10.5, new Point(sx + 4, sy + 6));
        DrawFt(ctx, "PC", headerFg, 10.5, new Point(sx + _instrIdColW + 6, sy + 6));
        ctx.DrawLine(
            gridPen,
            new Point(sx + _instrIdColW, sy),
            new Point(sx + _instrIdColW, sy + WaterfallControl.HeaderH)
        );
        ctx.DrawLine(
            gridPen,
            new Point(sx + gutterW, sy),
            new Point(sx + gutterW, sy + WaterfallControl.HeaderH)
        );
        ctx.DrawLine(
            gridPen,
            new Point(sx, sy + WaterfallControl.HeaderH),
            new Point(sx + gutterW, sy + WaterfallControl.HeaderH)
        );
    }

    // Renders the full waterfall (up to MaxRows × MaxCols) to a PNG file at 96 DPI.
    internal static void RenderToFile(WaterfallData data, bool isDark, string path) {
        var ctrl = new WaterfallControl { Data = data, IsDark = isDark };
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
