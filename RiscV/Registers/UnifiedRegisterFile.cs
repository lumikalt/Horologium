using Mechanism;

namespace RiscV.Registers;

/// <summary>
/// Unified 64-entry register file for RV32F.
/// Indices 0-31: integer registers (x0 hardwired zero).
/// Indices 32-63: floating-point registers (f0-f31, all writable).
/// </summary>
public sealed class UnifiedRegisterFile : IRegisterFile {
    private readonly uint[] _regs = new uint[64];

    public int Count => 64;
    public int Width => 32;

    public ulong Read(int index) {
        ValidateIndex(index);
        return _regs[index];
    }

    public void Write(int index, ulong value) {
        ValidateIndex(index);
        if (index == 0) return; // x0 hardwired zero
        _regs[index] = (uint)value;
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