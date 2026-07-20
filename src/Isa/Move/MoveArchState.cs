#region

using Mechanism;
using Move.Registers;

#endregion

namespace Move;

public sealed class MoveArchState : IArchState {
    public readonly ushort[] R = new ushort[8];
    public ushort AluIn1;

    public byte AluOp;
    public ushort AluOut;

    public ushort BrCond;

    public ushort MemAddr;
    public ushort MemOut;

    public MoveArchState() {
        var regs = new MoveRegisterFile(this);
        IntegerRegisters = regs;
    }

    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;

    // Move only ever runs on SingleCycleTrain, which has no forwarding overlay to swap this
    // property for; the setter exists solely to satisfy IArchState.
    public IRegisterFile IntegerRegisters { get; set; }
    public ISystemRegisters SystemRegisters => NullSystemRegisters.Instance;

    public IArchState Snapshot() {
        var s = new MoveArchState {
            Pc = Pc,
            PrivilegeLevel = PrivilegeLevel,
            AluOp = AluOp,
            AluIn1 = AluIn1,
            AluOut = AluOut,
            MemAddr = MemAddr,
            MemOut = MemOut,
            BrCond = BrCond,
        };
        Array.Copy(R, s.R, 8);
        return s;
    }

    public void Reset() {
        Pc = 0;
        Array.Clear(R);
        AluOp = 0;
        AluIn1 = 0;
        AluOut = 0;
        MemAddr = 0;
        MemOut = 0;
        BrCond = 0;
    }
}