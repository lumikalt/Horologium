using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Face.Models;
using Orrery.Observation;

namespace Face.Controls;

public sealed class WaterfallControl : Control {
    private const double InstrIdColW = 64;
    private const double PcColW = 76;
    private const double SpecPcColW = 72; // gutter column showing fetch-window start PC
    private const double CellW = 44;
    private const double RowH = 20;
    private const double HeaderH = 26;
    private const int MaxRows = 500;
    private const int MaxCols = 300;

    // Total fixed gutter width (left of the cycle columns)
    private const double GutterW = WaterfallControl.InstrIdColW + WaterfallControl.PcColW + WaterfallControl.SpecPcColW;

    private static readonly Typeface Mono = new("JetBrainsMono Nerd Font Mono, DejaVu Sans Mono, FreeMono");

    private static readonly IBrush DarkHeaderBg = new SolidColorBrush(Color.Parse("#2A2A3E"));
    private static readonly IBrush DarkRowBg0 = new SolidColorBrush(Color.Parse("#1C1C28"));
    private static readonly IBrush DarkRowBg1 = new SolidColorBrush(Color.Parse("#22222F"));
    private static readonly IBrush DarkLabelFg = new SolidColorBrush(Color.Parse("#B0B0C8"));
    private static readonly IBrush DarkHeaderFg = new SolidColorBrush(Color.Parse("#E0E0F0"));
    private static readonly IBrush DarkSpecPcFg = new SolidColorBrush(Color.Parse("#8AAEC8"));
    private static readonly IBrush DarkStallHdrFg = new SolidColorBrush(Color.Parse("#606878"));
    private static readonly IPen DarkGridPen = new Pen(new SolidColorBrush(Color.Parse("#3A3A52")), 0.5);

    private static readonly IBrush LightHeaderBg = new SolidColorBrush(Color.Parse("#D8D8E8"));
    private static readonly IBrush LightRowBg0 = new SolidColorBrush(Color.Parse("#F5F5FC"));
    private static readonly IBrush LightRowBg1 = new SolidColorBrush(Color.Parse("#EDEDF8"));
    private static readonly IBrush LightLabelFg = new SolidColorBrush(Color.Parse("#404060"));
    private static readonly IBrush LightHeaderFg = new SolidColorBrush(Color.Parse("#1A1A30"));
    private static readonly IBrush LightSpecPcFg = new SolidColorBrush(Color.Parse("#3A6080"));
    private static readonly IBrush LightStallHdrFg = new SolidColorBrush(Color.Parse("#888898"));
    private static readonly IPen LightGridPen = new Pen(new SolidColorBrush(Color.Parse("#C0C0D4")), 0.5);

    private static readonly IBrush FlushHdrBg = new SolidColorBrush(Color.Parse("#6B2020"));
    private static readonly IBrush FlushColTint = new SolidColorBrush(Color.FromArgb(45, 200, 60, 60));

    private static readonly Dictionary<PEventKind, (IBrush Bg, string Label)> KindStyle = new() {
        [PEventKind.Fetch] = (new SolidColorBrush(Color.Parse("#4A7EC7")), "F"),
        [PEventKind.Decode] = (new SolidColorBrush(Color.Parse("#1A9490")), "DC"),
        [PEventKind.Dispatch] = (new SolidColorBrush(Color.Parse("#2EA5A0")), "D"),
        [PEventKind.Issue] = (new SolidColorBrush(Color.Parse("#8A6BD4")), "IS"),
        [PEventKind.Execute] = (new SolidColorBrush(Color.Parse("#C87A2A")), "EX"),
        [PEventKind.Retire] = (new SolidColorBrush(Color.Parse("#4AB04A")), "RT"),
        [PEventKind.Flush] = (new SolidColorBrush(Color.Parse("#C45050")), "FL"),
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
        int rows = Math.Min(data.Rows.Count, WaterfallControl.MaxRows);
        long cols = Math.Min(data.MaxCycle - data.MinCycle + 1, WaterfallControl.MaxCols);
        return new Size(
            WaterfallControl.GutterW + cols * WaterfallControl.CellW,
            WaterfallControl.HeaderH + rows * WaterfallControl.RowH
        );
    }

