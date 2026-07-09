using Mechanism;

namespace RiscV32.Registers;

/// <summary>
/// RV32I integer register file. 32 registers, 32 bits wide.
/// x0 is hardwired to zero — writes are silently ignored.
/// </summary>
public sealed class Rv32IntegerRegisterFile : IRegisterFile {
    private readonly uint[] _regs = new uint[32];

    public int Count => 32;
    public int Width => 32;

    public ulong Read(int index) {
        ValidateIndex(index);
        return _regs[index];
    }

    public void Write(int index, ulong value) {
        ValidateIndex(index);
        if (index == 0) return; // x0 is hardwired zero
        _regs[index] = (uint)value;
    }

    public void Reset() => Array.Clear(_regs);

    private static void ValidateIndex(int index) {
        if ((uint)index >= 32)
            throw new ArgumentOutOfRangeException(
                nameof(index),
                $"Register index {index} is out of range for RV32I (0–31)."
            );
    }
}