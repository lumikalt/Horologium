namespace Pipeline.Ooo;

/// <summary>
///     Holds speculative register values for OoOE execution.
///     <para>
///         Each slot has a ready bit: cleared at Dispatch when an instruction claims
///         the register as its destination; set again at Complete when the CDB
///         broadcasts the result. Waiting RS entries check IsReady before issuing.
///     </para>
///     <para>
///         Optionally tracks the per-register state for CPR-style aggressive reclamation
///         (Akkary, Rajwar &amp; Srinivasan, MICRO 2003 §4.3, after Moudgill et al., MICRO 1993):
///         a <em>use counter</em> — one reference per renamed-but-not-yet-captured reader plus one
///         per live checkpoint whose RAT snapshot maps to this register — and an
///         <em>
///             unmapped
///             flag
///         </em>
///         , set when the architectural register this physical register backed is renamed
///         again. A register is reclaimable (<see cref="IsReclaimable" />) once its counter is zero,
///         its unmapped flag is set, and its value can no longer arrive through this name (it is
///         ready, or its producer drained to the CFP slice buffer / was squashed —
///         <see cref="MarkAbandoned" />). This may be long before the producing instruction's
///         checkpoint retires — the whole point of decoupling reclamation from in-order retirement.
///         <c>OooeTrain</c> ignores all of this state; only <c>CprTrain</c> drives it.
///     </para>
///     <para>
///         For CFP (Srinivasan et al., ASPLOS 2004 §4.2.1) each register also carries a NAV
///         ("Not a Value") bit: set when the register is the destination of an L2-miss-class load
///         or of an instruction that consumed a NAV source, cleared when a real value is finally
///         written or the register is re-allocated. NAV registers are deliberately <em>not</em>
///         marked ready — slice consumers are identified and drained by the issue stage's NAV
///         scan, never woken with a poison value.
///     </para>
/// </summary>
public sealed class PhysicalRegisterFile {
    private readonly bool[] _abandoned;
    private readonly bool[] _allocated;
    private readonly int[] _allocGen;
    private readonly bool[] _nav;
    private readonly bool[] _ready;
    private readonly bool[] _unmapped;
    private readonly int[] _useCount;
    private readonly ulong[] _values;

    public PhysicalRegisterFile(int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        Count = count;
        _values = new ulong[count];
        _ready = new bool[count];
        _useCount = new int[count];
        _unmapped = new bool[count];
        _abandoned = new bool[count];
        _nav = new bool[count];
        _allocated = new bool[count];
        _allocGen = new int[count];
        Array.Fill(_ready, true); // all arch regs start with a valid zero value
    }

    public int Count { get; }

    public ulong Read(int phys) {
        Validate(phys);
        return _values[phys];
    }

    public bool IsReady(int phys) {
        Validate(phys);
        return _ready[phys];
    }

    /// <summary>Writes a result and marks the register ready. Called by the CDB at Complete.</summary>
    public void Write(int phys, ulong value) {
        Validate(phys);
        _values[phys] = value;
        _ready[phys] = true;
        _nav[phys] = false;
    }

    /// <summary>Marks a register as pending (no valid value yet). Called at Dispatch/Rename.</summary>
    public void MarkPending(int phys) {
        Validate(phys);
        _ready[phys] = false;
        _nav[phys] = false;
        _unmapped[phys] = false;
        _abandoned[phys] = false;
        _allocated[phys] = true;
        _allocGen[phys]++;
    }

    /// <summary>
    ///     Monotonic per-slot allocation counter, bumped by <see cref="MarkPending" />. A discarded
    ///     instruction's recovery bookkeeping compares the generation it recorded at rename against
    ///     the current one before marking its destination reclaimable — under aggressive
    ///     reclamation the same physical register may have been legitimately freed and re-allocated
    ///     to someone else in the meantime.
    /// </summary>
    public int AllocationGeneration(int phys) {
        Validate(phys);
        return _allocGen[phys];
    }

    // ── CPR aggressive-reclamation state ────────────────────────────────────────

    /// <summary>Outstanding references: unread renamed readers + checkpoints snapshotting this register.</summary>
    public int UseCount(int phys) {
        Validate(phys);
        return _useCount[phys];
    }

    /// <summary>Adds a reference (a renamed reader, or a checkpoint whose snapshot maps to this register).</summary>
    public void AddRef(int phys) {
        Validate(phys);
        _useCount[phys]++;
    }

