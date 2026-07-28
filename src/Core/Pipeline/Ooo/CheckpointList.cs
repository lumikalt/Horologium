#region

using Mechanism;

#endregion

namespace Pipeline.Ooo;

/// <summary>
///     Per-instruction bookkeeping for one instruction within a <see cref="Checkpoint" /> epoch.
///     Unlike <see cref="RobEntry" />, this carries no RAT walk-back field
///     (<c>PrevPhysDestination</c>): CPR never walks the RAT back per instruction — recovery
///     always restores a whole <see cref="Checkpoint.RatSnapshot" /> in one shot, and the
///     free list is rebuilt from use counters rather than restored.
///     <para>
///         <see cref="ResultValue" /> is captured at CDB broadcast so that bulk commit can sync
///         <c>IArchState</c> per entry without reading the PRF — under aggressive reclamation the
///         producing physical register may have been legitimately reclaimed and re-allocated long
///         before this entry's checkpoint retires, so the PRF slot is not authoritative at commit
///         time. This is bookkeeping the hardware doesn't need (its architectural state <em>is</em>
///         the PRF + RAT); the simulator needs it only to keep the separate <c>IArchState</c>
///         mirror in sync.
///     </para>
/// </summary>
public sealed class CheckpointEntry {
    public bool Valid { get; set; }
    public ulong Pc { get; set; }
    public ulong InstrId { get; set; }
    public ITooth? Instruction { get; set; }

    /// <summary>
    ///     The branch's own InstrId when this entry is a macro-fused compare+branch pair (see
    ///     <see cref="IMacroFuser" />), null otherwise. The branch was assigned its own InstrId
    ///     at Fetch, before fusion collapsed it into this single checkpoint entry — without this
    ///     field, that InstrId is never retired or flushed anywhere, leaving a dangling
    ///     Fetch-only row in <see cref="PEventLog" /> traces.
    /// </summary>
    public ulong? FusedSecondInstrId { get; set; }

    /// <summary><see cref="Checkpoint.Seq" /> of the epoch this instruction belongs to.</summary>
    public ulong CheckpointSeq { get; set; }

    /// <summary>Architectural destination register, or -1 (0 = x0 is never renamed).</summary>
    public int ArchDestination { get; set; } = -1;

    /// <summary>Physical register this entry's rename allocated, or -1 if no destination.</summary>
    public int PhysDestination { get; set; } = -1;

    /// <summary>
    ///     <see cref="PhysicalRegisterFile.AllocationGeneration" /> of <see cref="PhysDestination" />
    ///     as of this entry's (front-end or back-end) rename. A squash only marks the destination
    ///     reclaimable when the generation still matches — aggressive reclamation may have already
    ///     freed and re-allocated the register to someone else.
    /// </summary>
    public int PhysDestGen { get; set; }

    public bool IsComplete { get; set; }

    /// <summary>
    ///     CFP NAV tag (Srinivasan et al., ASPLOS 2004 §4.2.1): set when this entry drained into
    ///     the Slice Data Buffer instead of executing — it is an L2-class-miss load, or it
    ///     consumed a NAV-tagged source. A drained entry stays incomplete (it does not decrement
    ///     its checkpoint's completion counter), so its checkpoint cannot bulk-commit until the
    ///     slice re-inserts and the entry executes for real.
    /// </summary>
    public bool IsNav { get; set; }

    /// <summary>Destination value captured at CDB broadcast; see class summary.</summary>
    public ulong ResultValue { get; set; }

    public bool HasResultValue { get; set; }

    /// <summary>Fetch-time predicted successor PC, for mispredict detection and replay training.</summary>
    public ulong PredictedNextPc { get; set; }

    /// <summary>Execute-time resolved successor PC once known, or unset for non-branches.</summary>
    public (ulong Value, bool HasValue) ResolvedNextPc { get; set; }

    public bool HasTrap { get; set; }
    public TrapInfo? Trap { get; set; }

    public bool IsHalt { get; set; }
    public bool RequestHalt { get; set; }
    public bool IsReturnFromTrap { get; set; }
    public PrivilegeLevel? ReturnPrivilege { get; set; }

    /// <summary>Deferred non-register architectural mutation, applied at bulk commit in program order.</summary>
    public Action<IArchState>? SideEffect { get; set; }

