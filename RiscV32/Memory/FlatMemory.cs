using System.Runtime.CompilerServices;
using Mechanism;

namespace RiscV32.Memory;

/// <summary>
/// A simple flat byte-array memory. Sufficient for single-core simulation
/// without caches or memory-mapped I/O. Little-endian.
///
/// <paramref name="baseAddress"/> allows the backing array to start at an
/// address other than 0 (e.g. 0x80000000 for Spike-compatible DRAM layout),
/// so ELF images linked at high addresses do not require a multi-GB allocation.
/// All public addresses are virtual; the implementation subtracts the base
/// before indexing into the array.
/// </summary>
public sealed class FlatMemory : IMemory {
    private readonly byte[] _data;
    private readonly ulong _base;

    public int Size => _data.Length;

    public FlatMemory(int sizeBytes, ulong baseAddress = 0) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        _data = new byte[sizeBytes];
        _base = baseAddress;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Offset(ulong address) => (int)(address - _base);

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
        if (address < _base || address - _base + (ulong)bytes > (ulong)_data.Length)
            throw new AccessViolationException(
                $"Memory access out of bounds: address=0x{address:X8}, bytes={bytes}, base=0x{_base:X8}, size={_data.Length}"
            );
    }
}