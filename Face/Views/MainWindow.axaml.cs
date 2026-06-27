using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Face.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;

namespace Face.Views;

public partial class MainWindow : Window {
    private AvaPlot? _chartView;
    private DataGrid? _resultsGrid;
    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    public MainWindow() {
        InitializeComponent();
        DataContext = new MainWindowViewModel();

        Loaded += (_, _) => {
            _chartView = this.FindControl<AvaPlot>("ChartView");
            _resultsGrid = this.FindControl<DataGrid>("ResultsGrid");
            if (Vm is not null) {
                Vm.ResultsUpdated += () => Dispatcher.UIThread.Post(OnResultsUpdated);
                Vm.PropertyChanged += (_, e) => {
                    if (e.PropertyName == nameof(MainWindowViewModel.IsDarkTheme))
                        OnResultsUpdated();
                };
            }
            ApplyChartStyle();
        };
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e) {
        TopLevel topLevel = GetTopLevel(this)!;
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
            plt.DataBackground.Color  = Color.FromHex("#1C1C28");
            plt.Grid.MajorLineColor   = Color.FromHex("#3A3A52");
            plt.Axes.Color(Colors.White);
        } else {
            plt.FigureBackground.Color = Color.FromHex("#F5F5F5");
            plt.DataBackground.Color   = Color.FromHex("#FFFFFF");
            plt.Grid.MajorLineColor    = Color.FromHex("#CCCCDD");
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

        // Horizontal bars: config names on Y axis, values on X — no label rotation needed
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