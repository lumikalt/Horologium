#region

using Mechanism;

#endregion

namespace RiscV32.Trace;

/// <summary>
///     Pass-through <see cref="IMemory" /> that records the most recent access since
///     the last <see cref="Reset" />. A trace writer uses this to recover the
///     effective address of a load/store — which is <c>base_reg + imm</c>, computed
///     at runtime and not derivable from the instruction encoding alone (and the
///     base register may be overwritten by the instruction itself).
///     <para>
///         Unlike the OoO pipeline's CapturingMemory it does not defer writes; reads and
///         writes pass straight through to <paramref name="inner" />.
///     </para>
/// </summary>
public sealed class TracingMemory(IMemory inner) : IMemory {
    /// <summary>True if any access has occurred since the last <see cref="Reset" />.</summary>
    public bool HasAccess { get; private set; }

    /// <summary>Address of the most recent access.</summary>
    public ulong Address { get; private set; }

    /// <summary>Width in bytes of the most recent access.</summary>
    public int Bytes { get; private set; }

    /// <summary>True if the most recent access was a write.</summary>
    public bool IsWrite { get; private set; }

    /// <summary>Data value of the most recent access (value read for loads, value written for stores).</summary>
    public ulong Value { get; private set; }

    public ulong Read(ulong address, int bytes) {
        HasAccess = true;
        Address = address;
        Bytes = bytes;
        IsWrite = false;
        Value = inner.Read(address, bytes);
        return Value;
    }

    public void Write(ulong address, ulong value, int bytes) {
        HasAccess = true;
        Address = address;
        Bytes = bytes;
        IsWrite = true;
        Value = value;
        inner.Write(address, value, bytes);
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) => inner.Load(address, data);

    public void Reset() => HasAccess = false;
}