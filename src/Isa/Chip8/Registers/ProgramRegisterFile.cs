#region

using Mechanism;

#endregion

namespace Chip8.Registers;

public class ProgramRegisterFile : ISystemRegisters {
    public const short I = 0x0;
    public const byte DelayTimer = 0x1;
    public const byte SoundTimer = 0x2;

    private readonly Dictionary<uint, ulong> _regs = new();

    public ulong Read(uint address, PrivilegeLevel currentPrivilege) => address switch {
        (uint)ProgramRegisterFile.I    => _regs.TryGetValue((uint)ProgramRegisterFile.I, out ulong val) ? val : 0,
        ProgramRegisterFile.DelayTimer => _regs.TryGetValue(ProgramRegisterFile.DelayTimer, out ulong val) ? val : 0,
        ProgramRegisterFile.SoundTimer => _regs.TryGetValue(ProgramRegisterFile.SoundTimer, out ulong val) ? val : 0,
        _                              => throw new NotImplementedException(),
    };

    public void Write(uint address, ulong value, PrivilegeLevel currentPrivilege) { _regs[address] = value; }

    public bool Exists(uint address) => address switch {
        (uint)ProgramRegisterFile.I    => true,
        ProgramRegisterFile.DelayTimer => true,
        ProgramRegisterFile.SoundTimer => true,
        _                              => false,
    };
}