using Mechanism;
using RiscV.Registers;

namespace RiscV.State;

/// <summary>
/// The complete architectural state of one RV32IF hart.
/// </summary>
public sealed class RvArchState : IArchState {
    private readonly UnifiedRegisterFile _intRegs;

    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.Machine;
    public IRegisterFile IntegerRegisters => _intRegs;
    public ICsrFile? Csrs => CsrFile;

    /// <summary>Typed access to the concrete CSR file for internal use.</summary>
    internal CsrFile CsrFile { get; }

    public RvArchState() {
        _intRegs = new UnifiedRegisterFile();
        CsrFile = new CsrFile();
    }

    private RvArchState(RvArchState source) {
        Pc = source.Pc;
        PrivilegeLevel = source.PrivilegeLevel;
        _intRegs = new UnifiedRegisterFile();
        CsrFile = new CsrFile();

        // Copy integer and floating-point registers (indices 0-63)
        for (var i = 0; i < 64; i++) _intRegs.Write(i, source._intRegs.Read(i));

        // Copy CSRs via direct access
        foreach (uint addr in new[] {
                     CsrFile.Fflags, CsrFile.Frm, CsrFile.Fcsr,
                     CsrFile.Mstatus, CsrFile.Misa, CsrFile.Mie,
                     CsrFile.Mtvec, CsrFile.Mscratch, CsrFile.Mepc,
                     CsrFile.Mcause, CsrFile.Mtval, CsrFile.Mip,
                     CsrFile.Mcycle, CsrFile.Minstret,
                 })
            CsrFile.DirectWrite(addr, source.CsrFile.DirectRead(addr));
    }

    public IArchState Snapshot() => new RvArchState(this);

    public void Reset() {
        Pc = 0;
        PrivilegeLevel = PrivilegeLevel.Machine;
        _intRegs.Reset();
        CsrFile.Reset();
    }
}