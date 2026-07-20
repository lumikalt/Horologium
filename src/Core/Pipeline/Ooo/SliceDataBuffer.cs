#region

using Mechanism;

#endregion

namespace Pipeline.Ooo;

/// <summary>
///     One slice instruction parked in the <see cref="SliceDataBuffer" /> (Srinivasan et al.,
///     ASPLOS 2004 §4.2.2, Figure 4): opcode, up to two-and-a-half source descriptors (either a
///     recorded ready value read from a completed source register, or the physical name of a NAV
///     source to be resolved through the slice remapper on re-insertion), the destination's
///     physical map, and control state.
/// </summary>
public sealed class SdbEntry {
    public ITooth Instruction { get; init; } = null!;
    public ulong Pc { get; init; }
    public ulong InstrId { get; init; }

    /// <summary>Checkpoint identifier, for squashing slice instructions younger than a recovery point.</summary>
    public ulong CheckpointSeq { get; init; }

    /// <summary>The in-flight bookkeeping entry this slice instruction still owns.</summary>
    public CheckpointEntry Entry { get; init; } = null!;

    // Per-source descriptors (index 0..2). A source is exactly one of:
    //   unused        — Tag < 0
    //   recorded value — IsValue, Value holds the data read from a completed register at drain
    //   NAV name      — !IsValue, Tag is the original physical name; remapped at re-insertion
    public int Src1Tag { get; init; } = -1;
    public bool Src1IsValue { get; init; }
    public ulong Src1Value { get; init; }
    public int Src2Tag { get; init; } = -1;
    public bool Src2IsValue { get; init; }
    public ulong Src2Value { get; init; }
    public int Src3Tag { get; init; } = -1;
    public bool Src3IsValue { get; init; }
    public ulong Src3Value { get; init; }

    /// <summary>Original physical destination name (NAV-tagged at drain), or -1.</summary>
    public int DestPhys { get; init; } = -1;

    /// <summary>Architectural destination register, or -1.</summary>
    public int DestArch { get; init; } = -1;

    public int LqIdx { get; init; } = -1;
    public int SqIdx { get; init; } = -1;
    public ulong PredictedNextPc { get; init; }
}

/// <summary>
///     CFP Slice Data Buffer: a FIFO holding the forward slice of long-latency load misses —
///     the miss loads themselves plus every instruction that consumed a NAV source — together
///     with their completed source values and physical register maps, in data dependence order
///     (drain order). Because ready source <em>values</em> travel with the slice, the registers
///     they came from are released the moment the instruction drains; because the destination
///     maps are only names, the destination registers are released too and re-acquired by
///     back-end renaming at re-insertion. That is the entire non-blocking-register-file trick.
///     <para>
///         The buffer is modeled as an unordered-removal list rather than a hardware FIFO so a
///         recovery can squash entries by checkpoint identifier (ASPLOS 2004 §4.2.2 (b)) while
///         older slices survive. Drain order is preserved: re-insertion always takes the oldest
///         entry first, and a chained dependent miss re-drains by appending fresh entries at the
///         tail, which is safe because its consumers wait on the remapped destination name in the
///         issue queue rather than on SDB position.
///     </para>
/// </summary>
public sealed class SliceDataBuffer {
    private readonly List<SdbEntry> _entries = [];

    public SliceDataBuffer(int capacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int Count => _entries.Count;
    public bool IsFull => _entries.Count >= Capacity;
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>Oldest entry — the next candidate for re-insertion. Call only when non-empty.</summary>
    public SdbEntry Head => _entries[0];

    public void Append(SdbEntry entry) {
        if (IsFull) throw new InvalidOperationException("SDB is full. Check IsFull before appending.");
        _entries.Add(entry);
    }

    /// <summary>Removes and returns the oldest entry. Call only when non-empty.</summary>
    public SdbEntry PopHead() {
        SdbEntry e = _entries[0];
        _entries.RemoveAt(0);
        return e;
    }

    /// <summary>
    ///     Squashes every slice instruction belonging to checkpoint <paramref name="checkpointSeq" />
    ///     or younger — the recovery path's counterpart to the pipeline squash.
    /// </summary>
    public void SquashFromCheckpoint(ulong checkpointSeq) =>
        _entries.RemoveAll(e => e.CheckpointSeq >= checkpointSeq);

    public void Flush() => _entries.Clear();

    /// <summary>Enumerates entries oldest → youngest (drain order = data dependence order).</summary>
    public IEnumerable<SdbEntry> InOrder() => _entries;
}