#region

using Mechanism;
using Pdp8.Registers;

#endregion

namespace Pdp8;

public sealed class Pdp8ArchState : IArchState {
    private readonly Pdp8RegisterFile _regs = new();

    public Pdp8ArchState() => IntegerRegisters = _regs;

    public ulong Ac {
        get => _regs.Read(0);
        set => _regs.Write(0, value);
    }

    public ulong L {
        get => _regs.Read(1);
        set => _regs.Write(1, value);
    }

    public ulong Pc { get; set; }

    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;

    // Pdp8 only ever runs on SingleCycleTrain, which has no forwarding overlay to swap this
    // property for; the setter exists solely to satisfy IArchState.
    public IRegisterFile IntegerRegisters { get; set; }
    public ISystemRegisters SystemRegisters => NullSystemRegisters.Instance;

    public IArchState Snapshot() {
        var s = new Pdp8ArchState { Pc = Pc, PrivilegeLevel = PrivilegeLevel, };
        s.Ac = Ac;
        s.L = L;
        return s;
    }

    public void Reset() {
        Pc = 0;
        Ac = 0;
        L = 0;
    }
}