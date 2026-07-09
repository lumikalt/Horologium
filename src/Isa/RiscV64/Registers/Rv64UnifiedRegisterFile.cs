using Mechanism;

namespace RiscV64.Registers;

/// <summary>
/// Unified 64-entry register file for RV64F.
/// Indices 0-31: integer registers (x0 hardwired zero).
/// Indices 32-63: floating-point registers (f0-f31, all writable).
/// Values are stored as full 64-bit words.
/// </summary>
public sealed class Rv64UnifiedRegisterFile : IRegisterFile {
    private readonly ulong[] _regs = new ulong[64];

    public int Count => 64;
    public int Width => 64;

    public ulong Read(int index) {
        ValidateIndex(index);
        return _regs[index];
    }

    public void Write(int index, ulong value) {
        ValidateIndex(index);
        if (index == 0) return; // x0 hardwired zero
        _regs[index] = value;
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