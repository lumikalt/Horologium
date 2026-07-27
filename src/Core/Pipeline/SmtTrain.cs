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

    // Top-Down Microarchitecture Analysis slot accounting (Yasin, ISPASS 2014). Each cycle
    // contributes issueWidth slots, shared across harts; there is no branch speculation in
    // this in-order barrel design (each hart resolves its own PC synchronously), so Bad
    // Speculation stays at zero — a correct reading, not a gap.
    private Counter _tdExecStallCyclesCounter = null!;
    private Counter _tdFetchBubblesCounter = null!;
    private Counter _tdFetchLatencyCyclesCounter = null!;
    private Counter _tdMemStallLoadCyclesCounter = null!;
    private Counter _tdMemStallStoreCyclesCounter = null!;
    private Counter _tdSlotsIssuedCounter = null!;
    private Counter _tdTotalSlotsCounter = null!;

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

        // ── Top-Down Microarchitecture Analysis (Yasin, ISPASS 2014) ──────────────
        TopDownCounters td = TopDownBreakdown.RegisterCounters(Dials, ComputeTopDown);
        _tdTotalSlotsCounter = td.TotalSlots;
        _tdSlotsIssuedCounter = td.SlotsIssued;
        _tdFetchBubblesCounter = td.FetchBubbles;
        _tdFetchLatencyCyclesCounter = td.FetchLatencyCycles;
        _tdExecStallCyclesCounter = td.ExecStallCycles;
        _tdMemStallLoadCyclesCounter = td.MemStallLoadCycles;
        _tdMemStallStoreCyclesCounter = td.MemStallStoreCycles;
    }

    private TopDownBreakdown ComputeTopDown() =>
        TopDownBreakdown.Compute(
            _tdTotalSlotsCounter.Value,
            _tdSlotsIssuedCounter.Value,
            _retiredCounter.Value,
            _tdFetchBubblesCounter.Value,
            0, // No recovery bubbles: no speculative rollback exists in this in-order design.
            _cyclesCounter.Value,
            _tdFetchLatencyCyclesCounter.Value,
            _tdExecStallCyclesCounter.Value,
            _tdMemStallLoadCyclesCounter.Value,
            _tdMemStallStoreCyclesCounter.Value,
            0, // No branch speculation: each hart resolves its own PC synchronously in-order.
            0
        );

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
        long iStallsTotal = 0, dStallsTotal = 0;

        fetchPolicy.BeginCycle(n);

        while (issued < issueWidth) {
            int found = fetchPolicy.SelectHart(available);
            if (found < 0) break;

            HartContext ctx = _harts[found];
            bool cut = IssueOne(ctx);
            issuedThisCycle[found] = true;

            long iStalls = ctx.ILayers.ConsumeAllStalls();
            long dStalls = ctx.DLayers.ConsumeAllStalls();
            iStallsTotal += iStalls;
            dStallsTotal += dStalls;
            fetchPolicy.OnIssued(found, iStalls + dStalls);

            available[found] = !cut;
            issued++;
        }

        // Drain accumulated cache stall penalties from harts that didn't get an issue slot.
        for (var i = 0; i < n; i++)
            if (!issuedThisCycle[i]) {
                iStallsTotal += _harts[i].ILayers.ConsumeAllStalls();
                dStallsTotal += _harts[i].DLayers.ConsumeAllStalls();
            }

        long cacheStalls = iStallsTotal + dStallsTotal;

        // TMA: split by side so an I-side miss attributes to Frontend Latency Bound and a
        // D-side miss to Backend Memory Bound, mirroring OooTrain/CprTrain's DrainStalls.
        // Each hart has independent I/D memory layers, so this sums misses across harts —
        // the lump-sum stall model already freezes the whole cycle group for their combined
        // penalty (see below), so the attribution is consistent with that simplification.
        if (iStallsTotal > 0) {
            _tdFetchBubblesCounter.IncrementBy(iStallsTotal * issueWidth);
            _tdFetchLatencyCyclesCounter.IncrementBy(iStallsTotal);
        }

        if (dStallsTotal > 0) {
            _tdExecStallCyclesCounter.IncrementBy(dStallsTotal);
            // No store buffer and no per-load in-flight tracking in this in-order model (like
            // DaeTrain), so a D-side miss cannot be split into load vs. store; credited to
            // MemStallLoad since a stalling load is the common case.
            _tdMemStallLoadCyclesCounter.IncrementBy(dStallsTotal);
        }

        // Per-hart OnCycle advances each cycle CSR — self-timing workloads (rdcycle
        // calibration loops) never terminate without it.
        _cyclesCounter.Increment();
        _tdTotalSlotsCounter.IncrementBy(issueWidth);
        _tdSlotsIssuedCounter.IncrementBy(issued);
        foreach (HartContext ctx in _harts) ctx.ArchState.OnCycle();
        if (cacheStalls > 0) {
            _stallsCounter.IncrementBy(cacheStalls);
            _cyclesCounter.IncrementBy(cacheStalls);
            _tdTotalSlotsCounter.IncrementBy(cacheStalls * issueWidth);
            for (long i = 0; i < cacheStalls; i++)
                foreach (HartContext ctx in _harts)
                    ctx.ArchState.OnCycle();
        }

        if (issued < issueWidth) {
            _stallsCounter.Increment();
            // TMA: fewer harts issued than there were issue slots. There is no ROB/IQ-style
            // backend resource in this in-order design to structurally block dispatch, so an
            // underfilled cycle always reads as Frontend Bound — thread starvation (too few
            // runnable harts, or every available hart was cut by a branch/halt/trap this
            // cycle) rather than a backend stall.
            _tdFetchBubblesCounter.IncrementBy(issueWidth - issued);
            if (issued == 0) _tdFetchLatencyCyclesCounter.Increment();
        }

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

        if (result.RequestBlock)
            // Still blocked (e.g. futex(FUTEX_WAIT) that hasn't cleared): don't retire,
            // don't apply SideEffect/register write, don't advance Pc — ctx.ArchState.Pc is
            // already this instruction's own Pc, so leaving it untouched IS the retry-in-
            // place redirect. Cut this hart's slot for the rest of THIS cycle only (same as
            // a branch/halt/trap below) so it doesn't spin-retry within the same cycle and
            // starve sibling harts of issue slots — next cycle the fetch policy considers it
            // available again and re-attempts from the same Pc. Every other HartContext is a
            // disjoint object never touched here, so sibling harts (including whichever one
            // is expected to clear this block) keep advancing normally.
            return true;

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