    /// <summary>Drops a reference (reader captured the value, or a holding checkpoint was released).</summary>
    public void Release(int phys) {
        Validate(phys);
        if (_useCount[phys] > 0) _useCount[phys]--;
    }

    /// <summary>True once the architectural register this physical register backed was renamed again.</summary>
    public bool IsUnmapped(int phys) {
        Validate(phys);
        return _unmapped[phys];
    }

    /// <summary>Sets the unmapped flag: the RAT no longer maps any architectural register here.</summary>
    public void MarkUnmapped(int phys) {
        Validate(phys);
        _unmapped[phys] = true;
    }

    /// <summary>
    ///     Marks that no value will ever arrive through this register name: its producer either
    ///     drained into the CFP slice buffer (and will re-acquire a fresh register via back-end
    ///     renaming unless the rename filter preserves this one) or was squashed. Satisfies the
    ///     "value can no longer arrive" leg of <see cref="IsReclaimable" /> for a never-written slot.
    /// </summary>
    public void MarkAbandoned(int phys) {
        Validate(phys);
        _abandoned[phys] = true;
    }

    /// <summary>
    ///     Whether this register is currently backed by an allocation (dispensed by
    ///     <see cref="RenameMap" /> and not yet returned to its free list). Guards against
    ///     double-freeing a register that recovery bookkeeping visits through more than one
    ///     discarded instruction after an intervening early reclaim + re-allocation.
    /// </summary>
    public bool IsAllocated(int phys) {
        Validate(phys);
        return _allocated[phys];
    }

    /// <summary>
    ///     True when the register may be returned to the free list: no outstanding references,
    ///     unmapped from the RAT, and no write can still arrive through this name. The caller
    ///     (CprTrain) pairs this with <see cref="MarkFreed" /> and <see cref="RenameMap.FreePhysical" />.
    /// </summary>
    public bool IsReclaimable(int phys) {
        Validate(phys);
        return _allocated[phys] && _useCount[phys] == 0 && _unmapped[phys]
            && (_ready[phys] || _abandoned[phys]);
    }

    /// <summary>Records that the register was returned to the free list.</summary>
    public void MarkFreed(int phys) {
        Validate(phys);
        _allocated[phys] = false;
        _unmapped[phys] = false;
        _abandoned[phys] = false;
        _nav[phys] = false;
        _useCount[phys] = 0;
    }

    /// <summary>
    ///     Marks phys registers [0, archCount) as the live allocation backing the initial identity
    ///     RAT, and everything above as free. Call after construction/reset by trains that use the
    ///     allocated-flag bookkeeping (CprTrain); OooeTrain never reads it.
    /// </summary>
    public void InitializeAllocation(int archCount) {
        for (var i = 0; i < Count; i++) _allocated[i] = i < archCount;
    }

    // ── CFP NAV bit ─────────────────────────────────────────────────────────────

    public bool IsNav(int phys) {
        Validate(phys);
        return _nav[phys];
    }

    /// <summary>Tags the register NAV; its producer has drained into the slice buffer.</summary>
    public void MarkNav(int phys) {
        Validate(phys);
        _nav[phys] = true;
    }

    /// <summary>
    ///     Clears the NAV tag without writing a value: the register's producer re-entered the
    ///     pipeline keeping this original name (a rename-filter live-out), so waiting consumers
    ///     should now block on its broadcast instead of draining into the slice buffer.
    /// </summary>
    public void ClearNav(int phys) {
        Validate(phys);
        _nav[phys] = false;
    }

    /// <summary>
    ///     Re-marks a register as a live RAT mapping: clears the unmapped/abandoned legs of the
    ///     reclaim condition. Called by recovery for every register in the restored RAT snapshot —
    ///     a register re-renamed only by now-squashed instructions is mapped again.
    /// </summary>
    public void MarkMapped(int phys) {
        Validate(phys);
        _unmapped[phys] = false;
        _abandoned[phys] = false;
    }

    public void Reset() {
        Array.Clear(_values);
        Array.Fill(_ready, true);
        Array.Clear(_useCount);
        Array.Clear(_unmapped);
        Array.Clear(_abandoned);
        Array.Clear(_nav);
        Array.Clear(_allocated);
    }

    private void Validate(int phys) {
        if ((uint)phys >= (uint)Count)
            throw new ArgumentOutOfRangeException(
                nameof(phys),
                $"Physical register {phys} is out of range [0, {Count})."
            );
    }
}