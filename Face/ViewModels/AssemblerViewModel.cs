using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Face.Models;
using Mechanism;
using Orrery.Cache;
using Orrery.Devices;
using Orrery.Observation;
using Pipeline;
using RiscV32;
using RiscV32.Config;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.State;

// ReSharper disable UnusedParameterInPartialMethod

namespace Face.ViewModels;

public partial class AssemblerViewModel : ObservableObject {
    [GeneratedRegex(@"^[^ \t]+horologium_asm\.s:", RegexOptions.Multiline)]
    private static partial Regex AsmErrPrefix { get; }

    [GeneratedRegex(@"^[^ \t]*horologium_src\.c:", RegexOptions.Multiline)]
    private static partial Regex CErrPrefix { get; }

    [GeneratedRegex(
        @"^\s*(\d+)\s+([0-9a-fA-F]+)\s+[0-9a-fA-F]", RegexOptions.Multiline
    )]
    private static partial Regex ListingLineRx { get; }

    [GeneratedRegex(@"^(?<path>[^\s].*):(?<line>\d+)(?:\s+\(discriminator \d+\))?$")]
    private static partial Regex ObjdumpSrcLineRx { get; }

    [GeneratedRegex(@"^\s+(?<addr>[0-9a-fA-F]+):\s")]
    private static partial Regex ObjdumpInsnRx { get; }

    private readonly Rv32Decoder _decoder = new();
    private readonly Rv32Executor _executor = new();
    private IMemory? _memory;
    private Rv32ArchState? _archState;
    private byte[]? _binaryData;
    private byte[]? _elfBytes;
    private int _binarySize;
    private int _stepCount;
    private Dictionary<ulong, int> _pcToLine = [];
    private CancellationTokenSource? _runCts;
    private StringWriter? _uartSw;

    // Pipeline stepping
    private readonly PEventLog _pEventLog = new();
    private FiveStageTrain? _fiveStageTrain;
    private OooeTrain? _oooeTrain;
    private long _currentCycle;

    // Cache hit-rate history — one sample per StepCycle call
    private readonly List<(long Cycle, double HitRate)> _iCacheHitHistory = [];
    private readonly List<(long Cycle, double HitRate)> _dCacheHitHistory = [];

    [ObservableProperty]
    public partial string SourceCode { get; set; } =
        """
        # Stores to 0x10013000 (SiFive UART0 txdata) appear in the Console tab.
        # Fills a 64-word array, then sums it sequentially and with stride 4.
        # Try different D-cache prefetchers in the sidebar to compare hit rates.

                .equ  UART, 0x10013000
                .equ  N,    64
                j     _start

        # putchar(a0) -- write one byte to UART
        putchar:
                li    t0, UART
                sw    a0, 0(t0)
                ret

        # puts(a0) -- print null-terminated string
        puts:
                addi  sp, sp, -8
                sw    ra, 4(sp)
                sw    s0, 0(sp)
                mv    s0, a0
        .puts_loop:
                lbu   a0, 0(s0)
                beqz  a0, .puts_ret
                call  putchar
                addi  s0, s0, 1
                j     .puts_loop
        .puts_ret:
                lw    s0, 0(sp)
                lw    ra, 4(sp)
                addi  sp, sp, 8
                ret

        # putdec(a0) -- print unsigned decimal (digits buffered on stack)
        putdec:
                addi  sp, sp, -32
                sw    ra, 28(sp)
                sw    s0, 24(sp)
                sw    s1, 20(sp)
                mv    s0, sp              # digit buffer at sp+0
                li    s1, 0              # digit count
                li    t0, 10
                bnez  a0, .pd_fill
                li    a0, '0'
                call  putchar
                j     .pd_ret
        .pd_fill:
                beqz  a0, .pd_print
                remu  t1, a0, t0
                divu  a0, a0, t0
                add   t2, s0, s1
                addi  t1, t1, '0'
                sb    t1, 0(t2)           # buf[count++] = LSB digit
                addi  s1, s1, 1
                j     .pd_fill
        .pd_print:
                addi  s1, s1, -1
                bltz  s1, .pd_ret
                add   t2, s0, s1
                lbu   a0, 0(t2)
                call  putchar
                j     .pd_print
        .pd_ret:
                lw    s1, 20(sp)
                lw    s0, 24(sp)
                lw    ra, 28(sp)
                addi  sp, sp, 32
                ret

        _start:
                li    sp, 0xFFF00

                # Fill: arr[i] = i+1  for i in [0, N)
                la    t0, arr
                li    t1, 0
        .fill:
                addi  t2, t1, 1
                sw    t2, 0(t0)
                addi  t0, t0, 4
                addi  t1, t1, 1
                li    t3, N
                blt   t1, t3, .fill

                # Sequential sum: arr[0]+arr[1]+...+arr[63]  =>  2080
                la    t0, arr
                li    t1, 0
                li    s0, 0
        .seq:
                lw    t2, 0(t0)
                add   s0, s0, t2
                addi  t0, t0, 4
                addi  t1, t1, 1
                li    t3, N
                blt   t1, t3, .seq

                # Strided sum: arr[0]+arr[4]+arr[8]+...+arr[60]  =>  496
                la    t0, arr
                li    t1, 0
                li    s1, 0
        .stride:
                lw    t2, 0(t0)
                add   s1, s1, t2
                addi  t0, t0, 16         # skip 4 words at a time
                addi  t1, t1, 1
                li    t3, N/4
                blt   t1, t3, .stride

                la    a0, msg_seq
                call  puts
                mv    a0, s0
                call  putdec
                li    a0, '\n'
                call  putchar

                la    a0, msg_stride
                call  puts
                mv    a0, s1
                call  putdec
                li    a0, '\n'
                call  putchar

                ebreak

                .section .data
        msg_seq:    .asciz "seq:    "
        msg_stride: .asciz "stride: "
        arr:        .space N*4
        """;

    [ObservableProperty]
    public partial string CSourceCode { get; set; } =
        """
        /* SiFive UART0 txdata — each store sends one byte to the Console tab. */
        #define UART_TXDATA (*(volatile unsigned int *)0x10013000)

        static void uart_putchar(char c)        { UART_TXDATA = (unsigned char)c; }
        static void uart_puts(const char *s)    { while (*s) uart_putchar(*s++); }
        static void uart_putdec(unsigned int n) { if (n >= 10) uart_putdec(n / 10); uart_putchar('0' + n % 10); }

        static int factorial(int n) {
            int r = 1;
            while (n > 0) r *= n--;
            return r;
        }

        int main(void) {
            int result = factorial(5);
            uart_puts("5! = ");
            uart_putdec((unsigned int)result);
            uart_putchar('\n');
            return result;
        }
        """;

    [ObservableProperty] public partial bool IsCMode { get; set; }

    [ObservableProperty] public partial string OptLevel { get; set; } = "-O1";

    [ObservableProperty] public partial string AssembleError { get; set; } = "";

