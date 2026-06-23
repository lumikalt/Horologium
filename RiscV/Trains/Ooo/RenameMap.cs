namespace RiscV.Trains.Ooo;

/// <summary>
/// Register Alias Table (RAT) paired with a free-physical-register list.
///
/// Maps each architectural register to the physical register holding its
/// current speculative value. At Dispatch, each instruction's destination
/// is renamed to a freshly-allocated physical register; the old mapping is
/// stored in the ROB so it can be restored on a flush (walk-back recovery)
/// or freed on commit (the old value is no longer live).
/// </summary>
public sealed class RenameMap {
    private readonly int[] _rat;
    private readonly int _archCount;
    private readonly int _physCount;
    private readonly Queue<int> _freeList;

    /// <summary>True when at least one physical register is available for allocation.</summary>
    public bool HasFree => _freeList.Count > 0;

    /// <summary>Number of unallocated physical registers.</summary>
    public int FreeCount => _freeList.Count;

    /// <param name="archCount">Number of architectural registers (e.g. 64 for RV32F).</param>
    /// <param name="physCount">
    /// Total physical registers. Must exceed <paramref name="archCount"/>;
    /// the surplus forms the initial free list.
    /// </param>
    public RenameMap(int archCount, int physCount) {
        ArgumentOutOfRangeException.ThrowIfLessThan(physCount, archCount + 1, nameof(physCount));
        _archCount = archCount;
        _physCount = physCount;
        _rat = new int[archCount];
        _freeList = new Queue<int>(physCount - archCount);

        // Identity mapping for the initial arch registers, rest are free.
        for (var i = 0; i < archCount; i++) _rat[i] = i;
        for (int i = archCount; i < physCount; i++) _freeList.Enqueue(i);
    }

    /// <summary>Returns the physical register currently mapped to an architectural register.</summary>
    public int Lookup(int arch) {
        ValidateArch(arch);
        return _rat[arch];
    }

    /// <summary>
    /// Renames an architectural destination register: allocates a new physical register,
    /// updates the RAT, and returns <c>(newPhys, oldPhys)</c>.
    ///
    /// <c>oldPhys</c> is stored in the ROB entry so that on flush it can be
    /// written back into the RAT (walk-back recovery), and on commit it can be
    /// returned to the free list.
    ///
    /// Call only when <see cref="HasFree"/> is true.
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
    /// Returns a physical register to the free list.
    /// Called at commit once the instruction that previously occupied
    /// the slot for this architectural register has committed.
    /// </summary>
    public void FreePhysical(int phys) {
        if ((uint)phys >= (uint)_physCount) throw new ArgumentOutOfRangeException(nameof(phys));
        _freeList.Enqueue(phys);
    }

    /// <summary>
    /// Directly writes the RAT for one architectural register.
    /// Used during flush recovery: walk the ROB from youngest to oldest,
    /// calling RestoreMapping for each entry that renamed a destination.
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

    private void ValidateArch(int arch) {
        if ((uint)arch >= (uint)_archCount)
            throw new ArgumentOutOfRangeException(
                nameof(arch),
                $"Architectural register {arch} is out of range [0, {_archCount})."
            );
    }
}