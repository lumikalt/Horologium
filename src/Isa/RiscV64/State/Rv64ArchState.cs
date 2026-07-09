using Mechanism;
using RiscV32.State;
using RiscV64.Registers;

namespace RiscV64.State;

/// <summary>
/// The complete architectural state of one RV64IFV hart.
/// Extends Rv32ArchState with a 64-bit integer register file.
/// </summary>
public class Rv64ArchState : Rv32ArchState {
    public Rv64ArchState() : base(new Rv64UnifiedRegisterFile()) { }

    protected Rv64ArchState(Rv64ArchState source) : base(source, new Rv64UnifiedRegisterFile()) { }

    public override IArchState Snapshot() => new Rv64ArchState(this);
}