using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit.Highlighting;
using Face.Controls;
using Face.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;

namespace Face.Views;

public partial class AssemblerView : UserControl {
    private AssemblerViewModel? _vm;
    private Timer? _debounce;
    private readonly CurrentLineHighlighter _lineHighlighter = new();
    private AvaPlot? _cacheChartView;

    public AssemblerView() {
        InitializeComponent();
        Editor.TextArea.TextView.BackgroundRenderers.Add(_lineHighlighter);
        Editor.TextArea.AddHandler(InputElement.KeyDownEvent, OnEditorClipboardKey, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
        Editor.TextChanged += OnEditorTextChanged;
        if (Application.Current is not null) Application.Current.ActualThemeVariantChanged += OnThemeVariantChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) {
        _cacheChartView = this.FindControl<AvaPlot>("CacheChartView");
        ApplyCacheChartStyle();
        if (this.FindControl<TabControl>("MainTabControl") is { } tc)
            tc.SelectionChanged += (_, _) => RefreshCacheChart();
    }

    private bool IsDark =>
        Application.Current?.ActualThemeVariant != ThemeVariant.Light;

    private IHighlightingDefinition ActiveHighlighting =>
        _vm?.IsCMode == true ? CHighlighting.GetDefinition(IsDark) : RvHighlighting.GetDefinition(IsDark);

    private string ActiveSource => _vm?.IsCMode == true ? _vm.CSourceCode : _vm?.SourceCode ?? "";

    private void OnThemeVariantChanged(object? sender, EventArgs e) {
        Editor.SyntaxHighlighting = ActiveHighlighting;
        ApplyCacheChartStyle();
        _cacheChartView?.Refresh();
    }

    private void OnDataContextChanged(object? sender, EventArgs e) {
        if (_vm != null) {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.CacheUpdated -= OnCacheUpdated;
        }

        _vm = DataContext as AssemblerViewModel;
        if (_vm == null) return;
        Editor.Text = ActiveSource;
        Editor.SyntaxHighlighting = ActiveHighlighting;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.CacheUpdated += OnCacheUpdated;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(AssemblerViewModel.IsCMode)) {
            Editor.Text = ActiveSource;
            Editor.SyntaxHighlighting = ActiveHighlighting;
            return;
        }

        if (e.PropertyName != nameof(AssemblerViewModel.CurrentSourceLine)) return;
        int line = _vm?.CurrentSourceLine ?? 0;
        _lineHighlighter.Line = line;
        Editor.TextArea.TextView.InvalidateLayer(_lineHighlighter.Layer);
        if (line > 0) Editor.ScrollToLine(line);
    }

    private void OnCacheUpdated() => RefreshCacheChart();

    private void RefreshCacheChart() {
        if (_cacheChartView == null || _vm == null) return;
        (double[] x, double[] y, double[] ma) = _vm.GetCacheChartData();
        Plot plt = _cacheChartView.Plot;
        plt.Clear();
        ApplyCacheChartStyle();
        if (x.Length > 1) {
            Scatter total = plt.Add.ScatterLine(x, y);
            total.Color = Color.FromHex("#5B9BD5");
            total.LineWidth = 1;
            Scatter moving = plt.Add.ScatterLine(x, ma);
            moving.Color = Color.FromHex("#F0A050");
            moving.LineWidth = 1.5f;
            plt.Axes.SetLimitsX(0, x[^1] * 1.05 + 1);
        }
        else { plt.Axes.SetLimitsX(0, 10); }

        plt.Axes.SetLimitsY(0, 100);
        plt.Axes.Bottom.Label.Text = "Cycle";
        plt.Axes.Left.Label.Text = "Hit Rate (%)";
        _cacheChartView.Refresh();
    }

    private void ApplyCacheChartStyle() {
        if (_cacheChartView == null) return;
        Plot plt = _cacheChartView.Plot;
        bool dark = IsDark;
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
    }

    private void OnEditorTextChanged(object? sender, EventArgs e) {
        if (_vm == null) return;
        if (_vm.IsCMode)
            _vm.CSourceCode = Editor.Text;
        else
            _vm.SourceCode = Editor.Text;
        _debounce?.Dispose();
        _debounce = new Timer(
            _ =>
                Dispatcher.UIThread.Post(() => {
                        if (_vm.AssembleCommand.CanExecute(null)) _vm.AssembleCommand.Execute(null);
                    }
                ),
            null, 600, Timeout.Infinite
        );
    }

    private async void OnEditorClipboardKey(object? sender, KeyEventArgs e) {
        if (e.KeyModifiers != KeyModifiers.Control) return;
        if (e.Key is not (Key.C or Key.X or Key.V)) return;

        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        e.Handled = true;

        switch (e.Key) {
            case Key.C:
                string? sel = Editor.SelectedText;
                if (!string.IsNullOrEmpty(sel)) await clipboard.SetTextAsync(sel);
                break;

            case Key.X:
                sel = Editor.SelectedText;
                if (!string.IsNullOrEmpty(sel)) {
                    await clipboard.SetTextAsync(sel);
                    Editor.TextArea.Selection.ReplaceSelectionWithText("");
                }

                break;

            case Key.V:
                string? text = await clipboard.TryGetValueAsync(DataFormat.Text);
                if (!string.IsNullOrEmpty(text)) Editor.TextArea.PerformTextInput(text);
                break;
        }
    }
}