#region

using Avalonia.Controls;
using Face.ViewModels;

#endregion

namespace Face.Views;

public partial class MainWindow : Window {
    public MainWindow() {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}