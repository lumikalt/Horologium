using Mechanism;

namespace Move.Registers;

public sealed class MoveRegisterFile(MoveArchState state) : IRegisterFile {
    public int Count => 8;
    public int Width => 16;
    public ulong Read(int index) => index is >= 0 and < 8 ? state.R[index] : 0ul;
    public void Write(int index, ulong value) {
        if (index is >= 0 and < 8) state.R[index] = (ushort)value;
    }
}

public sealed class NullSystemRegisters : ISystemRegisters {
    public static readonly NullSystemRegisters Instance = new();
    public ulong Read(uint address, PrivilegeLevel _) => 0;
    public void Write(uint address, ulong value, PrivilegeLevel _) { }
    public bool Exists(uint address) => false;
}
