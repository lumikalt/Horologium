using Mechanism;

namespace Pipeline.Ooo;

/// <summary>
/// One entry (reservation station) in the Issue Queue.
///
/// Each source operand is described by a tag (the physical register index)
/// and a ready/value pair. A tag of -1 means that source is not used by this
/// instruction. When the CDB broadcasts a result, all entries whose tag
/// matches the broadcasting register have their Ready bit set and Value filled.
///
/// An entry becomes eligible for issue once all needed sources are ready.
/// </summary>
public sealed class RsEntry {
    public bool Busy { get; set; }

    /// <summary>ROB slot this RS entry is paired with.</summary>
    public int RobIndex { get; set; }

    public ITooth? Instruction { get; set; }
    public ulong Pc { get; set; }
    public ulong PredictedNextPc { get; set; }

    // Source operand 1
    public int Src1Tag { get; set; } = -1; // -1 = not used
    public bool Src1Ready { get; set; }
    public ulong Src1Value { get; set; }

    // Source operand 2
    public int Src2Tag { get; set; } = -1;
    public bool Src2Ready { get; set; }
    public ulong Src2Value { get; set; }

    // Source operand 3 — used by R4-type instructions (FMADD / FMSUB / FNMADD / FNMSUB)
    public int Src3Tag { get; set; } = -1;
    public bool Src3Ready { get; set; }
    public ulong Src3Value { get; set; }

    /// <summary>Physical destination register, or -1 if this instruction produces no result.</summary>
    public int PhysDestination { get; set; } = -1;

    /// <summary>True when all required source operands have their values.</summary>
    public bool IsReady =>
        (Src1Tag < 0 || Src1Ready) &&
        (Src2Tag < 0 || Src2Ready) &&
        (Src3Tag < 0 || Src3Ready);

    internal void Clear() {
        Busy = false;
        RobIndex = 0;
        Instruction = null;
        Pc = 0;
        PredictedNextPc = 0;
        Src1Tag = Src2Tag = Src3Tag = -1;
        Src1Ready = Src2Ready = Src3Ready = false;
        Src1Value = Src2Value = Src3Value = 0;
        PhysDestination = -1;
    }
}

/// <summary>
/// Unified Issue Queue (reservation stations).
///
/// Instructions wait here after Dispatch until all their source operands are
/// ready. At Issue, the scheduler scans for ready entries and selects up to
/// <c>issueWidth</c> per cycle (one per distinct functional-unit class, or
/// simply the oldest-first policy for a single-port design).
///
/// CDB broadcasts (<see cref="Broadcast"/>) propagate results to all waiting
/// entries in O(capacity) time — the issue queue size should be kept small
/// (16–32 entries) so this is cheap.
/// </summary>
public sealed class IssueQueue {
    private readonly RsEntry[] _slots;

    public int Capacity { get; }
    public int Count { get; private set; }
    public bool IsFull => Count == Capacity;
    public bool IsEmpty => Count == 0;

    public IssueQueue(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _slots = new RsEntry[capacity];
        for (var i = 0; i < capacity; i++) _slots[i] = new RsEntry();
    }

    /// <summary>Returns the entry at the given slot index.</summary>
    public RsEntry At(int index) => _slots[index];

    /// <summary>
    /// Finds a free slot, marks it Busy, and returns its index.
    /// Returns -1 if the queue is full (check <see cref="IsFull"/> first).
    /// </summary>
    public int Allocate() {
        for (var i = 0; i < Capacity; i++) {
            if (_slots[i].Busy) continue;
            _slots[i].Busy = true;
            Count++;
            return i;
        }

        return -1;
    }

    /// <summary>
    /// CDB broadcast: wakes every entry that is waiting for <paramref name="physReg"/>.
    /// For each such entry, the corresponding source is marked ready and its value captured.
    /// </summary>
    public void Broadcast(int physReg, ulong value) {
        foreach (RsEntry e in _slots) {
            if (!e.Busy) continue;
            if (e.Src1Tag == physReg && !e.Src1Ready) {
                e.Src1Value = value;
                e.Src1Ready = true;
            }

            if (e.Src2Tag == physReg && !e.Src2Ready) {
                e.Src2Value = value;
                e.Src2Ready = true;
            }

            if (e.Src3Tag == physReg && !e.Src3Ready) {
                e.Src3Value = value;
                e.Src3Ready = true;
            }
        }
    }

    /// <summary>
    /// Returns the slot index of the first ready entry matching an optional
    /// predicate (e.g., a specific functional-unit class), or -1 if none.
    /// </summary>
    public int FindReady(Func<RsEntry, bool>? filter = null) {
        for (var i = 0; i < Capacity; i++) {
            RsEntry e = _slots[i];
            if (e is { Busy: true, IsReady: true, } && (filter is null || filter(e))) return i;
        }

        return -1;
    }

    /// <summary>
    /// Collects up to <paramref name="maxCount"/> ready entries into
    /// <paramref name="results"/>, optionally filtering by functional-unit class.
    /// Returns the number of entries collected.
    /// </summary>
    public int FindReadyBatch(Span<int> results, int maxCount, Func<RsEntry, bool>? filter = null) {
        var found = 0;
        for (var i = 0; i < Capacity && found < maxCount; i++) {
            RsEntry e = _slots[i];
            if (e is { Busy: true, IsReady: true, } && (filter is null || filter(e))) results[found++] = i;
        }

        return found;
    }

    /// <summary>Returns a slot to the free pool after the instruction has been issued.</summary>
    public void Free(int index) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Capacity);
        _slots[index].Clear();
        Count--;
    }

    /// <summary>Squashes all entries. Called on pipeline flush.</summary>
    public void Flush() {
        foreach (RsEntry e in _slots) e.Clear();
        Count = 0;
    }
}