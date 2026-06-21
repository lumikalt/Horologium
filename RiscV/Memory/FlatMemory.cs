using Mechanism;

namespace RiscV.Memory;

/// <summary>
/// A simple flat byte-array memory. Sufficient for single-core simulation
/// without caches or memory-mapped I/O. Little-endian.
/// </summary>
public sealed class FlatMemory : IMemory {
    private readonly byte[] _data;

    public int Size => _data.Length;

    public FlatMemory(int sizeBytes) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        _data = new byte[sizeBytes];
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        Span<byte> span = _data.AsSpan((int)address, data.Length);
        data.CopyTo(span);
    }

    public ulong Read(ulong address, int bytes) {
        ValidateAccess(address, bytes);
        ulong result = 0;
        for (var i = 0; i < bytes; i++) result |= (ulong)_data[address + (ulong)i] << (i * 8);
        return result;
    }

    public void Write(ulong address, ulong value, int bytes) {
        ValidateAccess(address, bytes);
        for (var i = 0; i < bytes; i++) _data[address + (ulong)i] = (byte)(value >> (i * 8));
    }

    private void ValidateAccess(ulong address, int bytes) {
        if (address + (ulong)bytes > (ulong)_data.Length)
            throw new AccessViolationException(
                $"Memory access out of bounds: address=0x{address:X8}, bytes={bytes}, size={_data.Length}"
            );
    }
}