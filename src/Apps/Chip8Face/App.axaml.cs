#region

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Chip8Face.ViewModels;
using Chip8Face.Views;

#endregion

namespace Chip8Face;

public class App : Application {
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted() {
        switch (ApplicationLifetime) {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new MainWindow { DataContext = new Chip8ViewModel(), };
                break;
            case ISingleViewApplicationLifetime singleView: {
                var view = new Chip8View {
                    DataContext = new Chip8ViewModel(),
                };
                singleView.MainView = view;
                break;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}