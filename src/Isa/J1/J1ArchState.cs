using J1.Registers;
using Mechanism;

namespace J1;

public sealed class J1ArchState : IArchState {
    private const int StackDepth = 32;

    private readonly ushort[] _dstack = new ushort[J1ArchState.StackDepth];

    private readonly J1RegisterFile _regs;
    private readonly ushort[] _rstack = new ushort[J1ArchState.StackDepth];
    private int _rsp;

    public J1ArchState() => _regs = new J1RegisterFile(this);

    public ushort T {
        get => _dstack[Dsp & (J1ArchState.StackDepth - 1)];
        set => _dstack[Dsp & (J1ArchState.StackDepth - 1)] = value;
    }

    public ushort N {
        get => _dstack[(Dsp - 1) & (J1ArchState.StackDepth - 1)];
        set => _dstack[(Dsp - 1) & (J1ArchState.StackDepth - 1)] = value;
    }

    public int Dsp { get; private set; }

    public ushort R {
        get => _rstack[_rsp & (J1ArchState.StackDepth - 1)];
        set => _rstack[_rsp & (J1ArchState.StackDepth - 1)] = value;
    }

    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;
    public IRegisterFile IntegerRegisters => _regs;
    public ISystemRegisters SystemRegisters => NullSystemRegisters.Instance;

    public IArchState Snapshot() {
        var s = new J1ArchState { Pc = Pc, PrivilegeLevel = PrivilegeLevel, };
        Array.Copy(_dstack, s._dstack, J1ArchState.StackDepth);
        Array.Copy(_rstack, s._rstack, J1ArchState.StackDepth);
        s.Dsp = Dsp;
        s._rsp = _rsp;
        return s;
    }

    public void Reset() {
        Pc = 0;
        Dsp = 0;
        _rsp = 0;
        Array.Clear(_dstack);
        Array.Clear(_rstack);
    }

    public void DPush(ushort value) {
        Dsp = (Dsp + 1) & (J1ArchState.StackDepth - 1);
        _dstack[Dsp] = value;
    }

    public ushort DPop() {
        ushort v = T;
        Dsp = (Dsp - 1) & (J1ArchState.StackDepth - 1);
        return v;
    }

    public void RPush(ushort value) {
        _rsp = (_rsp + 1) & (J1ArchState.StackDepth - 1);
        _rstack[_rsp] = value;
    }

    public ushort RPop() {
        ushort v = R;
        _rsp = (_rsp - 1) & (J1ArchState.StackDepth - 1);
        return v;
    }
}