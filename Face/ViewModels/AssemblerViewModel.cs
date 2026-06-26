using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Face.Models;
using Mechanism;
using RiscV.Decode;
using RiscV.Execute;
using RiscV.Memory;
using RiscV.State;

namespace Face.ViewModels;

public partial class AssemblerViewModel : ObservableObject {
    private readonly RvDecoder _decoder = new();
    private readonly RvExecutor _executor = new();
    private FlatMemory? _memory;
    private RvArchState? _archState;
    private int _binarySize;
    private int _stepCount;

    [ObservableProperty] private string _sourceCode =
        ".text\n.globl _start\n_start:\n    li a0, 10\n    li a1, 32\n    add a2, a0, a1\n";

    [ObservableProperty] private string _assembleError = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _statusText = "Enter assembly and click Assemble.";
    [ObservableProperty] private bool _canStep;
    [ObservableProperty] private bool _isAssembling;

    [ObservableProperty] private AssemblyRow? _selectedInstruction;

    [ObservableProperty] private RegFormat _intRegFormat = RegFormat.Hex;
    [ObservableProperty] private RegFormat _floatRegFormat = RegFormat.Hex;

    public ObservableCollection<AssemblyRow> Instructions { get; } = [];
    public ObservableCollection<RegEntry> IntRegisters { get; } = [];
    public ObservableCollection<RegEntry> FloatRegisters { get; } = [];

    public IReadOnlyList<RegFormat> IntFormatOptions { get; } = [
        RegFormat.Hex, RegFormat.DecimalSigned, RegFormat.DecimalUnsigned, RegFormat.Binary,
    ];

    public IReadOnlyList<RegFormat> FloatFormatOptions { get; } = [
        RegFormat.Hex, RegFormat.Float, RegFormat.Binary,
    ];

    public bool IsDecodeVisible => SelectedInstruction != null;
    public string DecodeTitle => SelectedInstruction is { } r ? $"0x{r.Offset:X}: {r.HexEncoding}  {r.Mnemonic}" : "";
    public IReadOnlyList<InstrField> DecodeFields => SelectedInstruction?.Fields ?? [];

    public AssemblerViewModel() { InitRegisterEntries(); }

    private void InitRegisterEntries() {
        string[] intNames = [
            "zero", "ra", "sp", "gp", "tp", "t0", "t1", "t2",
            "s0", "s1", "a0", "a1", "a2", "a3", "a4", "a5",
            "a6", "a7", "s2", "s3", "s4", "s5", "s6", "s7",
            "s8", "s9", "s10", "s11", "t3", "t4", "t5", "t6",
        ];
        string[] fpNames = [
            "ft0", "ft1", "ft2", "ft3", "ft4", "ft5", "ft6", "ft7",
            "fs0", "fs1", "fa0", "fa1", "fa2", "fa3", "fa4", "fa5",
            "fa6", "fa7", "fs2", "fs3", "fs4", "fs5", "fs6", "fs7",
            "fs8", "fs9", "fs10", "fs11", "ft8", "ft9", "ft10", "ft11",
        ];
        foreach (string n in intNames) IntRegisters.Add(new RegEntry(n));
        foreach (string n in fpNames) FloatRegisters.Add(new RegEntry(n));
    }

    partial void OnSelectedInstructionChanged(AssemblyRow? value) {
        OnPropertyChanged(nameof(IsDecodeVisible));
        OnPropertyChanged(nameof(DecodeTitle));
        OnPropertyChanged(nameof(DecodeFields));
    }

    partial void OnIntRegFormatChanged(RegFormat value) => RefreshIntRegisters();
    partial void OnFloatRegFormatChanged(RegFormat value) => RefreshFloatRegisters();

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
            string binFile = Path.Combine(tmpDir, "horologium_asm.bin");

