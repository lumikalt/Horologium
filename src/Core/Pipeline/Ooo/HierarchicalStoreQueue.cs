namespace Pipeline.Ooo;

/// <summary>
///     Two-tier Store Queue with a Membership Test Buffer (CPR, Akkary, Rajwar &amp; Srinivasan,
///     MICRO 2003 §3.2): a small fast L1 tier (the youngest <c>l1Capacity</c> entries) backed by a
///     larger slow L2 tier, plus a non-tagged direct-mapped MTB that lets a load's disambiguation
///     check short-circuit to "definitely no matching store" without touching either tier.
///     <para>
///         L1/L2 membership is derived from allocation order rather than physically moving entries
///         between two arrays: the youngest <c>l1Capacity</c> live entries (nearest the tail) are
///         "L1"; everything older is "L2". As new stores are appended, older entries age out of the
///         L1 window automatically — functionally identical to the paper's explicit L1-evicts-to-L2,
///         without a copy.
///     </para>
///     <para>
///         The MTB tracks a per-slot live-store <em>count</em> (not a sticky bit) precisely so it
///         can be safely decremented on retire/flush/squash — a bit that only ever gets set would
///         accumulate false positives (never a soundness problem, since the MTB is a fast-negative
///         filter only; a true membership check still requires walking <see cref="InOrder" />) but
///         would degrade to "always maybe" over a long run.
///     </para>
/// </summary>
public sealed class HierarchicalStoreQueue {
    private readonly int _l1Capacity;
    private readonly int _mtbBlockShift;
    private readonly int[] _mtbCount;
    private readonly int _mtbMask;
    private readonly SqEntry[] _slots;
    private int _head;
    private int _tail;

    public HierarchicalStoreQueue(int l1Capacity, int l2Capacity, int mtbSize = 1024, int blockBytes = 64) {
        ArgumentOutOfRangeException.ThrowIfLessThan(l1Capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(l2Capacity, 0);
        if ((mtbSize & (mtbSize - 1)) != 0)
            throw new ArgumentException("mtbSize must be a power of two.", nameof(mtbSize));
        if ((blockBytes & (blockBytes - 1)) != 0)
            throw new ArgumentException("blockBytes must be a power of two.", nameof(blockBytes));

        _l1Capacity = l1Capacity;
        Capacity = l1Capacity + l2Capacity;
        _slots = new SqEntry[Capacity];
        for (var i = 0; i < Capacity; i++) _slots[i] = new SqEntry();

        _mtbCount = new int[mtbSize];
        _mtbMask = mtbSize - 1;
        _mtbBlockShift = 0;
        while (1 << _mtbBlockShift < blockBytes) _mtbBlockShift++;
    }

    public int Capacity { get; }
    public int Count { get; private set; }
    public bool IsFull => Count == Capacity;
    public bool IsEmpty => Count == 0;

    /// <summary>
    ///     Allocates a new slot at the tail and returns its index.
    ///     The caller must set RobIdx and SeqNo on the returned entry.
    /// </summary>
    public int Allocate() {
        if (IsFull) throw new InvalidOperationException("Store queue is full. Check IsFull before allocating.");
        int index = _tail;
        _slots[index].Valid = true;
        _slots[index].RobIdx = -1;
        _tail = (_tail + 1) % Capacity;
        Count++;
        return index;
    }

    /// <summary>Returns the entry at a given index.</summary>
    public SqEntry At(int index) => _slots[index % Capacity];

    /// <summary>True if the entry at <paramref name="index" /> currently sits in the fast L1 tier.</summary>
    public bool IsL1(int index) {
        int age = (index - _head + Capacity) % Capacity; // 0 = oldest (at head)
        return age >= Count - _l1Capacity;
    }

    /// <summary>Records a store's resolved address, marking it in the MTB. Call once the store executes.</summary>
    public void RecordAddressKnown(int index, ulong address) {
        SqEntry e = _slots[index % Capacity];
        e.AddressKnown = true;
        e.Address = address;
        _mtbCount[MtbIndex(address)]++;
    }

    /// <summary>
    ///     Fast-negative disambiguation probe: false means no live store can possibly match
    ///     <paramref name="address" />, so the caller may skip a full <see cref="InOrder" /> walk.
    ///     True is not proof of a match (non-tagged, aliasing is possible) — fall back to a full scan.
    /// </summary>
    public bool MayHaveMatchingStore(ulong address) => _mtbCount[MtbIndex(address)] > 0;

    /// <summary>Retires the head entry (the oldest unretired store), advancing the head pointer.</summary>
    public void Retire() {
        if (IsEmpty) throw new InvalidOperationException("Store queue is empty; nothing to retire.");
        ClearMtb(_slots[_head]);
        _slots[_head].Clear();
        _head = (_head + 1) % Capacity;
        Count--;
    }

    /// <summary>Squashes all in-flight entries, resetting the queue to empty.</summary>
    public void Flush() {
        for (var i = 0; i < Count; i++) ClearMtb(_slots[(_head + i) % Capacity]);
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
            ClearMtb(_slots[last]);
            _slots[last].Clear();
            _tail = last;
            Count--;
        }
    }

    /// <summary>Enumerates entries from oldest to youngest (head → tail).</summary>
    public IEnumerable<SqEntry> InOrder() {
        for (var i = 0; i < Count; i++) yield return _slots[(_head + i) % Capacity];
    }

    /// <summary>
    ///     Enumerates entries oldest → youngest together with their slot index, so callers can
    ///     combine a forwarding scan with the <see cref="IsL1" /> tier test (an L2-tier forward
    ///     costs a data-cache-miss-equivalent penalty; see MICRO 2003 §4.2.3).
    /// </summary>
    public IEnumerable<(int Index, SqEntry Entry)> InOrderIndexed() {
        for (var i = 0; i < Count; i++) {
            int idx = (_head + i) % Capacity;
            yield return (idx, _slots[idx]);
        }
    }

    private void ClearMtb(SqEntry e) {
        if (e.AddressKnown) _mtbCount[MtbIndex(e.Address)]--;
    }

    private int MtbIndex(ulong address) => (int)((address >> _mtbBlockShift) & (ulong)_mtbMask);
}