    [ObservableProperty] public partial bool HasError { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "Enter assembly and click Assemble.";

    [ObservableProperty] public partial string ConsoleOutput { get; set; } = "";

    [ObservableProperty] private partial bool CanStep { get; set; }

    [ObservableProperty] private partial bool IsAssembling { get; set; }

    [ObservableProperty] public partial AssemblyRow? SelectedInstruction { get; set; }

    [ObservableProperty] public partial int CurrentSourceLine { get; set; } // 1-based; 0 = none

    [ObservableProperty] public partial RegFormat IntRegFormat { get; set; } = RegFormat.Hex;

    [ObservableProperty] public partial RegFormat FloatRegFormat { get; set; } = RegFormat.Hex;

    [ObservableProperty] public partial decimal MsPerCycle { get; set; } = 100;

    [ObservableProperty] public partial string PipelineModeLabel { get; set; } = "Single Cycle";

    // ── Cache config ──────────────────────────────────────────────────────────
    [ObservableProperty] public partial bool ICacheEnabled { get; set; }
    [ObservableProperty] public partial int ICacheCapacityKb { get; set; } = 4;
    [ObservableProperty] public partial int ICacheWays { get; set; } = 4;
    [ObservableProperty] public partial int ICacheBlockBytes { get; set; } = 32;
    [ObservableProperty] public partial int ICacheMissLatency { get; set; } = 10;
    [ObservableProperty] public partial int ICacheTagLatency { get; set; } = 0;
    [ObservableProperty] public partial int ICacheDataLatency { get; set; } = 0;
    [ObservableProperty] public partial string ICacheWritePolicy { get; set; } = "write_through";
    [ObservableProperty] public partial string ICacheWriteMissPolicy { get; set; } = "no_write_allocate";
    [ObservableProperty] public partial int ICacheWbCapacity { get; set; } = 0;
    [ObservableProperty] public partial bool DCacheEnabled { get; set; }
    [ObservableProperty] public partial int DCacheCapacityKb { get; set; } = 4;
    [ObservableProperty] public partial int DCacheWays { get; set; } = 4;
    [ObservableProperty] public partial int DCacheBlockBytes { get; set; } = 32;
    [ObservableProperty] public partial int DCacheMissLatency { get; set; } = 10;
    [ObservableProperty] public partial int DCacheTagLatency { get; set; } = 0;
    [ObservableProperty] public partial int DCacheDataLatency { get; set; } = 0;
    [ObservableProperty] public partial string DCacheWritePolicy { get; set; } = "write_through";
    [ObservableProperty] public partial string DCacheWriteMissPolicy { get; set; } = "no_write_allocate";
    [ObservableProperty] public partial int DCacheWbCapacity { get; set; } = 0;
    [ObservableProperty] public partial bool L2CacheEnabled { get; set; }
    [ObservableProperty] public partial int L2CacheCapacityKb { get; set; } = 256;
    [ObservableProperty] public partial int L2CacheWays { get; set; } = 8;
    [ObservableProperty] public partial int L2CacheBlockBytes { get; set; } = 64;
    [ObservableProperty] public partial int L2CacheMissLatency { get; set; } = 20;
    [ObservableProperty] public partial int L2CacheTagLatency { get; set; } = 0;
    [ObservableProperty] public partial int L2CacheDataLatency { get; set; } = 0;
    [ObservableProperty] public partial string L2CacheWritePolicy { get; set; } = "write_through";
    [ObservableProperty] public partial string L2CacheWriteMissPolicy { get; set; } = "no_write_allocate";
    [ObservableProperty] public partial int L2CacheWbCapacity { get; set; } = 0;
    [ObservableProperty] public partial string CacheReplacementPolicy { get; set; } = "lru";
    [ObservableProperty] public partial string DCachePrefetcher { get; set; } = "none";
    [ObservableProperty] public partial int DCachePrefetcherTableSize { get; set; } = 64;
    [ObservableProperty] public partial int DCachePrefetcherDepth { get; set; } = 8;
    [ObservableProperty] public partial int DCachePrefetchLatency { get; set; } = 0;

    public bool HasDCachePrefetcherTableSize => DCachePrefetcher is "stride" or "stream";
    public bool HasDCachePrefetcherDepth => DCachePrefetcher == "stream";
    public bool HasDCachePrefetcherParams => DCachePrefetcher != "none";

    // ── OoO pipeline parameters ───────────────────────────────────────────────
    [ObservableProperty] public partial int OooIssueWidth { get; set; } = 2;
    [ObservableProperty] public partial int OooRobCapacity { get; set; } = 32;
    [ObservableProperty] public partial int OooIqCapacity { get; set; } = 8;
    [ObservableProperty] public partial int OooExtraPhysRegs { get; set; } = 32;
    [ObservableProperty] public partial bool OooFlatIq { get; set; } = false;
    [ObservableProperty] public partial int OooMshrCapacity { get; set; } = 0;

    public bool IsOooMode => CurrentMode == PipelineMode.OoO;

    // ── Cache display state ───────────────────────────────────────────────────
    [ObservableProperty] public partial int SelectedCacheTab { get; set; }
    [ObservableProperty] public partial string CacheHits { get; set; } = "–";
    [ObservableProperty] public partial string CacheMisses { get; set; } = "–";
    [ObservableProperty] public partial string CacheHitRate { get; set; } = "–";
    [ObservableProperty] public partial string CacheEvictions { get; set; } = "–";
    [ObservableProperty] public partial ulong? CacheLastAddress { get; set; }
    [ObservableProperty] public partial bool CacheLastIsHit { get; set; }
    [ObservableProperty] public partial int CacheTagBits { get; set; }
    [ObservableProperty] public partial int CacheIndexBits { get; set; }
    [ObservableProperty] public partial int CacheOffsetBits { get; set; }

    public static IReadOnlyList<string> PipelineModeLabels { get; } = ["Single Cycle", "5-Stage", "OoO",];
    public static IReadOnlyList<string> OptLevelOptions { get; } = ["-O0", "-O1", "-O2", "-Os",];
    public static IReadOnlyList<int> CacheCapacityKbOptions { get; } = [1, 2, 4, 8, 16, 32,];
    public static IReadOnlyList<int> CacheWaysOptions { get; } = [1, 2, 4, 8,];
    public static IReadOnlyList<int> CacheBlockBytesOptions { get; } = [8, 16, 32, 64,];
    public static IReadOnlyList<int> L2CacheCapacityKbOptions { get; } = [64, 128, 256, 512, 1024, 4096,];
    public static IReadOnlyList<int> L2CacheWaysOptions { get; } = [4, 8, 16,];
    public static IReadOnlyList<int> L2CacheBlockBytesOptions { get; } = [32, 64, 128,];

    public static IReadOnlyList<string> CacheReplacementPolicyOptions { get; } =
        ["lru", "mru", "clock", "fifo", "plru", "random", "srrip", "brrip", "drrip", "ship", "ship_pc", "hawkeye",];

    public static IReadOnlyList<string> DPrefetcherOptions { get; } =
        ["none", "next_line", "stride", "stream", "ipcp", "berti", "pythia", "sms",];

    public static IReadOnlyList<string> WritePolicyOptions { get; } = ["write_through", "write_back",];
    public static IReadOnlyList<string> WriteMissPolicyOptions { get; } = ["no_write_allocate", "write_allocate",];

    public string CacheMetadataLabel => CacheReplacementPolicy switch {
        "srrip" or "brrip" or "drrip" or "ship" or "ship_pc" or "hawkeye" => "RRPV",
        "clock"                                                           => "Ref",
        "random"                                                          => "-",
        _                                                                 => "Age",
    };

    public ObservableCollection<AssemblyRow> Instructions { get; } = [];
    public ObservableCollection<RegEntry> IntRegisters { get; } = [];
    public ObservableCollection<RegEntry> FloatRegisters { get; } = [];
    public ObservableCollection<CacheLineEntry> CacheRows { get; } = [];

    public event Action? CacheUpdated;

    public bool IsPipelineMode => CurrentMode != PipelineMode.SingleCycle;

    public IReadOnlyList<RegFormat> IntFormatOptions { get; } = [
        RegFormat.Hex, RegFormat.DecimalSigned, RegFormat.DecimalUnsigned, RegFormat.Binary,
    ];

    public IReadOnlyList<RegFormat> FloatFormatOptions { get; } = [
        RegFormat.Hex, RegFormat.Float, RegFormat.Binary,
    ];

    public IReadOnlyList<ExtensionToggle> AvailableExtensions { get; } = [
        new("M", RvExtension.M),
        new("A", RvExtension.A),
        new("F", RvExtension.F),
        new("C", RvExtension.C),
        new("V", RvExtension.V),
        new("Zba", RvExtension.Zba),
        new("Zbb", RvExtension.Zbb),
        new("Zbc", RvExtension.Zbc),
        new("Zbs", RvExtension.Zbs),
    ];

    public string GasArchString => AvailableExtensions.Where(t => t.IsEnabled)
                                                      .Aggregate(RvExtension.None, (current, t) => current | t.Flag)
                                                      .ToIsaString();

    private string GasAbi => AvailableExtensions.Any(t => t is { Flag: RvExtension.F, IsEnabled: true, })
        ? "ilp32f"
        : "ilp32";

    private PipelineMode CurrentMode => PipelineModeLabel switch {
        "5-Stage" => PipelineMode.FiveStage,
        "OoO"     => PipelineMode.OoO,
        _         => PipelineMode.SingleCycle,
    };

    private IArchState? ActiveArchState => CurrentMode switch {
        PipelineMode.FiveStage => _fiveStageTrain?.ArchState,
        PipelineMode.OoO       => _oooeTrain?.ArchState,
        _                      => _archState,
    };

    public bool IsDecodeVisible => SelectedInstruction != null;
    public string DecodeTitle => SelectedInstruction is { } r ? $"{r.Offset:X}: {r.HexEncoding}  {r.Mnemonic}" : "";
    public IReadOnlyList<InstrField> DecodeFields => SelectedInstruction?.Fields ?? [];

    public AssemblerViewModel() {
        InitRegisterEntries();
        foreach (ExtensionToggle ext in AvailableExtensions)
            ext.PropertyChanged += (_, _) => {
                OnPropertyChanged(nameof(GasArchString));
                if (Instructions.Count > 0 && !IsAssembling) _ = AssembleCommand.ExecuteAsync(null);
            };
    }

    private void InitRegisterEntries() {
        for (var i = 0; i < 32; i++) IntRegisters.Add(new RegEntry($"x{i}"));
        for (var i = 0; i < 32; i++) FloatRegisters.Add(new RegEntry($"f{i}"));
    }

    partial void OnSelectedInstructionChanged(AssemblyRow? value) {
        OnPropertyChanged(nameof(IsDecodeVisible));
        OnPropertyChanged(nameof(DecodeTitle));
        OnPropertyChanged(nameof(DecodeFields));
    }

    partial void OnIntRegFormatChanged(RegFormat value) => RefreshIntRegisters();

    partial void OnFloatRegFormatChanged(RegFormat value) => RefreshFloatRegisters();

    partial void OnPipelineModeLabelChanged(string value) {
        _runCts?.Cancel();
        if (_binaryData != null) {
            SetupPipeline();
            CanStep = Instructions.Count > 0;
            StatusText = CurrentMode == PipelineMode.SingleCycle
                ? $"PC=0x{_archState?.Pc:X}"
                : "Cycle 0. Press Step to advance.";
        }

        OnPropertyChanged(nameof(IsPipelineMode));
        OnPropertyChanged(nameof(IsOooMode));
    }

    partial void OnOooIssueWidthChanged(int value) => ApplyCacheConfigChange();
    partial void OnOooRobCapacityChanged(int value) => ApplyCacheConfigChange();
    partial void OnOooIqCapacityChanged(int value) => ApplyCacheConfigChange();
    partial void OnOooExtraPhysRegsChanged(int value) => ApplyCacheConfigChange();
    partial void OnOooFlatIqChanged(bool value) => ApplyCacheConfigChange();
    partial void OnOooMshrCapacityChanged(int value) => ApplyCacheConfigChange();

    partial void OnOptLevelChanged(string value) {
        if (IsCMode && Instructions.Count > 0 && !IsAssembling) AssembleCommand.Execute(null);
    }

    partial void OnICacheEnabledChanged(bool value) => ApplyCacheConfigChange();

    partial void OnDCacheEnabledChanged(bool value) => ApplyCacheConfigChange();

    partial void OnL2CacheEnabledChanged(bool value) => ApplyCacheConfigChange();

    partial void OnCacheReplacementPolicyChanged(string value) {
        OnPropertyChanged(nameof(CacheMetadataLabel));
        ApplyCacheConfigChange();
    }

    partial void OnDCachePrefetcherChanged(string value) {
        OnPropertyChanged(nameof(HasDCachePrefetcherTableSize));
        OnPropertyChanged(nameof(HasDCachePrefetcherDepth));
        OnPropertyChanged(nameof(HasDCachePrefetcherParams));
        ApplyCacheConfigChange();
    }

    partial void OnDCachePrefetcherTableSizeChanged(int value) => ApplyCacheConfigChange();
    partial void OnDCachePrefetcherDepthChanged(int value) => ApplyCacheConfigChange();
    partial void OnDCachePrefetchLatencyChanged(int value) => ApplyCacheConfigChange();

    partial void OnSelectedCacheTabChanged(int value) {
        RefreshCacheDisplay();
        CacheUpdated?.Invoke();
    }

    private void ApplyCacheConfigChange() {
        _runCts?.Cancel();
        if (_binaryData != null) {
            SetupPipeline();
            CanStep = Instructions.Count > 0;
            StatusText = CurrentMode == PipelineMode.SingleCycle
                ? $"PC=0x{_archState?.Pc:X}"
                : "Cycle 0. Press Step to advance.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back() {
        _runCts?.Cancel();
        if (CurrentMode == PipelineMode.SingleCycle)
            BackSingleCycle();
        else
            BackPipeline();
    }

    private void BackSingleCycle() {
        int target = _stepCount - 1;
        if (target < 0) return;
        ConsoleOutput = "";
        _memory = BuildFreshMemory();
        _archState = new Rv32ArchState();
        _stepCount = 0;
        for (var i = 0; i < target; i++) {
            if (!CoreStepSingleCycle()) break;
            _stepCount++;
        }

        CanStep = Instructions.Count > 0;
        RefreshAllRegisters();
        UpdateStages();
        FlushUartOutput();
        StatusText = _stepCount == 0
            ? $"PC=0x{_archState.Pc:X}"
            : $"Step {_stepCount}: PC=0x{_archState.Pc:X}";
        BackCommand.NotifyCanExecuteChanged();
    }

    private void BackPipeline() {
        long targetCycle = _currentCycle - 1;
        if (targetCycle < 0) return;
        ConsoleOutput = "";
        SetupPipeline();
        for (long i = 0; i < targetCycle; i++) {
            bool running;
            if (_fiveStageTrain != null) {
                running = _fiveStageTrain.StepCycle();
                _currentCycle = _fiveStageTrain.CurrentTick;
            }
            else if (_oooeTrain != null) {
                running = _oooeTrain.StepCycle();
                _currentCycle = _oooeTrain.CurrentTick;
            }
            else { return; }

            SampleCacheHistory();
            if (!running) break;
        }

        CanStep = Instructions.Count > 0 && _currentCycle < AssemblerViewModel.MaxPipelineCycles;
        UpdateStages();
        RefreshAllRegisters();
        RefreshCacheDisplay();
        FlushUartOutput();
        CacheUpdated?.Invoke();
        StatusText = _currentCycle == 0 ? "Reset. Cycle 0." : $"Cycle {_currentCycle}";
        BackCommand.NotifyCanExecuteChanged();
    }

    private bool CanBack() => CurrentMode == PipelineMode.SingleCycle
        ? _stepCount > 0
        : _currentCycle > 0;

    private bool CoreStepSingleCycle() {
        if (_archState == null || _memory == null) return false;
        ulong pc = _archState.Pc;
        if (pc >= (ulong)_binarySize) return false;
        try {
            ITooth tooth = _decoder.Decode(pc, _memory);
            ExecuteResult result = _executor.Execute(tooth, _archState, _memory);
            result.SideEffect?.Invoke(_archState);
            if (result.RegisterResult.HasValue && tooth.DestinationRegister >= 0)
                _archState.IntegerRegisters.Write(tooth.DestinationRegister, result.RegisterResult.Value);
            if (result.IsHalt) {
                _archState.Pc = pc + (ulong)tooth.SizeBytes;
                return false;
            }

            if (result.Trap != null) return false;
            _archState.Pc = result is { BranchTaken: true, BranchTarget: not null, }
                ? result.BranchTarget.Value
                : pc + (ulong)tooth.SizeBytes;
            return true;
        }
        catch { return false; }
    }

    private IMemory BuildFreshMemory() {
        var flat = new FlatMemory(1 << 20);
        if (_elfBytes != null)
            Rv32ElfLoader.Load(flat, _elfBytes);
        else if (_binaryData != null) flat.Load(0, _binaryData);
        _uartSw = new StringWriter();
        return new PeripheralBus(flat, [(new UartDevice(_uartSw), UartDevice.DefaultBase, UartDevice.RegionSize),]);
    }

    private void FlushUartOutput() {
        string text = _uartSw?.GetStringBuilder().ToString() ?? "";
        if (ConsoleOutput != text) ConsoleOutput = text;
    }

    [RelayCommand]
    private async Task Assemble() {
        HasError = false;
        AssembleError = "";
        IsAssembling = true;
        StatusText = IsCMode ? "Compiling…" : "Assembling…";

        try {
            if (OperatingSystem.IsBrowser()) {
                HasError = true;
                AssembleError = "Assembly requires the native GAS toolchain — not available in browser.";
                StatusText = "Not supported in browser.";
                return;
            }

            string? prefix = FindToolchainPrefix();
            if (prefix == null) {
                HasError = true;
                AssembleError = "riscv32-none-elf-as not found in PATH.\nRun inside the dev shell: nix develop";
                StatusText = "Toolchain not found.";
                return;
            }

            if (IsCMode)
                await CompileC(prefix);
            else
                await AssembleAsm(prefix);
        }
        catch (Exception ex) {
            HasError = true;
            AssembleError = ex.Message;
            StatusText = "Error.";
        }
        finally { IsAssembling = false; }
    }

    [UnsupportedOSPlatform("browser")]
    private async Task AssembleAsm(string prefix) {
        string tmpDir = Path.GetTempPath();
        string asmFile = Path.Combine(tmpDir, "horologium_asm.s");
        string objFile = Path.Combine(tmpDir, "horologium_asm.o");
        string elfFile = Path.Combine(tmpDir, "horologium_asm.elf");
        string binFile = Path.Combine(tmpDir, "horologium_asm.bin");
        string lstFile = Path.Combine(tmpDir, "horologium_asm.lst");

        try {
            await File.WriteAllTextAsync(asmFile, SourceCode);

            (int asExit, _, string asErr) = await RunProcess(
                prefix + "as",
                $"-march={GasArchString} -mabi={GasAbi} -mno-relax -al=\"{lstFile}\" -o \"{objFile}\" \"{asmFile}\""
            );
            if (asExit != 0) {
                HasError = true;
                AssembleError = string.IsNullOrWhiteSpace(asErr)
                    ? $"Assembler exited {asExit}"
                    : AsmErrPrefix.Replace(asErr, "").Trim();
                StatusText = "Assembly failed.";
                return;
            }

            (int ldExit, _, string ldErr) = await RunProcess(
                prefix + "ld",
                $"-Ttext=0x0 --no-relax -o \"{elfFile}\" \"{objFile}\""
            );
            if (ldExit != 0) {
                HasError = true;
                AssembleError = string.IsNullOrWhiteSpace(ldErr)
                    ? $"Linker exited {ldExit}"
                    : ldErr.Trim();
                StatusText = "Linking failed.";
                return;
            }

            byte[]? binary = await ExtractBinary(prefix, elfFile, binFile);
            if (binary == null) return;

            string lstContent = File.Exists(lstFile) ? await File.ReadAllTextAsync(lstFile) : "";
            _pcToLine = ParseListing(lstContent);
            LoadBinary(binary);
        }
        finally {
            TryDelete(asmFile);
            TryDelete(objFile);
            TryDelete(elfFile);
            TryDelete(binFile);
            TryDelete(lstFile);
        }
    }

    // Startup stub for C mode: place the stack at the top of the 1 MiB FlatMemory,
    // run main, then halt. Linked first so _start sits at PC 0.
    private const string CStartStub =
        """
            .text
            .globl _start
        _start:
            li   sp, 0x100000
            call main
            ebreak
        """;

    [UnsupportedOSPlatform("browser")]
    private async Task CompileC(string prefix) {
        string tmpDir = Path.GetTempPath();
        string srcFile = Path.Combine(tmpDir, "horologium_src.c");
        string srcObj = Path.Combine(tmpDir, "horologium_src.o");
        string startFile = Path.Combine(tmpDir, "horologium_start.s");
        string startObj = Path.Combine(tmpDir, "horologium_start.o");
        string elfFile = Path.Combine(tmpDir, "horologium_src.elf");
        string binFile = Path.Combine(tmpDir, "horologium_src.bin");

        try {
            await File.WriteAllTextAsync(srcFile, CSourceCode);
            await File.WriteAllTextAsync(startFile, AssemblerViewModel.CStartStub);

            // -fno-reorder-functions: at -O2/-Os gcc otherwise moves main into
            // .text.startup, which the default linker script places before the
            // stub's .text — dislodging _start from PC 0.
            (int ccExit, _, string ccErr) = await RunProcess(
                prefix + "gcc",
                $"-march={GasArchString} -mabi={GasAbi} {OptLevel} -fno-reorder-functions "
              + $"-g -ffreestanding -nostdlib -mno-relax -c \"{srcFile}\" -o \"{srcObj}\""
            );
            if (ccExit != 0) {
                HasError = true;
                AssembleError = string.IsNullOrWhiteSpace(ccErr)
                    ? $"Compiler exited {ccExit}"
                    : CErrPrefix.Replace(ccErr, "").Trim();
                StatusText = "Compilation failed.";
                return;
            }

            (int asExit, _, string asErr) = await RunProcess(
                prefix + "as",
                $"-march={GasArchString} -mabi={GasAbi} -mno-relax -o \"{startObj}\" \"{startFile}\""
            );
            if (asExit != 0) {
                HasError = true;
                AssembleError = string.IsNullOrWhiteSpace(asErr) ? $"Assembler exited {asExit}" : asErr.Trim();
                StatusText = "Startup stub assembly failed.";
                return;
            }

            (int ldExit, _, string ldErr) = await RunProcess(
                prefix + "ld",
                $"-Ttext=0x0 --no-relax -e _start -o \"{elfFile}\" \"{startObj}\" \"{srcObj}\""
            );
            if (ldExit != 0) {
                HasError = true;
                AssembleError = string.IsNullOrWhiteSpace(ldErr)
                    ? $"Linker exited {ldExit}"
                    : ldErr.Trim();
                StatusText = "Linking failed.";
                return;
            }

            byte[]? binary = await ExtractBinary(prefix, elfFile, binFile);
            if (binary == null) return;

            // The trains always start at PC 0, so the stub must be first in .text.
            var entry = BitConverter.ToUInt32(_elfBytes!, 0x18);
            if (entry != 0) {
                HasError = true;
                AssembleError = $"_start linked at 0x{entry:X}, not 0 — the simulator starts at PC 0.";
                StatusText = "Bad entry point.";
                return;
            }

            (int odExit, string odOut, _) = await RunProcess(prefix + "objdump", $"-d -l \"{elfFile}\"");
            _pcToLine = odExit == 0 ? ParseObjdump(odOut) : [];
            LoadBinary(binary);
        }
        finally {
            TryDelete(srcFile);
            TryDelete(srcObj);
            TryDelete(startFile);
            TryDelete(startObj);
            TryDelete(elfFile);
            TryDelete(binFile);
        }
    }

    /// Runs objcopy to extract .text, reads the ELF + flat binary, and stores the ELF
    /// for memory loading. Returns null (with error state set) on failure.
    [UnsupportedOSPlatform("browser")]
    private async Task<byte[]?> ExtractBinary(string prefix, string elfFile, string binFile) {
        (int cpExit, _, string cpErr) = await RunProcess(
            prefix + "objcopy",
            $"-O binary -j .text \"{elfFile}\" \"{binFile}\""
        );
        if (cpExit != 0) {
            HasError = true;
            AssembleError = string.IsNullOrWhiteSpace(cpErr)
                ? $"objcopy exited {cpExit}"
                : cpErr.Trim();
            StatusText = "Binary extraction failed.";
            return null;
        }

        byte[] elfBytes = await File.ReadAllBytesAsync(elfFile);
        byte[] binary = await File.ReadAllBytesAsync(binFile);
        if (binary.Length == 0) {
            HasError = true;
            AssembleError = "Empty binary — no .text section produced.";
            StatusText = "Assembly produced no code.";
            return null;
        }

        _elfBytes = elfBytes;
        return binary;
    }

    [RelayCommand]
    private void Step() {
        if (!CanStep) return;
        StepOnce();
        if (!IsPipelineMode) return;
        RefreshCacheDisplay();
        CacheUpdated?.Invoke();
    }

    [RelayCommand]
    private async Task Run() {
        if (!CanStep) return;
        if (_runCts != null) await _runCts.CancelAsync();
        _runCts = new CancellationTokenSource();
        CancellationToken token = _runCts.Token;

        const int maxSteps = 1_000_000;
        var delay = (int)MsPerCycle;

        for (var i = 0; i < maxSteps && CanStep && !token.IsCancellationRequested; i++) {
            StepOnce();
            if (!CanStep || token.IsCancellationRequested) break;
            if (delay <= 0) continue;
            if (IsPipelineMode) {
                RefreshCacheDisplay();
                CacheUpdated?.Invoke();
            }

            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) { break; }
        }

        if (IsPipelineMode) {
            RefreshCacheDisplay();
            CacheUpdated?.Invoke();
        }

        if (CanStep && !token.IsCancellationRequested) {
            if (CurrentMode == PipelineMode.SingleCycle && _archState != null)
                StatusText = $"Ran {maxSteps} steps (hit limit). PC=0x{_archState.Pc:X}";
            else
                StatusText = $"Ran {maxSteps} cycles (hit limit). Cycle {_currentCycle}";
        }
    }

    [RelayCommand]
    private void Reset() {
        _runCts?.Cancel();
        ConsoleOutput = "";
        if (CurrentMode == PipelineMode.SingleCycle && _binaryData != null) _memory = BuildFreshMemory();
        if (CurrentMode == PipelineMode.SingleCycle) {
            _archState?.Reset();
            _stepCount = 0;
        }

        if (_binaryData != null) SetupPipeline();
        CanStep = Instructions.Count > 0;
        StatusText = CurrentMode == PipelineMode.SingleCycle ? "Reset. PC=0x0" : "Reset. Cycle 0.";
        BackCommand.NotifyCanExecuteChanged();
    }

    private void StepOnce() {
        switch (CurrentMode) {
            case PipelineMode.SingleCycle: StepSingleCycle(); break;
            case PipelineMode.FiveStage when _fiveStageTrain != null:
            case PipelineMode.OoO when _oooeTrain != null:
                StepPipeline();
                break;
            default: throw new ArgumentOutOfRangeException();
        }

        FlushUartOutput();
    }

    private void StepSingleCycle() {
        if (_archState == null || _memory == null) return;
        ulong pc = _archState.Pc;

        if (pc >= (ulong)_binarySize) {
            CanStep = false;
            StatusText = $"PC 0x{pc:X} past end of program.";
            return;
        }

        try {
            ITooth tooth = _decoder.Decode(pc, _memory);
            ExecuteResult result = _executor.Execute(tooth, _archState, _memory);

            result.SideEffect?.Invoke(_archState);

            if (result.RegisterResult.HasValue && tooth.DestinationRegister >= 0)
                _archState.IntegerRegisters.Write(tooth.DestinationRegister, result.RegisterResult.Value);

            if (result.IsHalt) {
                _archState.Pc = pc + (ulong)tooth.SizeBytes;
                CanStep = false;
                _stepCount++;
                RefreshAllRegisters();
                UpdateStages();
                StatusText = $"Halted at step {_stepCount}.";
                BackCommand.NotifyCanExecuteChanged();
                return;
            }

            if (result.Trap != null) {
                CanStep = false;
                _stepCount++;
                RefreshAllRegisters();
                UpdateStages();
                StatusText = $"Trap at 0x{pc:X}: {result.Trap.Cause}";
                BackCommand.NotifyCanExecuteChanged();
                return;
            }

            if (result is { BranchTaken: true, BranchTarget: not null, })
                _archState.Pc = result.BranchTarget.Value;
            else
                _archState.Pc = pc + (ulong)tooth.SizeBytes;

            _stepCount++;
            RefreshAllRegisters();
            UpdateStages();
            StatusText = $"Step {_stepCount}: PC=0x{_archState.Pc:X}";
            BackCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) {
            CanStep = false;
            StatusText = $"Error at 0x{pc:X}: {ex.Message}";
        }
    }

    private const long MaxPipelineCycles = 500_000;

    private void SampleCacheHistory() {
        SetAssociativeCache? ic = _fiveStageTrain?.ICache ?? _oooeTrain?.ICache;
        SetAssociativeCache? dc = _fiveStageTrain?.DCache ?? _oooeTrain?.DCache;
        if (ic is not null) {
            long total = ic.Hits + ic.Misses;
            if (total > 0) _iCacheHitHistory.Add((_currentCycle, (double)ic.Hits / total));
        }

        if (dc is not null) {
            long total = dc.Hits + dc.Misses;
            if (total > 0) _dCacheHitHistory.Add((_currentCycle, (double)dc.Hits / total));
        }
    }

    private void StepPipeline() {
        bool running;

        if (_fiveStageTrain != null) {
            running = _fiveStageTrain.StepCycle();
            _currentCycle = _fiveStageTrain.CurrentTick;
        }
        else if (_oooeTrain != null) {
            running = _oooeTrain.StepCycle();
            _currentCycle = _oooeTrain.CurrentTick;
        }
        else { return; }

        SampleCacheHistory();
        UpdateStages();
        RefreshAllRegisters();

        if (!running) {
            CanStep = false;
            StatusText = $"Halted at cycle {_currentCycle}.";
        }
        else if (_binarySize > 0 && (ActiveArchState?.Pc ?? 0) >= (ulong)_binarySize) {
            CanStep = false;
            StatusText = $"PC past program end at cycle {_currentCycle}.";
        }
        else if (_currentCycle >= AssemblerViewModel.MaxPipelineCycles) {
            CanStep = false;
            StatusText = $"Stopped at cycle {_currentCycle} (limit reached — add ebreak to terminate).";
        }
        else { StatusText = $"Cycle {_currentCycle}"; }

        BackCommand.NotifyCanExecuteChanged();
    }

    private void LoadBinary(byte[] binary) {
        _binaryData = binary;
        ConsoleOutput = "";
        _memory = BuildFreshMemory();
        _binarySize = binary.Length;
        _archState = new Rv32ArchState();
        _stepCount = 0;

        Instructions.Clear();
        ulong pc = 0;
        while (pc + 1 < (ulong)binary.Length) {
            var lo = BitConverter.ToUInt16(binary, (int)pc);
            uint raw;
            bool compressed;
            if ((lo & 3) != 3) {
                raw = lo;
                compressed = true;
            }
            else if (pc + 3 < (ulong)binary.Length) {
                raw = BitConverter.ToUInt32(binary, (int)pc);
                compressed = false;
            }
            else { break; }

            ITooth tooth;
            string mnemonic;
            try {
                tooth = _decoder.Decode(pc, raw);
                mnemonic = RvDisassembler.Disassemble(((RvInstruction)tooth).Payload, pc);
            }
            catch {
                mnemonic = "???";
                int size = compressed ? 2 : 4;
                Instructions.Add(new AssemblyRow(pc, compressed ? $"{raw:X4}" : $"{raw:X8}", mnemonic, compressed, []));
                pc += (ulong)size;
                continue;
            }

            string hexStr = compressed ? $"{raw:X4}" : $"{raw:X8}";
            IReadOnlyList<InstrField> fields = RvFieldInfo.GetFields(compressed ? raw & 0xFFFF : raw);
            Instructions.Add(new AssemblyRow(pc, hexStr, mnemonic, compressed, fields));
            pc += (ulong)tooth.SizeBytes;
        }

        SetupPipeline();
        CanStep = Instructions.Count > 0;
        StatusText = $"Assembled: {Instructions.Count} instructions, {binary.Length} bytes.";
    }

    private static ReplacementPolicyKind ParseReplacementPolicy(string? s) =>
        s?.ToLowerInvariant() switch {
            "srrip"   => ReplacementPolicyKind.Srrip,
            "brrip"   => ReplacementPolicyKind.Brrip,
            "drrip"   => ReplacementPolicyKind.Drrip,
            "ship"    => ReplacementPolicyKind.Ship,
            "ship_pc" => ReplacementPolicyKind.ShipPc,
            "random"  => ReplacementPolicyKind.Random,
            "fifo"    => ReplacementPolicyKind.Fifo,
            "plru"    => ReplacementPolicyKind.Plru,
            "mru"     => ReplacementPolicyKind.Mru,
            "clock"   => ReplacementPolicyKind.Clock,
            "hawkeye" => ReplacementPolicyKind.Hawkeye,
            _         => ReplacementPolicyKind.Lru,
        };

    private static WritePolicyKind ParseWritePolicy(string s) =>
        s == "write_back" ? WritePolicyKind.WriteBack : WritePolicyKind.WriteThrough;

    private static WriteMissPolicyKind ParseWriteMissPolicy(string s) =>
        s == "write_allocate" ? WriteMissPolicyKind.WriteAllocate : WriteMissPolicyKind.NoWriteAllocate;

    private static MemoryConfig BuildCacheConfig(
        bool enabled,
        int capacityKb,
        int ways,
        int blockBytes,
        int missLatency,
        ReplacementPolicyKind policy = ReplacementPolicyKind.Lru,
        int tagLatency = 0,
        int dataLatency = 0,
        WritePolicyKind writePolicy = WritePolicyKind.WriteThrough,
        WriteMissPolicyKind writeMissPolicy = WriteMissPolicyKind.NoWriteAllocate,
        int wbCapacity = 0
    ) =>
        enabled
            ? new MemoryConfig(capacityKb * 1024, ways, blockBytes, missLatency)
                with {
                    ReplacementPolicy = policy,
                    CacheTagLatency = tagLatency,
                    CacheDataLatency = dataLatency,
                    CacheWritePolicy = writePolicy,
                    CacheWriteMissPolicy = writeMissPolicy,
                    CacheWbCapacity = wbCapacity,
                }
            : MemoryConfig.None;

    private void SetupPipeline() {
        _fiveStageTrain = null;
        _oooeTrain = null;
        _pEventLog.Reset();
        _currentCycle = 0;
        _iCacheHitHistory.Clear();
        _dCacheHitHistory.Clear();

        ReplacementPolicyKind policy = ParseReplacementPolicy(CacheReplacementPolicy);
        MemoryConfig iCfg = BuildCacheConfig(
            ICacheEnabled, ICacheCapacityKb, ICacheWays, ICacheBlockBytes, ICacheMissLatency, policy,
            ICacheTagLatency, ICacheDataLatency,
            ParseWritePolicy(ICacheWritePolicy), ParseWriteMissPolicy(ICacheWriteMissPolicy),
            ICacheWbCapacity
        );
        MemoryConfig dCfg = BuildCacheConfig(
            DCacheEnabled, DCacheCapacityKb, DCacheWays, DCacheBlockBytes, DCacheMissLatency, policy,
            DCacheTagLatency, DCacheDataLatency,
            ParseWritePolicy(DCacheWritePolicy), ParseWriteMissPolicy(DCacheWriteMissPolicy),
            DCacheWbCapacity
        );
        if (L2CacheEnabled) {
            WritePolicyKind l2Wp = ParseWritePolicy(L2CacheWritePolicy);
            WriteMissPolicyKind l2Wmp = ParseWriteMissPolicy(L2CacheWriteMissPolicy);
            iCfg = iCfg with {
                L2CapacityBytes = L2CacheCapacityKb * 1024,
                L2Ways = L2CacheWays,
                L2BlockBytes = L2CacheBlockBytes,
                L2MissLatency = L2CacheMissLatency,
                L2TagLatency = L2CacheTagLatency,
                L2DataLatency = L2CacheDataLatency,
                L2WritePolicy = l2Wp,
                L2WriteMissPolicy = l2Wmp,
                L2WbCapacity = L2CacheWbCapacity,
            };
            dCfg = dCfg with {
                L2CapacityBytes = L2CacheCapacityKb * 1024,
                L2Ways = L2CacheWays,
                L2BlockBytes = L2CacheBlockBytes,
                L2MissLatency = L2CacheMissLatency,
                L2TagLatency = L2CacheTagLatency,
                L2DataLatency = L2CacheDataLatency,
                L2WritePolicy = l2Wp,
                L2WriteMissPolicy = l2Wmp,
                L2WbCapacity = L2CacheWbCapacity,
            };
        }

        PrefetcherKind prefKind = DCachePrefetcher switch {
            "next_line" => PrefetcherKind.NextLine,
            "stride"    => PrefetcherKind.Stride,
            "stream"    => PrefetcherKind.Stream,
            "ipcp"      => PrefetcherKind.Ipcp,
            "pythia"    => PrefetcherKind.Pythia,
            "berti"     => PrefetcherKind.Berti,
            "sms"       => PrefetcherKind.Sms,
            _           => PrefetcherKind.None,
        };
        if (prefKind != PrefetcherKind.None)
            dCfg = dCfg with {
                Prefetcher = prefKind,
                PrefetcherTableSize = DCachePrefetcherTableSize,
                PrefetcherDepth = DCachePrefetcherDepth,
                PrefetchLatency = DCachePrefetchLatency,
            };

        if (dCfg.CacheCapacityBytes > 0 || dCfg.L2CapacityBytes > 0)
            dCfg = dCfg with { UncacheableBase = UartDevice.DefaultBase, UncacheableSize = UartDevice.RegionSize, };

        switch (CurrentMode) {
            case PipelineMode.FiveStage when _binaryData != null: {
                IMemory mem = BuildFreshMemory();
                _fiveStageTrain = new FiveStageTrain(
                    new Rv32Mechanism(), mem,
                    iMemConfig: iCfg, dMemConfig: dCfg, pEventLog: _pEventLog
                );
                _fiveStageTrain.BeginStepping();
                break;
            }
            case PipelineMode.OoO when _binaryData != null: {
                IMemory mem = BuildFreshMemory();
                _oooeTrain = new OooeTrain(
                    new Rv32Mechanism(), mem,
                    iMemConfig: iCfg, dMemConfig: dCfg, pEventLog: _pEventLog,
                    issueWidth: OooIssueWidth,
                    robCapacity: OooRobCapacity,
                    iqCapacity: OooIqCapacity,
                    extraPhysRegs: OooExtraPhysRegs,
                    flatIq: OooFlatIq,
                    mshrCapacity: OooMshrCapacity
                );
                _oooeTrain.BeginStepping();
                break;
            }
            case PipelineMode.SingleCycle:
                _archState?.Reset();
                _stepCount = 0;
                break;
            default: throw new ArgumentOutOfRangeException();
        }

        UpdateStages();
        RefreshAllRegisters();
        RefreshCacheDisplay();
        CacheUpdated?.Invoke();
        BackCommand.NotifyCanExecuteChanged();
    }

    private void UpdateStages() {
        if (CurrentMode == PipelineMode.SingleCycle) {
            ulong pc = _archState?.Pc ?? 0;
            foreach (AssemblyRow row in Instructions) row.Stage = row.Offset == pc ? "PC" : "";
            CurrentSourceLine = _pcToLine.GetValueOrDefault(pc, 0);
        }
        else {
            Dictionary<ulong, string> stageMap = ComputeStages();
            foreach (AssemblyRow row in Instructions) row.Stage = stageMap.GetValueOrDefault(row.Offset, "");
            CurrentSourceLine = 0;
        }
    }

    private Dictionary<ulong, string> ComputeStages() {
        // For each InstrId: find the latest event with Cycle <= _currentCycle
        var latest = new Dictionary<ulong, PEvent>();
        foreach (PEvent ev in _pEventLog.Events) {
            if (ev.Cycle > _currentCycle) continue;
            if (!latest.TryGetValue(ev.InstrId, out PEvent existing) || ev.Cycle > existing.Cycle)
                latest[ev.InstrId] = ev;
        }

        // For each PC: pick the entry with the highest InstrId (most recently fetched iteration)
        var byPc = new Dictionary<ulong, PEvent>();
        foreach (PEvent ev in latest.Values)
            if (!byPc.TryGetValue(ev.Pc, out PEvent existing) || ev.InstrId > existing.InstrId)
                byPc[ev.Pc] = ev;

        // Map to stage labels
        var result = new Dictionary<ulong, string>();
        foreach ((ulong pc, PEvent ev) in byPc) {
            string stage = CurrentMode == PipelineMode.FiveStage
                ? MapFiveStage(ev.Kind, ev.Cycle)
                : MapOoo(ev.Kind, ev.Cycle);
            if (stage != "") result[pc] = stage;
        }

        return result;
    }

    private string MapFiveStage(PEventKind kind, long eventCycle) => kind switch {
        PEventKind.Fetch                                    => "IF",
        PEventKind.FetchStall                               => "IF",
        PEventKind.Decode                                   => "ID",
        PEventKind.Execute when eventCycle == _currentCycle => "EX",
        PEventKind.Execute                                  => "MEM",
        PEventKind.Retire when eventCycle == _currentCycle  => "WB",
        _                                                   => "",
    };

    private string MapOoo(PEventKind kind, long eventCycle) => kind switch {
        PEventKind.Fetch                                   => "IF",
        PEventKind.Rename                                  => "Rn",
        PEventKind.Dispatch                                => "Dis",
        PEventKind.Issue                                   => "Iss",
        PEventKind.Execute                                 => "Ex",
        PEventKind.Retire when eventCycle == _currentCycle => "Ret",
        _                                                  => "",
    };

    private static Dictionary<ulong, int> ParseListing(string text) {
        var map = new Dictionary<ulong, int>();
        foreach (Match m in ListingLineRx.Matches(text))
            if (ulong.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, null, out ulong addr))
                map.TryAdd(addr, int.Parse(m.Groups[1].Value));
        return map;
    }

