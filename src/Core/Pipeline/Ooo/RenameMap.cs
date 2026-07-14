namespace Pipeline.Ooo;

/// <summary>A point-in-time capture of a <see cref="RenameMap" />'s RAT array and free list.</summary>
public readonly struct RenameMapSnapshot {
    public required int[] Rat { get; init; }
    public required int[] FreeList { get; init; }
}

/// <summary>
///     Register Alias Table (RAT) paired with a free-physical-register list.
///     <para>
///         Maps each architectural register to the physical register holding its
///         current speculative value. At Dispatch, each instruction's destination
///         is renamed to a freshly-allocated physical register; the old mapping is
///         stored in the ROB so it can be restored on a flush (walk-back recovery)
///         or freed on commit (the old value is no longer live).
///     </para>
/// </summary>
public sealed class RenameMap {
    private readonly int _archCount;
    private readonly Queue<int> _freeList;
    private readonly int _physCount;
    private readonly int[] _rat;

    /// <param name="archCount">Number of architectural registers (e.g. 64 for RV32F).</param>
    /// <param name="physCount">
    ///     Total physical registers. Must exceed <paramref name="archCount" />;
    ///     the surplus forms the initial free list.
    /// </param>
    public RenameMap(int archCount, int physCount) {
        ArgumentOutOfRangeException.ThrowIfLessThan(physCount, archCount + 1);
        _archCount = archCount;
        _physCount = physCount;
        _rat = new int[archCount];
        _freeList = new Queue<int>(physCount - archCount);

        // Identity mapping for the initial arch registers, rest are free.
        for (var i = 0; i < archCount; i++) _rat[i] = i;
        for (int i = archCount; i < physCount; i++) _freeList.Enqueue(i);
    }

    /// <summary>True when at least one physical register is available for allocation.</summary>
    public bool HasFree => _freeList.Count > 0;

    /// <summary>Number of unallocated physical registers.</summary>
    public int FreeCount => _freeList.Count;

    /// <summary>Returns the physical register currently mapped to an architectural register.</summary>
    public int Lookup(int arch) {
        ValidateArch(arch);
        return _rat[arch];
    }

    /// <summary>
    ///     Renames an architectural destination register: allocates a new physical register,
    ///     updates the RAT, and returns <c>(newPhys, oldPhys)</c>.
    ///     <para>
    ///         <c>oldPhys</c> is stored in the ROB entry so that on flush it can be
    ///         written back into the RAT (walk-back recovery), and on commit it can be
    ///         returned to the free list.
    ///     </para>
    ///     <para>Call only when <see cref="HasFree" /> is true.</para>
    /// </summary>
    public (int NewPhys, int OldPhys) Rename(int arch) {
        ValidateArch(arch);
        if (!HasFree)
            throw new InvalidOperationException(
                "No physical registers available. Check HasFree before calling Rename."
            );
        int old = _rat[arch];
        int newPhys = _freeList.Dequeue();
        _rat[arch] = newPhys;
        return (newPhys, old);
    }

    /// <summary>
    ///     Allocates a physical register without touching the RAT — CFP back-end renaming
    ///     (Srinivasan et al., ASPLOS 2004 §4.1.3): the slice remapper maps physical names to
    ///     physical names, so re-inserted slice instructions acquire fresh destination registers
    ///     that no architectural register ever points at. Call only when <see cref="HasFree" />.
    /// </summary>
    public int AllocatePhysical() {
        if (!HasFree)
            throw new InvalidOperationException(
                "No physical registers available. Check HasFree before calling AllocatePhysical."
            );
        return _freeList.Dequeue();
    }

    /// <summary>
    ///     Returns a physical register to the free list.
    ///     Called at commit once the instruction that previously occupied
    ///     the slot for this architectural register has committed.
    /// </summary>
    public void FreePhysical(int phys) {
        if ((uint)phys >= (uint)_physCount) throw new ArgumentOutOfRangeException(nameof(phys));
        _freeList.Enqueue(phys);
    }

    /// <summary>
    ///     Directly writes the RAT for one architectural register.
    ///     Used during flush recovery: walk the ROB from youngest to oldest,
    ///     calling RestoreMapping for each entry that renamed a destination.
    /// </summary>
    public void RestoreMapping(int arch, int phys) {
        ValidateArch(arch);
        _rat[arch] = phys;
    }

    /// <summary>Resets to the initial identity mapping and replenishes the free list.</summary>
    public void Reset() {
        for (var i = 0; i < _archCount; i++) _rat[i] = i;
        _freeList.Clear();
        for (int i = _archCount; i < _physCount; i++) _freeList.Enqueue(i);
    }

    /// <summary>Captures the current RAT mapping and free list so they can be perfectly restored later.</summary>
    public RenameMapSnapshot Snapshot() => new() { Rat = (int[])_rat.Clone(), FreeList = _freeList.ToArray(), };

    /// <summary>
    ///     Captures only the RAT array — a CPR map-table checkpoint (Akkary, Rajwar &amp;
    ///     Srinivasan, MICRO 2003 §4.1). Deliberately excludes the free list: under CPR's
    ///     aggressive reclamation the free list at recovery time is <em>not</em> the free list at
    ///     checkpoint-creation time (registers were reclaimed and re-allocated in between);
    ///     recovery restores the mapping via <see cref="RestoreRat" /> and rebuilds register
    ///     availability from use counters instead.
    /// </summary>
    public int[] SnapshotRat() => (int[])_rat.Clone();

    /// <summary>Restores the RAT array from a <see cref="SnapshotRat" /> capture, leaving the free list untouched.</summary>
    public void RestoreRat(int[] rat) => Array.Copy(rat, _rat, _rat.Length);

    /// <summary>True if any architectural register currently maps to <paramref name="phys" />.</summary>
    public bool IsMapped(int phys) {
        foreach (int p in _rat)
            if (p == phys)
                return true;
        return false;
    }

    /// <summary>
    ///     Overwrites the RAT and free list from a prior <see cref="Snapshot" />, discarding every
    ///     rename performed since. Used by runahead execution to undo a shadow episode's allocations:
    ///     because this replaces state wholesale rather than undoing deltas, it is safe to call even
    ///     when the caller doesn't know exactly how far a speculative episode progressed.
    /// </summary>
    public void Restore(RenameMapSnapshot snapshot) {
        Array.Copy(snapshot.Rat, _rat, _rat.Length);
        _freeList.Clear();
        foreach (int phys in snapshot.FreeList) _freeList.Enqueue(phys);
    }

    private void ValidateArch(int arch) {
        if ((uint)arch >= (uint)_archCount)
            throw new ArgumentOutOfRangeException(
                nameof(arch),
                $"Architectural register {arch} is out of range [0, {_archCount})."
            );
    }
}