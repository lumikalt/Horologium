using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Face.ViewModels;
using Face.Views;

namespace Face;

public class App : Application {
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            desktop.MainWindow = new MainWindow();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView) {
            var panel = new MainPanel();
            panel.DataContext = new MainWindowViewModel();
            singleView.MainView = panel;
        }

        base.OnFrameworkInitializationCompleted();
    }
}