using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Chip8;
using Chip8.Memory;
using Chip8.Trains;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Face.ViewModels;

public partial class Chip8ViewModel : ObservableObject {
    private readonly Chip8Memory _memory = new();
    private readonly Chip8Train _train;
    private byte[]? _currentRom;
    private bool _interactive;
    private DispatcherTimer? _timer;

    public Chip8ViewModel(Action goToLauncher) {
        GoToLauncherCommand = new RelayCommand(goToLauncher);
        _train = new Chip8Train(new Chip8Mechanism(), _memory);
    }

    public WriteableBitmap Bitmap { get; } = new(
        new PixelSize(64, 32),
        new Vector(96, 96),
        PixelFormats.Bgra8888,
        AlphaFormat.Opaque
    );

    public IRelayCommand GoToLauncherCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartPauseLabel))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "Load a ROM to begin.";

    [ObservableProperty] public partial int InstructionsPerFrame { get; set; } = 10;

    public string StartPauseLabel => IsRunning ? "Pause" : "Start";

    public void LoadRom(byte[] rom) {
        StopInteractive();
        _currentRom = rom;
        _memory.Reset();
        _memory.Load(0x200, rom);
        StartFresh();
        StatusText = $"ROM loaded ({rom.Length} bytes). Running.";
    }

    [RelayCommand]
    private void StartPause() {
        if (IsRunning)
            Pause();
        else
            Resume();
    }

    [RelayCommand]
    private void Reset() {
        if (_currentRom is null) return;
        StopInteractive();
        _memory.Reset();
        _memory.Load(0x200, _currentRom);
        ((Chip8ArchState)_train.ArchState).Reset();
        StartFresh();
        StatusText = "Reset.";
    }

    public void KeyDown(int chip8Key) => ((Chip8ArchState)_train.ArchState).Keys[chip8Key] = true;
    public void KeyUp(int chip8Key) => ((Chip8ArchState)_train.ArchState).Keys[chip8Key] = false;

    private void StartFresh() {
        _train.Reset();
        ((Chip8ArchState)_train.ArchState).Reset();
        _train.BeginInteractive();
        _interactive = true;
        StartTimer();
    }

    private void Resume() {
        if (!_interactive) {
            _train.BeginInteractive();
            _interactive = true;
        }

        StartTimer();
    }

    private void Pause() {
        _timer?.Stop();
        _timer = null;
        IsRunning = false;
        StatusText = "Paused.";
    }

    private void StopInteractive() {
        _timer?.Stop();
        _timer = null;
        if (_interactive) {
            _train.FinalizeInteractive();
            _interactive = false;
        }

        IsRunning = false;
    }

    private void StartTimer() {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 60), };
        _timer.Tick += OnFrame;
        _timer.Start();
        IsRunning = true;
    }

    private void OnFrame(object? sender, EventArgs e) {
        try {
            _train.StepN(InstructionsPerFrame);

            var state = (Chip8ArchState)_train.ArchState;
            if (state.DelayTimer > 0) state.DelayTimer--;
            if (state.SoundTimer > 0) state.SoundTimer--;

            UpdateBitmap(state.Display);

            if (_train.IsHalted) {
                Pause();
                StatusText = "Program halted.";
            }
        }
        catch (Exception ex) {
            Pause();
            StatusText = $"Error: {ex.Message}";
        }
    }

    public event Action? DisplayUpdated;

    private void UpdateBitmap(bool[] display) {
        using (ILockedFramebuffer buf = Bitmap.Lock()) {
            int stride = buf.RowBytes / 4;
            unsafe {
                var ptr = (uint*)buf.Address;
                for (var y = 0; y < 32; y++)
                for (var x = 0; x < 64; x++)
                    ptr[y * stride + x] = display[y * 64 + x] ? 0xFFE8E8E8u : 0xFF111111u;
            }
        }

        DisplayUpdated?.Invoke();
    }
}