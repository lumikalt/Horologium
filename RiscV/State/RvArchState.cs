using Mechanism;
using RiscV.Registers;

namespace RiscV.State;

/// <summary>
/// The complete architectural state of one RV32IFV hart.
/// </summary>
public sealed class RvArchState : IArchState {
    private readonly UnifiedRegisterFile _intRegs;

    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = RvPrivilege.Machine;
    public IRegisterFile IntegerRegisters => _intRegs;
    public ISystemRegisters SystemRegisters => CsrFile;

    /// <summary>Typed access to the concrete CSR file for internal use.</summary>
    internal CsrFile CsrFile { get; }

    /// <summary>Vector register file (v0-v31, VLEN=128 bits each).</summary>
    public VectorRegisterFile VectorRegisters { get; }

    /// <summary>UVE scalar accumulator registers and store-stream cursors (u0–u31).</summary>
    public UveState UveState { get; } = new();

    public IUveScalars? UveScalars => UveState;

    public RvArchState() {
        _intRegs = new UnifiedRegisterFile();
        CsrFile = new CsrFile();
        VectorRegisters = new VectorRegisterFile();
    }

    private RvArchState(RvArchState source) {
        Pc = source.Pc;
        PrivilegeLevel = source.PrivilegeLevel;
        _intRegs = new UnifiedRegisterFile();
        CsrFile = new CsrFile();
        VectorRegisters = new VectorRegisterFile();

        // Copy integer and floating-point registers (indices 0-63)
        for (var i = 0; i < 64; i++) _intRegs.Write(i, source._intRegs.Read(i));

        // Copy vector registers
        for (var i = 0; i < VectorRegisterFile.Count; i++) VectorRegisters.Write(i, source.VectorRegisters.Read(i));

        // UveState is not copied: Snapshot() is only called by in-order trains (FiveStage,
        // SingleCycle) which don't issue UVE ops. OooeTrain never calls Snapshot().

        // Copy CSRs via direct access
        foreach (uint addr in new[] {
                     CsrFile.Fflags, CsrFile.Frm, CsrFile.Fcsr,
                     CsrFile.Satp,
                     CsrFile.Sstatus, CsrFile.Sie, CsrFile.Stvec,
                     CsrFile.Sscratch, CsrFile.Sepc, CsrFile.Scause, CsrFile.Stval, CsrFile.Sip,
                     CsrFile.Mstatus, CsrFile.Misa, CsrFile.Medeleg, CsrFile.Mideleg,
                     CsrFile.Mie, CsrFile.Mtvec, CsrFile.Mcounteren,
                     CsrFile.Mscratch, CsrFile.Mepc, CsrFile.Mcause, CsrFile.Mtval, CsrFile.Mip,
                     CsrFile.Mcycle, CsrFile.Minstret,
                     CsrFile.Vstart, CsrFile.Vxsat, CsrFile.Vxrm, CsrFile.Vcsr,
                     CsrFile.Vl, CsrFile.Vtype, CsrFile.Vlenb,
                 })
            CsrFile.DirectWrite(addr, source.CsrFile.DirectRead(addr));
    }

    public IArchState Snapshot() => new RvArchState(this);

    public void Reset() {
        Pc = 0;
        PrivilegeLevel = RvPrivilege.Machine;
        _intRegs.Reset();
        CsrFile.Reset();
        VectorRegisters.Reset();
        UveState.Reset();
    }
}