#region

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Face.ViewModels;
using Face.Views;

#endregion

namespace Face;

public class App : Application {
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted() {
        switch (ApplicationLifetime) {
            case IClassicDesktopStyleApplicationLifetime desktop: desktop.MainWindow = new MainWindow(); break;
            case ISingleViewApplicationLifetime singleView: {
                var panel = new MainPanel();
                panel.DataContext = new MainWindowViewModel();
                singleView.MainView = panel;
                break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}