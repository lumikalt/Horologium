using Mechanism;
using RiscV.Registers;

namespace RiscV.State;

/// <summary>
/// The complete architectural state of one RV32I hart.
/// </summary>
public sealed class RvArchState : IArchState {
    private readonly IntegerRegisterFile _intRegs;

    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.Machine;
    public IRegisterFile IntegerRegisters => _intRegs;
    public ICsrFile? Csrs => CsrFile;

    /// <summary>Typed access to the concrete CSR file for internal use.</summary>
    internal CsrFile CsrFile { get; }

    public RvArchState() {
        _intRegs = new IntegerRegisterFile();
        CsrFile = new CsrFile();
    }

    private RvArchState(RvArchState source) {
        Pc = source.Pc;
        PrivilegeLevel = source.PrivilegeLevel;
        _intRegs = new IntegerRegisterFile();
        CsrFile = new CsrFile();

        // Copy integer registers
        for (var i = 0; i < 32; i++) _intRegs.Write(i, source._intRegs.Read(i));

        // Copy CSRs via direct access
        foreach (uint addr in new[] {
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