using Mechanism;

namespace RiscV32.Registers;

/// <summary>
/// Unified 64-entry register file for RV32F/D.
/// Indices 0-31: integer registers (x0 hardwired zero, always 32-bit truncated).
/// Indices 32-63: floating-point registers (f0-f31, 64-bit; NaN-boxing by executor).
/// </summary>
public sealed class Rv32UnifiedRegisterFile : IRegisterFile {
    private readonly ulong[] _regs = new ulong[64];

    public int Count => 64;
    public int Width => 32; // XLEN

    public ulong Read(int index) {
        ValidateIndex(index);
        return _regs[index];
    }

    public void Write(int index, ulong value) {
        ValidateIndex(index);
        if (index == 0) return; // x0 hardwired zero
        // Integer registers stay 32-bit; FP registers store full 64 bits (for D extension).
        _regs[index] = index < 32 ? (uint)value : value;
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