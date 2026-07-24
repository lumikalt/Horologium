namespace Pipeline.Ooo;

/// <summary>One slot in the Load Queue.</summary>
public sealed class LqEntry {
    /// <summary>ROB index of the owning instruction.</summary>
    public int RobIdx { get; set; } = -1;

    /// <summary>Monotonic per-instruction age, for partial-squash younger-than comparisons.</summary>
    public ulong InstrId { get; set; }

    /// <summary>
    ///     Monotonic dispatch sequence number shared with the StoreQueue.
    ///     Used to determine the program-order relationship between LQ and SQ entries
    ///     without relying on ROB index comparison (which wraps).
    /// </summary>
    public ulong SeqNo { get; set; }

    /// <summary>True once the load has executed and Address/Bytes are valid.</summary>
    public bool Executed { get; set; }

    /// <summary>Effective address read at execute time.</summary>
    public ulong Address { get; set; }

    /// <summary>Number of bytes accessed.</summary>
    public int Bytes { get; set; }

    /// <summary>
    ///     Set when a younger-to-this-load store resolved with an overlapping address,
    ///     meaning the load may have read a stale value. Triggers re-execution at commit.
    /// </summary>
    public bool Violated { get; set; }

    /// <summary>
    ///     PC of the store that caused the violation (set alongside Violated = true).
    ///     Used by the store-set predictor's RecordViolation at commit time.
    /// </summary>
    public ulong ViolatingStorePc { get; set; }

    /// <summary>
    ///     SeqNo of the predicted dependent store from the store-set predictor (0 = none).
    ///     Set at dispatch; the load stalls at issue until that store's address is known.
    /// </summary>
    public ulong PredStoreSeqNo { get; set; }

    /// <summary>
    ///     Set at Dispatch when the SMB predictor (<see cref="SmbPredictor" />, NoSQ — Sha,
    ///     Martin &amp; Roth, MICRO 2006) matched a live, width-compatible producing store in
    ///     the SQ. Distinct from <see cref="PredStoreSeqNo" />: that field is the store-set
    ///     predictor's stall hint; this one drives an early speculative completion.
    /// </summary>
    public bool Bypassed { get; set; }

    /// <summary>SeqNo of the store <see cref="Bypassed" /> predicts as this load's producer.</summary>
    public ulong PredictedProducerSeqNo { get; set; }

    /// <summary>True once the early SMB write-back/broadcast has been issued for this load.</summary>
    public bool SpeculativelyCompleted { get; set; }

    /// <summary>
    ///     Set when the load's own (shadow) execution disagrees with the value already
    ///     speculatively broadcast. Triggers the same commit-time squash as <see cref="Violated" />.
    /// </summary>
    public bool BypassMispredicted { get; set; }

    /// <summary>
    ///     True if the shadow execution that discovered <see cref="BypassMispredicted" /> found a
    ///     real producing store at all (as opposed to the load's value coming from memory/no
    ///     in-flight store). Set alongside <see cref="ActualProducerSeqNo" />, consumed by
    ///     <see cref="SmbPredictor" /> retraining at commit.
    /// </summary>
    public bool HasActualProducer { get; set; }

    /// <summary>SeqNo of the store the shadow execution found actually produced this load's value.</summary>
    public ulong ActualProducerSeqNo { get; set; }

    internal void Clear() {
        RobIdx = -1;
        InstrId = 0;
        SeqNo = 0;
        Executed = false;
        Address = 0;
        Bytes = 0;
        Violated = false;
        ViolatingStorePc = 0;
        PredStoreSeqNo = 0;
        Bypassed = false;
        PredictedProducerSeqNo = 0;
        SpeculativelyCompleted = false;
        HasActualProducer = false;
        ActualProducerSeqNo = 0;
        BypassMispredicted = false;
    }
}

/// <summary>
///     Circular Load Queue — tracks all in-flight speculative loads for memory-order
///     violation detection. Entries are allocated at Dispatch (for Load and Atomic
///     instructions) and retired at Commit, always in program order.
/// </summary>
public sealed class LoadQueue {
    private readonly LqEntry[] _slots;
    private int _head;
    private int _tail;

    public LoadQueue(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _slots = new LqEntry[capacity];
        for (var i = 0; i < capacity; i++) _slots[i] = new LqEntry();
    }

    public int Capacity { get; }
    public int Count { get; private set; }
    public bool IsFull => Count == Capacity;
    public bool IsEmpty => Count == 0;

    /// <summary>
    ///     Allocates a new slot at the tail and returns its LQ index.
    ///     The caller must set RobIdx and SeqNo on the returned entry.
    /// </summary>
    public int Allocate() {
        if (IsFull) throw new InvalidOperationException("LQ is full. Check IsFull before allocating.");
        int index = _tail;
        _slots[index].RobIdx = -1;
        _tail = (_tail + 1) % Capacity;
        Count++;
        return index;
    }

    /// <summary>Returns the entry at a given LQ index.</summary>
    public LqEntry At(int index) => _slots[index % Capacity];

    /// <summary>
    ///     Retires the head entry (the oldest unretired load), advancing the head pointer.
    ///     Called at Commit when a load or atomic instruction retires from the ROB.
    /// </summary>
    public void Retire() {
        if (IsEmpty) throw new InvalidOperationException("LQ is empty; nothing to retire.");
        _slots[_head].Clear();
        _head = (_head + 1) % Capacity;
        Count--;
    }

    /// <summary>Squashes all in-flight entries, resetting the LQ to empty.</summary>
    public void Flush() {
        for (var i = 0; i < Capacity; i++) _slots[i].Clear();
        _head = 0;
        _tail = 0;
        Count = 0;
    }

    /// <summary>
    ///     Removes entries younger than <paramref name="instrId" /> from the tail (they are the most
    ///     recently allocated, so program-order youngest sit at the tail). Used by an execute-time
    ///     partial squash. Entries are contiguous in age, so this walks the tail back while the newest
    ///     entry's InstrId exceeds the threshold.
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
    public IEnumerable<LqEntry> InOrder() {
        for (var i = 0; i < Count; i++) yield return _slots[(_head + i) % Capacity];
    }
}