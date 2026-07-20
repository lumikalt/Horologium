#region

using Mechanism;

#endregion

namespace J1.Registers;

public sealed class J1RegisterFile(J1ArchState state) : IRegisterFile {
    public int Count => 2;
    public int Width => 16;
    public ulong Read(int index) => index == 0 ? state.T : state.N;

    public void Write(int index, ulong value) {
        if (index == 0)
            state.T = (ushort)value;
        else
            state.N = (ushort)value;
    }
}

public sealed class NullSystemRegisters : ISystemRegisters {
    public static readonly NullSystemRegisters Instance = new();
    public ulong Read(uint address, PrivilegeLevel _) => 0;
    public void Write(uint address, ulong value, PrivilegeLevel _) { }
    public bool Exists(uint address) => false;
}