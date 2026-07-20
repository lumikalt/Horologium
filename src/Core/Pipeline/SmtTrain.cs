#region

using JetBrains.Annotations;
using Mechanism;
using Mechanism.SmtFetchPolicies;
using Orrery.Cache;
using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;

#endregion

namespace Pipeline;

// ── Public wrapper ─────────────────────────────────────────────────────────────

/// <summary>
///     Simultaneous Multi-Threading (barrel-processor) Train: N independent hart
///     contexts share a single issue window of width <c>issueWidth</c>.  Each tick
///     the coordinator distributes available issue slots across active harts using a
///     pluggable <see cref="ISmtFetchPolicy" /> — <see cref="RoundRobinFetchPolicy" /> by
///     default, or <see cref="IcountFetchPolicy" /> for Tullsen et al.'s ICOUNT policy.
///     <para>
///         All harts share the same Escapement and therefore advance in lock-step.
///         Each hart has its own <see cref="IArchState" /> and <see cref="MemoryLayers" />
///         (typically backed by per-hart caches sharing a <c>MoesifBus</c>), making MOESIF
///         coherence effects observable at instruction granularity.
///     </para>
///     <para>
///         Usage:
///         <code>
///   var smt = new SmtTrain(
///       new[] { new Rv32Mechanism(), new Rv32Mechanism() },
///       new IMemory[] { cache0, cache1 },
///       entryPoints: new ulong[] { 0x00, 0x40 },
///       issueWidth: 2
///   );
///   smt.Run(1_000_000);
/// </code>
///     </para>
/// </summary>
public sealed class SmtTrain : ISteppableTrain {
    private readonly SmtCore _core;
    private readonly Train _train;

    public SmtTrain(
        IMechanism[] mechanisms,
        IMemory[] perHartMemory,
        ulong[]? entryPoints = null,
        int issueWidth = 2,
        ISmtFetchPolicy? fetchPolicy = null
    ) {
        ArgumentNullException.ThrowIfNull(mechanisms);
        ArgumentNullException.ThrowIfNull(perHartMemory);
        if (mechanisms.Length == 0) throw new ArgumentException("At least one hart required.", nameof(mechanisms));
        if (perHartMemory.Length != mechanisms.Length)
            throw new ArgumentException("perHartMemory length must match mechanisms.", nameof(perHartMemory));

        ulong[] eps = entryPoints ?? new ulong[mechanisms.Length];
        if (eps.Length != mechanisms.Length)
            throw new ArgumentException("entryPoints length must match mechanisms.", nameof(entryPoints));

        var esc = new Escapement();
        _train = new Train("smt", esc);
        _core = _train.AddGear(
            new SmtCore(
                "pipeline", _train.Root, esc, mechanisms, perHartMemory, eps, issueWidth,
                fetchPolicy ?? new RoundRobinFetchPolicy()
            )
        );
        _train.Build();
    }

    public int HartCount => _core.HartCount;

    public bool IsIdle => _train.IsIdle;

    public RevolutionResult Run(long maxTicks = 1_000_000, long warmupTicks = 0, long snapshotInterval = 0) =>
        _train.Run(maxTicks, warmupTicks, snapshotInterval);

    public void BeginStepping() => _train.BeginStepping();
    public bool StepCycle() => _train.StepCycle();
    public RevolutionResult FinishStepping() => _train.FinishStepping();

    public IArchState StateOf(int hartId) => _core.StateOf(hartId);
}

// ── Per-hart context ───────────────────────────────────────────────────────────

internal sealed class HartContext {
    public readonly IArchState ArchState;
    public readonly MemoryLayers DLayers;
    public readonly ulong EntryPoint;
    public readonly MemoryLayers ILayers;
    public readonly IMechanism Mechanism;
    public IFetchTranslator? FetchTranslator;
    public bool Halted;

    public HartContext(IMechanism mechanism, IMemory memory, ulong entryPoint) {
        Mechanism = mechanism;
        ArchState = mechanism.CreateArchState();
        ILayers = MemoryLayers.Build(memory, MemoryConfig.None);
        DLayers = MemoryLayers.Build(memory, MemoryConfig.None);
        EntryPoint = entryPoint;
    }
}

