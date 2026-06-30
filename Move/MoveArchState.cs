using Mechanism;
using Move.Registers;

namespace Move;

public sealed class MoveArchState : IArchState {
    public readonly ushort[] R = new ushort[8];

    public byte   AluOp  = 0;
    public ushort AluIn1 = 0;
    public ushort AluOut = 0;

    public ushort MemAddr = 0;
    public ushort MemOut  = 0;

    public ushort BrCond = 0;

    private readonly MoveRegisterFile _regs;

    public MoveArchState() {
        _regs = new MoveRegisterFile(this);
    }

    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;
    public IRegisterFile IntegerRegisters => _regs;
    public ISystemRegisters SystemRegisters => NullSystemRegisters.Instance;

    public IArchState Snapshot() {
        var s = new MoveArchState {
            Pc             = Pc,
            PrivilegeLevel = PrivilegeLevel,
            AluOp          = AluOp,
            AluIn1         = AluIn1,
            AluOut         = AluOut,
            MemAddr        = MemAddr,
            MemOut         = MemOut,
            BrCond         = BrCond,
        };
        Array.Copy(R, s.R, 8);
        return s;
    }

    public void Reset() {
        Pc = 0;
        Array.Clear(R);
        AluOp = 0; AluIn1 = 0; AluOut = 0;
        MemAddr = 0; MemOut = 0; BrCond = 0;
    }
}
