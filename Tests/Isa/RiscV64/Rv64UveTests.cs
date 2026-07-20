#region

using Mechanism;
using RiscV32.Decode;
using RiscV32.Memory;
using RiscV64.Execute;
using RiscV64.State;

#endregion

namespace Tests.Isa.RiscV64;

/// <summary>
///     UVE-on-RV64 GPR-write width coverage. so.v.mvvs, so.a.adde (integer path), and so.c.getvl/setvl
///     all move a 32-bit UVE lane/reduction/VL value into a destination GPR; Rv32Executor.Uve.cs does
///     this via an implicit uint-&gt;ulong widen, i.e. zero-extension. Neither the UVE2 dissertation
///     (~/dl/uve2.pdf — no XLEN/RV64 discussion at all) nor SPEC_NOTES.md say anything about RV64
///     GPR-write width. The AnaBSF Spike fork's so_v_mvvs.h settles the one case it implements:
///     WRITE_REG(destReg, value) with value typed uint32_t widens via an unsigned (zero-extending)
///     conversion to Spike's 64-bit reg_t. so.c.getvl/setvl aren't implemented in that Spike fork at
///     all, but the dissertation frames them as CSR-style (VLEN CSR read/write), and the Zicsr
///     convention is that a CSR narrower than XLEN zero-extends into rd — same direction as the Spike
///     evidence. so.a.adde's Spike implementation writes into the UVE stream-register bank rather than
///     the integer file at all, so it doesn't bear on GPR width, but there's no evidence pointing away
///     from zero-extension either. All three read the same way, so this locks in the existing
///     zero-extension behaviour (inherited unmodified from Rv32Executor — no Rv64Executor override
///     needed) rather than changing it.
/// </summary>
public class Rv64UveTests {
    private static ExecuteResult Exec(RvOp payload, Rv64ArchState state) {
        var instr = new RvInstruction(0x1000, 0xDEADBEEF, -1, [], ToothClass.Uve, payload);
        return new Rv64Executor().Execute(instr, state, new FlatMemory(256));
    }

    [Fact]
    public void SoVMvvs_ZeroExtendsHighBitSetValueIntoGpr() {
        var state = new Rv64ArchState();
        state.UveState.SetScalar(3, BitConverter.Int32BitsToSingle(unchecked((int)0x80000001u)));

        ExecuteResult er = Exec(new RvUveSoVMvvs(3, 7), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(0x80000001UL, state.IntegerRegisters.Read(7));
    }

    [Fact]
    public void SoASadde_Int_ZeroExtendsNegativeSumIntoGpr() {
        var state = new Rv64ArchState();
        uint[] vals = [0x7FFFFFFFu, 2u,]; // sum overflows into a negative int32 (0x80000001)
        state.UveState.SetVectorRaw(1, vals, 2, false);

        ExecuteResult er = Exec(new RvUveSoASadde(false, false, 7, 1), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(0x80000001UL, state.IntegerRegisters.Read(7));
    }

    [Fact]
    public void SoCGetvl_And_SoCSetvl_WorkOnRv64() {
        var state = new Rv64ArchState { UveState = { VectorLength = 8, }, };
        state.IntegerRegisters.Write(2, 32u);

        ExecuteResult setEr = Exec(new RvUveSoCSetvl(7, 2), state);
        setEr.SideEffect?.Invoke(state);
        Assert.Equal(32, state.UveState.VectorLength);
        Assert.Equal(8UL, state.IntegerRegisters.Read(7)); // old VL returned, zero-extended

        ExecuteResult getEr = Exec(new RvUveSoCGetvl(9), state);
        getEr.SideEffect?.Invoke(state);
        Assert.Equal(32UL, state.IntegerRegisters.Read(9));
    }
}