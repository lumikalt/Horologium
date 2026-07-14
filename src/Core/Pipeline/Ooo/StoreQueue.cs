namespace Pipeline.Ooo;

/// <summary>One slot in the Store Queue.</summary>
public sealed class SqEntry {
    public bool Valid { get; set; }

    /// <summary>ROB index of the owning instruction.</summary>
    public int RobIdx { get; set; } = -1;

    /// <summary>Monotonic per-instruction age, for partial-squash younger-than comparisons.</summary>
    public ulong InstrId { get; set; }

    /// <summary>
    ///     Monotonic dispatch sequence number shared with the LoadQueue.
    ///     An Atomic instruction receives the same SeqNo in both its LQ and SQ entry.
    /// </summary>
    public ulong SeqNo { get; set; }

    /// <summary>True once the store has executed and Address/Value/Width are valid.</summary>
    public bool AddressKnown { get; set; }

    /// <summary>Effective address written at execute time.</summary>
    public ulong Address { get; set; }

    /// <summary>Value to be written to memory at commit time.</summary>
    public ulong Value { get; set; }

    /// <summary>Number of bytes to write.</summary>
    public int Width { get; set; }

    /// <summary>PC of the store instruction (used by the store-set predictor).</summary>
    public ulong Pc { get; set; }

    /// <summary>
    ///     Static, address-independent access width (<see cref="Mechanism.ITooth.MemoryAccessBytes" />)
    ///     known at dispatch, before the store's address/value are known. Used by the SMB predictor
    ///     (<see cref="SmbPredictor" />, NoSQ) to check a candidate producing store's width against a
    ///     bypassing load's width without needing either instruction's address.
    /// </summary>
    public int StaticBytes { get; set; }

    internal void Clear() {
        Valid = false;
        RobIdx = -1;
        InstrId = 0;
        SeqNo = 0;
        AddressKnown = false;
        Address = 0;
        Value = 0;
        Width = 0;
        Pc = 0;
        StaticBytes = 0;
    }
}

/// <summary>
///     Circular Store Queue — tracks all in-flight stores (and the write-half of atomics)
///     for memory-order violation detection and store-to-load forwarding.
///     Entries are allocated at Dispatch (for Store and Atomic instructions) and retired
///     at Commit after the memory write has been performed, always in program order.
/// </summary>
public sealed class StoreQueue {
    private readonly SqEntry[] _slots;
    private int _head;
    private int _tail;

    public StoreQueue(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _slots = new SqEntry[capacity];
        for (var i = 0; i < capacity; i++) _slots[i] = new SqEntry();
    }

    public int Capacity { get; }
    public int Count { get; private set; }
    public bool IsFull => Count == Capacity;
    public bool IsEmpty => Count == 0;

    /// <summary>
    ///     Allocates a new slot at the tail and returns its SQ index.
    ///     The caller must set RobIdx and SeqNo on the returned entry.
    /// </summary>
    public int Allocate() {
        if (IsFull) throw new InvalidOperationException("SQ is full. Check IsFull before allocating.");
        int index = _tail;
        _slots[index].Valid = true;
        _slots[index].RobIdx = -1;
        _tail = (_tail + 1) % Capacity;
        Count++;
        return index;
    }

    /// <summary>Returns the entry at a given SQ index.</summary>
    public SqEntry At(int index) => _slots[index % Capacity];

    /// <summary>
    ///     Retires the head entry (the oldest unretired store/atomic), advancing the head pointer.
    ///     Called at Commit after the memory write has been performed and the instruction retires.
    /// </summary>
    public void Retire() {
        if (IsEmpty) throw new InvalidOperationException("SQ is empty; nothing to retire.");
        _slots[_head].Clear();
        _head = (_head + 1) % Capacity;
        Count--;
    }

    /// <summary>Squashes all in-flight entries, resetting the SQ to empty.</summary>
    public void Flush() {
        for (var i = 0; i < Capacity; i++) _slots[i].Clear();
        _head = 0;
        _tail = 0;
        Count = 0;
    }

    /// <summary>
    ///     Removes entries younger than <paramref name="instrId" /> from the tail. Used by an
    ///     execute-time partial squash; the newest (highest-InstrId) entries sit at the tail.
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
    ///     Enumerates entries from oldest to youngest (head → tail).
    ///     SeqNo values are monotonically increasing in this order.
    /// </summary>
    public IEnumerable<SqEntry> InOrder() {
        for (var i = 0; i < Count; i++) yield return _slots[(_head + i) % Capacity];
    }
}