    public bool IsLoad { get; set; }
    public bool IsStore { get; set; }
    public int LqIdx { get; set; } = -1;
    public int SqIdx { get; set; } = -1;

    // ── CPI-stack accounting (Eyerman et al., ASPLOS 2006); mirrors RobEntry ─────────────

    /// <summary>Cycle this entry was appended (entered the window); anchors the branch misprediction penalty.</summary>
    public long DispatchCycle { get; set; }

    /// <summary>
    ///     Snapshot of the train's backend/store-classified cycle count at append; see
    ///     <see cref="RobEntry.CpiStolenAtDispatch" />.
    /// </summary>
    public long CpiStolenAtDispatch { get; set; }

    /// <summary>Deepest memory-hierarchy level this load/atomic missed at execute time.</summary>
    public CpiMissClass DMissClass { get; set; }

    /// <summary>sFMT miss bit: this instruction's fetch suffered an I-cache/I-TLB miss.</summary>
    public bool IcacheMiss { get; set; }

    internal void Clear() {
        Valid = false;
        Pc = 0;
        InstrId = 0;
        Instruction = null;
        FusedSecondInstrId = null;
        CheckpointSeq = 0;
        ArchDestination = -1;
        PhysDestination = -1;
        PhysDestGen = 0;
        IsComplete = false;
        IsNav = false;
        ResultValue = 0;
        HasResultValue = false;
        PredictedNextPc = 0;
        ResolvedNextPc = default((ulong, bool));
        HasTrap = false;
        Trap = null;
        IsHalt = false;
        RequestHalt = false;
        IsReturnFromTrap = false;
        ReturnPrivilege = null;
        SideEffect = null;
        IsLoad = false;
        IsStore = false;
        LqIdx = -1;
        SqIdx = -1;
        DispatchCycle = 0;
        CpiStolenAtDispatch = 0;
        DMissClass = CpiMissClass.None;
        IcacheMiss = false;
    }
}

/// <summary>
///     One CPR checkpoint ("epoch"): a RAT snapshot taken before its first instruction renamed,
///     plus every instruction renamed since (in program order) until the next checkpoint opened.
///     <para>
///         Bulk-committable in one shot once every entry is complete — the defining property that
///         lets CPR retire hundreds of instructions at once instead of walking a ROB head one
///         entry at a time (Akkary, Rajwar &amp; Srinivasan, MICRO 2003 §4.1.2). The completion
///         counter is modeled by <see cref="CompletedCount" /> vs <see cref="Entries" />.Count;
///         counter overflow is prevented by the train forcing a new checkpoint at a maximum
///         entry count.
///     </para>
/// </summary>
public sealed class Checkpoint {
    private readonly List<CheckpointEntry> _entries = [];

    public bool Valid { get; set; }

    /// <summary>Monotonic checkpoint identifier — never reused, unlike the buffer slot index.</summary>
    public ulong Seq { get; set; }

    /// <summary>PC of the checkpoint's first instruction; recovery restarts fetch here.</summary>
    public ulong RestartPc { get; set; }

    /// <summary>RAT array as of just before the first instruction of this epoch renamed.</summary>
    public int[] RatSnapshot { get; set; } = [];

    public int CompletedCount { get; private set; }

    /// <summary>
    ///     Cursor into <see cref="Entries" /> tracking how many leading entries have been
    ///     committed for real (SideEffect applied / store written / PC advanced) so far. Distinct
    ///     from <see cref="CompletedCount" />, which tracks execute-completion: the bulk-commit
    ///     walk can span multiple cycles (e.g. a wide checkpoint committing more stores than
    ///     there are D-cache write ports in one cycle) without re-committing entries already
    ///     committed on an earlier cycle.
    /// </summary>
    public int CommittedCount { get; set; }

    public IReadOnlyList<CheckpointEntry> Entries => _entries;

    public bool AllComplete => CompletedCount == _entries.Count;

    /// <summary>InstrId of the first instruction in this epoch (call only when non-empty).</summary>
    public ulong FirstInstrId => _entries[0].InstrId;

    /// <summary>InstrId of the youngest instruction in this epoch (call only when non-empty).</summary>
    public ulong LastInstrId => _entries[^1].InstrId;

