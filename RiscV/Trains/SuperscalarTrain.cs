using JetBrains.Annotations;
using Mechanism;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;
using RiscV.Decode;
using RiscV.State;

namespace RiscV.Trains;

// ── Public wrapper ─────────────────────────────────────────────────────────────

/// <summary>
/// Superscalar in-order Train: issues up to <c>issueWidth</c> instructions per
/// cycle, executing them sequentially so intra-group RAW dependencies resolve
/// naturally without any hazard detection logic.
///
/// There is no speculation across branches — the issue group stops at any
/// branch or jump, paying a "group-cutoff" penalty instead of a flush penalty.
/// This makes it straightforward to compare against <see cref="OooeTrain"/>:
/// same issue width, same branch predictor absence, purely in-order semantics.
/// </summary>
public sealed class SuperscalarTrain {
    private readonly Train _train;
    private readonly SuperscalarCore _core;

    public IArchState ArchState => _core.ArchState;

    public SuperscalarTrain(
        IMechanism mechanism,
        IMemory memory,
        ulong entryPoint = 0,
        int issueWidth = 2
    ) {
        var esc = new Escapement();
        _train = new Train("superscalar", esc);
        _core = _train.AddGear(
            new SuperscalarCore(
                "pipeline", _train.Root, esc, mechanism, memory, entryPoint, issueWidth
            )
        );
        _train.Build();
    }

    public RevolutionResult Run(long maxTicks = 1_000_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);
}

// ── Pipeline core Gear ─────────────────────────────────────────────────────────

/// <summary>
/// The superscalar core Gear. Each tick it issues up to <c>issueWidth</c>
/// instructions in program order.
///
/// Stalls are counted as cycles where the group ran shorter than the issue
/// width (due to a branch, halt, or memory fault cutting the group short).
/// branch_misses is always zero because there is no speculative fetch.
/// </summary>
internal sealed class SuperscalarCore(
    string name,
    SimNode parent,
    Escapement esc,
    IMechanism mechanism,
    IMemory memory,
    ulong entryPoint,
    int issueWidth
) : Gear(name, parent, esc) {
    private Counter _cyclesCounter = null!;
    private Counter _retiredCounter = null!;
    private Counter _stallsCounter = null!;
    [UsedImplicitly] private Counter _branchMissCounter = null!;

    public IArchState ArchState { get; } = mechanism.CreateArchState();

    public override void Initialize() {
        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired");
        _stallsCounter = Dials.AddCounter("stalls", "Cycles where issue group < issueWidth");
        _branchMissCounter = Dials.AddCounter("branch_misses", "Branch mispredictions (0: no speculation)");

        Dials.AddDial(
            "cpi",
            () => _retiredCounter.Value == 0 ? 0.0 : _cyclesCounter.Value / (double)_retiredCounter.Value,
            "Cycles per instruction"
        );
        Dials.AddDial(
            "ipc",
            () => _cyclesCounter.Value == 0 ? 0.0 : _retiredCounter.Value / (double)_cyclesCounter.Value,
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
        Escapement.ScheduleNextTick(RunCycle, Phase.Execute);
    }

    private void RunCycle() {
        _cyclesCounter.Increment();

        var issued = 0;
        var halt = false;

        while (issued < issueWidth) {
            ulong pc = ArchState.Pc;

            // Fetch & Decode
            ITooth instr;
            try { instr = mechanism.Decoder.Decode(pc, memory); }
            catch (IllegalInstructionException ex) {
                var trap = new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc);
                ArchState.Pc = mechanism.TrapController.RaiseTrap(trap, ArchState);
                break; // end group after trap
            }

            // Execute
            ExecuteResult result = mechanism.Executor.Execute(instr, ArchState, memory);
            issued++;
            _retiredCounter.Increment();

            // EBREAK halts the simulation
            if (instr.Payload is RvEbreak) {
                halt = true;
                break;
            }

            // Other trap (e.g. MRET, misaligned access)
            if (result.HasTrap) {
                ulong target = instr.Payload is RvMret
                    ? mechanism.TrapController.ReturnFromTrap(PrivilegeLevel.Machine, ArchState)
                    : mechanism.TrapController.RaiseTrap(result.Trap!, ArchState);
                ArchState.Pc = target;
                break; // end group after trap redirect
            }

            // Writeback
            if (result.RegisterResult.HasValue && instr.DestinationRegister >= 0)
                ArchState.IntegerRegisters.Write(instr.DestinationRegister, result.RegisterResult.Value);

            // PC update
            if (result is { BranchTaken: true, BranchTarget: not null, })
                ArchState.Pc = result.BranchTarget.Value;
            else
                ArchState.Pc = pc + (ulong)instr.SizeBytes;

            // Halt on infinite self-loop (JAL x0, 0 — common bare-metal halt idiom)
            if (ArchState.Pc == pc && instr.Class == ToothClass.Branch) {
                halt = true;
                break;
            }

            // Stop the issue group at any branch — no speculative fetch past control flow.
            if (instr.Class is ToothClass.Branch or ToothClass.ConditionalBranch) break;
        }

        // A cycle where the group ran short counts as a stall cycle.
        if (issued < issueWidth) _stallsCounter.Increment();

        if (!halt) Escapement.ScheduleNextTick(RunCycle, Phase.Execute);
    }
}