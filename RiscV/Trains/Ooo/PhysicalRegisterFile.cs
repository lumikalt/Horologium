namespace RiscV.Trains.Ooo;

/// <summary>
/// Holds speculative register values for OoOE execution.
///
/// Each slot has a ready bit: cleared at Dispatch when an instruction claims
/// the register as its destination; set again at Complete when the CDB
/// broadcasts the result. Waiting RS entries check IsReady before issuing.
/// </summary>
public sealed class PhysicalRegisterFile {
    private readonly ulong[] _values;
    private readonly bool[] _ready;

    public int Count { get; }

    public PhysicalRegisterFile(int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1, nameof(count));
        Count = count;
        _values = new ulong[count];
        _ready = new bool[count];
        Array.Fill(_ready, true); // all arch regs start with a valid zero value
    }

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
    }

    /// <summary>Marks a register as pending (no valid value yet). Called at Dispatch.</summary>
    public void MarkPending(int phys) {
        Validate(phys);
        _ready[phys] = false;
    }

    public void Reset() {
        Array.Clear(_values);
        Array.Fill(_ready, true);
    }

    private void Validate(int phys) {
        if ((uint)phys >= (uint)Count)
            throw new ArgumentOutOfRangeException(
                nameof(phys),
                $"Physical register {phys} is out of range [0, {Count})."
            );
    }
}