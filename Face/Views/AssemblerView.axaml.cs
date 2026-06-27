using System.ComponentModel;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Face.Controls;
using Face.ViewModels;

namespace Face.Views;

public partial class AssemblerView : UserControl {
    private AssemblerViewModel? _vm;
    private Timer? _debounce;
    private readonly CurrentLineHighlighter _lineHighlighter = new();

    public AssemblerView() {
        InitializeComponent();
        Editor.TextArea.TextView.BackgroundRenderers.Add(_lineHighlighter);
        Editor.TextArea.AddHandler(InputElement.KeyDownEvent, OnEditorClipboardKey, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
        Editor.TextChanged += OnEditorTextChanged;
        if (Application.Current is not null) Application.Current.ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    private bool IsDark =>
        Application.Current?.ActualThemeVariant != ThemeVariant.Light;

    private void OnThemeVariantChanged(object? sender, EventArgs e) =>
        Editor.SyntaxHighlighting = RvHighlighting.GetDefinition(IsDark);

    private void OnDataContextChanged(object? sender, EventArgs e) {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as AssemblerViewModel;
        if (_vm == null) return;
        Editor.Text = _vm.SourceCode;
        Editor.SyntaxHighlighting = RvHighlighting.GetDefinition(IsDark);
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(AssemblerViewModel.CurrentSourceLine)) return;
        int line = _vm?.CurrentSourceLine ?? 0;
        _lineHighlighter.Line = line;
        Editor.TextArea.TextView.InvalidateLayer(_lineHighlighter.Layer);
        if (line > 0) Editor.ScrollToLine(line);
    }

    private void OnEditorTextChanged(object? sender, EventArgs e) {
        if (_vm == null) return;
        _vm.SourceCode = Editor.Text;
        _debounce?.Dispose();
        _debounce = new Timer(
            _ =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
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