    public override void Render(DrawingContext ctx) {
        WaterfallData? data = Data;
        if (data is null || data.Rows.Count == 0) return;

        bool dark = IsDark;
        IBrush headerBg = dark ? WaterfallControl.DarkHeaderBg : WaterfallControl.LightHeaderBg;
        IBrush rowBg0 = dark ? WaterfallControl.DarkRowBg0 : WaterfallControl.LightRowBg0;
        IBrush rowBg1 = dark ? WaterfallControl.DarkRowBg1 : WaterfallControl.LightRowBg1;
        IBrush labelFg = dark ? WaterfallControl.DarkLabelFg : WaterfallControl.LightLabelFg;
        IBrush headerFg = dark ? WaterfallControl.DarkHeaderFg : WaterfallControl.LightHeaderFg;
        IBrush specPcFg = dark ? WaterfallControl.DarkSpecPcFg : WaterfallControl.LightSpecPcFg;
        IBrush stallHdrFg = dark ? WaterfallControl.DarkStallHdrFg : WaterfallControl.LightStallHdrFg;
        IPen gridPen = dark ? WaterfallControl.DarkGridPen : WaterfallControl.LightGridPen;

        int numRows = Math.Min(data.Rows.Count, WaterfallControl.MaxRows);
        long numCols = Math.Min(data.MaxCycle - data.MinCycle + 1, WaterfallControl.MaxCols);
        double totalW = WaterfallControl.GutterW + numCols * WaterfallControl.CellW;
        double totalH = WaterfallControl.HeaderH + numRows * WaterfallControl.RowH;
        long minCy = data.MinCycle;

        // Query the nearest ancestor ScrollViewer for the currently visible rectangle,
        // so we can skip drawing rows/columns that are entirely off-screen.
        ScrollViewer? sv = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        double sx = sv?.Offset.X ?? 0;
        double sy = sv?.Offset.Y ?? 0;
        double vw = sv?.Viewport.Width ?? totalW;
        double vh = sv?.Viewport.Height ?? totalH;

        int rFirst = Math.Max(0, (int)((sy - WaterfallControl.HeaderH) / WaterfallControl.RowH));
        int rLast = Math.Min(numRows - 1, (int)((sy + vh - WaterfallControl.HeaderH) / WaterfallControl.RowH) + 1);

        // Extra margin of 1 so partially-visible columns at the edges are included.
        long cFirst = Math.Max(0L, (long)((sx - WaterfallControl.GutterW) / WaterfallControl.CellW) - 1);
        long cLast = Math.Min(numCols - 1, (long)((sx + vw - WaterfallControl.GutterW) / WaterfallControl.CellW) + 1);

        // ── Header row ────────────────────────────────────────────────────────────
        ctx.DrawRectangle(headerBg, null, new Rect(0, 0, totalW, WaterfallControl.HeaderH));
        DrawFt(ctx, "InstrId", headerFg, 10.5, new Point(4, 6));
        DrawFt(ctx, "PC", headerFg, 10.5, new Point(WaterfallControl.InstrIdColW + 6, 6));
        DrawFt(
            ctx, "SpecPC", headerFg, 10.5,
            new Point(WaterfallControl.InstrIdColW + WaterfallControl.PcColW + 6, 6)
        );
        for (long c = cFirst; c <= cLast; c++) {
            long cycle = minCy + c;
            double cx = WaterfallControl.GutterW + c * WaterfallControl.CellW;
            bool isFlush = data.FlushCycles.Contains(cycle);
            bool isStall = data.FetchStallCycles.Contains(cycle);
            if (isFlush)
                ctx.DrawRectangle(
                    WaterfallControl.FlushHdrBg, null, new Rect(cx, 0, WaterfallControl.CellW, WaterfallControl.HeaderH)
                );
            IBrush cycleFg = isFlush ? Brushes.White : isStall ? stallHdrFg : headerFg;
            DrawFt(ctx, $"C{cycle}", cycleFg, 9, new Point(cx + 3, 7));
        }

        // ── Instruction row backgrounds (visible range) ───────────────────────────
        for (int r = rFirst; r <= rLast; r++) {
            double rowY = WaterfallControl.HeaderH + r * WaterfallControl.RowH;
            ctx.DrawRectangle(
                r % 2 == 0 ? rowBg0 : rowBg1, null,
                new Rect(0, rowY, totalW, WaterfallControl.RowH)
            );
        }

        // ── Flush column tint over visible instruction rows ───────────────────────
        double colTintTop = WaterfallControl.HeaderH + rFirst * WaterfallControl.RowH;
        double colTintH = (rLast - rFirst + 1) * WaterfallControl.RowH;
        for (long c = cFirst; c <= cLast; c++)
            if (data.FlushCycles.Contains(minCy + c)) {
                double cx = WaterfallControl.GutterW + c * WaterfallControl.CellW;
                ctx.DrawRectangle(
                    WaterfallControl.FlushColTint, null, new Rect(cx, colTintTop, WaterfallControl.CellW, colTintH)
                );
            }

        // ── Instruction row cells and labels ──────────────────────────────────────
        for (int r = rFirst; r <= rLast; r++) {
            WaterfallRow row = data.Rows[r];
            double rowY = WaterfallControl.HeaderH + r * WaterfallControl.RowH;

            DrawFt(ctx, row.InstrId.ToString(), labelFg, 10, new Point(4, rowY + 3));
            DrawFt(
                ctx, $"{row.Pc:X}", labelFg, 10, new Point(WaterfallControl.InstrIdColW + 4, rowY + 3)
            );
            DrawFt(
                ctx, $"{row.SpecPc:X}", specPcFg, 10,
                new Point(WaterfallControl.InstrIdColW + WaterfallControl.PcColW + 4, rowY + 3)
            );

            foreach ((long cycle, PEventKind kind) in row.Events) {
                if (!WaterfallControl.KindStyle.TryGetValue(kind, out (IBrush Bg, string Label) style)) continue;
                long ci = cycle - minCy;
                if (ci < cFirst || ci > cLast) continue;
                double cx = WaterfallControl.GutterW + ci * WaterfallControl.CellW;
                ctx.DrawRectangle(
                    style.Bg, null,
                    new Rect(cx + 1, rowY + 1, WaterfallControl.CellW - 2, WaterfallControl.RowH - 2), 2, 2
                );
                DrawFt(ctx, style.Label, Brushes.White, 10, new Point(cx + 5, rowY + 3));
            }
        }

        // ── Grid lines ───────────────────────────────────────────────────────────
        ctx.DrawLine(gridPen, new Point(0, WaterfallControl.HeaderH), new Point(totalW, WaterfallControl.HeaderH));
        // Horizontal row lines (visible instruction rows only)
        for (int r = rFirst + 1; r <= rLast + 1; r++) {
            double y = WaterfallControl.HeaderH + r * WaterfallControl.RowH;
            ctx.DrawLine(gridPen, new Point(WaterfallControl.GutterW, y), new Point(totalW, y));
        }

        // Vertical gutter separators (full height)
        ctx.DrawLine(
            gridPen, new Point(WaterfallControl.InstrIdColW, 0), new Point(WaterfallControl.InstrIdColW, totalH)
        );
        ctx.DrawLine(
            gridPen, new Point(WaterfallControl.InstrIdColW + WaterfallControl.PcColW, 0),
            new Point(WaterfallControl.InstrIdColW + WaterfallControl.PcColW, totalH)
        );
        ctx.DrawLine(gridPen, new Point(WaterfallControl.GutterW, 0), new Point(WaterfallControl.GutterW, totalH));
        // Vertical cycle column lines (visible range; omit when columns are dense)
        if (cLast - cFirst <= 100)
            for (long c = cFirst; c <= cLast + 1; c++) {
                double x = WaterfallControl.GutterW + c * WaterfallControl.CellW;
                ctx.DrawLine(gridPen, new Point(x, 0), new Point(x, totalH));
            }
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