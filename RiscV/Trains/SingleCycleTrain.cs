using Mechanism;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;
using RiscV.Decode;
using RiscV.State;

namespace RiscV.Trains;

/// <summary>
/// The simplest possible Train: one Gear that fetches, decodes, executes,
/// and writes back one instruction per tick. No pipeline, no hazards.
/// Used to validate the Mechanism before any pipeline complexity is added.
/// </summary>
public sealed class SingleCycleTrain {
    private readonly Train _train;
    private readonly SingleCycleCore _core;

    public IArchState ArchState => _core.ArchState;

    public SingleCycleTrain(IMechanism mechanism, IMemory memory, ulong entryPoint = 0) {
        var esc = new Escapement();
        _train = new Train("single_cycle", esc);
        _core = _train.AddGear(
            new SingleCycleCore(
                "core", _train.Root, esc, mechanism, memory, entryPoint
            )
        );
        _train.Build();
    }

    public RevolutionResult Run(long maxTicks = 100_000) =>
        _train.Run(maxTicks);

    public string DumpTopology() => _train.DumpTopology();
}

/// <summary>
/// The single-cycle core Gear. Each tick: fetch → decode → execute → writeback.
/// Stops scheduling when it encounters a halt condition (infinite loop to self,
/// or explicit EBREAK).
/// </summary>
internal sealed class SingleCycleCore(
    string name,
    SimNode parent,
    Escapement esc,
    IMechanism mechanism,
    IMemory memory,
    ulong entryPoint
)
    : Gear(name, parent, esc) {
    private Counter _cyclesCounter = null!;
    private Counter _retiredCounter = null!;
    private Histogram _opcodeHistogram = null!;

    public IArchState ArchState { get; } = mechanism.CreateArchState();

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

    public override void Reset() {
        base.Reset();
        ArchState.Pc = entryPoint;
        ((RvArchState)ArchState).Reset();
        ArchState.Pc = entryPoint;
    }

    public override void Wind() {
        ArchState.Pc = entryPoint;
        ScheduleNextInstruction();
    }

    private void ScheduleNextInstruction() { Escapement.ScheduleNextTick(ExecuteOneCycle, Phase.Execute); }

    private void ExecuteOneCycle() {
        ulong pc = ArchState.Pc;

        // Fetch & Decode
        ITooth instr;
        try { instr = mechanism.Decoder.Decode(pc, memory); }
        catch (IllegalInstructionException ex) {
            _cyclesCounter.Increment();
            var trap = new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc);
            ulong vector = mechanism.TrapController.RaiseTrap(trap, ArchState);
            ArchState.Pc = vector;
            ScheduleNextInstruction();
            return;
        }

        // Execute
        ExecuteResult result = mechanism.Executor.Execute(instr, ArchState, memory);

        // EBREAK halts the simulation without consuming a cycle or retiring.
        if (result.HasTrap && instr.Payload is RvEbreak) return;

        _cyclesCounter.Increment();

        // Writeback
        if (result.HasTrap) {
            switch (instr.Payload) {
                case RvMret: {
                    ulong ret = mechanism.TrapController.ReturnFromTrap(
                        PrivilegeLevel.Machine, ArchState
                    );
                    ArchState.Pc = ret;
                    break;
                }
                default: {
                    ulong vector = mechanism.TrapController.RaiseTrap(result.Trap!, ArchState);
                    ArchState.Pc = vector;
                    break;
                }
            }
        }
        else {
            // Write register result
            if (result.RegisterResult.HasValue && instr.DestinationRegister >= 0)
                ArchState.IntegerRegisters.Write(
                    instr.DestinationRegister, result.RegisterResult.Value
                );

            // Update PC
            if (result is { BranchTaken: true, BranchTarget: not null, })
                ArchState.Pc = result.BranchTarget.Value;
            else
                ArchState.Pc = pc + (ulong)instr.SizeBytes;
        }

        _opcodeHistogram.Observe(instr.Payload?.GetType().Name ?? "unknown");
        _retiredCounter.Increment();

        // Detect halt: infinite self-loop (JAL x0, 0 — common halt idiom)
        if (ArchState.Pc == pc && instr.Class == ToothClass.Branch) return; // stop scheduling — we're halted

        ScheduleNextInstruction();
    }
}