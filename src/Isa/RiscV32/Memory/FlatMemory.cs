#region

using System.Runtime.CompilerServices;
using Mechanism;

#endregion

namespace RiscV32.Memory;

/// <summary>
///     A simple flat byte-array memory. Sufficient for single-core simulation
///     without caches or memory-mapped I/O. Little-endian.
///     <c> baseAddress </c> allows the backing array to start at an
///     address other than 0 (e.g., 0x80000000 for Spike-compatible DRAM layout),
///     so ELF images linked at high addresses do not require a multi-GB allocation.
///     All public addresses are virtual; the implementation subtracts the base
///     before indexing into the array.
/// </summary>
public sealed class FlatMemory : ISnapshotableMemory {
    private readonly byte[] _data;

    public FlatMemory(int sizeBytes, ulong baseAddress = 0) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        _data = new byte[sizeBytes];
        BaseAddress = baseAddress;
    }

    public ulong BaseAddress { get; }

    public int SizeBytes => _data.Length;

    public void CopyTo(Span<byte> dest) => _data.AsSpan().CopyTo(dest);
    public void LoadFrom(ReadOnlySpan<byte> data) => data.CopyTo(_data);

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        Span<byte> span = _data.AsSpan(Offset(address), data.Length);
        data.CopyTo(span);
    }

    public ulong Read(ulong address, int bytes) {
        ValidateAccess(address, bytes);
        int off = Offset(address);
        return bytes switch {
            1 => _data[off],
            2 => Unsafe.ReadUnaligned<ushort>(ref _data[off]),
            4 => Unsafe.ReadUnaligned<uint>(ref _data[off]),
            8 => Unsafe.ReadUnaligned<ulong>(ref _data[off]),
            _ => ReadSlow(address, bytes),
        };
    }

    public void Write(ulong address, ulong value, int bytes) {
        ValidateAccess(address, bytes);
        int off = Offset(address);
        switch (bytes) {
            case 1:  _data[off] = (byte)value; break;
            case 2:  Unsafe.WriteUnaligned(ref _data[off], (ushort)value); break;
            case 4:  Unsafe.WriteUnaligned(ref _data[off], (uint)value); break;
            case 8:  Unsafe.WriteUnaligned(ref _data[off], value); break;
            default: WriteSlow(address, value, bytes); break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Offset(ulong address) => (int)(address - BaseAddress);

    private ulong ReadSlow(ulong address, int bytes) {
        ulong result = 0;
        for (var i = 0; i < bytes; i++) result |= (ulong)_data[Offset(address) + i] << (i * 8);
        return result;
    }

    private void WriteSlow(ulong address, ulong value, int bytes) {
        int off = Offset(address);
        for (var i = 0; i < bytes; i++) _data[off + i] = (byte)(value >> (i * 8));
    }

    private void ValidateAccess(ulong address, int bytes) {
        if (address < BaseAddress || address - BaseAddress + (ulong)bytes > (ulong)_data.Length)
            throw new AccessViolationException(
                $"Memory access out of bounds: address=0x{address:X8}, bytes={bytes}, base=0x{BaseAddress:X8}, size={_data.Length}"
            );
    }
}