            try {
                await File.WriteAllTextAsync(asmFile, SourceCode);

                (int asExit, _, string asErr) = await RunProcess(
                    prefix + "as",
                    $"-march=rv32imafcv -mabi=ilp32f -o \"{objFile}\" \"{asmFile}\""
                );
                if (asExit != 0) {
                    HasError = true;
                    AssembleError = string.IsNullOrWhiteSpace(asErr) ? $"Assembler exited {asExit}" : asErr;
                    StatusText = "Assembly failed.";
                    return;
                }

                (int cpExit, _, string cpErr) = await RunProcess(
                    prefix + "objcopy",
                    $"-O binary -j .text \"{objFile}\" \"{binFile}\""
                );
                if (cpExit != 0) {
                    HasError = true;
                    AssembleError = string.IsNullOrWhiteSpace(cpErr) ? $"objcopy exited {cpExit}" : cpErr;
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

                LoadBinary(binary);
            }
            finally {
                TryDelete(asmFile);
                TryDelete(objFile);
                TryDelete(binFile);
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
    private void Run() {
        if (!CanStep) return;
        const int MaxSteps = 10_000;
        for (var i = 0; i < MaxSteps && CanStep; i++) {
            StepOnce();
            if (!CanStep) break;
        }

        if (CanStep && _archState != null) StatusText = $"Ran {MaxSteps} steps (hit limit). PC=0x{_archState.Pc:X}";
    }

    [RelayCommand]
    private void Reset() {
        _archState?.Reset();
        _stepCount = 0;
        UpdateCurrentRow();
        RefreshAllRegisters();
        CanStep = Instructions.Count > 0;
        StatusText = "Reset. PC=0x0";
    }

    private void StepOnce() {
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
                UpdateCurrentRow();
                StatusText = $"Halted at step {_stepCount}.";
                return;
            }

            if (result.Trap != null) {
                CanStep = false;
                _stepCount++;
                RefreshAllRegisters();
                UpdateCurrentRow();
                StatusText = $"Trap at 0x{pc:X}: {result.Trap.Cause}";
                return;
            }

            if (result.BranchTaken && result.BranchTarget.HasValue)
                _archState.Pc = result.BranchTarget.Value;
            else
                _archState.Pc = pc + (ulong)tooth.SizeBytes;

            _stepCount++;
            RefreshAllRegisters();
            UpdateCurrentRow();
            StatusText = $"Step {_stepCount}: PC=0x{_archState.Pc:X}";
        }
        catch (Exception ex) {
            CanStep = false;
            StatusText = $"Error at 0x{pc:X}: {ex.Message}";
        }
    }

    private void LoadBinary(byte[] binary) {
        const int MemSize = 1 << 20;
        _memory = new FlatMemory(MemSize);
        _memory.Load(0, binary);
        _binarySize = binary.Length;
        _archState = new RvArchState();
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
                string hex = compressed ? $"{raw & 0xFFFF:X4}" : $"{raw:X8}";
                Instructions.Add(new AssemblyRow(pc, hex, mnemonic, compressed, []));
                pc += (ulong)size;
                continue;
            }

            string hexStr = compressed ? $"{raw & 0xFFFF:X4}" : $"{raw:X8}";
            IReadOnlyList<InstrField> fields = RvFieldInfo.GetFields(compressed ? raw & 0xFFFF : raw);
            Instructions.Add(new AssemblyRow(pc, hexStr, mnemonic, compressed, fields));
            pc += (ulong)tooth.SizeBytes;
        }

        UpdateCurrentRow();
        RefreshAllRegisters();
        CanStep = Instructions.Count > 0;
        StatusText = $"Assembled: {Instructions.Count} instructions, {binary.Length} bytes.";
    }

    private void UpdateCurrentRow() {
        ulong pc = _archState?.Pc ?? 0;
        foreach (AssemblyRow row in Instructions) row.IsCurrent = row.Offset == pc;
    }

    private void RefreshAllRegisters() {
        RefreshIntRegisters();
        RefreshFloatRegisters();
    }

    private void RefreshIntRegisters() {
        if (_archState == null) return;
        for (var i = 0; i < 32; i++) {
            IntRegisters[i].Display = FormatInt(_archState.IntegerRegisters.Read(i));
            IntRegisters[i].Changed = false;
        }
    }

    private void RefreshFloatRegisters() {
        if (_archState == null) return;
        for (var i = 0; i < 32; i++) {
            FloatRegisters[i].Display = FormatFloat(_archState.IntegerRegisters.Read(i + 32));
            FloatRegisters[i].Changed = false;
        }
    }

    private string FormatInt(ulong val) => IntRegFormat switch {
        RegFormat.Hex             => $"0x{(uint)val:X8}",
        RegFormat.DecimalSigned   => ((int)(uint)val).ToString(),
        RegFormat.DecimalUnsigned => ((uint)val).ToString(),
        RegFormat.Binary          => Convert.ToString((long)(uint)val, 2).PadLeft(32, '0'),
        _                         => $"0x{(uint)val:X8}",
    };

    private string FormatFloat(ulong val) => FloatRegFormat switch {
        RegFormat.Hex    => $"0x{(uint)val:X8}",
        RegFormat.Float  => BitConverter.UInt32BitsToSingle((uint)val).ToString("G6"),
        RegFormat.Binary => Convert.ToString((long)(uint)val, 2).PadLeft(32, '0'),
        _                => $"0x{(uint)val:X8}",
    };

    private static string? FindToolchainPrefix() {
        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathEnv.Split(':')) {
            string candidate = Path.Combine(dir, "riscv32-none-elf-as");
            if (File.Exists(candidate)) return Path.Combine(dir, "riscv32-none-elf-");
        }

        return null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcess(string exe, string args) {
        using var proc = new Process {
            StartInfo = new ProcessStartInfo(exe, args) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        proc.Start();
        string stdout = await proc.StandardOutput.ReadToEndAsync();
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }

    private static void TryDelete(string path) {
        try { File.Delete(path); }
        catch { }
    }
}