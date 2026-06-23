using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Face.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace Face.Views;

public partial class MainWindow : Window {
    private AvaPlot?    _chartView;
    private DataGrid?   _resultsGrid;
    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    public MainWindow() {
        InitializeComponent();
        DataContext = new MainWindowViewModel();

        Loaded += (_, _) => {
            _chartView   = this.FindControl<AvaPlot>("ChartView");
            _resultsGrid = this.FindControl<DataGrid>("ResultsGrid");
            if (Vm is not null)
                Vm.ResultsUpdated += () => Dispatcher.UIThread.Post(OnResultsUpdated);

            BrowseButton.Click += OnBrowseClick;
            ApplyChartStyle();
        };
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e) {
        var topLevel = TopLevel.GetTopLevel(this)!;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title          = "Open ELF Binary",
            AllowMultiple  = false,
        });
        if (files.Count > 0)
            Vm?.SetWorkloadPath(files[0].Path.LocalPath);
    }

    private void OnResultsUpdated() {
        RefreshChart();
        RebuildTable();
    }

    private void RebuildTable() {
        if (_resultsGrid is null || Vm is null) return;

        _resultsGrid.Columns.Clear();
        foreach (string header in Vm.TableHeaders) {
            _resultsGrid.Columns.Add(new DataGridTextColumn {
                Header    = header,
                Binding   = new Binding($"[{header}]"),
                IsReadOnly = true,
            });
        }
        _resultsGrid.ItemsSource = Vm.TableRows;
    }

    private void ApplyChartStyle() {
        if (_chartView is null) return;
        var plt = _chartView.Plot;
        plt.FigureBackground.Color = ScottPlot.Color.FromHex("#1C1C28");
        plt.DataBackground.Color   = ScottPlot.Color.FromHex("#1C1C28");
        plt.Grid.MajorLineColor    = ScottPlot.Color.FromHex("#3A3A52");
        plt.Axes.Color(ScottPlot.Colors.White);
        _chartView.Refresh();
    }

    private void RefreshChart() {
        if (_chartView is null || Vm is null) return;
        var (names, values) = Vm.GetChartData();
        if (names.Length == 0) return;

        var plt = _chartView.Plot;
        plt.Clear();
        ApplyChartStyle();

        // Horizontal bars: config names on Y axis, values on X — no label rotation needed
        var bars = Enumerable.Range(0, names.Length)
            .Select(i => new Bar {
                Position  = i,
                Value     = values[i],
                FillColor = ScottPlot.Color.FromHex("#5B9BD5"),
            })
            .ToArray();

        var barPlot = plt.Add.Bars(bars);
        barPlot.Horizontal = true;

        double[] positions = Enumerable.Range(0, names.Length).Select(i => (double)i).ToArray();
        plt.Axes.Left.SetTicks(positions, names);
        plt.Axes.Bottom.Label.Text = Vm.SelectedMetric ?? "";
        plt.Axes.Margins(left: 0);

        _chartView.Refresh();
    }
}
