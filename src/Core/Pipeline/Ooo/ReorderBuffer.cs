using Mechanism;

namespace Pipeline.Ooo;

/// <summary>
///     One slot in the Reorder Buffer.
///     <para>
///         Allocated at Dispatch and retired at Commit. Instructions may complete
///         out-of-order (IsComplete may become true in any order) but they are
///         retired strictly in program order from the head of the ROB.
///     </para>
/// </summary>
public sealed class RobEntry {
    public bool Valid { get; set; }
    public ulong Pc { get; set; }
    public ulong InstrId { get; set; }
    public ITooth? Instruction { get; set; }

    // ── Register tracking ─────────────────────────────────────────────────────

    /// <summary>Architectural destination register, or -1 if none.</summary>
    public int ArchDestination { get; set; } = -1;

    /// <summary>Physical destination register allocated at Dispatch, or -1 if none.</summary>
    public int PhysDestination { get; set; } = -1;

    /// <summary>
    ///     The physical register that the arch destination was mapped to before this
    ///     instruction renamed it. On flush: written back into the RAT (walk-back).
    ///     On commit: returned to the free list (the old value is now dead).
    /// </summary>
    public int PrevPhysDestination { get; set; } = -1;

    // ── Execution result ──────────────────────────────────────────────────────

    public bool IsComplete { get; set; }
    public ulong? Result { get; set; }

    // ── Traps ─────────────────────────────────────────────────────────────────

    public bool HasTrap { get; set; }
    public TrapInfo? Trap { get; set; }

    // ── Branch resolution ─────────────────────────────────────────────────────

    /// <summary>Next PC speculatively taken at fetch time.</summary>
    public ulong PredictedNextPc { get; set; }

    /// <summary>
    ///     Actual next PC, filled in at Complete when a branch instruction finishes.
    ///     Null until then; a misprediction is detected when this differs from
    ///     PredictedNextPc.
    /// </summary>
    public (ulong Value, bool HasValue) ResolvedNextPc { get; set; }

    /// <summary>
    ///     Speculative branch-history snapshot captured at this instruction's fetch (before it folded
    ///     its own predicted direction). Populated only for branches; used to recover exact predictor
    ///     history on an execute-time partial squash. Default for non-branches.
    /// </summary>
    public BranchHistoryCheckpoint HistCheckpoint { get; set; }

    // ── Memory queue links ────────────────────────────────────────────────────

    /// <summary>True for load and atomic instructions.</summary>
    public bool IsLoad { get; set; }

    /// <summary>True for scalar store instructions.</summary>
    public bool IsStore { get; set; }

    /// <summary>
    ///     Index into the LoadQueue for this instruction's LQ entry, or -1 if not a load/atomic.
    ///     The LQ entry holds the load's address and violation flag.
    /// </summary>
    public int LqIdx { get; set; } = -1;

    /// <summary>
    ///     Index into the StoreQueue for this instruction's SQ entry, or -1 if not a store/atomic.
    ///     The SQ entry holds the store's deferred address, value, and width.
    /// </summary>
    public int SqIdx { get; set; } = -1;

    // ── Misc ──────────────────────────────────────────────────────────────────

    /// <summary>True for EBREAK or other halt-on-commit instructions.</summary>
    public bool IsHalt { get; set; }

    /// <summary>
    ///     True when the engine should halt <em>after</em> this instruction commits
    ///     (e.g. an HTIF tohost-exit store). Unlike <see cref="IsHalt" />, the
    ///     instruction retires normally first.
    /// </summary>
    public bool RequestHalt { get; set; }

    /// <summary>True if this instruction returns from a trap at commit (e.g. MRET).</summary>
    public bool IsReturnFromTrap { get; set; }

    /// <summary>Privilege level to return to when IsReturnFromTrap is true.</summary>
    public PrivilegeLevel? ReturnPrivilege { get; set; }

    /// <summary>
    ///     Deferred non-register architectural mutation (e.g. fflags CSR update) from a
    ///     scalar instruction's ExecuteResult, applied at Commit — in program order — to
    ///     preserve precise-exception semantics for instructions that execute speculatively
    ///     out of order. Vector/UVE ops are head-serialized and apply their SideEffect
    ///     immediately at Execute instead (see OooeTrain.ExecuteOne), so this is left null
    ///     for those.
    /// </summary>
    public Action<IArchState>? SideEffect { get; set; }

    // ── Critical-path prediction (Fields, Rubin & Bodík, ISCA 2001) ─────────────────────────

    /// <summary>True if this was the first instruction dispatched since a mispredicted branch (ED edge).</summary>
    public bool DGatedByRedirect { get; set; }

    /// <summary>InstrId of the mispredicting branch, valid only when <see cref="DGatedByRedirect" />.</summary>
    public ulong DRedirectSourceInstrId { get; set; }

    /// <summary>True if the ROB was full and stalled dispatch the previous cycle (CD edge).</summary>
    public bool DGatedByStall { get; set; }

    /// <summary>True if all source operands were ready by dispatch time (DE edge, "own D").</summary>
    public bool ESourceIsOwnD { get; set; }

    /// <summary>InstrId of the producer of the last-arriving source operand (EE edge), if not <see cref="ESourceIsOwnD" />.</summary>
    public ulong ESourceProducerInstrId { get; set; }

    /// <summary>Tick this entry completed (broadcast on the CDB); used to resolve the EC/CC commit-source rule.</summary>
    public ulong CompletedTick { get; set; }

    // ── CPI-stack accounting (Eyerman et al., ASPLOS 2006) ───────────────────────────────

    /// <summary>Cycle this entry was dispatched (entered the ROB); anchors the branch misprediction penalty window.</summary>
    public long DispatchCycle { get; set; }