// ── Pipeline core Gear ─────────────────────────────────────────────────────────

/// <summary>
///     The SMT core Gear. Each tick it distributes up to <c>issueWidth</c> issue slots
///     across the N hart contexts via <c>fetchPolicy</c>, skipping halted harts
///     and harts that have been blocked by a branch or halt within the current cycle.
/// </summary>
internal sealed class SmtCore(
    string name,
    SimNode parent,
    Escapement esc,
    IMechanism[] mechanisms,
    IMemory[] memories,
    ulong[] entryPoints,
    int issueWidth,
    ISmtFetchPolicy fetchPolicy
) : Gear(name, parent, esc) {
    private readonly HartContext[] _harts = CreateHarts(mechanisms, memories, entryPoints);
    [UsedImplicitly] private Counter _branchMissCounter = null!;

    private Counter _cyclesCounter = null!;
    private Counter _retiredCounter = null!;

    private Action? _runCycle;
    private Counter _stallsCounter = null!;

    public int HartCount => _harts.Length;
    public IArchState StateOf(int i) => _harts[i].ArchState;

    private static HartContext[] CreateHarts(IMechanism[] mechs, IMemory[] mems, ulong[] eps) {
        var harts = new HartContext[mechs.Length];
        for (var i = 0; i < mechs.Length; i++) harts[i] = new HartContext(mechs[i], mems[i], eps[i]);
        return harts;
    }

    public override void Initialize() {
        foreach (HartContext ctx in _harts)
            ctx.FetchTranslator = ctx.Mechanism.CreateFetchTranslator(ctx.ArchState, ctx.ILayers.Accessor);

        _cyclesCounter = Dials.AddCounter("cycles", "Total cycles");
        _retiredCounter = Dials.AddCounter("retired", "Instructions retired (aggregate)");
        _stallsCounter = Dials.AddCounter("stalls", "Cycles where issued < issueWidth or cache miss");
        _branchMissCounter = Dials.AddCounter("branch_misses", "Branch mispredictions (0: no speculation)");
        Dials.AddDial(
            "ipc",
            () => _cyclesCounter.Value == 0 ? 0.0 : _retiredCounter.Value / (double)_cyclesCounter.Value,
            "Instructions per cycle (aggregate)"
        );
        Dials.AddDial(
            "cpi",
            () => _retiredCounter.Value == 0 ? 0.0 : _cyclesCounter.Value / (double)_retiredCounter.Value,
            "Cycles per instruction (aggregate)"
        );
    }

    public override void Reset() {
        base.Reset();
        foreach (HartContext ctx in _harts) {
            ctx.ArchState.Reset();
            ctx.ArchState.Pc = ctx.EntryPoint;
            ctx.Halted = false;
        }
    }

    public override void Wind() {
        foreach (HartContext ctx in _harts) ctx.ArchState.Pc = ctx.EntryPoint;
        Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Execute);
    }

    private void RunCycle() {
        int n = _harts.Length;
        var available = new bool[n];
        var issuedThisCycle = new bool[n];
        for (var i = 0; i < n; i++) available[i] = !_harts[i].Halted;

        var issued = 0;
        long cacheStalls = 0;

        fetchPolicy.BeginCycle(n);

        while (issued < issueWidth) {
            int found = fetchPolicy.SelectHart(available);
            if (found < 0) break;

            HartContext ctx = _harts[found];
            bool cut = IssueOne(ctx);
            issuedThisCycle[found] = true;

            long hartStalls = ctx.ILayers.ConsumeAllStalls() + ctx.DLayers.ConsumeAllStalls();
            cacheStalls += hartStalls;
            fetchPolicy.OnIssued(found, hartStalls);

            available[found] = !cut;
            issued++;
        }

        // Drain accumulated cache stall penalties from harts that didn't get an issue slot.
        for (var i = 0; i < n; i++)
            if (!issuedThisCycle[i])
                cacheStalls += _harts[i].ILayers.ConsumeAllStalls() + _harts[i].DLayers.ConsumeAllStalls();

        // Per-hart OnCycle advances each cycle CSR — self-timing workloads (rdcycle
        // calibration loops) never terminate without it.
        _cyclesCounter.Increment();
        foreach (HartContext ctx in _harts) ctx.ArchState.OnCycle();
        if (cacheStalls > 0) {
            _stallsCounter.IncrementBy(cacheStalls);
            _cyclesCounter.IncrementBy(cacheStalls);
            for (long i = 0; i < cacheStalls; i++)
                foreach (HartContext ctx in _harts)
                    ctx.ArchState.OnCycle();
        }

        if (issued < issueWidth) _stallsCounter.Increment();

        var anyActive = false;
        foreach (HartContext ctx in _harts)
            if (!ctx.Halted) {
                anyActive = true;
                break;
            }

        if (anyActive) Escapement.ScheduleNextTick(_runCycle ??= RunCycle, Phase.Execute);
    }

    // Issues one instruction for the given hart.
    // Returns true if the hart should be blocked for the rest of this cycle
    // (branch taken/not-taken, halt, trap, MRET/SRET, or self-loop detected).
    private bool IssueOne(HartContext ctx) {
        ulong pc = ctx.ArchState.Pc;

        ITooth instr;
        if (ctx.FetchTranslator is not null) {
            (ulong physPc, int faultCause) = ctx.FetchTranslator.Translate(pc);
            if (faultCause != 0) {
                ctx.ArchState.Pc = ctx.Mechanism.TrapController.RaiseTrap(
                    new TrapInfo(faultCause, pc, pc), ctx.ArchState
                );
                return true;
            }

            try {
                var raw = (uint)ctx.ILayers.Accessor.Read(physPc, 4);
                instr = ctx.Mechanism.Decoder.Decode(pc, raw);
            }
            catch (IllegalInstructionException ex) {
                ctx.ArchState.Pc = ctx.Mechanism.TrapController.RaiseTrap(
                    new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc), ctx.ArchState
                );
                return true;
            }
        }
        else {
            try { instr = ctx.Mechanism.Decoder.Decode(pc, ctx.ILayers.Accessor); }
            catch (IllegalInstructionException ex) {
                ctx.ArchState.Pc = ctx.Mechanism.TrapController.RaiseTrap(
                    new TrapInfo(TrapCause.IllegalInstruction, ex.Encoding, pc), ctx.ArchState
                );
                return true;
            }
        }

        ctx.DLayers.Accessor.SetRequestPc(pc);
        ExecuteResult result = ctx.Mechanism.Executor.Execute(instr, ctx.ArchState, ctx.DLayers.Accessor);
        _retiredCounter.Increment();

        if (result.IsHalt || result.RequestHalt) {
            ctx.Halted = true;
            return true;
        }

        if (result.HasTrap) {
            ctx.ArchState.Pc = ctx.Mechanism.TrapController.RaiseTrap(result.Trap!, ctx.ArchState);
            return true;
        }

        if (result.IsReturnFromTrap) {
            ctx.ArchState.Pc = ctx.Mechanism.TrapController.ReturnFromTrap(
                result.ReturnPrivilege!.Value, ctx.ArchState
            );
            return true;
        }

        result.SideEffect?.Invoke(ctx.ArchState);
        if (result.RegisterResult.HasValue && instr.DestinationRegister >= 0)
            ctx.ArchState.IntegerRegisters.Write(instr.DestinationRegister, result.RegisterResult.Value);

        if (result is { BranchTaken: true, BranchTarget: not null, })
            ctx.ArchState.Pc = result.BranchTarget.Value;
        else
            ctx.ArchState.Pc = pc + (ulong)instr.SizeBytes;

        // Self-loop halt detection (e.g. JAL x0, 0)
        if (ctx.ArchState.Pc == pc && instr.Class == ToothClass.Branch) {
            ctx.Halted = true;
            return true;
        }

        // Branch/jump cuts this hart's contribution to the current cycle group.
        if (instr.Class is ToothClass.Branch or ToothClass.ConditionalBranch) return true;

        // Check for pending interrupts after a normal retire.
        TrapInfo? interrupt = ctx.Mechanism.TrapController.PeekInterrupt(ctx.ArchState);
        if (interrupt is not null) ctx.ArchState.Pc = ctx.Mechanism.TrapController.RaiseTrap(interrupt, ctx.ArchState);

        return false;
    }
}