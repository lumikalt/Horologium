#region

using Mechanism;

#endregion

namespace Pdp8.Registers;

// Register 0 = AC (accumulator, 12-bit), Register 1 = L (link bit)
public sealed class Pdp8RegisterFile : IRegisterFile {
    private readonly ulong[] _r = new ulong[2];

    public int Count => 2;
    public int Width => 12;

    public ulong Read(int index) => index is 0 or 1 ? _r[index] : 0;

    public void Write(int index, ulong value) {
        switch (index) {
            case 0: _r[0] = value & 0xFFF; break;
            case 1: _r[1] = value & 1; break;
        }
    }
}

public sealed class NullSystemRegisters : ISystemRegisters {
    public static readonly NullSystemRegisters Instance = new();
    public ulong Read(uint address, PrivilegeLevel currentPrivilege) => 0;
    public void Write(uint address, ulong value, PrivilegeLevel currentPrivilege) { }
    public bool Exists(uint address) => false;
}