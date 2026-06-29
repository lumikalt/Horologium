using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Face.Models;
using Mechanism;
using Orrery.Observation;
using Pipeline;
using RiscV32;
using RiscV32.Config;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.State;

namespace Face.ViewModels;

public partial class AssemblerViewModel : ObservableObject {
    [GeneratedRegex(@"^[^ \t]+horologium_asm\.s:", RegexOptions.Multiline)]
    private static partial Regex AsmErrPrefix { get; }

    [GeneratedRegex(
        @"^\s*(\d+)\s+([0-9a-fA-F]+)\s+[0-9a-fA-F]", RegexOptions.Multiline
    )]
    private static partial Regex ListingLineRx { get; }

    private readonly Rv32Decoder _decoder = new();
    private readonly Rv32Executor _executor = new();
    private FlatMemory? _memory;
    private Rv32ArchState? _archState;
    private byte[]? _binaryData;
    private int _binarySize;
    private int _stepCount;
    private Dictionary<ulong, int> _pcToLine = [];
    private CancellationTokenSource? _runCts;

    // Pipeline stepping
    private readonly PEventLog _pEventLog = new();
    private FiveStageTrain? _fiveStageTrain;
    private OooeTrain? _oooeTrain;
    private long _currentCycle;

    [ObservableProperty]
    public partial string SourceCode { get; set; } =
        """
        j _start

        factorial:
            li   t0, 1
        loop:
            blez a0, done
            mul  t0, t0, a0
            addi a0, a0, -1
            j    loop
        done:
            mv   a0, t0
            ret

        _start:
            li   a0, 5
            call factorial
            ebreak
        """;

    [ObservableProperty] public partial string AssembleError { get; set; } = "";

    [ObservableProperty] public partial bool HasError { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "Enter assembly and click Assemble.";

    [ObservableProperty] public partial bool CanStep { get; set; }

    [ObservableProperty] public partial bool IsAssembling { get; set; }

    [ObservableProperty] public partial AssemblyRow? SelectedInstruction { get; set; }

    [ObservableProperty] public partial int CurrentSourceLine { get; set; } // 1-based; 0 = none

    [ObservableProperty] public partial RegFormat IntRegFormat { get; set; } = RegFormat.Hex;

    [ObservableProperty] public partial RegFormat FloatRegFormat { get; set; } = RegFormat.Hex;

    [ObservableProperty] public partial decimal MsPerCycle { get; set; } = 100;

    [ObservableProperty] public partial string PipelineModeLabel { get; set; } = "Single Cycle";

    public static IReadOnlyList<string> PipelineModeLabels { get; } = ["Single Cycle", "5-Stage", "OoO",];

    public ObservableCollection<AssemblyRow> Instructions { get; } = [];
    public ObservableCollection<RegEntry> IntRegisters { get; } = [];
    public ObservableCollection<RegEntry> FloatRegisters { get; } = [];

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

    public string GasArchString {
        get {
            var flags = RvExtension.None;
            foreach (ExtensionToggle t in AvailableExtensions)
                if (t.IsEnabled)
                    flags |= t.Flag;
            return flags.ToIsaString();
        }
    }

    private RvExtension ActiveExtensions {
        get {
            var flags = RvExtension.None;
            foreach (ExtensionToggle t in AvailableExtensions)
                if (t.IsEnabled)
                    flags |= t.Flag;
            return flags;
        }
    }

    private string GasAbi => AvailableExtensions.Any(t => t.Flag == RvExtension.F && t.IsEnabled)
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
    }

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back() { }

    private static bool CanBack() => false;

    [RelayCommand]
    private async Task Assemble() {
        HasError = false;
        AssembleError = "";
        IsAssembling = true;
        StatusText = "Assembling…";

        try {
            string? prefix = FindToolchainPrefix();
            if (prefix == null) {
                HasError = true;
                AssembleError = "riscv32-none-elf-as not found in PATH.\nRun inside the dev shell: nix develop";
                StatusText = "Toolchain not found.";
                return;
            }

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
                    return;
                }

                byte[] binary = await File.ReadAllBytesAsync(binFile);
                if (binary.Length == 0) {
                    HasError = true;
                    AssembleError = "Empty binary — no .text section produced.";
                    StatusText = "Assembly produced no code.";
                    return;
                }

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
        catch (Exception ex) {
            HasError = true;
            AssembleError = ex.Message;
            StatusText = "Error.";
        }
        finally { IsAssembling = false; }
    }

    [RelayCommand]
    private void Step() {
        if (!CanStep) return;
        StepOnce();
    }