    /// Parses `objdump -d -l` output: a `path:NN` marker line applies to the
    /// instruction addresses that follow it, until the next marker.
    private static Dictionary<ulong, int> ParseObjdump(string text) {
        var map = new Dictionary<ulong, int>();
        var currentLine = 0;
        foreach (string rawLine in text.Split('\n')) {
            string line = rawLine.TrimEnd();
            Match src = ObjdumpSrcLineRx.Match(line);
            if (src.Success) {
                currentLine = src.Groups["path"].Value.EndsWith("horologium_src.c")
                    ? int.Parse(src.Groups["line"].Value)
                    : 0;
                continue;
            }

            if (currentLine == 0) continue;
            Match insn = ObjdumpInsnRx.Match(line);
            if (insn.Success
             && ulong.TryParse(insn.Groups["addr"].Value, NumberStyles.HexNumber, null, out ulong addr))
                map.TryAdd(addr, currentLine);
        }

        return map;
    }

    private void RefreshAllRegisters() {
        RefreshIntRegisters();
        RefreshFloatRegisters();
    }

    private void RefreshIntRegisters() {
        IArchState? state = ActiveArchState;
        if (state == null) return;
        for (var i = 0; i < 32; i++) {
            IntRegisters[i].Display = FormatInt(state.IntegerRegisters.Read(i));
            IntRegisters[i].Changed = false;
        }
    }

