#region

using Mechanism;

#endregion

namespace F18A.Registers;

// Register indices exposed to the Face register panel
public static class F18AReg {
    public const int T = 0;
    public const int S = 1;
    public const int A = 2;
    public const int B = 3;
    public const int R = 4;
}

public sealed class F18ARegisterFile(F18AArchState state) : IRegisterFile {
    public int Count => 5;
    public int Width => 18;

    public ulong Read(int index) => index switch {
        F18AReg.T => state.T,
        F18AReg.S => state.S,
        F18AReg.A => state.A,
        F18AReg.B => state.B,
        F18AReg.R => state.R,
        _         => 0ul,
    };

    public void Write(int index, ulong value) {
        var v = (uint)(value & 0x3FFFFu);
        switch (index) {
            case F18AReg.T: state.T = v; break;
            case F18AReg.A: state.A = v; break;
            case F18AReg.B: state.B = v; break;
            case F18AReg.R: state.R = v; break;
        }
    }
}

public sealed class NullSystemRegisters : ISystemRegisters {
    public static readonly NullSystemRegisters Instance = new();
    public ulong Read(uint address, PrivilegeLevel _) => 0;
    public void Write(uint address, ulong value, PrivilegeLevel _) { }
    public bool Exists(uint address) => false;
}