using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Face.Controls;
using Face.Models;
using Face.ViewModels;
using Orrery.Observation;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;

namespace Face.Views;

public partial class MainPanel : UserControl {
    private AvaPlot? _chartView;
    private DataGrid? _resultsGrid;
    private WaterfallRow? _lastHoveredRow;
    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    public MainPanel() {
        InitializeComponent();

        Loaded += (_, _) => {
            _chartView = this.FindControl<AvaPlot>("ChartView");
            _resultsGrid = this.FindControl<DataGrid>("ResultsGrid");
            if (Vm is not null) {
                Vm.ResultsUpdated += () => Dispatcher.UIThread.Post(OnResultsUpdated);
                Vm.PropertyChanged += (_, e) => {
                    if (e.PropertyName == nameof(MainWindowViewModel.IsDarkTheme)) OnResultsUpdated();
                };
            }

            ApplyChartStyle();

            // Attach a tunneling key handler at TopLevel so zoom works regardless of which
            // child control holds focus.
            TopLevel.GetTopLevel(this)
                    ?.AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown,
                                 RoutingStrategies.Tunnel);
        };
    }

    // ── Zoom via keyboard (tunnel from TopLevel — no focus dependency) ─────────
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e) {
        if (Vm?.IsPEventsTab != true || WaterfallCtrl.Data is null) return;
        // Don't intercept when a text entry control is focused.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()
                is TextBox or NumericUpDown) return;
        switch (e.Key) {
            case Key.OemPlus or Key.Add:
                WaterfallCtrl.ZoomIn();  e.Handled = true; break;
            case Key.OemMinus or Key.Subtract:
                WaterfallCtrl.ZoomOut(); e.Handled = true; break;
            case Key.OemQuestion or Key.Divide or Key.Oem2:
                WaterfallCtrl.ZoomReset(); e.Handled = true; break;
        }
    }

    // ── Hover popup ───────────────────────────────────────────────────────────
    private void OnWaterfallPointerMoved(object? sender, PointerEventArgs e) {
        WaterfallRow? row = WaterfallCtrl.GetRowAt(e.GetPosition(WaterfallCtrl));
        if (row == _lastHoveredRow) return;
        _lastHoveredRow = row;

        if (row is null) {
            InstrCard.IsVisible = false;
            return;
        }

        PopulateCard(row);
        PositionCard(e.GetPosition(InstrOverlay));
        InstrCard.IsVisible = true;
    }

    private void OnWaterfallPointerExited(object? sender, PointerEventArgs e) {
        _lastHoveredRow = null;
        InstrCard.IsVisible = false;
    }

    private void PositionCard(Point cursorInCanvas) {
        const double offsetX = 16;
        const double offsetY = 4;

        // Measure the card so we know its size for edge clamping.
        InstrCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double cardW = InstrCard.DesiredSize.Width;
        double cardH = InstrCard.DesiredSize.Height;
        double canvasW = InstrOverlay.Bounds.Width;
        double canvasH = InstrOverlay.Bounds.Height;

        double left = Math.Clamp(cursorInCanvas.X + offsetX, 0, Math.Max(0, canvasW - cardW));
        double top  = Math.Clamp(cursorInCanvas.Y + offsetY, 0, Math.Max(0, canvasH - cardH));

        Canvas.SetLeft(InstrCard, left);
        Canvas.SetTop(InstrCard,  top);
    }

    private void PopulateCard(WaterfallRow row) {
        PopupPcLine.Text     = $"PC: 0x{row.Pc:X}";
        PopupDisasmLine.Text = row.Disassembly;

        var sb = new System.Text.StringBuilder();
        foreach (PSpan s in row.Spans.Where(s => s.Stage != PEventKind.FetchStall)) {
            string name  = StageName(s.Stage);
            string dur   = $"{s.Duration} cycle{(s.Duration == 1 ? "" : "s")}";
            string range = $"[{s.Start}–{s.End - 1}]";
            sb.AppendLine($"{name,-10}  {dur,-12}  {range}");
        }

        if (row.SrcRegs is { Count: > 0 }) {
            sb.AppendLine();
            for (int i = 0; i < row.SrcRegs.Count; i++) {
                int r = row.SrcRegs[i];
                if (r < 0) continue;
                sb.AppendLine($"  {AbiName(r),-4} = 0x{row.SrcVals[i]:X8}");
            }
        }
        if (row.DestReg > 0) {
            if (row.SrcRegs is not { Count: > 0 }) sb.AppendLine();
            sb.AppendLine($"  {AbiName(row.DestReg),-4} ← 0x{row.DestVal:X8}");
        }

        PopupStages.Text = sb.ToString().TrimEnd();

        static string StageName(PEventKind k) => k switch {
            PEventKind.Fetch    => "Fetch",
            PEventKind.Decode   => "Decode",
            PEventKind.Dispatch => "Dispatch",
            PEventKind.Issue    => "Issue",
            PEventKind.Execute  => "Execute",
            PEventKind.Retire   => "Commit",
            PEventKind.Flush    => "Flush (squashed)",
            _                   => k.ToString(),
        };

        static string AbiName(int r) => r switch {
            0  => "zero", 1  => "ra",  2  => "sp",  3  => "gp",
            4  => "tp",   5  => "t0",  6  => "t1",  7  => "t2",
            8  => "s0",   9  => "s1",  10 => "a0",  11 => "a1",
            12 => "a2",  13 => "a3",  14 => "a4",  15 => "a5",
            16 => "a6",  17 => "a7",  18 => "s2",  19 => "s3",
            20 => "s4",  21 => "s5",  22 => "s6",  23 => "s7",
            24 => "s8",  25 => "s9",  26 => "s10", 27 => "s11",
            28 => "t3",  29 => "t4",  30 => "t5",  31 => "t6",
            _  => $"x{r}",
        };
    }

    // ── Save waterfall image ──────────────────────────────────────────────────
    private async void OnSaveWaterfallClick(object? sender, RoutedEventArgs e) {
        if (Vm?.CurrentWaterfall is not { } data) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions {
                Title = "Save Waterfall Image",
                DefaultExtension = "png",
                SuggestedFileName = "waterfall",
                FileTypeChoices = [new FilePickerFileType("PNG Image") { Patterns = ["*.png",], },],
            }
        );
        if (file is null) return;
        bool dark = Vm.IsDarkTheme;
        await Task.Run(() => WaterfallControl.RenderToFile(data, dark, file.Path.LocalPath));
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e) {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions {
                Title = "Open ELF Binary",
                AllowMultiple = false,
            }
        );
        if (files.Count > 0) Vm?.SetWorkloadPath(files[0].Path.LocalPath);
    }

    private void OnResultsUpdated() {
        RefreshChart();
        RebuildTable();
    }

    private void RebuildTable() {
        if (_resultsGrid is null || Vm is null) return;

        _resultsGrid.Columns.Clear();
        foreach (string header in Vm.TableHeaders)
            _resultsGrid.Columns.Add(
                new DataGridTextColumn {
                    Header = header,
                    Binding = new Binding($"[{header}]"),
                    IsReadOnly = true,
                }
            );
        _resultsGrid.ItemsSource = Vm.TableRows;
    }

    private void ApplyChartStyle() {
        if (_chartView is null) return;
        bool dark = Vm?.IsDarkTheme ?? true;
        Plot plt = _chartView.Plot;
        if (dark) {
            plt.FigureBackground.Color = Color.FromHex("#1C1C28");
            plt.DataBackground.Color = Color.FromHex("#1C1C28");
            plt.Grid.MajorLineColor = Color.FromHex("#3A3A52");
            plt.Axes.Color(Colors.White);
        }
        else {
            plt.FigureBackground.Color = Color.FromHex("#F5F5F5");
            plt.DataBackground.Color = Color.FromHex("#FFFFFF");
            plt.Grid.MajorLineColor = Color.FromHex("#CCCCDD");
            plt.Axes.Color(Colors.Black);
        }

        _chartView.Refresh();
    }

    private void RefreshChart() {
        if (_chartView is null || Vm is null) return;
        (string[] names, double[] values) = Vm.GetChartData();

        Plot plt = _chartView.Plot;
        plt.Clear();
        ApplyChartStyle();

        if (names.Length == 0) {
            _chartView.Refresh();
            return;
        }

        Bar[] bars = Enumerable.Range(0, names.Length)
                               .Select(i => new Bar {
                                        Position = i,
                                        Value = values[i],
                                        FillColor = Color.FromHex("#5B9BD5"),
                                    }
                                )
                               .ToArray();

        BarPlot barPlot = plt.Add.Bars(bars);
        barPlot.Horizontal = true;

        double[] positions = Enumerable.Range(0, names.Length).Select(i => (double)i).ToArray();
        plt.Axes.Left.SetTicks(positions, names);
        plt.Axes.Bottom.Label.Text = Vm.SelectedMetric ?? "";
        plt.Axes.Margins(left: 0);

        _chartView.Refresh();
    }
}