    public void Open(int[] ratSnapshot, ulong restartPc, ulong seq) {
        Valid = true;
        RatSnapshot = ratSnapshot;
        RestartPc = restartPc;
        Seq = seq;
        CompletedCount = 0;
        CommittedCount = 0;
        _entries.Clear();
    }

    public CheckpointEntry Append() {
        var e = new CheckpointEntry { Valid = true, };
        _entries.Add(e);
        return e;
    }

    /// <summary>Call once when an entry transitions to complete (mirrors the checkpoint counter decrement).</summary>
    public void MarkEntryComplete() => CompletedCount++;

    /// <summary>
    ///     Recovery reopens this checkpoint as the new tail: its instructions are squashed and
    ///     will be re-fetched from <see cref="RestartPc" />, but the epoch itself — its RAT
    ///     snapshot and the use-counter references that snapshot holds — survives unchanged.
    /// </summary>
    public void ReopenForRecovery() {
        CompletedCount = 0;
        CommittedCount = 0;
        _entries.Clear();
    }

    internal void Clear() {
        Valid = false;
        Seq = 0;
        RestartPc = 0;
        RatSnapshot = [];
        CompletedCount = 0;
        CommittedCount = 0;
        _entries.Clear();
    }
}

/// <summary>
///     FIFO buffer of in-flight <see cref="Checkpoint" /> epochs — the ROB-free backbone of a CPR
///     train. Capacity bounds the number of <em>concurrent checkpoints</em>, not the number of
///     in-flight instructions: the effective instruction window is instead bounded by physical
///     register availability and per-checkpoint counter width, which is precisely CPR's
///     scalability argument — a large window without a large ROB.
/// </summary>
public sealed class CheckpointList {
    private readonly Checkpoint[] _slots;
    private int _head;
    private int _tail;

    public CheckpointList(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _slots = new Checkpoint[capacity];
        for (var i = 0; i < capacity; i++) _slots[i] = new Checkpoint();
    }

    public int Capacity { get; }
    public int Count { get; private set; }
    public bool IsFull => Count == Capacity;
    public bool IsEmpty => Count == 0;

    public Checkpoint Head => _slots[_head];

    /// <summary>The most recently opened checkpoint — newly renamed instructions join this one.</summary>
    public Checkpoint Tail => _slots[(_tail - 1 + Capacity) % Capacity];

    /// <summary>Opens a new checkpoint at the tail. Check <see cref="IsFull" /> first.</summary>
    public Checkpoint Open(int[] ratSnapshot, ulong restartPc, ulong seq) {
        if (IsFull) throw new InvalidOperationException("Checkpoint list is full. Check IsFull before opening.");
        Checkpoint cp = _slots[_tail];
        cp.Open(ratSnapshot, restartPc, seq);
        _tail = (_tail + 1) % Capacity;
        Count++;
        return cp;
    }

    /// <summary>Retires (bulk-commits) the head checkpoint, advancing the head pointer.</summary>
    public void RetireHead() {
        if (IsEmpty) throw new InvalidOperationException("Checkpoint list is empty; nothing to retire.");
        _slots[_head].Clear();
        _head = (_head + 1) % Capacity;
        Count--;
    }

    /// <summary>
    ///     Pops and returns the youngest checkpoint without clearing it, leaving the caller
    ///     responsible for reading its <see cref="Checkpoint.RatSnapshot" />/<see cref="Checkpoint.Entries" />
    ///     (to balance <see cref="PhysicalRegisterFile" /> use counts) before calling
    ///     <see cref="Checkpoint.Clear" />. Used to unwind the tail during a recovery.
    /// </summary>
    public Checkpoint DiscardTail() {
        if (IsEmpty) throw new InvalidOperationException("Checkpoint list is empty; nothing to discard.");
        _tail = (_tail - 1 + Capacity) % Capacity;
        Count--;
        return _slots[_tail];
    }

    /// <summary>Squashes every in-flight checkpoint, resetting the list to empty.</summary>
    public void Flush() {
        for (var i = 0; i < Capacity; i++) _slots[i].Clear();
        _head = 0;
        _tail = 0;
        Count = 0;
    }

    /// <summary>Enumerates checkpoints from oldest to youngest (head → tail).</summary>
    public IEnumerable<Checkpoint> InOrder() {
        for (var i = 0; i < Count; i++) yield return _slots[(_head + i) % Capacity];
    }
}