    [RelayCommand]
    private async Task Run() {
        if (!CanStep) return;
        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        CancellationToken token = _runCts.Token;

        const int maxSteps = 10_000;
        var delay = (int)MsPerCycle;

        for (var i = 0; i < maxSteps && CanStep && !token.IsCancellationRequested; i++) {
            StepOnce();
            if (!CanStep || token.IsCancellationRequested) break;
            if (delay > 0)
                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) { break; }
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
        if (CurrentMode == PipelineMode.SingleCycle) {
            _archState?.Reset();
            _stepCount = 0;
        }

        if (_binaryData != null) SetupPipeline();
        CanStep = Instructions.Count > 0;
        StatusText = CurrentMode == PipelineMode.SingleCycle ? "Reset. PC=0x0" : "Reset. Cycle 0.";
    }

    private void StepOnce() {
        switch (CurrentMode) {
            case PipelineMode.SingleCycle: StepSingleCycle(); break;
            case PipelineMode.FiveStage when _fiveStageTrain != null:
            case PipelineMode.OoO when _oooeTrain != null:
                StepPipeline();
                break;
        }
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
                return;
            }

            if (result.Trap != null) {
                CanStep = false;
                _stepCount++;
                RefreshAllRegisters();
                UpdateStages();
                StatusText = $"Trap at 0x{pc:X}: {result.Trap.Cause}";
                return;
            }

            if (result.BranchTaken && result.BranchTarget.HasValue)
                _archState.Pc = result.BranchTarget.Value;
            else
                _archState.Pc = pc + (ulong)tooth.SizeBytes;

            _stepCount++;
            RefreshAllRegisters();
            UpdateStages();
            StatusText = $"Step {_stepCount}: PC=0x{_archState.Pc:X}";
        }
        catch (Exception ex) {
            CanStep = false;
            StatusText = $"Error at 0x{pc:X}: {ex.Message}";
        }
    }

    private const long MaxPipelineCycles = 500_000;

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
    }

    private void LoadBinary(byte[] binary) {
        _binaryData = binary;
        const int memSize = 1 << 20;
        _memory = new FlatMemory(memSize);
        _memory.Load(0, binary);
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

    private void SetupPipeline() {
        _fiveStageTrain = null;
        _oooeTrain = null;
        _pEventLog.Reset();
        _currentCycle = 0;

        switch (CurrentMode) {
            case PipelineMode.FiveStage when _binaryData != null: {
                var mem = new FlatMemory(1 << 20);
                mem.Load(0, _binaryData);
                _fiveStageTrain = new FiveStageTrain(
                    new Rv32Mechanism(extensions: ActiveExtensions), mem,
                    pEventLog: _pEventLog
                );
                _fiveStageTrain.BeginStepping();
                break;
            }
            case PipelineMode.OoO when _binaryData != null: {
                var mem = new FlatMemory(1 << 20);
                mem.Load(0, _binaryData);
                _oooeTrain = new OooeTrain(
                    new Rv32Mechanism(extensions: ActiveExtensions), mem,
                    pEventLog: _pEventLog
                );
                _oooeTrain.BeginStepping();
                break;
            }
            case PipelineMode.SingleCycle:
                _archState?.Reset();
                _stepCount = 0;
                break;
        }

        UpdateStages();
        RefreshAllRegisters();
    }

    private void UpdateStages() {
        if (CurrentMode == PipelineMode.SingleCycle) {
            ulong pc = _archState?.Pc ?? 0;
            foreach (AssemblyRow row in Instructions) row.Stage = row.Offset == pc ? "PC" : "";
            CurrentSourceLine = _pcToLine.TryGetValue(pc, out int line) ? line : 0;
        }
        else {
            Dictionary<ulong, string> stageMap = ComputeStages();
            foreach (AssemblyRow row in Instructions)
                row.Stage = stageMap.TryGetValue(row.Offset, out string? stage) ? stage : "";
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
        PEventKind.Dispatch                                => "Dis",
        PEventKind.Issue                                   => "Iss",
        PEventKind.Execute                                 => "Ex",
        PEventKind.Retire when eventCycle == _currentCycle => "Ret",
        _                                                  => "",
    };

    private static Dictionary<ulong, int> ParseListing(string text) {
        var map = new Dictionary<ulong, int>();
        foreach (Match m in ListingLineRx.Matches(text))
            if (ulong.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
                map.TryAdd(addr, int.Parse(m.Groups[1].Value));
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

    private static string? FindToolchainPrefix() {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        return (from dir in pathEnv.Split(':')
                let candidate = Path.Combine(dir, "riscv32-none-elf-as")
                where File.Exists(candidate)
                select Path.Combine(dir, "riscv32-none-elf-")).FirstOrDefault();
    }

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