    /// <summary>
    ///     Snapshot of the train's cumulative backend/store-classified cycle count at dispatch.
    ///     A mispredicted branch's penalty window is its ROB residency minus the cycles that
    ///     were classified to backend components in between (the paper's "unless the ROB is
    ///     full" rule), computed as the delta of this counter.
    /// </summary>
    public long CpiStolenAtDispatch { get; set; }

    /// <summary>Deepest memory-hierarchy level this load/atomic missed at execute time.</summary>
    public CpiMissClass DMissClass { get; set; }

    /// <summary>
    ///     True when this instruction's fetch suffered an I-cache/I-TLB miss (the sFMT miss
    ///     bit): its retirement proves the stalled fetch was on the correct path and posts the
    ///     train's pending I-side miss cycles to the global CPI-stack counters.
    /// </summary>
    public bool IcacheMiss { get; set; }

    internal void Clear() {
        Valid = false;
        Pc = 0;
        InstrId = 0;
        Instruction = null;
        ArchDestination = -1;
        PhysDestination = -1;
        PrevPhysDestination = -1;
        IsComplete = false;
        Result = null;
        HasTrap = false;
        Trap = null;
        PredictedNextPc = 0;
        ResolvedNextPc = default((ulong Value, bool HasValue));
        HistCheckpoint = default(BranchHistoryCheckpoint);
        IsLoad = false;
        IsStore = false;
        LqIdx = -1;
        SqIdx = -1;
        IsHalt = false;
        RequestHalt = false;
        IsReturnFromTrap = false;
        ReturnPrivilege = null;
        SideEffect = null;
        DGatedByRedirect = false;
        DRedirectSourceInstrId = 0;
        DGatedByStall = false;
        ESourceIsOwnD = false;
        ESourceProducerInstrId = 0;
        CompletedTick = 0;
        DispatchCycle = 0;
        CpiStolenAtDispatch = 0;
        DMissClass = CpiMissClass.None;
        IcacheMiss = false;
    }
}

/// <summary>
///     Circular Reorder Buffer — the backbone of in-order commitment in an OoOE pipeline.
///     <para>
///         Instructions are allocated at the <em>tail</em> (Dispatch phase) and retired
///         from the <em>head</em> (Commit phase). Execution results arrive out-of-order
///         and are written into the matching entry; the head can only retire once its
///         IsComplete flag is set, ensuring precise exception semantics.
///     </para>
/// </summary>
public sealed class ReorderBuffer {
    private readonly RobEntry[] _slots;
    private int _tail;

    public ReorderBuffer(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        Capacity = capacity;
        _slots = new RobEntry[capacity];
        for (var i = 0; i < capacity; i++) _slots[i] = new RobEntry();
    }

    public int Capacity { get; }
    public int Count { get; private set; }
    public bool IsFull => Count == Capacity;
    public bool IsEmpty => Count == 0;

    /// <summary>ROB index of the oldest in-flight instruction.</summary>
    public int HeadIndex { get; private set; }

    /// <summary>Returns the head (oldest, next-to-retire) entry.</summary>
    public RobEntry Head => _slots[HeadIndex];

    /// <summary>Returns the entry at a given ROB index.</summary>
    public RobEntry At(int index) => _slots[index % Capacity];

    /// <summary>
    ///     Allocates a new slot at the tail and returns its ROB index.
    ///     Check <see cref="IsFull" /> before calling.
    /// </summary>
    public int Allocate() {
        if (IsFull) throw new InvalidOperationException("ROB is full. Check IsFull before allocating.");
        int index = _tail;
        _slots[index].Valid = true;
        _tail = (_tail + 1) % Capacity;
        Count++;
        return index;
    }

    /// <summary>
    ///     Retires the head entry (advances the head pointer).
    ///     The caller must have verified <see cref="Head" />.IsComplete is true.
    /// </summary>
    public void Retire() {
        if (IsEmpty) throw new InvalidOperationException("ROB is empty; nothing to retire.");
        _slots[HeadIndex].Clear();
        HeadIndex = (HeadIndex + 1) % Capacity;
        Count--;
    }

    /// <summary>
    ///     Squashes all in-flight entries, resetting the ROB to empty.
    ///     <para>
    ///         Before calling Flush, the caller should iterate <see cref="InOrder" />
    ///         in <em>reverse</em> to walk back the RAT: for each entry with a valid
    ///         ArchDestination, call RenameMap.RestoreMapping and
    ///         RenameMap.FreePhysical to undo the Dispatch-time rename.
    ///     </para>
    /// </summary>
    public void Flush() {
        for (var i = 0; i < Capacity; i++) _slots[i].Clear();
        HeadIndex = 0;
        _tail = 0;
        Count = 0;
    }

    /// <summary>
    ///     Removes entries younger than <paramref name="instrId" /> from the tail (highest-InstrId
    ///     entries sit at the tail). Used by an execute-time partial squash, which retains the
    ///     redirecting branch and every older in-flight instruction. The caller must first walk the
    ///     removed entries youngest-to-oldest to restore the RAT (see <see cref="InOrder" />).
    /// </summary>
    public void TruncateYoungerThan(ulong instrId) {
        while (Count > 0) {
            int last = (_tail - 1 + Capacity) % Capacity;
            if (_slots[last].InstrId <= instrId) break;
            _slots[last].Clear();
            _tail = last;
            Count--;
        }
    }

    /// <summary>
    ///     Enumerates (index, entry) pairs from head to tail (oldest → youngest).
    ///     For flush/RAT-recovery, iterate this in reverse.
    /// </summary>
    public IEnumerable<(int Index, RobEntry Entry)> InOrder() {
        for (var i = 0; i < Count; i++) {
            int idx = (HeadIndex + i) % Capacity;
            yield return (idx, _slots[idx]);
        }
    }
}