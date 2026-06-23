using Mechanism;

namespace RiscV.Trains.Ooo;

/// <summary>
/// One slot in the Reorder Buffer.
///
/// Allocated at Dispatch and retired at Commit. Instructions may complete
/// out-of-order (IsComplete may become true in any order) but they are
/// retired strictly in program order from the head of the ROB.
/// </summary>
public sealed class RobEntry {
    public bool Valid { get; set; }
    public ulong Pc { get; set; }
    public ITooth? Instruction { get; set; }

    // ── Register tracking ─────────────────────────────────────────────────────

    /// <summary>Architectural destination register, or -1 if none.</summary>
    public int ArchDestination { get; set; } = -1;

    /// <summary>Physical destination register allocated at Dispatch, or -1 if none.</summary>
    public int PhysDestination { get; set; } = -1;

    /// <summary>
    /// The physical register that the arch destination was mapped to before this
    /// instruction renamed it. On flush: written back into the RAT (walk-back).
    /// On commit: returned to the free list (the old value is now dead).
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
    /// Actual next PC, filled in at Complete when a branch instruction finishes.
    /// Null until then; a misprediction is detected when this differs from
    /// PredictedNextPc.
    /// </summary>
    public ulong? ResolvedNextPc { get; set; }

    // ── Deferred store ────────────────────────────────────────────────────────

    /// <summary>
    /// True for store instructions. The memory write is deferred to Commit
    /// so that stores become visible to other harts in program order.
    /// </summary>
    public bool IsStore { get; set; }

    public ulong StoreAddress { get; set; }
    public ulong StoreValue { get; set; }
    public int StoreWidth { get; set; }

    // ── Misc ──────────────────────────────────────────────────────────────────

    /// <summary>True for EBREAK or other halt-on-commit instructions.</summary>
    public bool IsHalt { get; set; }

    internal void Clear() {
        Valid = false;
        Pc = 0;
        Instruction = null;
        ArchDestination = -1;
        PhysDestination = -1;
        PrevPhysDestination = -1;
        IsComplete = false;
        Result = null;
        HasTrap = false;
        Trap = null;
        PredictedNextPc = 0;
        ResolvedNextPc = null;
        IsStore = false;
        StoreAddress = 0;
        StoreValue = 0;
        StoreWidth = 0;
        IsHalt = false;
    }
}

/// <summary>
/// Circular Reorder Buffer — the backbone of in-order commitment in an OoOE pipeline.
///
/// Instructions are allocated at the <em>tail</em> (Dispatch phase) and retired
/// from the <em>head</em> (Commit phase). Execution results arrive out-of-order
/// and are written into the matching entry; the head can only retire once its
/// IsComplete flag is set, ensuring precise exception semantics.
/// </summary>
public sealed class ReorderBuffer {
    private readonly RobEntry[] _slots;
    private int _head;
    private int _tail;

    public int Capacity { get; }
    public int Count { get; private set; }
    public bool IsFull => Count == Capacity;
    public bool IsEmpty => Count == 0;

    /// <summary>ROB index of the oldest in-flight instruction.</summary>
    public int HeadIndex => _head;

    public ReorderBuffer(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        Capacity = capacity;
        _slots = new RobEntry[capacity];
        for (var i = 0; i < capacity; i++) _slots[i] = new RobEntry();
    }

    /// <summary>Returns the entry at a given ROB index.</summary>
    public RobEntry At(int index) => _slots[index % Capacity];

    /// <summary>Returns the head (oldest, next-to-retire) entry.</summary>
    public RobEntry Head => _slots[_head];

    /// <summary>
    /// Allocates a new slot at the tail and returns its ROB index.
    /// Check <see cref="IsFull"/> before calling.
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
    /// Retires the head entry (advances the head pointer).
    /// The caller must have verified <see cref="Head"/>.IsComplete is true.
    /// </summary>
    public void Retire() {
        if (IsEmpty) throw new InvalidOperationException("ROB is empty; nothing to retire.");
        _slots[_head].Clear();
        _head = (_head + 1) % Capacity;
        Count--;
    }

    /// <summary>
    /// Squashes all in-flight entries, resetting the ROB to empty.
    ///
    /// Before calling Flush, the caller should iterate <see cref="InOrder"/>
    /// in <em>reverse</em> to walk back the RAT: for each entry with a valid
    /// ArchDestination, call RenameMap.RestoreMapping and
    /// RenameMap.FreePhysical to undo the Dispatch-time rename.
    /// </summary>
    public void Flush() {
        for (var i = 0; i < Capacity; i++) _slots[i].Clear();
        _head = 0;
        _tail = 0;
        Count = 0;
    }

    /// <summary>
    /// Enumerates (index, entry) pairs from head to tail (oldest → youngest).
    /// For flush/RAT-recovery, iterate this in reverse.
    /// </summary>
    public IEnumerable<(int Index, RobEntry Entry)> InOrder() {
        for (var i = 0; i < Count; i++) {
            int idx = (_head + i) % Capacity;
            yield return (idx, _slots[idx]);
        }
    }
}