    private void RefreshFloatRegisters() {
        IArchState? state = ActiveArchState;
        if (state == null) return;
        for (var i = 0; i < 32; i++) {
            FloatRegisters[i].Display = FormatFloat(state.IntegerRegisters.Read(i + 32));
            FloatRegisters[i].Changed = false;
        }
    }

    private void RefreshCacheDisplay() {
        SetAssociativeCache? cache = SelectedCacheTab == 0
            ? _fiveStageTrain?.ICache ?? _oooeTrain?.ICache
            : _fiveStageTrain?.DCache ?? _oooeTrain?.DCache;

        if (cache == null) {
            CacheHits = "–";
            CacheMisses = "–";
            CacheHitRate = "–";
            CacheEvictions = "–";
            CacheLastAddress = null;
            CacheTagBits = 0;
            CacheIndexBits = 0;
            CacheOffsetBits = 0;
            CacheRows.Clear();
            return;
        }

        long hits = cache.Hits, misses = cache.Misses, total = hits + misses;
        CacheHits = hits.ToString("N0");
        CacheMisses = misses.ToString("N0");
        CacheHitRate = total > 0 ? $"{100.0 * hits / total:F1}%" : "–";
        CacheEvictions = cache.Evictions.ToString("N0");

        CacheLastAddress = cache.LastAccessAddress;
        CacheLastIsHit = cache.LastAccessWasHit;
        CacheOffsetBits = cache.OffsetBits;
        CacheIndexBits = cache.IndexBits;
        CacheTagBits = 32 - cache.IndexBits - cache.OffsetBits;

        ulong? lastAddr = cache.LastAccessAddress;
        int lastSet = -1;
        ulong lastTag = 0;
        if (lastAddr.HasValue) {
            lastSet = (int)((lastAddr.Value >> cache.OffsetBits) & (uint)((1 << cache.IndexBits) - 1));
            lastTag = lastAddr.Value >> (cache.OffsetBits + cache.IndexBits);
        }

        CacheLine[] snapshot = cache.GetSnapshot();
        CacheRows.Clear();
        foreach (CacheLine line in snapshot) {
            bool isLast = lastSet >= 0 && line.Set == lastSet && line.Valid && line.Tag == lastTag;
            CacheRows.Add(new CacheLineEntry(line, isLast));
        }
    }

