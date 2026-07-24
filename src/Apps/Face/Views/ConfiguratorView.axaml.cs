#region

using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Highlighting;
using Face.ViewModels;

#endregion

namespace Face.Views;

public partial class ConfiguratorView : UserControl {
    private Timer? _debounce;
    private bool _suppressTextChanged;
    private ConfiguratorViewModel? _vm;

    public ConfiguratorView() {
        InitializeComponent();
        Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#");
        DataContextChanged += OnDataContextChanged;
        Editor.TextChanged += OnEditorTextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e) {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as ConfiguratorViewModel;
        if (_vm == null) return;
        SyncEditorTextFromVm();
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    // The FileSystemWatcher path updates Vm.ScriptText directly (an external save reloads the
    // buffer); mirror that into the editor here rather than only reacting to in-app typing.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(ConfiguratorViewModel.ScriptText)) SyncEditorTextFromVm();
    }

    private void SyncEditorTextFromVm() {
        if (_vm == null || Editor.Text == _vm.ScriptText) return;
        _suppressTextChanged = true;
        Editor.Text = _vm.ScriptText;
        _suppressTextChanged = false;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e) {
        if (_vm == null || _suppressTextChanged) return;
        _vm.ScriptText = Editor.Text;
        _debounce?.Dispose();
        _debounce = new Timer(
            _ => Dispatcher.UIThread.Post(() => {
                    if (_vm.RebuildCommand.CanExecute(null)) _vm.RebuildCommand.Execute(null);
                }
            ),
            null, 600, Timeout.Infinite
        );
    }

    private async void OnBrowseWorkloadClick(object? sender, RoutedEventArgs e) {
        try {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions { Title = "Open ELF Binary", AllowMultiple = false, }
            );
            if (files.Count > 0) _vm?.SetWorkloadPath(files[0].Path.LocalPath);
        }
        catch (Exception) {
            // ignored
        }
    }

    // Saves the current buffer to disk and starts watching it, so the user can switch to a real
    // editor (Rider, vim, …) for tooling this in-app editor doesn't have (no completion/diagnostics).
    private async void OnSaveScriptClick(object? sender, RoutedEventArgs e) {
        try {
            if (_vm is null) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;
            IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions {
                    Title = "Save Architecture Script",
                    DefaultExtension = "csx",
                    SuggestedFileName = "architecture",
                    FileTypeChoices = [new FilePickerFileType("C# Script") { Patterns = ["*.csx",], },],
                }
            );
            if (file is null) return;

            await _vm.SetScriptFilePathAsync(file.Path.LocalPath);

            // Best-effort — no associated app for .csx is a normal outcome, not an error to surface.
            try { Process.Start(new ProcessStartInfo(file.Path.LocalPath) { UseShellExecute = true, }); }
            catch (Win32Exception) { }
        }
        catch (Exception) {
            // ignored
        }
    }
}