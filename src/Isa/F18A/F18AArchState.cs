using F18A.Registers;
using Mechanism;

namespace F18A;

public sealed class F18AArchState : IArchState {
    private const int Depth = 8;

    private readonly uint[] _dstack = new uint[F18AArchState.Depth];
    private readonly uint[] _rstack = new uint[F18AArchState.Depth];
    private int _dsp;
    private int _rsp;

    public F18AArchState() => IntegerRegisters = new F18ARegisterFile(this);

    public uint T {
        get => _dstack[_dsp & (F18AArchState.Depth - 1)];
        set => _dstack[_dsp & (F18AArchState.Depth - 1)] = value;
    }

    public uint S => _dstack[(_dsp - 1) & (F18AArchState.Depth - 1)];

    public uint R {
        get => _rstack[_rsp & (F18AArchState.Depth - 1)];
        set => _rstack[_rsp & (F18AArchState.Depth - 1)] = value;
    }

    public uint A { get; set; }
    public uint B { get; set; }

    public ulong Pc { get; set; }

    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;

    // F18A only ever runs on SingleCycleTrain, which has no forwarding overlay to swap this
    // property for; the setter exists solely to satisfy IArchState.
    public IRegisterFile IntegerRegisters { get; set; }
    public ISystemRegisters SystemRegisters => NullSystemRegisters.Instance;

    public IArchState Snapshot() {
        var s = new F18AArchState();
        s.CopyFrom(this);
        return s;
    }

    public void Reset() {
        Pc = 0;
        PrivilegeLevel = PrivilegeLevel.User;
        _dsp = 0;
        _rsp = 0;
        A = 0;
        B = 0;
        Array.Clear(_dstack);
        Array.Clear(_rstack);
    }

    public void DPush(uint value) {
        _dsp++;
        T = value;
    }

    public uint DPop() {
        uint v = T;
        _dsp--;
        return v;
    }

    public void RPush(uint value) {
        _rsp++;
        R = value;
    }

    public uint RPop() {
        uint v = R;
        _rsp--;
        return v;
    }

    public void CopyFrom(F18AArchState src) {
        Pc = src.Pc;
        PrivilegeLevel = src.PrivilegeLevel;
        _dsp = src._dsp;
        _rsp = src._rsp;
        A = src.A;
        B = src.B;
        src._dstack.CopyTo(_dstack, 0);
        src._rstack.CopyTo(_rstack, 0);
    }
}