    public (double[] X, double[] Y, double[] Ma) GetCacheChartData() {
        List<(long Cycle, double HitRate)> history =
            SelectedCacheTab == 0 ? _iCacheHitHistory : _dCacheHitHistory;
        if (history.Count == 0) return ([], [], []);
        double[] x = history.Select(p => (double)p.Cycle).ToArray();
        double[] y = history.Select(p => p.HitRate * 100.0).ToArray();
        const int window = 20;
        var ma = new double[y.Length];
        for (var i = 0; i < y.Length; i++) {
            int start = Math.Max(0, i - window + 1);
            double sum = 0;
            for (int j = start; j <= i; j++) sum += y[j];
            ma[i] = sum / (i - start + 1);
        }

        return (x, y, ma);
    }

    private string FormatInt(ulong val) => IntRegFormat switch {
        RegFormat.Hex             => $"0x{(uint)val:X8}",
        RegFormat.DecimalSigned   => ((int)(uint)val).ToString(),
        RegFormat.DecimalUnsigned => ((uint)val).ToString(),
        RegFormat.Binary          => Convert.ToString((uint)val, 2).PadLeft(32, '0'),
        _                         => $"0x{(uint)val:X8}",
    };

    private string FormatFloat(ulong val) => FloatRegFormat switch {
        RegFormat.Hex    => $"0x{(uint)val:X8}",
        RegFormat.Float  => BitConverter.UInt32BitsToSingle((uint)val).ToString("G6"),
        RegFormat.Binary => Convert.ToString((uint)val, 2).PadLeft(32, '0'),
        _                => $"0x{(uint)val:X8}",
    };

    [UnsupportedOSPlatform("browser")]
    private static string? FindToolchainPrefix() {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        return (from dir in pathEnv.Split(':')
                let candidate = Path.Combine(dir, "riscv32-none-elf-as")
                where File.Exists(candidate)
                select Path.Combine(dir, "riscv32-none-elf-")).FirstOrDefault();
    }

    [UnsupportedOSPlatform("browser")]
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcess(string exe, string args) {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo(exe, args) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        proc.Start();
        string stdout = await proc.StandardOutput.ReadToEndAsync();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }

    private static void TryDelete(string path) {
        try { File.Delete(path); }
        catch {
            // ignored
        }
    }
}