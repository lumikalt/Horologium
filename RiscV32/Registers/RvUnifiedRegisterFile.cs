using Mechanism;

namespace RiscV32.Registers;

/// <summary>
/// Unified 64-entry register file parameterized by word width.
/// Indices 0-31: integer registers (x0 hardwired zero).
/// Indices 32-63: floating-point registers (f0-f31, all writable).
/// Values are stored as 64-bit words; writes are masked to <paramref name="width"/> bits.
/// </summary>
public sealed class RvUnifiedRegisterFile(int width) : IRegisterFile {
    private readonly ulong[] _regs = new ulong[64];
    private readonly ulong _mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1UL;

    public int Count => 64;
    public int Width => width;

    public ulong Read(int index) {
        ValidateIndex(index);
        return _regs[index];
    }

    public void Write(int index, ulong value) {
        ValidateIndex(index);
        if (index == 0) return; // x0 hardwired zero
        _regs[index] = value & _mask;
    }

    public void Reset() => Array.Clear(_regs);

    private static void ValidateIndex(int index) {
        if ((uint)index >= 64)
            throw new ArgumentOutOfRangeException(
                nameof(index),
                $"Register index {index} is out of range (0–63)."
            );
    }
}
