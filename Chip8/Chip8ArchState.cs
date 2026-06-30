using Chip8.Registers;
using Mechanism;

namespace Chip8;

public sealed class Chip8ArchState : IArchState {
    private readonly DataRegisterFile _vRegs = new();
    private readonly ProgramRegisterFile _programRegs = new();
    private readonly Stack<ushort> _stack = new();

    public ulong Pc { get; set; } = 0x200;
    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;
    public IRegisterFile IntegerRegisters => _vRegs;
    public ISystemRegisters SystemRegisters => _programRegs;

    public bool[] Display { get; } = new bool[64 * 32];
    public bool[] Keys { get; } = new bool[16];

    public ushort I {
        get => (ushort)_programRegs.Read((uint)ProgramRegisterFile.I, PrivilegeLevel.User);
        set => _programRegs.Write((uint)ProgramRegisterFile.I, value, PrivilegeLevel.User);
    }

    public byte DelayTimer {
        get => (byte)_programRegs.Read(ProgramRegisterFile.DelayTimer, PrivilegeLevel.User);
        set => _programRegs.Write(ProgramRegisterFile.DelayTimer, value, PrivilegeLevel.User);
    }

    public byte SoundTimer {
        get => (byte)_programRegs.Read(ProgramRegisterFile.SoundTimer, PrivilegeLevel.User);
        set => _programRegs.Write(ProgramRegisterFile.SoundTimer, value, PrivilegeLevel.User);
    }

    public void PushStack(ushort addr) => _stack.Push(addr);
    public ushort PopStack() => _stack.Pop();

    public bool TryGetPressedKey(out int key) {
        for (var i = 0; i < 16; i++) {
            if (!Keys[i]) continue;
            key = i;
            return true;
        }

        key = -1;
        return false;
    }

    public IArchState Snapshot() {
        var snap = new Chip8ArchState { Pc = Pc, PrivilegeLevel = PrivilegeLevel, };
        for (var i = 0; i < 16; i++) snap.IntegerRegisters.Write(i, IntegerRegisters.Read(i));
        snap.I = I;
        snap.DelayTimer = DelayTimer;
        snap.SoundTimer = SoundTimer;
        Display.CopyTo(snap.Display, 0);
        Keys.CopyTo(snap.Keys, 0);
        return snap;
    }

    public void Reset() {
        Pc = 0x200;
        PrivilegeLevel = PrivilegeLevel.User;
        for (var i = 0; i < 16; i++) _vRegs.Write(i, 0);
        I = 0;
        DelayTimer = 0;
        SoundTimer = 0;
        _stack.Clear();
        Array.Clear(Display);
        Array.Clear(Keys);
    }
}