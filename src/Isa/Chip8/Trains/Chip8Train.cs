#region

using Mechanism;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;

#endregion

namespace Chip8.Trains;

public sealed class Chip8Train {
    private readonly SingleCycleCore _core;
    private readonly Train _train;

    public Chip8Train(IMechanism mechanism, IMemory memory, ulong entryPoint = 0x200) {
        var esc = new Escapement();
        _train = new Train("single_cycle", esc);
        _core = _train.AddGear(
            new SingleCycleCore("single_cycle_core", _train.Root, esc, mechanism, memory, entryPoint)
        );
        _train.Build();
    }

    public IArchState ArchState => _core.ArchState;

    public bool IsHalted => _train.IsIdle;

    public RevolutionResult Run(long maxTicks = 100_000, long snapshotInterval = 0) =>
        _train.Run(maxTicks, snapshotInterval: snapshotInterval);

    public void BeginInteractive() => _train.BeginStepping();

    public void StepN(int n) {
        for (var i = 0; i < n && !_train.IsIdle; i++) _train.StepCycle();
    }

    public void FinalizeInteractive() => _train.FinishStepping();

    public void Reset() => _train.Reset();

    public string DumpTopology() => _train.DumpTopology();
}

internal class SingleCycleCore(
    string name,
    SimNode parent,
    Escapement esc,
    IMechanism mechanism,
    IMemory memory,
    ulong entryPoint
) : Gear(name, parent, esc) {
    private Counter _cyclesCounter = null!;
    private Histogram _opcodeHistogram = null!;
    private Counter _retiredCounter = null!;

    public IArchState ArchState { get; set; } = mechanism.CreateArchState();

    public override void Initialize() {
        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles elapsed");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _opcodeHistogram = Dials.AddHistogram("opcodes", "Retired instructions by opcode");
        Dials.AddDial(
            "ipc", () =>
                _cyclesCounter.Value == 0 ? 0.0 : _retiredCounter.Value / (double)_cyclesCounter.Value,
            "Instructions per cycle"
        );
    }

    public override void Wind() {
        ArchState.Pc = entryPoint;
        ScheduleNextInstruction();
    }

    private void ScheduleNextInstruction() { Escapement.ScheduleNextTick(ExecuteOneCycle, Phase.Execute); }

    private void ExecuteOneCycle() {
        _cyclesCounter.Increment();

        ulong pc = ArchState.Pc;

        // Fetch & Decode
        ITooth instr;
        try { instr = mechanism.Decoder.Decode(pc, memory); }
        catch (IllegalInstructionException ex) {
            var trap = new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc);
            ulong vector = mechanism.TrapController.RaiseTrap(trap, ArchState);
            ArchState.Pc = vector;
            ScheduleNextInstruction();
            return;
        }

        // Execute
        ExecuteResult result = mechanism.Executor.Execute(instr, ArchState, memory);

        // Writeback
        if (result.RegisterResult.HasValue && instr.DestinationRegister >= 0)
            ArchState.IntegerRegisters.Write(instr.DestinationRegister, result.RegisterResult.Value);

        if (result is { BranchTaken: true, BranchTarget: not null, })
            ArchState.Pc = result.BranchTarget.Value;
        else
            ArchState.Pc = pc + (ulong)instr.SizeBytes;

        Type? instrType = instr.Payload?.GetType();
        if (instrType is not null) _opcodeHistogram.Observe(instrType);
        _retiredCounter.Increment();

        // Halt check
        if (ArchState.Pc == pc && instr.Class == ToothClass.Branch) return;

        ScheduleNextInstruction();
    }
}