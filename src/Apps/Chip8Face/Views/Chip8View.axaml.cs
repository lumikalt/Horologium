#region

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Chip8Face.ViewModels;

#endregion

namespace Chip8Face.Views;

public partial class Chip8View : UserControl {
    private Chip8ViewModel? _subscribedVm;

    public Chip8View() {
        InitializeComponent();
        Loaded += (_, _) => Focus();
        DataContextChanged += OnDataContextChanged;
    }

    private Chip8ViewModel? Vm => DataContext as Chip8ViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e) {
        if (_subscribedVm is not null) {
            _subscribedVm.DisplayUpdated -= InvalidateDisplay;
            _subscribedVm = null;
        }

        if (DataContext is Chip8ViewModel vm) {
            _subscribedVm = vm;
            vm.DisplayUpdated += InvalidateDisplay;
        }
    }

    private void InvalidateDisplay() => DisplayImage.InvalidateVisual();

    private async void OnLoadRomClick(object? sender, RoutedEventArgs e) {
        try {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions {
                    Title = "Open CHIP-8 ROM",
                    AllowMultiple = false,
                    FileTypeFilter = [
                        new FilePickerFileType("CHIP-8 ROM") { Patterns = ["*.ch8", "*.rom", "*.c8",], },
                        new FilePickerFileType("All Files") { Patterns = ["*",], },
                    ],
                }
            );
            if (files.Count == 0) return;
            byte[] rom = await File.ReadAllBytesAsync(files[0].Path.LocalPath);
            Vm?.LoadRom(rom);
            Focus();
        }
        catch (Exception) {
            // ignored
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) {
        if (Chip8KeyFor(e.Key) is { } k) Vm?.KeyDown(k);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e) {
        if (Chip8KeyFor(e.Key) is { } k) Vm?.KeyUp(k);
    }

    // Standard CHIP-8 keyboard layout mapped to QWERTY
    private static int? Chip8KeyFor(Key key) => key switch {
        Key.X  => 0x0, Key.D1 => 0x1, Key.D2 => 0x2, Key.D3 => 0x3,
        Key.Q  => 0x4, Key.W  => 0x5, Key.E  => 0x6, Key.A  => 0x7,
        Key.S  => 0x8, Key.D  => 0x9, Key.Z  => 0xA, Key.C  => 0xB,
        Key.D4 => 0xC, Key.R  => 0xD, Key.F  => 0xE, Key.V  => 0xF,
        _      => null,
    };
}
