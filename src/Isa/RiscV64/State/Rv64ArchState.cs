using Mechanism;
using RiscV32.State;
using RiscV64.Registers;

namespace RiscV64.State;

/// <summary>
///     The complete architectural state of one RV64IFV hart.
///     Extends Rv32ArchState with a 64-bit integer register file and a dedicated satp CSR (see
///     <see cref="Rv64CsrFile" />) wide enough for Sv39's MODE field.
/// </summary>
public class Rv64ArchState : Rv32ArchState {
    public Rv64ArchState() : base(new Rv64UnifiedRegisterFile()) { }

    protected Rv64ArchState(Rv64ArchState source) : base(source, new Rv64UnifiedRegisterFile()) =>
        Rv64Csrs.Satp = source.Rv64Csrs.Satp;

    internal Rv64CsrFile Rv64Csrs { get; } = new();

    public override IArchState Snapshot() => new Rv64ArchState(this);

    public override void WriteState(BinaryWriter w) {
        base.WriteState(w);
        w.Write(Rv64Csrs.Satp);
    }

    public override void ReadState(BinaryReader r) {
        base.ReadState(r);
        Rv64Csrs.Satp = r.ReadUInt64();
    }
}