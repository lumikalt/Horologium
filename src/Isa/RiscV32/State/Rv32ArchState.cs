#region

using Mechanism;
using RiscV32.Registers;

#endregion

namespace RiscV32.State;

/// <summary>
///     The complete architectural state of one RV32IFV hart.
/// </summary>
public class Rv32ArchState : IArchState {
    protected IRegisterFile IntRegs;

    public Rv32ArchState() : this(new Rv32UnifiedRegisterFile()) { }

    protected Rv32ArchState(IRegisterFile intRegs) {
        IntRegs = intRegs;
        CsrFile = new CsrFile();
        VectorRegisters = new VectorRegisterFile();
    }

    protected Rv32ArchState(Rv32ArchState source, IRegisterFile intRegs) {
        Pc = source.Pc;
        PrivilegeLevel = source.PrivilegeLevel;
        IntRegs = intRegs;
        CsrFile = new CsrFile();
        VectorRegisters = new VectorRegisterFile();

        // Copy integer and floating-point registers
        for (var i = 0; i < source.IntRegs.Count; i++) IntRegs.Write(i, source.IntRegs.Read(i));

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
                     CsrFile.Mcycle, CsrFile.Mcycleh, CsrFile.Minstret, CsrFile.Minstreth,
                     CsrFile.Vstart, CsrFile.Vxsat, CsrFile.Vxrm, CsrFile.Vcsr,
                     CsrFile.Vl, CsrFile.Vtype, CsrFile.Vlenb,
                 })
            CsrFile.DirectWrite(addr, source.CsrFile.DirectRead(addr));
    }

    /// <summary>Typed access to the concrete CSR file for internal use.</summary>
    internal CsrFile CsrFile { get; }

    /// <summary>Vector register file (v0-v31, VLEN=128 bits each).</summary>
    public VectorRegisterFile VectorRegisters { get; }

    /// <summary>UVE scalar accumulator registers and store-stream cursors (u0–u31).</summary>
    public UveState UveState { get; } = new();

    public ulong Pc { get; set; }
    public PrivilegeLevel PrivilegeLevel { get; set; } = RvPrivilege.Machine;

    public IRegisterFile IntegerRegisters {
        get => IntRegs;
        set => IntRegs = value;
    }

    public ISystemRegisters SystemRegisters => CsrFile;

    public IUveScalars UveScalars => UveState;

    public virtual IArchState Snapshot() => new Rv32ArchState(this, new Rv32UnifiedRegisterFile());

    public void Reset() {
        Pc = 0;
        PrivilegeLevel = RvPrivilege.Machine;
        IntRegs.Reset();
        CsrFile.Reset();
        VectorRegisters.Reset();
        UveState.Reset();
    }

    public void OnCycle() {
        uint lo = CsrFile.DirectRead(CsrFile.Mcycle);
        uint newLo = lo + 1;
        CsrFile.DirectWrite(CsrFile.Mcycle, newLo);
        if (newLo == 0) CsrFile.DirectWrite(CsrFile.Mcycleh, CsrFile.DirectRead(CsrFile.Mcycleh) + 1);
    }

    public void OnRetire() {
        uint lo = CsrFile.DirectRead(CsrFile.Minstret);
        uint newLo = lo + 1;
        CsrFile.DirectWrite(CsrFile.Minstret, newLo);
        if (newLo == 0) CsrFile.DirectWrite(CsrFile.Minstreth, CsrFile.DirectRead(CsrFile.Minstreth) + 1);
    }

    /// <summary>
    ///     Saves all present CSRs (via DirectRead), VRF (32 × 16 bytes), and UVE scalar state.
    ///     UVE store-stream cursors and pending config are transient mid-stream state and are not saved.
    /// </summary>
    public virtual void WriteState(BinaryWriter w) {
        // CSRs: write count then (address, value) pairs for all present entries.
        var count = 0;
        for (uint a = 0; a < 4096; a++)
            if (CsrFile.Exists(a))
                count++;
        w.Write(count);
        for (uint a = 0; a < 4096; a++) {
            if (!CsrFile.Exists(a)) continue;
            w.Write(a);
            w.Write(CsrFile.DirectRead(a));
        }

        // Vector register file: 32 registers × 16 bytes each.
        for (var i = 0; i < VectorRegisterFile.Count; i++) {
            byte[] vr = VectorRegisters.Read(i);
            w.Write(vr);
        }

        // UVE register state: lane 0 as float32 (scalar value), kind, stream-done, dim-done flags.
        for (var i = 0; i < UveState.Count; i++) {
            w.Write(BitConverter.Int32BitsToSingle((int)UveState.GetLane32(i, 0)));
            w.Write((byte)UveState.RegKind[i]);
            w.Write(UveState.StreamDone[i]);
            for (var d = 0; d < UveState.MaxDims; d++) w.Write(UveState.DimDone[i, d]);
        }
    }

    /// <summary>Restores state written by <see cref="WriteState" />.</summary>
    public virtual void ReadState(BinaryReader r) {
        int count = r.ReadInt32();
        for (var i = 0; i < count; i++) {
            uint addr = r.ReadUInt32();
            uint value = r.ReadUInt32();
            CsrFile.DirectWrite(addr, value);
        }

        for (var i = 0; i < VectorRegisterFile.Count; i++) {
            byte[] vr = r.ReadBytes(VectorRegisterFile.VLenB);
            VectorRegisters.Write(i, vr);
        }

        for (var i = 0; i < UveState.Count; i++) {
            UveState.SetLane32(i, 0, (uint)BitConverter.SingleToInt32Bits(r.ReadSingle()));
            UveState.RegKind[i] = (UveRegKind)r.ReadByte();
            UveState.StreamDone[i] = r.ReadBoolean();
            for (var d = 0; d < UveState.MaxDims; d++) UveState.DimDone[i, d] = r.ReadBoolean();
        }
    }
}