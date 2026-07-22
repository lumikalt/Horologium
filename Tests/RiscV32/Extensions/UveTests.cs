#region

using Mechanism;
using Orrery.Streaming;
using Pipeline;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.State;

#endregion

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Tests for the UVE extension: stream setup, scalar broadcast, arithmetic ops, and branches.
///     Also includes a SAXPY-style integration test through the OoO pipeline.
/// </summary>
public class UveTests {
    // ── Helpers ───────────────────────────────────────────────────────────────

    // Execute a single UVE instruction directly via the executor.
    private static ExecuteResult Exec(RvOp payload, Rv32ArchState state, IMemory? memory = null) {
        var instr = new RvInstruction(0x1000, 0xDEADBEEF, -1, [], ToothClass.Uve, payload);
        return new Rv32Executor().Execute(instr, state, memory ?? new FlatMemory(256));
    }

    // ── Encode helpers ────────────────────────────────────────────────────────

    // so.v.dp.(width) ud, rs1 — custom-1, funct7=0x56; funct3: 0=b, 1=h, 2=w, 3=d
    private static uint SoVDp(int ud, int rs1, int elemBytes) {
        uint funct3 = elemBytes switch {
            1 => 0u, 2 => 1u, 4 => 2u, 8 => 3u, _ => throw new ArgumentOutOfRangeException(),
        };
        return (0x56u << 25) | (uint)((rs1 & 0x1F) << 15) | (funct3 << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu;
    }

    private static uint SoVDpW(int ud, int rs1) => SoVDp(ud, rs1, 4);

    // so.v.mvvs rd, us1 — funct7=0x54, rs2=16
    private static uint SoVMvvs(int rd, int us1) =>
        (0x54u << 25) | (16u << 20) | (uint)((us1 & 0x1F) << 15) | (uint)((rd & 0x1F) << 7) | 0x2Bu;

    // so.v.mvsv.(width) ud, rs1 — funct7=0x54, rs2=24; funct3: 0=b, 1=h, 2=w, 3=d
    private static uint SoVMvsv(int ud, int rs1, int elemBytes) {
        uint funct3 = elemBytes switch {
            1 => 0u, 2 => 1u, 4 => 2u, 8 => 3u, _ => throw new ArgumentOutOfRangeException(),
        };
        return (0x54u << 25) | (24u << 20) | (uint)((rs1 & 0x1F) << 15) | (funct3 << 12) | (uint)((ud & 0x1F) << 7)
             | 0x2Bu;
    }

    // so.a.fp ud, usrc1, usrc2 — custom-1; (funct7>>3, funct3) encodes the operation.
    // usrc2=-1 for unary ops (Abs, Inc, Dec) — rs2 field set to 0 in encoding.
    private static uint SoAFp(UveFpOp op, int ud, int usrc1, int usrc2) {
        (uint funct3, uint top4) = op switch {
            UveFpOp.Add     => (1u, 0u),
            UveFpOp.Sub     => (5u, 0u),
            UveFpOp.Mul     => (1u, 1u),
            UveFpOp.Div     => (5u, 1u),
            UveFpOp.Mac     => (5u, 3u),
            UveFpOp.Min     => (1u, 4u),
            UveFpOp.Max     => (5u, 4u),
            UveFpOp.Abs     => (1u, 3u),
            UveFpOp.Inc     => (1u, 6u),
            UveFpOp.Dec     => (5u, 6u),
            UveFpOp.Adde    => (1u, 2u),
            UveFpOp.AddeAcc => (1u, 2u), // same group+lower+FP; rs2=1 distinguishes from Adde
            UveFpOp.Mine    => (1u, 5u),
            UveFpOp.Maxe    => (5u, 5u),
            _               => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        uint funct7 = top4 << 3;
        // AddeAcc: rs2=1 in the binary distinguishes it from Adde (rs2=0).
        int rs2Enc = op == UveFpOp.AddeAcc ? 1 : usrc2 < 0 ? 0 : usrc2;
        return (funct7 << 25) | (uint)((rs2Enc & 0x1F) << 20) | (uint)((usrc1 & 0x1F) << 15)
             | (funct3 << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu;
    }

    // so.a.int ud, usrc1, usrc2 — integer arithmetic; usrc2=-1 for unary ops (Abs, Inc, Dec).
    private static uint SoAInt(UveIntOp op, bool signed, int ud, int usrc1, int usrc2) {
        (int group, bool upper) = op switch {
            UveIntOp.Add     => (0, false),
            UveIntOp.Sub     => (0, true),
            UveIntOp.Mul     => (1, false),
            UveIntOp.Div     => (1, true),
            UveIntOp.Mac     => (3, true),
            UveIntOp.Min     => (4, false),
            UveIntOp.Max     => (4, true),
            UveIntOp.Abs     => (3, false),
            UveIntOp.Inc     => (6, false),
            UveIntOp.Dec     => (6, true),
            UveIntOp.Adde    => (2, false),
            UveIntOp.AddeAcc => (2, false), // same as Adde but rs2=1
            UveIntOp.Mine    => (5, false),
            UveIntOp.Maxe    => (5, true),
            _                => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        // Spike encodes ABS_SG with funct3=0 (type=0 slot); no ABS_US variant exists.
        int opType = op == UveIntOp.Abs ? 0 : signed ? 2 : 0;
        var funct3 = (uint)(opType | (upper ? 4 : 0));
        var funct7 = (uint)(group << 3);
        int rs2Enc = op == UveIntOp.AddeAcc ? 1 : usrc2 < 0 ? 0 : usrc2;
        return (funct7 << 25) | (uint)((rs2Enc & 0x1F) << 20) | (uint)((usrc1 & 0x1F) << 15)
             | (funct3 << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu;
    }

    // so.a.logic ud, usrc1, usrc2 — bitwise logic; usrc2=-1 for Not (unary).
    private static uint SoALogic(UveLogicOp op, int ud, int usrc1, int usrc2) {
        int f3 = op switch {
            UveLogicOp.Nand => 0,
            UveLogicOp.And  => 1,
            UveLogicOp.Nor  => 2,
            UveLogicOp.Or   => 3,
            UveLogicOp.Not  => 4,
            UveLogicOp.Xor  => 5,
            _               => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        const uint funct7 = 12u << 3;
        int rs2Enc = usrc2 < 0 ? 0 : usrc2;
        return (funct7 << 25) | (uint)((rs2Enc & 0x1F) << 20) | (uint)((usrc1 & 0x1F) << 15)
             | ((uint)f3 << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu;
    }

    // so.a.shift.v ud, usrc1, usrc2 — vector-vector shift (amount from u-reg).
    private static uint SoAShiftV(UveShiftOp op, int ud, int usrc1, int usrc2) {
        int f3 = op switch {
            UveShiftOp.Sll => 0, UveShiftOp.Srl => 2, UveShiftOp.Sra => 4,
            _              => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        const uint funct7 = 13u << 3;
        return (funct7 << 25) | (uint)((usrc2 & 0x1F) << 20) | (uint)((usrc1 & 0x1F) << 15)
             | ((uint)f3 << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu;
    }

    // so.a.shift.s ud, usrc1, rs2 — scalar-register shift (amount from integer register).
    private static uint SoAShiftS(UveShiftOp op, int ud, int usrc1, int rs2) {
        int f3 = op switch {
            UveShiftOp.Sll => 1, UveShiftOp.Srl => 3, UveShiftOp.Sra => 5,
            _              => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        const uint funct7 = 13u << 3;
        return (funct7 << 25) | (uint)((rs2 & 0x1F) << 20) | (uint)((usrc1 & 0x1F) << 15)
             | ((uint)f3 << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu;
    }

    // SO_C group (custom-1, funct7=0x58): stream lifecycle and VL control.
    // ss.stop ud (SO_C_BREAK, funct3=3) / ss.suspend ud (SUSPD, 1) / ss.resume ud (RESUM, 2)
    // ss.getvl rd (GETVL, funct3=7) / ss.setvl rd, rs1 (SETVL, funct3=0)
    private static uint SoCBreak(int ud) => SoC(3, ud, 0, 0);
    private static uint SoCSuspd(int ud) => SoC(1, ud, 0, 0);
    private static uint SoCGetvl(int rd) => SoC(7, rd, 0, 0);
    private static uint SoCSetvl(int rd, int rs1) => SoC(0, rd, rs1, 0);

    private static uint SoC(int funct3, int rd, int rs1, int rs2) =>
        (0x58u << 25) | (uint)((rs2 & 0x1F) << 20) | (uint)((rs1 & 0x1F) << 15)
      | ((uint)(funct3 & 7) << 12) | (uint)((rd & 0x1F) << 7) | 0x2Bu;

    // sadde rd, usrc1 / fsadde rd, usrc1 — group 2 upper; funct3 upper bit set.
    // isFp=true → type=1 (FP, funct3=5); isFp=false → type=0 (US int, funct3=4).
    // acc=true → rs2=1 (accumulate); acc=false → rs2=0 (overwrite).
    private static uint SoASadde(bool isFp, bool acc, int rd, int usrc1) {
        uint funct3 = 4u | (isFp ? 1u : 0u);
        const uint funct7 = 2u << 3;
        uint rs2 = acc ? 1u : 0u;
        return (funct7 << 25) | (rs2 << 20) | (uint)((usrc1 & 0x1F) << 15)
             | (funct3 << 12) | (uint)((rd & 0x1F) << 7) | 0x2Bu;
    }

    // Bit-cast helpers for integer ↔ float round-trips through Scalars[].
    private static float Ib(int v) => BitConverter.Int32BitsToSingle(v);
    private static float Ub(uint v) => BitConverter.Int32BitsToSingle((int)v);
    private static int Rib(float f) => BitConverter.SingleToInt32Bits(f);
    private static uint Rub(float f) => (uint)BitConverter.SingleToInt32Bits(f);

    // UVE non-standard B-type: bits[31:29]=111, bit28=imm[12](sign), bits[27:22]=imm[10:5],
    // bit7=imm[11], bits[11:8]=imm[4:1]. rs2=0b00001 → notDone; rs2=0b00000 → done.
    private static uint UveBTypeImm(int imm, uint rs1, uint rs2, uint funct3) {
        var i = (uint)imm;
        uint bit12 = (i >> 12) & 1;
        uint bit11 = (i >> 11) & 1;
        uint bits10To5 = (i >> 5) & 0x3F;
        uint bits4To1 = (i >> 1) & 0xF;
        return (0b111u << 29) | (bit12 << 28) | (bits10To5 << 22) | (rs2 << 20) | (rs1 << 15)
             | (funct3 << 12) | (bits4To1 << 8) | (bit11 << 7) | 0x2Bu;
    }

    // so.b.nc urs, imm — funct3=7, bit20=1 (notDone). Author-corrected encoding (2026-07-22):
    // Appendix B / Spike instead put this at funct3=0 — see SPEC_NOTES.md's "Branch `d` field" entry.
    private static uint SoBNc(int urs, int imm) => UveBTypeImm(imm, (uint)urs, 0b00001u, 0x7);

    // so.b.c urs, imm — funct3=7, bit20=0 (done)
    private static uint SoBc(int urs, int imm) => UveBTypeImm(imm, (uint)urs, 0b00000u, 0x7);

    // so.b.ndc.D urs, imm — funct3=D-1, bit20=1 (notDone). The dim param is the raw funct3
    // value, counting dimensions from the OUTERMOST (Spike convention). D ranges 1..7
    // (funct3 0..6); dc.8 no longer exists under the corrected encoding (funct3=7 is so.b.nc).
    private static uint SoBNdcD(int urs, int dim, int imm) => UveBTypeImm(imm, (uint)urs, 0b00001u, (uint)dim);

    // so.b.dc.D urs, imm — funct3=D-1, bit20=0 (done)
    private static uint SoBdcD(int urs, int dim, int imm) => UveBTypeImm(imm, (uint)urs, 0b00000u, (uint)dim);

    // ss.sta.ld.w ud, rs1 — funct2=0, funct3=0b110 (isLoad=1, ew=4)
    private static uint SsStaLdW(int ud, int rs1) =>
        (uint)(((rs1 & 0x1F) << 15) | (0x6u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // ss.sta.ld.w_v ud, rs1 — vector mode; rs3 bit[3]=1; bits[2:0]=dimIndex (7=innermost)
    // vecCfgDimBits: 0..6=explicit, 7=innermost (-1 in Horologium)
    private static uint SsStaLdWv(int ud, int rs1, int vecCfgDimBits = 7) {
        int rs3 = 0x8 | (vecCfgDimBits & 0x7);
        return ((uint)(rs3 & 0x1F) << 27) | ((uint)(rs1 & 0x1F) << 15) | (0x6u << 12) | ((uint)(ud & 0x1F) << 7)
             | 0x0Bu;
    }

    // ss.sta.ld.* ud, rs1 — funct2=0, funct3 encodes load+ew
    private static uint SsStaLdEw(int ud, int rs1, uint funct3) =>
        (uint)((rs1 & 0x1F) << 15) | (funct3 << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu;

    // ss.sta.st.w ud, rs1 — funct2=0, funct3=0b010 (isLoad=0, ew=4)
    private static uint SsStaStW(int ud, int rs1) =>
        (uint)(((rs1 & 0x1F) << 15) | (0x2u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // ss.app ud, rs1Offset, rs2, rs3 — funct2=1, funct3=0
    private static uint SsApp(int ud, int rs1Offset, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | (0x1u << 25) | (uint)((rs2 & 0x1F) << 20)
             | (uint)((rs1Offset & 0x1F) << 15) | (0x0u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // ss.end ud, rs1Offset, rs2, rs3 — funct2=2, funct3=0
    private static uint SsEnd(int ud, int rs1Offset, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | (0x2u << 25) | (uint)((rs2 & 0x1F) << 20)
             | (uint)((rs1Offset & 0x1F) << 15) | (0x0u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // ss.app.mod (UVE2): funct2=1 (APP), funct3=4 (MOD), b[24:22]=behavior, ta[21:20]=target
    // (Size=0, Stride=1, Offset=2), tdim[17:15]=target dim (outermost-first; 7="linked"),
    // rs3[31:27]=displacement register. Trigger dim is positional (last-appended dimension).
    private static uint SsAppMod(
        int ud,
        int tdim,
        StreamModifierTarget target,
        StreamModifierBehavior behavior,
        int rs3Disp
    ) {
        uint ta = target switch {
            StreamModifierTarget.Size   => 0u,
            StreamModifierTarget.Stride => 1u,
            StreamModifierTarget.Offset => 2u,
            _                           => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        return ((uint)(rs3Disp & 0x1F) << 27) | (0x1u << 25) | ((uint)behavior << 22) | (ta << 20)
             | ((uint)(tdim & 0x7) << 15) | (0x4u << 12) | ((uint)(ud & 0x1F) << 7) | 0x0Bu;
    }

    // ss.app.ind ud, tdim, target, behavior, rs1_indsrc — dynamic (indirect) modifier; funct2=1,
    // funct3=6, bit27=0 (distinguishes from ss.app.sgi, same funct2/funct3 with bit27=1).
    // tdim[30:28]; rs2[24:20]=(behavior<<2)|target; rs1=source stream id.
    private static uint SsAppInd(
        int ud,
        int tdim,
        StreamModifierTarget target,
        StreamModifierBehavior behavior,
        int rs1Source
    ) {
        uint ta = target switch {
            StreamModifierTarget.Size   => 0u,
            StreamModifierTarget.Stride => 1u,
            StreamModifierTarget.Offset => 2u,
            _                           => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        uint rs2 = ((uint)behavior << 2) | ta;
        return ((uint)(tdim & 0x7) << 28) | (0x1u << 25) | (rs2 << 20) | ((uint)(rs1Source & 0x1F) << 15)
             | (0x6u << 12) | ((uint)(ud & 0x1F) << 7) | 0x0Bu;
    }

    // ss.app.sgi: funct2=1, funct3=6, bit27=1; rs1=source stream id, rs2=(behavior<<2)
    private static uint SsAppSgi(int ud, int rs1Source, StreamModifierBehavior behavior) {
        uint rs2Literal = (uint)behavior << 2;
        return (1u << 27) | (0x1u << 25) | (rs2Literal << 20) | ((uint)(rs1Source & 0x1F) << 15)
             | (0x6u << 12) | ((uint)(ud & 0x1F) << 7) | 0x0Bu;
    }

    // ss.end.sgi: funct2=2, funct3=6, bit27=1; same field layout as ss.app.sgi
    private static uint SsEndSgi(int ud, int rs1Source, StreamModifierBehavior behavior) {
        uint rs2Literal = (uint)behavior << 2;
        return (1u << 27) | (0x2u << 25) | (rs2Literal << 20) | ((uint)(rs1Source & 0x1F) << 15)
             | (0x6u << 12) | ((uint)(ud & 0x1F) << 7) | 0x0Bu;
    }

    // EBREAK — halts the pipeline
    private static uint EBreak() => 0x00100073u;

    // ADDI rd, rs1, imm — used to set up integer registers in integration tests
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | (0x0 << 12) | ((rd & 0x1F) << 7) | 0x13);

    // ── Executor unit tests ───────────────────────────────────────────────────

    [Fact]
    public void SoVDpW_WritesBroadcastScalar() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(5, (uint)BitConverter.SingleToInt32Bits(3.14f));

        ExecuteResult er = Exec(new RvUveSoVDp(4, 5, 4), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(3.14f, state.UveState.GetScalar(4), 4);
        Assert.Equal(UveRegKind.Scalar, state.UveState.RegKind[4]);
    }

    [Fact]
    public void SoVDp_Byte_MasksToLowByte() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(5, 0xDEAD00ABu);
        ExecuteResult er = Exec(new RvUveSoVDp(4, 5, 1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(0xAB, BitConverter.SingleToInt32Bits(state.UveState.GetScalar(4)));
        Assert.Equal(UveRegKind.Scalar, state.UveState.RegKind[4]);
    }

    [Fact]
    public void SoVMvvs_WritesFirstElementToIntegerReg() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(3, BitConverter.Int32BitsToSingle(0x12345678));
        ExecuteResult er = Exec(new RvUveSoVMvvs(3, 7), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(0x12345678u, (uint)state.IntegerRegisters.Read(7));
    }

    [Fact]
    public void SoVMvsv_Word_SetsScalarSlot() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(2, 0xCAFEBABEu);
        ExecuteResult er = Exec(new RvUveSoVMvsv(6, 2, 4), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(unchecked((int)0xCAFEBABEu), BitConverter.SingleToInt32Bits(state.UveState.GetScalar(6)));
        Assert.Equal(UveRegKind.Scalar, state.UveState.RegKind[6]);
    }

    [Fact]
    public void SoVMvsv_Byte_MasksToLowByte() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(2, 0xDEAD00CDu);
        ExecuteResult er = Exec(new RvUveSoVMvsv(6, 2, 1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(0xCD, BitConverter.SingleToInt32Bits(state.UveState.GetScalar(6)));
        Assert.Equal(UveRegKind.Scalar, state.UveState.RegKind[6]);
    }

    [Fact]
    public void SoAFp_Mul_ComputesProduct() {
        var state = new Rv32ArchState();
        // Pipeline would inject source values; simulate by pre-setting scalars
        state.UveState.SetScalar(1, 3.0f);
        state.UveState.SetScalar(2, 4.0f);

        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Mul, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(12.0f, state.UveState.GetScalar(5), 4);
    }

    [Fact]
    public void SoAFp_Add_ComputesSum() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, 2.5f);
        state.UveState.SetScalar(2, 7.5f);

        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Add, 0, 1, 2), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(10.0f, state.UveState.GetScalar(0), 4);
    }

    [Fact]
    public void SoAFp_ToStoreStream_WritesMemory() {
        var state = new Rv32ArchState();
        var mem = new FlatMemory(64);

        // Configure u3 as a store stream starting at address 0
        var storeStream = new UveStoreStream {
            BaseAddress = 0, ElementBytes = 4,
            Dimensions = [new StreamDimension(4, 4),], Indices = [0,],
        };
        storeStream.Initialize();
        state.UveState.StoreStreams[3] = storeStream;
        state.UveState.RegKind[3] = UveRegKind.StoreStream;

        state.UveState.SetScalar(1, 5.0f);
        state.UveState.SetScalar(2, 3.0f);

        // so.a.mul.fp u3, u1, u2 → should write 5*3=15 to address 0 and advance cursor
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Mul, 3, 1, 2), state, mem);
        er.SideEffect?.Invoke(state);

        float written = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(0, 4));
        Assert.Equal(15.0f, written, 4);
        Assert.Equal(4UL, state.UveState.StoreStreams[3]!.CurrentAddress); // cursor advanced to next element
    }

    /// <summary>
    ///     so.v.mv's destination writing through to memory when bound to an active store stream —
    ///     the gap found while porting the UVE2 <c>stream</c> benchmark's Copy kernel (c = a via
    ///     so.v.mv), where <see cref="ExecuteUveSoVMv" /> used to only update vd's own lanes,
    ///     unlike the arithmetic ops' <c>UveWriteResult</c> path (see
    ///     <see cref="SoAFp_ToStoreStream_WritesMemory" />). Confirmed against Spike's
    ///     so_v_mv.h/so_v_mvt.h, which both write through the same generic per-register path used
    ///     by every writer.
    /// </summary>
    [Fact]
    public void SoVMv_ToStoreStream_WritesMemory() {
        var state = new Rv32ArchState();
        var mem = new FlatMemory(64);

        var storeStream = new UveStoreStream {
            BaseAddress = 0, ElementBytes = 4,
            Dimensions = [new StreamDimension(4, 4),], Indices = [0,],
        };
        storeStream.Initialize();
        state.UveState.StoreStreams[3] = storeStream;
        state.UveState.RegKind[3] = UveRegKind.StoreStream;

        state.UveState.SetScalar(1, 7.0f);

        // so.v.mv u3, u1, p0 → should write 7.0 to address 0 and advance the cursor
        ExecuteResult er = Exec(new RvUveSoVMv(false, 3, 1, 0), state, mem);
        er.SideEffect?.Invoke(state);

        float written = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(0, 4));
        Assert.Equal(7.0f, written, 4);
        Assert.Equal(4UL, state.UveState.StoreStreams[3]!.CurrentAddress); // cursor advanced to next element
        Assert.Equal(UveRegKind.StoreStream, state.UveState.RegKind[3]); // still a store stream, not downgraded
    }

    [Fact]
    public void SoBNc_TakenWhenNotDone() {
        var state = new Rv32ArchState();
        state.UveState.StreamDone[1] = false; // stream isn't exhausted

        // so.b.nc u1, -12 — should take branch back by 12 bytes
        ExecuteResult er = Exec(new RvUveSoBNc(1, -12), state);

        Assert.True(er.BranchTaken);
        Assert.Equal(0x1000UL - 12UL, er.BranchTarget); // PC was 0x1000 in Exec helper
    }

    [Fact]
    public void SoBNc_NotTakenWhenDone() {
        var state = new Rv32ArchState();
        state.UveState.StreamDone[1] = true; // stream exhausted

        ExecuteResult er = Exec(new RvUveSoBNc(1, -12), state);

        Assert.False(er.BranchTaken);
    }

    // ── Decoder tests ─────────────────────────────────────────────────────────

    [Fact]
    public void Decoder_SoBNc_Roundtrip() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        uint enc = SoBNc(1, -8);
        mem.Load(0, BitConverter.GetBytes(enc));

        ITooth tooth = dec.Decode(0, mem);

        Assert.IsType<RvUveSoBNc>(tooth.Payload);
        var op = (RvUveSoBNc)tooth.Payload!;
        Assert.Equal(1, op.Urs);
        Assert.Equal(-8, op.Imm);
    }

    [Fact]
    public void Decoder_SoAFp_Mul_Roundtrip() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        uint enc = SoAFp(UveFpOp.Mul, 5, 1, 2);
        mem.Load(0, BitConverter.GetBytes(enc));

        ITooth tooth = dec.Decode(0, mem);

        Assert.IsType<RvUveSoAFp>(tooth.Payload);
        var op = (RvUveSoAFp)tooth.Payload!;
        Assert.Equal(UveFpOp.Mul, op.Op);
        Assert.Equal(5, op.Ud);
        Assert.Equal(1, op.Usrc1);
        Assert.Equal(2, op.Usrc2);
    }

    // Helper: encode so.a.fp with explicit ps3 field (bits[27:25] = ps3 in funct7).
    private static uint SoAFpWithPs3(UveFpOp op, int ud, int usrc1, int usrc2, int ps3) {
        (uint funct3, uint top4) = op switch {
            UveFpOp.Add => (1u, 0u), UveFpOp.Mul => (1u, 1u),
            _           => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        uint funct7 = (top4 << 3) | ((uint)ps3 & 7);
        int rs2Enc = usrc2 < 0 ? 0 : usrc2;
        return (funct7 << 25) | (uint)((rs2Enc & 0x1F) << 20) | (uint)((usrc1 & 0x1F) << 15)
             | (funct3 << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu;
    }

    [Fact]
    public void Decoder_SoAFp_Ps3_RoundTrip() {
        // Confirm that bits[27:25] = ps3 are decoded and stored in Ps3.
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        uint enc = SoAFpWithPs3(UveFpOp.Add, 3, 1, 2, 5);
        mem.Load(0, BitConverter.GetBytes(enc));

        var op = (RvUveSoAFp)dec.Decode(0, mem).Payload!;
        Assert.Equal(UveFpOp.Add, op.Op);
        Assert.Equal(5, op.Ps3);
    }

    [Fact]
    public void SoAFp_GoverningPredicate_InactiveLanesMerge() {
        // Governing predicate p1: lanes 0 and 2 active, lanes 1 and 3 inactive.
        // Predicate byte for lane i (float32): (i+1)*4-1 = i*4+3.
        // u1 = [1, 2, 3, 4], u2 = [10, 20, 30, 40], ud (u3) = [100, 200, 300, 400] initially.
        // so.a.add.fp u3, u1, u2 with ps3=1:
        //   lane 0 (active):   1 + 10 = 11  → write
        //   lane 1 (inactive): keep existing = 200
        //   lane 2 (active):   3 + 30 = 33  → write
        //   lane 3 (inactive): keep existing = 400
        var state = new Rv32ArchState();
        uint[] src1 = [
            (uint)BitConverter.SingleToInt32Bits(1f),
            (uint)BitConverter.SingleToInt32Bits(2f),
            (uint)BitConverter.SingleToInt32Bits(3f),
            (uint)BitConverter.SingleToInt32Bits(4f),
        ];
        uint[] src2 = [
            (uint)BitConverter.SingleToInt32Bits(10f),
            (uint)BitConverter.SingleToInt32Bits(20f),
            (uint)BitConverter.SingleToInt32Bits(30f),
            (uint)BitConverter.SingleToInt32Bits(40f),
        ];
        uint[] dest = [
            (uint)BitConverter.SingleToInt32Bits(100f),
            (uint)BitConverter.SingleToInt32Bits(200f),
            (uint)BitConverter.SingleToInt32Bits(300f),
            (uint)BitConverter.SingleToInt32Bits(400f),
        ];
        state.UveState.SetVectorRaw(1, src1, 4, false);
        state.UveState.SetVectorRaw(2, src2, 4, false);
        state.UveState.SetVectorRaw(3, dest, 4, false);

        // p1: all-false by default; set representative bytes for lanes 0 and 2.
        state.UveState.PredicateRegs[1][3] = true;  // lane 0 active
        state.UveState.PredicateRegs[1][11] = true; // lane 2 active
        // bytes 7 and 15 remain false → lanes 1 and 3 inactive

        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Add, 3, 1, 2, 1), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(11f, BitConverter.Int32BitsToSingle((int)state.UveState.GetLane32(3, 0)), 4);
        Assert.Equal(200f, BitConverter.Int32BitsToSingle((int)state.UveState.GetLane32(3, 1)), 4); // merged
        Assert.Equal(33f, BitConverter.Int32BitsToSingle((int)state.UveState.GetLane32(3, 2)), 4);
        Assert.Equal(400f, BitConverter.Int32BitsToSingle((int)state.UveState.GetLane32(3, 3)), 4); // merged
    }

    [Fact]
    public void SoAFp_PartialVl_ZeroingMode_ZerosExcessLanes() {
        // Source registers have ValidElements=2 (simulating VL=2 after ss.setvl).
        // pm=0 (zeroing): dest lanes 2 and 3 must be zeroed even if they held old values.
        var state = new Rv32ArchState();
        var f1 = (uint)BitConverter.SingleToInt32Bits(1f);
        var f2 = (uint)BitConverter.SingleToInt32Bits(2f);
        _ = (uint)BitConverter.SingleToInt32Bits(3f);
        var sentinel = (uint)BitConverter.SingleToInt32Bits(99f);
        // src: 2 valid elements, pm=0 (merging=false → zeroing)
        state.UveState.SetVectorRaw(1, [f1, f2, sentinel, sentinel,], 2, false);
        state.UveState.SetVectorRaw(2, [f1, f2, sentinel, sentinel,], 2, false);
        // dest
        //
        // preloaded with old values in all 4 lanes
        state.UveState.SetVectorRaw(3, [sentinel, sentinel, sentinel, sentinel,], 4, false);

        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Add, 3, 1, 2), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(2f, BitConverter.Int32BitsToSingle((int)state.UveState.GetLane32(3, 0)), 4);
        Assert.Equal(4f, BitConverter.Int32BitsToSingle((int)state.UveState.GetLane32(3, 1)), 4);
        Assert.Equal(0u, state.UveState.GetLane32(3, 2)); // zeroed
        Assert.Equal(0u, state.UveState.GetLane32(3, 3)); // zeroed
        Assert.Equal(2, state.UveState.ValidElements[3]);
    }

    [Fact]
    public void SoAFp_PartialVl_MergingMode_PreservesExcessLanes() {
        // Source registers have ValidElements=2, pm=1 (merging).
        // Dest lanes 2 and 3 must keep their old values, not be zeroed.
        var state = new Rv32ArchState();
        var f1 = (uint)BitConverter.SingleToInt32Bits(1f);
        var f2 = (uint)BitConverter.SingleToInt32Bits(2f);
        var old2 = (uint)BitConverter.SingleToInt32Bits(77f);
        var old3 = (uint)BitConverter.SingleToInt32Bits(88f);
        // src: 2 valid elements, pm=1 (merging=true)
        state.UveState.SetVectorRaw(1, [f1, f2, 0u, 0u,], 2, true);
        state.UveState.SetVectorRaw(2, [f1, f2, 0u, 0u,], 2, true);
        // dest preloaded; lanes 2 and 3 must survive
        state.UveState.SetVectorRaw(3, [0u, 0u, old2, old3,], 4, false);

        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Add, 3, 1, 2), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(2f, BitConverter.Int32BitsToSingle((int)state.UveState.GetLane32(3, 0)), 4);
        Assert.Equal(4f, BitConverter.Int32BitsToSingle((int)state.UveState.GetLane32(3, 1)), 4);
        Assert.Equal(77f, BitConverter.Int32BitsToSingle((int)state.UveState.GetLane32(3, 2)), 4); // merged
        Assert.Equal(88f, BitConverter.Int32BitsToSingle((int)state.UveState.GetLane32(3, 3)), 4); // merged
        Assert.Equal(2, state.UveState.ValidElements[3]);
    }

    [Fact]
    public void SoASadde_GoverningPredicate_SkipsInactiveLanes() {
        // sadde with ps3=1 (p1): only elements where predicate byte (i+1)*4-1 is true contribute.
        // u1 = [1, 2, 3, 4] (scalar ints), p1 lanes 0 and 2 active.
        // Expected sum = 1 + 3 = 4.
        var state = new Rv32ArchState();
        uint[] vals = [1u, 2u, 3u, 4u,];
        state.UveState.SetVectorRaw(1, vals, 4, false);

        state.UveState.PredicateRegs[1][3] = true;  // lane 0 active
        state.UveState.PredicateRegs[1][11] = true; // lane 2 active

        ExecuteResult er = Exec(new RvUveSoASadde(false, false, 5, 1, 1), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(4u, (uint)state.IntegerRegisters.Read(5));
    }

    // ── Multi-dim stream unit tests ───────────────────────────────────────────

    [Fact]
    public void SsStaLdW_CreatesPendingConfig_NoDimension() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(1, 0x1000); // base

        ExecuteResult er = Exec(new RvUveSsStaLdW(2, 1), state);
        er.SideEffect?.Invoke(state);

        PendingStreamConfig? cfg = state.UveState.PendingConfig[2];
        Assert.NotNull(cfg);
        Assert.Equal(0x1000UL, cfg.BaseAddress);
        Assert.Equal(4, cfg.ElementBytes);
        Assert.True(cfg.IsLoad);
        Assert.Empty(cfg.Dimensions);
    }

    [Fact]
    public void SsApp_AppendsDimensionToPendingConfig() {
        var state = new Rv32ArchState();
        var cfg = new PendingStreamConfig { BaseAddress = 0x2000, ElementBytes = 4, IsLoad = true, };
        state.UveState.PendingConfig[3] = cfg;

        state.IntegerRegisters.Write(2, 3); // outer count
        state.IntegerRegisters.Write(3, 8); // outer stride: 8 elements × 4 bytes = 32 bytes stored

        ExecuteResult er = Exec(new RvUveSsApp(3, 0, 2, 3), state);
        er.SideEffect?.Invoke(state);

        Assert.Single(state.UveState.PendingConfig[3]!.Dimensions);
        Assert.Equal(3L, state.UveState.PendingConfig[3]!.Dimensions[0].Count);
        Assert.Equal(32L, state.UveState.PendingConfig[3]!.Dimensions[0].Stride); // 8 elems × 4 bytes
    }

    [Fact]
    public void SsEnd_ActivatesMultiDimLoadStream() {
        // Config order is outermost-first (Spike); ss.end appends the innermost dimension,
        // and the descriptor comes out in the engine's innermost-first order.
        var state = new Rv32ArchState();
        var cfg = new PendingStreamConfig { BaseAddress = 0x3000, ElementBytes = 4, IsLoad = true, };
        cfg.Dimensions.Add(new StreamDimension(4, 4));  // outermost dim
        cfg.Dimensions.Add(new StreamDimension(3, 32)); // middle dim
        state.UveState.PendingConfig[1] = cfg;

        state.IntegerRegisters.Write(2, 2); // innermost count
        state.IntegerRegisters.Write(3, 0); // innermost stride (unused here)

        ExecuteResult er = Exec(new RvUveSsEnd(1, 0, 2, 3), state);

        // Should produce a StreamConfig with 3 dimensions, innermost first
        Assert.True(er.StreamConfig.HasValue);
        Assert.Equal(1, er.StreamConfig!.Value.StreamId);
        StreamDescriptor desc = er.StreamConfig.Value.Descriptor;
        Assert.Equal(0x3000UL, desc.BaseAddress);
        Assert.Equal(3, desc.Dimensions.Length);
        Assert.Equal(2L, desc.Dimensions[0].Count); // ss.end dim = innermost
        Assert.Equal(3L, desc.Dimensions[1].Count);
        Assert.Equal(4L, desc.Dimensions[2].Count); // first-configured dim = outermost

        er.SideEffect?.Invoke(state);
        Assert.Null(state.UveState.PendingConfig[1]);
        Assert.Equal(UveRegKind.LoadStream, state.UveState.RegKind[1]);
    }

    [Fact]
    public void SsApp_Rs1OffsetAccumulatesIntoOffsetBytes() {
        var state = new Rv32ArchState();
        var cfg = new PendingStreamConfig { BaseAddress = 0x2000, ElementBytes = 4, IsLoad = true, };
        state.UveState.PendingConfig[3] = cfg;

        state.IntegerRegisters.Write(1, 3); // rs1: offset=3 → adds 3*4=12 bytes
        state.IntegerRegisters.Write(2, 5); // count
        state.IntegerRegisters.Write(3, 4); // stride

        ExecuteResult er = Exec(new RvUveSsApp(3, 1, 2, 3), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(12L, state.UveState.PendingConfig[3]!.OffsetBytes);
    }

    [Fact]
    public void SsEnd_Rs1OffsetShiftsBaseAddress() {
        var state = new Rv32ArchState();
        var cfg = new PendingStreamConfig { BaseAddress = 0x1000, ElementBytes = 4, IsLoad = true, };
        state.UveState.PendingConfig[0] = cfg;

        state.IntegerRegisters.Write(1, 2); // rs1: offset=2 → adds 2*4=8 bytes → base becomes 0x1008
        state.IntegerRegisters.Write(2, 5); // count
        state.IntegerRegisters.Write(3, 4); // stride

        ExecuteResult er = Exec(new RvUveSsEnd(0, 1, 2, 3), state);

        Assert.True(er.StreamConfig.HasValue);
        Assert.Equal(0x1008UL, er.StreamConfig!.Value.Descriptor.BaseAddress);
    }

    [Fact]
    public void SsEnd_Rs1OffsetStacksWithSsAppOffset() {
        // ss.app rs1=1 → +1*4=4 bytes; ss.end rs1=3 → +3*4=12 bytes; total=16 → base=0x1000+16=0x1010
        var state = new Rv32ArchState();
        var cfg = new PendingStreamConfig { BaseAddress = 0x1000, ElementBytes = 4, IsLoad = true, };
        cfg.Dimensions.Add(new StreamDimension(4, 4));
        cfg.OffsetBytes = 4; // simulates ss.app already having accumulated offset=1*4
        state.UveState.PendingConfig[0] = cfg;

        state.IntegerRegisters.Write(1, 3); // rs1: offset=3 → adds 3*4=12 bytes
        state.IntegerRegisters.Write(2, 2); // count
        state.IntegerRegisters.Write(3, 0); // stride

        ExecuteResult er = Exec(new RvUveSsEnd(0, 1, 2, 3), state);

        Assert.True(er.StreamConfig.HasValue);
        Assert.Equal(0x1010UL, er.StreamConfig!.Value.Descriptor.BaseAddress);
    }

    [Fact]
    public void SoBNdc_TakenWhenDimNotComplete() {
        var state = new Rv32ArchState();
        state.UveState.DimDone[2, 0] = false; // dim 0 not yet complete

        ExecuteResult er = Exec(new RvUveSoBNdc(2, 0, -8), state);

        Assert.True(er.BranchTaken);
        Assert.Equal(0x1000UL - 8UL, er.BranchTarget);
    }

    [Fact]
    public void SoBNdc_NotTakenWhenDimComplete() {
        var state = new Rv32ArchState();
        state.UveState.DimDone[2, 0] = true; // dim 0 just completed

        ExecuteResult er = Exec(new RvUveSoBNdc(2, 0, -8), state);

        Assert.False(er.BranchTaken);
    }

    // ── sb.c / sb.dc tests ────────────────────────────────────────────────────

    [Fact]
    public void SoBc_TakenWhenDone() {
        var state = new Rv32ArchState();
        state.UveState.StreamDone[3] = true;

        ExecuteResult er = Exec(new RvUveSoBc(3, 16), state);

        Assert.True(er.BranchTaken);
        Assert.Equal(0x1000UL + 16UL, er.BranchTarget);
    }

    [Fact]
    public void SoBc_NotTakenWhenNotDone() {
        var state = new Rv32ArchState();
        state.UveState.StreamDone[3] = false;

        ExecuteResult er = Exec(new RvUveSoBc(3, 16), state);

        Assert.False(er.BranchTaken);
    }

    [Fact]
    public void SoBdc_TakenWhenDimDone() {
        var state = new Rv32ArchState();
        state.UveState.DimDone[1, 2] = true;

        ExecuteResult er = Exec(new RvUveSoBdc(1, 2, -20), state);

        Assert.True(er.BranchTaken);
        Assert.Equal(0x1000UL - 20UL, er.BranchTarget);
    }

    [Fact]
    public void SoBdc_NotTakenWhenDimNotDone() {
        var state = new Rv32ArchState();
        state.UveState.DimDone[1, 2] = false;

        ExecuteResult er = Exec(new RvUveSoBdc(1, 2, -20), state);

        Assert.False(er.BranchTaken);
    }

    [Fact]
    public void Decoder_SoBc_Roundtrip() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoBc(5, 24)));

        ITooth tooth = dec.Decode(0, mem);

        Assert.IsType<RvUveSoBc>(tooth.Payload);
        var op = (RvUveSoBc)tooth.Payload!;
        Assert.Equal(5, op.Urs);
        Assert.Equal(24, op.Imm);
    }

    [Fact]
    public void Decoder_SoBdcD_Roundtrip() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoBdcD(2, 3, -16)));

        ITooth tooth = dec.Decode(0, mem);

        Assert.IsType<RvUveSoBdc>(tooth.Payload);
        var op = (RvUveSoBdc)tooth.Payload!;
        Assert.Equal(2, op.Urs);
        Assert.Equal(3, op.Dim);
        Assert.Equal(-16, op.Imm);
    }

    // ── StreamingEngine element-width tests ───────────────────────────────────

    [Fact]
    public void StreamingEngine_ByteElements_ReadsCorrectly() {
        // A stream of 4 bytes read one-at-a-time from a tightly packed array
        var mem = new FlatMemory(16);
        mem.Load(0, [0x0A, 0x0B, 0x0C, 0x0D,]);
        var desc = new StreamDescriptor(0, 1, [new StreamDimension(4, 1),]);

        var se = new StreamingEngine(8);
        se.Configure(0, desc);
        for (var i = 0; i < 8; i++) se.Step(mem);

        Assert.Equal(0x0A, (int)(uint)se.Consume(0));
        Assert.Equal(0x0B, (int)(uint)se.Consume(0));
        Assert.Equal(0x0C, (int)(uint)se.Consume(0));
        Assert.Equal(0x0D, (int)(uint)se.Consume(0));
        Assert.True(se.IsExhausted(0));
    }

    [Fact]
    public void StreamingEngine_2D_VisitsAllElements() {
        var dims = new StreamDimension[] {
            new(4, 4),
            new(3, 32),
        };
        var desc = new StreamDescriptor(0x0000, 4, dims);
        var mem = new FlatMemory(0x200);
        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 4; c++)
            mem.Load((ulong)(r * 32 + c * 4), BitConverter.GetBytes((float)(r * 4 + c + 1)));

        var se = new StreamingEngine(16);
        se.Configure(0, desc);
        for (var i = 0; i < 30; i++) se.Step(mem);

        for (var r = 0; r < 3; r++)
        for (var c = 0; c < 4; c++) {
            float got = BitConverter.Int32BitsToSingle((int)(uint)se.Consume(0));
            Assert.Equal(r * 4 + c + 1.0f, got, 4);
        }

        Assert.True(se.IsExhausted(0));
    }

    [Fact]
    public void Decoder_SsStaLdW_Roundtrip() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        uint enc = SsStaLdW(2, 1);
        mem.Load(0, BitConverter.GetBytes(enc));

        ITooth tooth = dec.Decode(0, mem);

        Assert.IsType<RvUveSsStaLdW>(tooth.Payload);
        var op = (RvUveSsStaLdW)tooth.Payload!;
        Assert.Equal(2, op.Ud);
        Assert.Equal(1, op.Rs1Base);
        Assert.Equal(4, op.ElementBytes);
        Assert.Equal(ToothClass.Uve, tooth.Class);
    }

    [Fact]
    public void Decoder_SsStaLdEw_DecodesElementBytes() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        // funct3=0b101: isLoad=1, ew=1<<(0b101&3)=1<<1=2 → .h
        mem.Load(0, BitConverter.GetBytes(SsStaLdEw(3, 2, 0b101u)));

        ITooth tooth = dec.Decode(0, mem);

        var op = Assert.IsType<RvUveSsStaLdW>(tooth.Payload);
        Assert.Equal(3, op.Ud);
        Assert.Equal(2, op.Rs1Base);
        Assert.Equal(2, op.ElementBytes);
    }

    [Fact]
    public void Decoder_SsStaLdWV_InnerMost_DecodesVectorMode() {
        // ss.sta.ld.w_v ud=1, rs1=2 — innermost (vecCfgDimBits=7 → rs3=0xF, VecCfgDim=-1)
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SsStaLdWv(1, 2)));
        var op = Assert.IsType<RvUveSsStaLdW>(new Rv32Decoder().Decode(0, mem).Payload);
        Assert.Equal(1, op.Ud);
        Assert.Equal(2, op.Rs1Base);
        Assert.Equal(4, op.ElementBytes);
        Assert.True(op.IsVectorMode);
        Assert.Equal(-1, op.VecCfgDim); // innermost sentinel
    }

    [Fact]
    public void Decoder_SsStaLdWV_ExplicitDim_DecodesVectorMode() {
        // ss.sta.ld.w_v_2 ud=3, rs1=4 — vecCfgDimBits=1 → rs3=0x9, VecCfgDim=1
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SsStaLdWv(3, 4, 1)));
        var op = Assert.IsType<RvUveSsStaLdW>(new Rv32Decoder().Decode(0, mem).Payload);
        Assert.Equal(3, op.Ud);
        Assert.Equal(4, op.Rs1Base);
        Assert.Equal(4, op.ElementBytes);
        Assert.True(op.IsVectorMode);
        Assert.Equal(1, op.VecCfgDim);
    }

    [Fact]
    public void Decoder_SsStaLdW_ScalarMode_HasNoVectorMode() {
        // Scalar variant: rs3=0 → IsVectorMode=false
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SsStaLdW(2, 1)));
        var op = Assert.IsType<RvUveSsStaLdW>(new Rv32Decoder().Decode(0, mem).Payload);
        Assert.False(op.IsVectorMode);
        Assert.False(op.MergingPredication);
        Assert.Equal(0, op.MemLevel);
    }

    [Fact]
    public void Decoder_SsStaLdW_PmAndMemFields() {
        // ss.sta.ld.w.m.mem2: pm bit[31]=1 (merging predication), mem bits[23:22]=2 (cache level)
        var mem = new FlatMemory(16);
        uint enc = SsStaLdW(2, 1) | (1u << 31) | (2u << 22);
        mem.Load(0, BitConverter.GetBytes(enc));
        var op = Assert.IsType<RvUveSsStaLdW>(new Rv32Decoder().Decode(0, mem).Payload);
        Assert.True(op.MergingPredication);
        Assert.Equal(2, op.MemLevel);
        Assert.False(op.IsVectorMode);
    }

    [Fact]
    public void Decoder_SsStaLdWInds_MemField() {
        // ss.sta.ld.w_inds_mem1: inds bit[24]=1, mem bits[23:22]=1
        var mem = new FlatMemory(16);
        uint enc = SsStaLdW(3, 2) | (1u << 24) | (1u << 22);
        mem.Load(0, BitConverter.GetBytes(enc));
        var op = Assert.IsType<RvUveSsStaLdWInds>(new Rv32Decoder().Decode(0, mem).Payload);
        Assert.Equal(3, op.Ud);
        Assert.Equal(1, op.MemLevel);
    }

    [Fact]
    public void Decoder_SoBNdcD_Roundtrip() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        uint enc = SoBNdcD(2, 1, -12); // stream=u2, dim=1, imm=-12
        mem.Load(0, BitConverter.GetBytes(enc));

        ITooth tooth = dec.Decode(0, mem);

        Assert.IsType<RvUveSoBNdc>(tooth.Payload);
        var op = (RvUveSoBNdc)tooth.Payload!;
        Assert.Equal(2, op.Urs);
        Assert.Equal(1, op.Dim);
        Assert.Equal(-12, op.Imm);
    }

    /// <summary>
    ///     Transcribes the UVE2 author's corrected branch-`d` encoding table directly (2026-07-22,
    ///     see SPEC_NOTES.md's "Branch `d` field" entry) using raw <see cref="UveBTypeImm" /> calls
    ///     with hand-picked funct3 literals — deliberately not going through the
    ///     <see cref="SoBNc" />/<see cref="SoBNdcD" /> encoder helpers, since those and the decoder
    ///     would silently agree with each other even if both encoded the wrong table (a round-trip
    ///     test alone can't catch that). Covers every point of the author's table: funct3=0 → dc.1,
    ///     funct3=6 → dc.7, funct3=7 → the no-suffix EOS-equivalent form.
    /// </summary>
    [Fact]
    public void Decoder_SoBBranchTable_MatchesAuthorCorrectedEncoding() {
        var dec = new Rv32Decoder();

        // funct3=0 (SO.B.NC.1 in the author's table) → so.b.ndc with Dim=0 (dc.1).
        var mem0 = new FlatMemory(16);
        mem0.Load(0, BitConverter.GetBytes(UveBTypeImm(-8, 3, 0b00001u, 0x0)));
        var op0 = Assert.IsType<RvUveSoBNdc>(dec.Decode(0, mem0).Payload);
        Assert.Equal(3, op0.Urs);
        Assert.Equal(0, op0.Dim);

        // funct3=6 (SO.B.NC.7) → so.b.ndc with Dim=6 (dc.7).
        var mem6 = new FlatMemory(16);
        mem6.Load(0, BitConverter.GetBytes(UveBTypeImm(-8, 3, 0b00001u, 0x6)));
        var op6 = Assert.IsType<RvUveSoBNdc>(dec.Decode(0, mem6).Payload);
        Assert.Equal(6, op6.Dim);

        // funct3=7 (SO.B.NC, no suffix) → the EOS-equivalent form, so.b.nc — NOT so.b.ndc.8
        // (Appendix B's original listing / Spike instead put the EOS-equivalent form at funct3=0).
        var mem7 = new FlatMemory(16);
        mem7.Load(0, BitConverter.GetBytes(UveBTypeImm(-8, 3, 0b00001u, 0x7)));
        var op7 = Assert.IsType<RvUveSoBNc>(dec.Decode(0, mem7).Payload);
        Assert.Equal(3, op7.Urs);
    }

    // ── Integration test: SAXPY via OoO pipeline ──────────────────────────────

    /// <summary>
    ///     Runs a SAXPY computation (Y = A*X + Y) through the OoO pipeline using UVE streams.
    ///     <para>
    ///         Memory layout:
    ///         [0x0000..0x003F]  source X array: 8 floats
    ///         [0x0040..0x007F]  destination Y array: 8 floats (output overwrites in-place)
    ///         [0x1000..]        code
    ///     </para>
    ///     <para>
    ///         Register assignments in setup ADDI sequence:
    ///         x1 = 0x0000   (base of X)
    ///         x2 = 0x0040   (base of Y)
    ///         x3 = 8        (element count)
    ///         x4 = 4        (stride in bytes, = element width)
    ///         x5 = bits (A) (scalar multiplier, as float32 raw bits)
    ///     </para>
    /// </summary>
    [Fact]
    public void Pipeline_Saxpy_CorrectResult() {
        const int n = 4; // keep small so test runs fast
        const float a = 2.0f;

        float[] x = [1.0f, 2.0f, 3.0f, 4.0f,];
        float[] y = [10.0f, 20.0f, 30.0f, 40.0f,];
        float[] expected = x.Zip(y, (xi, yi) => a * xi + yi).ToArray();

        var mem = new FlatMemory(0x2000);

        // Write X at 0x0000 and Y at 0x0100
        for (var i = 0; i < n; i++) {
            mem.Load((ulong)(i * 4), BitConverter.GetBytes(x[i]));
            mem.Load((ulong)(0x100 + i * 4), BitConverter.GetBytes(y[i]));
        }

        _ = (uint)BitConverter.SingleToInt32Bits(a);

        // A=2.0f bits = 0x40000000 → upper 20 bits = 0x40000
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, 0),     // x1 = 0 (base X)
            Addi(2, 0, 0x100), // x2 = 0x100 (base Y)
            Addi(3, 0, n),     // x3 = N (count)
            Addi(4, 0, 1),     // x4 = 1 (element stride → 4 bytes after scaling)
            Lui(5, 0x40000),   // x5 = 0x40000000 (A=2.0 raw bits)
            // 1D streams: ss.sta.ld/st.w (base) + ss.end (count, stride, activate)
            SsStaLdW(1, 1), SsEnd(1, 0, 3, 4), // u1 = load X
            SsStaLdW(2, 2), SsEnd(2, 0, 3, 4), // u2 = load Y
            SsStaStW(3, 2), SsEnd(3, 0, 3, 4), // u3 = store Y
            SoVDpW(4, 5),                      // u4 = broadcast A
            // loop: u5 = u1[i]*u4; u3[i] = u2[i]+u5; branch back -8 bytes (2 instrs)
            SoAFp(UveFpOp.Mul, 5, 1, 4),
            SoAFp(UveFpOp.Add, 3, 2, 5),
            SoBNc(1, -8),
            EBreak(),
        };

        // Load code at 0x1000
        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(2000);

        // Verify Y array was overwritten with A*X + Y
        for (var i = 0; i < n; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read((ulong)(0x100 + i * 4), 4));
            Assert.Equal(expected[i], actual, 2);
        }

        return;

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);
    }

    // ── Integration test: 2D strided load via OoO pipeline ────────────────────

    /// <summary>
    ///     Verifies multidimensional stream access and so.b.ndc.D loop control.
    ///     <para>
    ///         Memory layout (data region at 0x0000):
    ///         A 3×4 matrix stored in row-major order in an 8-float-wide (32 byte) row buffer.
    ///         Only the first 4 floats of each row are part of the matrix; the trailing 4 are padding.
    ///         Row 0: A[0][0..3] at 0x0000 - 0x000F, padding 0x0010 - 0x001F
    ///         Row 1: A[1][0..3] at 0x0020 - 0x002F, padding 0x0030 - 0x003F
    ///         Row 2: A[2][0..3] at 0x0040 - 0x004F, padding 0x0050 - 0x005F
    ///         Output: 12 floats at 0x0200 (linearized, row-major).
    ///     </para>
    ///     <para>
    ///         Stream u1 configured as a 2D load stream (config order outermost-first, Spike style):
    ///         ss.sta.ld.w u1, x1            — base=0x0000
    ///         ss.app      u1, x0, x5, x6    — outer dim: count=3 rows, stride=32
    ///         ss.end      u1, x0, x3, x4    — inner dim: count=4 cols, stride=4; activate
    ///     </para>
    ///     <para>
    ///         Loop structure:
    ///         outer: so.b.ndc.1 u1, outer  — outer dim (dim1) loop
    ///         inner: so.a.mul.fp u2, u1, u4   — u2 = elem * scalar
    ///         so.b.ndc.0 u1, inner     — inner dim (dim0) loop
    ///         ebreak
    ///     </para>
    ///     <para>Expected output[i*4+j] = A[i][j] * scalar.</para>
    /// </summary>
    [Fact]
    public void Pipeline_2D_StridedLoad_CorrectResult() {
        const int rows = 3, cols = 4;
        const int rowBytes = 8 * 4; // 8 floats per padded row = 32 bytes
        const float scalar = 3.0f;

        var inputMatrix = new float[rows, cols];
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
            inputMatrix[r, c] = r * cols + c + 1.0f; // 1..12

        var mem = new FlatMemory(0x2000);

        // Write matrix with padded rows at 0x0000
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
            mem.Load((ulong)(r * rowBytes + c * 4), BitConverter.GetBytes(inputMatrix[r, c]));

        _ = (uint)BitConverter.SingleToInt32Bits(scalar);

        // Code at 0x1000.
        // Register plan:
        //   x1 = 0x0000       base of matrix
        //   x2 = 0x0200       base of output
        //   x3 = 4            inner count (cols)
        //   x4 = 1            inner stride (1 element)
        //   x5 = 3            outer count (rows)
        //   x6 = 8            outer stride (8 elements = 1 padded row)
        //   x7 = bits(Scalar) scalar multiplier raw bits
        //   x8 = 12           output element count
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, 0x000),        // x1 = 0 (matrix base)
            Addi(2, 0, 0x200),        // x2 = 0x200 (output base)
            Addi(3, 0, cols),         // x3 = 4
            Addi(4, 0, 1),            // x4 = 1 (element stride)
            Addi(5, 0, rows),         // x5 = 3
            Addi(6, 0, rowBytes / 4), // x6 = 8 (elements per padded row)
            Addi(8, 0, rows * cols),  // x8 = 12
            Lui(7, 0x40400),          // x7 = bits(3.0f)
            // 2D load stream u1: ss.sta.ld.w (base) + ss.app (outer dim) + ss.end (inner dim, activate)
            SsStaLdW(1, 1),    // base=x1
            SsApp(1, 0, 5, 6), // outer dim: count=x5(3), stride=x6(8 elems)
            SsEnd(1, 0, 3, 4), // inner dim: count=x3(4), stride=x4(1 elem); activate
            // 1D store stream u2: ss.sta.st.w + ss.end
            SsStaStW(2, 2), SsEnd(2, 0, 8, 4), // count=x8(12), stride=x4(1 elem)
            SoVDpW(4, 7),
            SoAFp(UveFpOp.Mul, 2, 1, 4), // u2[i] = u1[elem] * u4
            SoBNc(1, -4),                // loop while u1 not done
            EBreak(),                    // u4 = broadcast Scalar
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(5000);

        // Verify output = A[r][c] * Scalar for every element, linearized row-major
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++) {
            var outAddr = (ulong)(0x200 + (r * cols + c) * 4);
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(outAddr, 4));
            float expected = inputMatrix[r, c] * scalar;
            Assert.Equal(expected, actual, 2);
        }

        return;

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);
    }

    /// <summary>
    ///     Copies 12 floats from a 1D source to a 3×4 matrix stored with padded rows (8 floats wide
    ///     = 32 bytes per row). Uses a 1D load stream (ss.ld.w) as a source and a 2D store stream
    ///     (ss.sta.st.w → ss.end) as destination. Verifies that UveStoreStream advances its inner/outer
    ///     indices correctly, skipping the 4-element padding gap between rows.
    /// </summary>
    [Fact]
    public void Pipeline_2D_StridedStore_CorrectResult() {
        const int rows = 3, cols = 4;
        const int rowBytes = 8 * 4; // 8 floats per padded row = 32 bytes

        // Source: 12 contiguous floats at 0x0000
        float[] src = [1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f,];

        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < src.Length; i++) mem.Load((ulong)(i * 4), BitConverter.GetBytes(src[i]));

        // Code at 0x1000.
        // Register plan:
        //   x1 = 0x0000       src base
        //   x2 = 0x0400       dst matrix base
        //   x3 = 12           load-stream count (all 12 elements)
        //   x4 = 1            inner element stride (1 element)
        //   x5 = 4            inner col count
        //   x6 = 3            outer row count
        //   x7 = bits(1.0f)   scalar multiplier (copy via mul)
        //   x8 = 8            outer row stride (8 elements = 1 padded row)
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, 0x000),        // x1 = 0
            Addi(2, 0, 0x400),        // x2 = 0x400
            Addi(3, 0, rows * cols),  // x3 = 12
            Addi(4, 0, 1),            // x4 = 1 (element stride)
            Addi(5, 0, cols),         // x5 = 4
            Addi(6, 0, rows),         // x6 = 3
            Addi(8, 0, rowBytes / 4), // x8 = 8 (elements per padded row)
            Lui(7, 0x3F800),          // x7 = bits(1.0f)
            // 1D load stream u1: ss.sta.ld.w + ss.end
            SsStaLdW(1, 1), SsEnd(1, 0, 3, 4), // count=x3(12), stride=x4(1 elem)
            // 2D store stream u2: ss.sta.st.w + ss.app (outer) + ss.end (inner)
            SsStaStW(2, 2),              // base=x2
            SsApp(2, 0, 6, 8),           // outer dim: count=x6(3 rows), stride=x8(8 elems)
            SsEnd(2, 0, 5, 4),           // inner dim: count=x5(4 cols), stride=x4(1 elem); activate
            SoVDpW(4, 7),                // u4 = 1.0f
            SoAFp(UveFpOp.Mul, 2, 1, 4), // u2[dst] = u1[i] * u4
            SoBNc(1, -4),                // loop while u1 not exhausted
            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(5000);

        // Verify each element landed at the right address in the strided matrix.
        // src[r*Cols + c] should be at dst base + r*RowBytes + c*4.
        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++) {
            var dstAddr = (ulong)(0x400 + r * rowBytes + c * 4);
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(dstAddr, 4));
            float expected = src[r * cols + c];
            Assert.Equal(expected, actual, 2);
        }

        return;

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);
    }

    /// <summary>
    ///     Regression test for a bug found while porting the <c>mvt</c> benchmark: <c>OooeTrain</c>'s
    ///     <c>UveBranchStreams</c> handling computed "done" purely from
    ///     <see cref="StreamingEngine" />.IsActive/IsExhausted, which store streams never register with
    ///     (they bypass the engine entirely). That collapsed to "always done" for any store-stream
    ///     branch operand, so <c>so.b.nc</c>/<c>so.b.c</c> checking a store stream exited after their
    ///     first iteration — every prior test happened to check a *load* stream's completion instead.
    ///     Writes 4 elements through a 1D store stream in a <c>so.b.nc</c>-driven loop; without the fix
    ///     only element 0 lands.
    /// </summary>
    [Fact]
    public void Pipeline_SoBNc_OnStoreStream_LoopsUntilExhausted() {
        const int n = 4;
        const ulong destBase = 0x0000;
        var mem = new FlatMemory(0x2000);

        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)destBase), Addi(2, 0, n), Addi(3, 0, 1),
            Lui(4, (int)(BitConverter.SingleToInt32Bits(7.0f) >> 12)), // x4 = bits(7.0f)

            SsStaStW(1, 1), SsEnd(1, 0, 2, 3), // u1 = store stream: count=n, stride=1 elem
            SoVDpW(2, 4),                      // u2 = 7.0f broadcast

            SoAFp(UveFpOp.Add, 1, 2, -1), // u1(store) = u2 + 0 (unary usrc2=-1)
            SoBNc(1, -4),                 // so.b.nc u1, (loop back to the write above)
            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 32, iqCapacity: 16
        );
        train.Run(2000);

        for (var i = 0; i < n; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(destBase + (ulong)(i * 4), 4));
            Assert.Equal(7.0f, actual, 2);
        }

        return;

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);
    }

    /// <summary>
    ///     Single-variable-changed sibling of <see cref="Pipeline_SoBNc_OnStoreStream_LoopsUntilExhausted" />:
    ///     identical 2-instruction write+<c>so.b.nc</c> loop body, identical broadcast source, identical
    ///     stream-under-check — the only change is that u1 is now a 2D store stream carrying a
    ///     Size-Inc modifier (lower-triangular growth) instead of a flat 1D store. Confirms the
    ///     store-stream-modifier mechanism survives the tightest possible write+branch loop shape, not
    ///     just the more spaced-out instruction sequences <c>covariance</c> happens to use.
    ///     <para>
    ///     An earlier version of this test asserted against consecutive flat addresses
    ///     (<c>destBase + i*4</c> for <c>i</c> in <c>0..expectedCount</c>) and appeared to fail with
    ///     nothing written at all. <c>Console.Error</c> tracing at the write site (temporary, removed
    ///     after use) showed every write landing at the correct address with the correct value — the
    ///     apparent failure was this test's own wrong expected-address assumption, not a pipeline bug:
    ///     row <c>r</c> of a Size-Inc-modified stream only fills <c>r+1</c> of its <c>rows</c> slots, so
    ///     the layout is sparse row-major, not compacted. The assertion below checks the full grid,
    ///     including the legitimately-untouched cells.
    ///     </para>
    /// </summary>
    [Fact]
    public void Pipeline_SoBNc_OnModifierBearingStoreStream_LoopsUntilExhausted() {
        const int rows = 3;
        const ulong destBase = 0x0000;
        var mem = new FlatMemory(0x2000);
        // Row-major layout with row stride = `rows` elements (matching the stream's D1 stride), NOT
        // a compacted/consecutive write pattern — row r only fills its first (r+1) of `rows` slots
        // (the Size-Inc modifier grows the innermost dimension by one element per row), leaving the
        // remaining slots at the sentinel. (A first version of this test asserted against consecutive
        // addresses 0,4,8,12,... and "failed" — that was a bug in the test's own verification, not the
        // pipeline: tracing showed every write landing at the correct sparse row-major address.)
        for (var i = 0; i < rows * rows; i++) mem.Load(destBase + (ulong)(i * 4), BitConverter.GetBytes(-1f));

        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)destBase), Addi(2, 0, rows), Addi(3, 0, rows), Addi(4, 0, 1), Addi(5, 0, 1),
            Lui(6, (int)(BitConverter.SingleToInt32Bits(7.0f) >> 12)), // x6 = bits(7.0f)

            // u1 = 2D store stream: D1(outer,"rows") count=rows stride=rows; D2(final, Size-Inc
            // modifier) count=1 stride=1 — the exact single variable changed from the sibling test
            // above (which used a flat 1D store with no modifier).
            SsStaStW(1, 1), SsApp(1, 0, 2, 3),
            SsAppMod(1, 1, StreamModifierTarget.Size, StreamModifierBehavior.Inc, 5), SsEnd(1, 0, 5, 4),
            SoVDpW(2, 6), // u2 = 7.0f broadcast

            SoAFp(UveFpOp.Add, 1, 2, -1), // u1(store) = u2 + 0 (unary usrc2=-1)
            SoBNc(1, -4),                 // so.b.nc u1, (loop back to the write above)
            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 32, iqCapacity: 16
        );
        train.Run(2000);

        for (var r = 0; r < rows; r++)
        for (var c = 0; c < rows; c++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(destBase + (ulong)((r * rows + c) * 4), 4));
            Assert.Equal(c <= r ? 7.0f : -1f, actual, 2);
        }

        return;

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);
    }

    // ── Integration test: STREAM (Copy/Scale/Add/Triad) via OoO pipeline ─────

    /// <summary>
    ///     Ports the four classic McCalpin STREAM kernels (Copy/Scale/Add/Triad) from the UVE2
    ///     reference benchmark suite's <c>stream</c> test
    ///     (github.com/hpc-ulisboa/UVE2, UVE-Testing/spike_test/benchmarks/stream), chained in one
    ///     program exactly as the reference kernel.c does: c=a; b=scalar*c; c=a+b; a=b+scalar*c.
    ///     Only the final <c>a[]</c> is checked, mirroring the reference's own main.c (which only
    ///     reads back <c>src_1</c>).
    ///     <para>Memory layout: a at 0x0000, b at 0x0100, c at 0x0200, code at 0x1000.</para>
    /// </summary>
    [Fact]
    public void Pipeline_Stream_CopyScaleAddTriad_CorrectResult() {
        const int n = 4;
        const float scalar = 3.0f;

        float[] a = [1.0f, 2.0f, 3.0f, 4.0f,];
        float[] b = [5.0f, 6.0f, 7.0f, 8.0f,];
        float[] c = [9.0f, 10.0f, 11.0f, 12.0f,];

        // Independent oracle: mirrors the reference's own RUN_SIMPLE fallback, not Horologium's
        // execution.
        float[] cCopy = a.ToArray();
        float[] bScale = cCopy.Select(v => scalar * v).ToArray();
        float[] cAdd = a.Zip(bScale, (ai, bi) => ai + bi).ToArray();
        float[] expectedA = bScale.Zip(cAdd, (bi, ci) => bi + scalar * ci).ToArray();

        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < n; i++) {
            mem.Load((ulong)(0x000 + i * 4), BitConverter.GetBytes(a[i]));
            mem.Load((ulong)(0x100 + i * 4), BitConverter.GetBytes(b[i]));
            mem.Load((ulong)(0x200 + i * 4), BitConverter.GetBytes(c[i]));
        }

        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, 0x000), // x1 = base a
            Addi(2, 0, 0x100), // x2 = base b
            Addi(3, 0, 0x200), // x3 = base c
            Addi(4, 0, n),     // x4 = count
            Addi(5, 0, 1),     // x5 = stride (1 elem)
            Lui(6, 0x40400),   // x6 = bits(3.0f) scalar

            // KERNEL COPY: c = a
            SsStaLdW(1, 1), SsEnd(1, 0, 4, 5), // u1 = load a
            SsStaStW(2, 3), SsEnd(2, 0, 4, 5), // u2 = store c
            SoVMv(2, 1), SoBNc(1, -4),

            // KERNEL SCALE: b = scalar * c
            SsStaLdW(1, 3), SsEnd(1, 0, 4, 5), // u1 = load c
            SsStaStW(2, 2), SsEnd(2, 0, 4, 5), // u2 = store b
            SoVDpW(10, 6),                     // u10 = broadcast scalar
            SoAFp(UveFpOp.Mul, 2, 1, 10), SoBNc(1, -4),

            // KERNEL ADD: c = a + b
            SsStaLdW(1, 1), SsEnd(1, 0, 4, 5), // u1 = load a
            SsStaLdW(2, 2), SsEnd(2, 0, 4, 5), // u2 = load b
            SsStaStW(3, 3), SsEnd(3, 0, 4, 5), // u3 = store c
            SoAFp(UveFpOp.Add, 3, 1, 2), SoBNc(1, -4),

            // KERNEL TRIAD: a = b + scalar * c
            SsStaLdW(1, 2), SsEnd(1, 0, 4, 5), // u1 = load b
            SsStaLdW(2, 3), SsEnd(2, 0, 4, 5), // u2 = load c
            SsStaStW(3, 1), SsEnd(3, 0, 4, 5), // u3 = store a
            SoVDpW(10, 6),                     // u10 = broadcast scalar
            SoAFp(UveFpOp.Mul, 20, 2, 10), SoAFp(UveFpOp.Add, 3, 1, 20), SoBNc(1, -8),

            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(6000);

        for (var i = 0; i < n; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read((ulong)(0x000 + i * 4), 4));
            Assert.Equal(expectedA[i], actual, 2);
        }

        return;

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);
    }

    // ── Integration test: SPMV_ELLPACK (indirect gather) via OoO pipeline ────

    /// <summary>
    ///     Ports the <c>spmv_ellpack</c> UVE2 reference benchmark
    ///     (github.com/hpc-ulisboa/UVE2, UVE-Testing/spike_test/benchmarks/spmv_ellpack): an ELLPACK
    ///     sparse matrix-vector product, <c>out[i] = sum_j nzval[i,j] * vec[cols[i,j]]</c>. Exercises
    ///     indirect/scatter-gather addressing (<c>ss.sta.ld.w.inds</c> index stream + <c>ss.end.sgi.ofs.add</c>
    ///     gather stream) combined with a two-level nested loop (<c>so.b.ndc.2</c> inner, <c>so.b.nc</c>
    ///     outer) — this is the one bucket of UVE2 functionality (indirect gather) not already exercised
    ///     by the saxpy/gemm/stream-style tests, and the reference kernel's own cols/nzval 2D streams
    ///     share the same [N,L] shape so the inner-dim-completion branch drives both the accumulate loop
    ///     and the gather's index stream in lockstep.
    ///     <para>
    ///         Memory layout: cols (int32, N*L) at 0x0000, nzval (float, N*L) at 0x0100, vec (float) at
    ///         0x0200, out (float, N) at 0x0300, code at 0x1000.
    ///     </para>
    /// </summary>
    [Fact]
    public void Pipeline_SpmvEllpack_CorrectResult() {
        const int n = 2, l = 2;
        int[] cols = [2, 0, 1, 2,];
        float[] nzval = [1.0f, 2.0f, 3.0f, 4.0f,];
        float[] vec = [10.0f, 20.0f, 30.0f,];

        // Independent oracle: mirrors the reference's own RUN_SIMPLE fallback, not Horologium's
        // execution.
        var expectedOut = new float[n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < l; j++)
            expectedOut[i] += nzval[i * l + j] * vec[cols[i * l + j]];

        const ulong colsBase = 0x0000, nzvalBase = 0x0100, vecBase = 0x0200, outBase = 0x0300;
        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < cols.Length; i++) mem.Load(colsBase + (ulong)(i * 4), BitConverter.GetBytes(cols[i]));
        for (var i = 0; i < nzval.Length; i++) mem.Load(nzvalBase + (ulong)(i * 4), BitConverter.GetBytes(nzval[i]));
        for (var i = 0; i < vec.Length; i++) mem.Load(vecBase + (ulong)(i * 4), BitConverter.GetBytes(vec[i]));

        // Register plan: x1=colsBase x2=nzvalBase x3=vecBase x4=outBase x5=N x6=L x7=1(stride)
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)colsBase), Addi(2, 0, (int)nzvalBase), Addi(3, 0, (int)vecBase),
            Addi(4, 0, (int)outBase), Addi(5, 0, n), Addi(6, 0, l), Addi(7, 0, 1),

            // u1: cols index stream (IndSource) — 2D, outermost-first: outer count=N stride=L elems,
            // inner count=L stride=1 elem.
            SsStaLdW(1, 1) | (1u << 24), SsApp(1, 0, 5, 6), SsEnd(1, 0, 6, 7),

            // u2: nzval load stream — same [N,L] shape, drives the loop-completion branches.
            SsStaLdW(2, 2), SsApp(2, 0, 5, 6), SsEnd(2, 0, 6, 7),

            // u3: vec gather stream — both dims stride=0 (pure indirect addressing); ss.end.sgi
            // activates using the two already-appended dims and attaches the sgi modifier (source=u1,
            // behavior=Add) instead of appending a third dimension.
            SsStaLdW(3, 3), SsApp(3, 0, 5, 0), SsApp(3, 0, 6, 0), SsEndSgi(3, 1, StreamModifierBehavior.Add),

            // u4: out store stream — 1D, count=N, stride=1 elem.
            SsStaStW(4, 4), SsEnd(4, 0, 5, 7),

            // .iLoop1:
            SoVDpW(5, 0), // u5 = 0.0 (accumulator reset)
            // .kloop1:
            SoAFp(UveFpOp.Mac, 5, 2, 3), // u5 += u2 * u3
            SoBNdcD(2, 1, -4),           // so.b.ndc.2 u2, .kloop1 (inner dim of the [N,L] shape)
            SoAFp(UveFpOp.Adde, 4, 5, -1), // out[i] = u5 (advances the store stream)
            SoBNc(2, -16),               // so.b.nc u2, .iLoop1 (whole-stream not done)
            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(6000);

        for (var i = 0; i < n; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(outBase + (ulong)(i * 4), 4));
            Assert.Equal(expectedOut[i], actual, 2);
        }
    }

    // ── Integration test: memcpy via OoO pipeline ────────────────────────────

    /// <summary>
    ///     Ports the <c>memcpy</c> UVE2 reference benchmark (github.com/hpc-ulisboa/UVE2,
    ///     UVE-Testing/spike_test/benchmarks/memcpy): a straight 1D load-stream/store-stream copy via
    ///     <c>so.v.mv</c>. Structurally identical to the <c>stream</c> port's Copy phase (same
    ///     so.v.mv-to-store-stream path, already exercised there) — ported mainly for regression
    ///     breadth, not new coverage.
    /// </summary>
    [Fact]
    public void Pipeline_Memcpy_CorrectResult() {
        const int size = 4;
        float[] src = [1.0f, 2.0f, 3.0f, 4.0f,];

        const ulong srcBase = 0x0000, destBase = 0x0100;
        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < src.Length; i++) mem.Load(srcBase + (ulong)(i * 4), BitConverter.GetBytes(src[i]));

        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)destBase), Addi(2, 0, (int)srcBase), Addi(3, 0, size), Addi(4, 0, 1),

            SsStaStW(1, 1), SsEnd(1, 0, 3, 4), // dest store stream: count=size, stride=1 elem
            SsStaLdW(2, 2), SsEnd(2, 0, 3, 4), // src load stream: count=size, stride=1 elem

            SoVMv(1, 2), SoBNc(2, -4),
            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(4000);

        for (var i = 0; i < size; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(destBase + (ulong)(i * 4), 4));
            Assert.Equal(src[i], actual, 2);
        }
    }

    // ── Integration test: jacobi-1d via OoO pipeline ─────────────────────────

    /// <summary>
    ///     Ports the <c>jacobi-1d</c> UVE2 reference benchmark (github.com/hpc-ulisboa/UVE2,
    ///     UVE-Testing/spike_test/benchmarks/jacobi-1d): a 3-point 1D stencil,
    ///     <c>B[i] = ct*(A[i-1]+A[i]+A[i+1])</c> then <c>A[i] = ct*(B[i-1]+B[i]+B[i+1])</c>, for
    ///     <c>i</c> in <c>[1, SIZE-2]</c>. Three overlapping-offset load streams over the same array
    ///     (base, base+1, base+2 elements) feed a running sum, ported scalar (dropping the reference's
    ///     <c>.v</c> vector-mode suffix, per the one-representative-width convention).
    /// </summary>
    [Fact]
    public void Pipeline_Jacobi1D_CorrectResult() {
        const int size = 6;
        const float ct = 0.5f;
        float[] a = [1.0f, 2.0f, 3.0f, 4.0f, 5.0f, 6.0f,];
        float[] b = [10.0f, 20.0f, 30.0f, 40.0f, 50.0f, 60.0f,];

        // Independent oracle: mirrors the reference's own RUN_SIMPLE fallback.
        float[] bNew = b.ToArray();
        for (var i = 1; i < size - 1; i++) bNew[i] = ct * (a[i - 1] + a[i] + a[i + 1]);
        float[] aNew = a.ToArray();
        for (var i = 1; i < size - 1; i++) aNew[i] = ct * (bNew[i - 1] + bNew[i] + bNew[i + 1]);

        const ulong aBase = 0x0000, bBase = 0x0100;
        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < size; i++) mem.Load(aBase + (ulong)(i * 4), BitConverter.GetBytes(a[i]));
        for (var i = 0; i < size; i++) mem.Load(bBase + (ulong)(i * 4), BitConverter.GetBytes(b[i]));

        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)aBase), Addi(2, 0, (int)aBase + 4), Addi(3, 0, (int)aBase + 8),
            Addi(4, 0, (int)bBase + 4), Addi(5, 0, size - 2), Addi(6, 0, 1),
            Lui(7, (int)(BitConverter.SingleToInt32Bits(ct) >> 12)),

            SsStaLdW(1, 1), SsEnd(1, 0, 5, 6), // u1 = A[0..]
            SsStaLdW(2, 2), SsEnd(2, 0, 5, 6), // u2 = A[1..]
            SsStaLdW(3, 3), SsEnd(3, 0, 5, 6), // u3 = A[2..]
            SsStaStW(4, 4), SsEnd(4, 0, 5, 6), // u4 = B[1..] store
            SoVDpW(5, 7),                      // u5 = ct broadcast

            // .uve_loop1:
            SoAFp(UveFpOp.Add, 10, 1, 2), SoAFp(UveFpOp.Add, 10, 10, 3), SoAFp(UveFpOp.Mul, 4, 10, 5),
            SoBNc(1, -12),

            Addi(1, 0, (int)bBase), Addi(2, 0, (int)bBase + 4), Addi(3, 0, (int)bBase + 8),
            Addi(4, 0, (int)aBase + 4),

            SsStaLdW(1, 1), SsEnd(1, 0, 5, 6), // u1 = B[0..]
            SsStaLdW(2, 2), SsEnd(2, 0, 5, 6), // u2 = B[1..]
            SsStaLdW(3, 3), SsEnd(3, 0, 5, 6), // u3 = B[2..]
            SsStaStW(4, 4), SsEnd(4, 0, 5, 6), // u4 = A[1..] store

            // .uve_loop2: (u5 = ct broadcast, unchanged from loop1)
            SoAFp(UveFpOp.Add, 10, 1, 2), SoAFp(UveFpOp.Add, 10, 10, 3), SoAFp(UveFpOp.Mul, 4, 10, 5),
            SoBNc(1, -12),

            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(8000);

        for (var i = 0; i < size; i++) {
            float actualA = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(aBase + (ulong)(i * 4), 4));
            float actualB = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(bBase + (ulong)(i * 4), 4));
            Assert.Equal(aNew[i], actualA, 2);
            Assert.Equal(bNew[i], actualB, 2);
        }

        return;

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);
    }

    // ── Integration test: mvt via OoO pipeline ───────────────────────────────

    /// <summary>
    ///     Ports the <c>mvt</c> UVE2 reference benchmark (github.com/hpc-ulisboa/UVE2,
    ///     UVE-Testing/spike_test/benchmarks/mvt): matrix-vector-transpose,
    ///     <c>x_1[i] += sum_j A[i,j]*y_1[j]</c> (row-major, SLOOP_1) then
    ///     <c>x_2[i] += sum_j A[j,i]*y_2[j]</c> (column-major over the same matrix, SLOOP_2). Same
    ///     inner-dim-completion-branch reduction pattern as spmv_ellpack/LowerTriangular, but exercises
    ///     both a stride-N and a stride-1 outer-dimension traversal of the same base array (row-major
    ///     vs. transposed access).
    /// </summary>
    [Fact]
    public void Pipeline_Mvt_CorrectResult() {
        const int n = 3;
        float[] a = [1, 2, 3, 4, 5, 6, 7, 8, 9,];
        float[] y1 = [1, 1, 1,];
        float[] x1 = [10, 20, 30,];
        float[] y2 = [1, 1, 1,];
        float[] x2 = [100, 200, 300,];

        // Independent oracle: mirrors the reference's own RUN_SIMPLE fallback.
        float[] x1New = x1.ToArray();
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            x1New[i] += a[i * n + j] * y1[j];
        float[] x2New = x2.ToArray();
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            x2New[i] += a[j * n + i] * y2[j];

        const ulong aBase = 0x0000, y1Base = 0x0100, x1Base = 0x0200, y2Base = 0x0300, x2Base = 0x0400;
        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < a.Length; i++) mem.Load(aBase + (ulong)(i * 4), BitConverter.GetBytes(a[i]));
        for (var i = 0; i < n; i++) mem.Load(y1Base + (ulong)(i * 4), BitConverter.GetBytes(y1[i]));
        for (var i = 0; i < n; i++) mem.Load(x1Base + (ulong)(i * 4), BitConverter.GetBytes(x1[i]));
        for (var i = 0; i < n; i++) mem.Load(y2Base + (ulong)(i * 4), BitConverter.GetBytes(y2[i]));
        for (var i = 0; i < n; i++) mem.Load(x2Base + (ulong)(i * 4), BitConverter.GetBytes(x2[i]));

        // Register plan: x1=aBase x2=N x3=1 x4=y1Base x5=x1Base x6=y2Base x7=x2Base
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)aBase), Addi(2, 0, n), Addi(3, 0, 1),
            Addi(4, 0, (int)y1Base), Addi(5, 0, (int)x1Base), Addi(6, 0, (int)y2Base), Addi(7, 0, (int)x2Base),

            // u4 = A row-major: outer count=N stride=N elems, inner count=N stride=1 elem
            SsStaLdW(4, 1), SsApp(4, 0, 2, 2), SsEnd(4, 0, 2, 3),
            // u5 = y1: outer count=N stride=0 (broadcast each row), inner count=N stride=1
            SsStaLdW(5, 4), SsApp(5, 0, 2, 0), SsEnd(5, 0, 2, 3),
            // u7 = x1 load: 1D count=N stride=1
            SsStaLdW(7, 5), SsEnd(7, 0, 2, 3),
            // u1 = x1 store: 1D count=N stride=1
            SsStaStW(1, 5), SsEnd(1, 0, 2, 3),

            // .SLOOP_1:
            SoVDpW(2, 0), // u2 = 0
            // .SLOOP_1_0:
            SoAFp(UveFpOp.Mac, 2, 4, 5),   // u2 += u4*u5
            SoBNdcD(4, 1, -4),             // so.b.ndc.2 u4, .SLOOP_1_0
            SoAFp(UveFpOp.Adde, 3, 2, -1), // u3 = u2
            SoAFp(UveFpOp.Add, 1, 7, 3),   // u1(store) = u7(x1 old) + u3
            SoBNc(1, -20),                 // so.b.nc u1, .SLOOP_1

            // u4 = A column-major (transposed access): outer count=N stride=1 elem, inner count=N stride=N elems
            SsStaLdW(4, 1), SsApp(4, 0, 2, 3), SsEnd(4, 0, 2, 2),
            // u5 = y2: outer count=N stride=0, inner count=N stride=1
            SsStaLdW(5, 6), SsApp(5, 0, 2, 0), SsEnd(5, 0, 2, 3),
            // u7 = x2 load: 1D count=N stride=1
            SsStaLdW(7, 7), SsEnd(7, 0, 2, 3),
            // u1 = x2 store: 1D count=N stride=1
            SsStaStW(1, 7), SsEnd(1, 0, 2, 3),

            // .SLOOP_2:
            SoVDpW(2, 0),
            // .SLOOP_2_0:
            SoAFp(UveFpOp.Mac, 2, 4, 5),
            SoBNdcD(4, 1, -4),
            SoAFp(UveFpOp.Adde, 3, 2, -1),
            SoAFp(UveFpOp.Add, 1, 7, 3),
            SoBNc(1, -20),

            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(10_000);

        for (var i = 0; i < n; i++) {
            float actualX1 = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(x1Base + (ulong)(i * 4), 4));
            float actualX2 = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(x2Base + (ulong)(i * 4), 4));
            Assert.Equal(x1New[i], actualX1, 2);
            Assert.Equal(x2New[i], actualX2, 2);
        }
    }

    // ── Integration test: jacobi-2d via OoO pipeline ─────────────────────────

    /// <summary>
    ///     Ports the <c>jacobi-2d</c> UVE2 reference benchmark (github.com/hpc-ulisboa/UVE2,
    ///     UVE-Testing/spike_test/benchmarks/jacobi-2d): a 5-point 2D stencil,
    ///     <c>B[i,j] = f*(A[i,j]+A[i,j-1]+A[i,j+1]+A[i+1,j]+A[i-1,j])</c> then the same with A/B
    ///     swapped, for <c>i,j</c> in <c>[1, SIZE-2]</c>. Five overlapping-offset 2D load streams over
    ///     the same array feed a running sum; <c>so.b.nc</c> checks a load stream's whole-stream
    ///     completion in both passes (unlike <c>mvt</c>, no store-stream branch check here).
    /// </summary>
    [Fact]
    public void Pipeline_Jacobi2D_CorrectResult() {
        const int size = 5;
        const float fval = 0.25f; // exact in binary (unlike the reference's literal 0.2)
        var a = new float[size * size];
        var b = new float[size * size];
        for (var i = 0; i < a.Length; i++) a[i] = i + 1;
        for (var i = 0; i < b.Length; i++) b[i] = 100 + i;

        // Independent oracle: mirrors the reference's own RUN_SIMPLE fallback. Edge rows/cols are
        // never written by either pass, so loop2 reads loop1's untouched (pre-loaded) B edges.
        float[] bNew = b.ToArray();
        for (var i = 1; i < size - 1; i++)
        for (var j = 1; j < size - 1; j++)
            bNew[i * size + j] = fval * (a[i * size + j] + a[i * size + j - 1] + a[i * size + j + 1]
                                        + a[(i + 1) * size + j] + a[(i - 1) * size + j]);
        float[] aNew = a.ToArray();
        for (var i = 1; i < size - 1; i++)
        for (var j = 1; j < size - 1; j++)
            aNew[i * size + j] = fval * (bNew[i * size + j] + bNew[i * size + j - 1] + bNew[i * size + j + 1]
                                        + bNew[(i + 1) * size + j] + bNew[(i - 1) * size + j]);

        const ulong aBase = 0x0000, bBase = 0x0200;
        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < a.Length; i++) mem.Load(aBase + (ulong)(i * 4), BitConverter.GetBytes(a[i]));
        for (var i = 0; i < b.Length; i++) mem.Load(bBase + (ulong)(i * 4), BitConverter.GetBytes(b[i]));

        // Register plan: x1..x5 = center/left/right/down/up source bases, x6 = store dest base,
        // x7=SIZE-2 x8=SIZE x9=1 x10=bits(fval)
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)aBase + (size + 1) * 4), Addi(2, 0, (int)aBase + size * 4),
            Addi(3, 0, (int)aBase + (size + 2) * 4), Addi(4, 0, (int)aBase + (2 * size + 1) * 4),
            Addi(5, 0, (int)aBase + 4), Addi(6, 0, (int)bBase + (size + 1) * 4),
            Addi(7, 0, size - 2), Addi(8, 0, size), Addi(9, 0, 1),
            Lui(10, (int)(BitConverter.SingleToInt32Bits(fval) >> 12)),

            SsStaLdW(1, 1), SsApp(1, 0, 7, 8), SsEnd(1, 0, 7, 9),
            SsStaLdW(2, 2), SsApp(2, 0, 7, 8), SsEnd(2, 0, 7, 9),
            SsStaLdW(3, 3), SsApp(3, 0, 7, 8), SsEnd(3, 0, 7, 9),
            SsStaLdW(4, 4), SsApp(4, 0, 7, 8), SsEnd(4, 0, 7, 9),
            SsStaLdW(5, 5), SsApp(5, 0, 7, 8), SsEnd(5, 0, 7, 9),
            SsStaStW(6, 6), SsApp(6, 0, 7, 8), SsEnd(6, 0, 7, 9),
            SoVDpW(7, 10), // u7 = fval broadcast

            // .loop_1:
            SoAFp(UveFpOp.Add, 8, 1, 2), SoAFp(UveFpOp.Add, 9, 3, 4), SoAFp(UveFpOp.Add, 10, 8, 5),
            SoAFp(UveFpOp.Add, 11, 10, 9), SoAFp(UveFpOp.Mul, 6, 11, 7),
            SoBNc(1, -20),

            Addi(1, 0, (int)bBase + (size + 1) * 4), Addi(2, 0, (int)bBase + size * 4),
            Addi(3, 0, (int)bBase + (size + 2) * 4), Addi(4, 0, (int)bBase + (2 * size + 1) * 4),
            Addi(5, 0, (int)bBase + 4), Addi(6, 0, (int)aBase + (size + 1) * 4),

            SsStaLdW(1, 1), SsApp(1, 0, 7, 8), SsEnd(1, 0, 7, 9),
            SsStaLdW(2, 2), SsApp(2, 0, 7, 8), SsEnd(2, 0, 7, 9),
            SsStaLdW(3, 3), SsApp(3, 0, 7, 8), SsEnd(3, 0, 7, 9),
            SsStaLdW(4, 4), SsApp(4, 0, 7, 8), SsEnd(4, 0, 7, 9),
            SsStaLdW(5, 5), SsApp(5, 0, 7, 8), SsEnd(5, 0, 7, 9),
            SsStaStW(6, 6), SsApp(6, 0, 7, 8), SsEnd(6, 0, 7, 9),

            // .loop_2: (u7 = fval broadcast, unchanged from loop1)
            SoAFp(UveFpOp.Add, 8, 1, 2), SoAFp(UveFpOp.Add, 9, 3, 4), SoAFp(UveFpOp.Add, 10, 8, 5),
            SoAFp(UveFpOp.Add, 11, 10, 9), SoAFp(UveFpOp.Mul, 6, 11, 7),
            SoBNc(1, -20),

            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(20_000);

        for (var i = 0; i < a.Length; i++) {
            float actualA = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(aBase + (ulong)(i * 4), 4));
            float actualB = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(bBase + (ulong)(i * 4), 4));
            Assert.Equal(aNew[i], actualA, 2);
            Assert.Equal(bNew[i], actualB, 2);
        }

        return;

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);
    }

    // ── Integration test: spmv_ellpack_delimiters via OoO pipeline ───────────

    /// <summary>
    ///     Ports the <c>spmv_ellpack_delimiters</c> UVE2 reference benchmark
    ///     (github.com/hpc-ulisboa/UVE2, UVE-Testing/spike_test/benchmarks/spmv_ellpack_delimiters): an
    ///     ELLPACK SpMV with a <c>rowDelimiters</c> array giving each row's *actual* nonzero count
    ///     (rather than <c>spmv_ellpack</c>'s fixed <c>L</c>). Exercises <c>ss.app.ind.siz.set</c> — the
    ///     first *engine-level* (not just descriptor-level) test of the indirect Size modifier, which
    ///     resizes each row stream's inner dimension from an IndSource each time it wraps (plus an
    ///     initial apply before the first row) — combined with the already-proven <c>sgi</c> gather for
    ///     the <c>vec</c> stream. <c>tdim</c> is chosen (not copied from the kernel's literal ".2"
    ///     suffix) to target engine index 0 (the inner/per-row dimension) for a 2-dim stream, mirroring
    ///     the already-verified <c>ss.app.mod</c> tdim convention.
    /// </summary>
    [Fact]
    public void Pipeline_SpmvEllpackDelimiters_CorrectResult() {
        const int n = 2, k = 3;
        int[] rowDelimiters = [2, 3,];
        int[] cols = [1, 0, 99, 0, 2, 1,]; // row0: [1,0,x] (only 2 valid); row1: [0,2,1]
        float[] val = [2.0f, 3.0f, 999.0f, 1.0f, 4.0f, 5.0f,];
        float[] vec = [10.0f, 20.0f, 30.0f,];

        // Independent oracle: mirrors the reference's own RUN_SIMPLE fallback.
        var expectedOut = new float[n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < rowDelimiters[i]; j++)
            expectedOut[i] += val[i * k + j] * vec[cols[i * k + j]];

        const ulong valBase = 0x0000, colsBase = 0x0100, rowDelimBase = 0x0200, vecBase = 0x0300,
            outBase = 0x0400;
        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < val.Length; i++) mem.Load(valBase + (ulong)(i * 4), BitConverter.GetBytes(val[i]));
        for (var i = 0; i < cols.Length; i++) mem.Load(colsBase + (ulong)(i * 4), BitConverter.GetBytes(cols[i]));
        for (var i = 0; i < rowDelimiters.Length; i++)
            mem.Load(rowDelimBase + (ulong)(i * 4), BitConverter.GetBytes(rowDelimiters[i]));
        for (var i = 0; i < vec.Length; i++) mem.Load(vecBase + (ulong)(i * 4), BitConverter.GetBytes(vec[i]));
        mem.Load(outBase, BitConverter.GetBytes(0.0f));
        mem.Load(outBase + 4, BitConverter.GetBytes(0.0f));

        // Register plan: x1=valBase x2=colsBase x3=rowDelimBase x4=vecBase x5=outBase x6=N x7=K x8=1
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)valBase), Addi(2, 0, (int)colsBase), Addi(3, 0, (int)rowDelimBase),
            Addi(4, 0, (int)vecBase), Addi(5, 0, (int)outBase), Addi(6, 0, n), Addi(7, 0, k), Addi(8, 0, 1),

            // u3, u6, u7: three independent copies of the rowDelimiters IndSource (one per consumer).
            // Stream register ids must stay within StreamingEngine.MaxStreams (0-7).
            SsStaLdW(3, 3) | (1u << 24), SsEnd(3, 0, 6, 8),
            SsStaLdW(6, 3) | (1u << 24), SsEnd(6, 0, 6, 8),
            SsStaLdW(7, 3) | (1u << 24), SsEnd(7, 0, 6, 8),

            // u1 = val: outer count=N stride=K elems; inner resized per-row from u3 (Set); base count=0.
            SsStaLdW(1, 1), SsApp(1, 0, 6, 7), SsAppInd(1, 1, StreamModifierTarget.Size, StreamModifierBehavior.Set, 3),
            SsEnd(1, 0, 0, 8),

            // u2 = cols (IndSource, feeds u4's gather below): outer count=N stride=K; inner resized from u6.
            SsStaLdW(2, 2) | (1u << 24), SsApp(2, 0, 6, 7),
            SsAppInd(2, 1, StreamModifierTarget.Size, StreamModifierBehavior.Set, 6), SsEnd(2, 0, 0, 8),

            // u4 = vec: outer count=N stride=0; inner resized from u7; sgi-gathers from u2.
            SsStaLdW(4, 4), SsApp(4, 0, 6, 0),
            SsAppInd(4, 1, StreamModifierTarget.Size, StreamModifierBehavior.Set, 7), SsApp(4, 0, 0, 0),
            SsEndSgi(4, 2, StreamModifierBehavior.Add),

            // u5 = out store: count=N, stride=1.
            SsStaStW(5, 5), SsEnd(5, 0, 6, 8),

            // .iLoop1:
            SoVDpW(0, 0), // u0 = 0 (accumulator)
            // .jloop:
            SoAFp(UveFpOp.Mac, 0, 1, 4), // u0 += u1*u4
            SoBNdcD(1, 1, -4),           // so.b.ndc.2 u1, .jloop
            SoAFp(UveFpOp.Adde, 5, 0, -1),
            SoBNc(1, -16), // so.b.nc u1, .iLoop1

            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(8000);

        for (var i = 0; i < n; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(outBase + (ulong)(i * 4), 4));
            Assert.Equal(expectedOut[i], actual, 2);
        }
    }

    // ── Integration test: 3mm (single matmul core) via OoO pipeline ──────────

    /// <summary>
    ///     Ports the generic matmul core shared by all three chained calls in the <c>3mm</c> UVE2
    ///     reference benchmark (github.com/hpc-ulisboa/UVE2, UVE-Testing/spike_test/benchmarks/3mm) —
    ///     <c>C[i,j] = sum_k A[i,k]*B[k,j]</c>. Ports a single instance (the three calls are the same
    ///     core with different operands/sizes) since the interesting part is the 3-dimensional stream
    ///     shape, not the chaining: A repeats each row across <c>j</c> (D2 stride=0) while B repeats
    ///     each column across <c>i</c> (D1 stride=0) — a genuinely new configuration (two independent
    ///     stride-0 broadcast dimensions in different positions of two 3D streams) not exercised by
    ///     mvt/spmv_ellpack's 2D streams.
    /// </summary>
    [Fact]
    public void Pipeline_3mm_CorrectResult() {
        const int sizeI = 2, sizeJ = 2, sizeK = 2;
        float[] a = [1, 2, 3, 4,]; // I x K row-major
        float[] b = [5, 6, 7, 8,]; // K x J row-major

        // Independent oracle: mirrors the reference's own RUN_SIMPLE fallback.
        var expectedC = new float[sizeI * sizeJ];
        for (var i = 0; i < sizeI; i++)
        for (var j = 0; j < sizeJ; j++)
        for (var k = 0; k < sizeK; k++)
            expectedC[i * sizeJ + j] += a[i * sizeK + k] * b[k * sizeJ + j];

        const ulong aBase = 0x0000, bBase = 0x0100, cBase = 0x0200;
        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < a.Length; i++) mem.Load(aBase + (ulong)(i * 4), BitConverter.GetBytes(a[i]));
        for (var i = 0; i < b.Length; i++) mem.Load(bBase + (ulong)(i * 4), BitConverter.GetBytes(b[i]));

        // Register plan: x1=A x2=B x3=C x4=sizeI x5=sizeJ x6=sizeK x7=1
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)aBase), Addi(2, 0, (int)bBase), Addi(3, 0, (int)cBase),
            Addi(4, 0, sizeI), Addi(5, 0, sizeJ), Addi(6, 0, sizeK), Addi(7, 0, 1),

            // u1 = A (I x K): D1 count=I stride=K elems; D2 count=J stride=0 (repeat row per j); D3 count=K stride=1
            SsStaLdW(1, 1), SsApp(1, 0, 4, 6), SsApp(1, 0, 5, 0), SsEnd(1, 0, 6, 7),
            // u2 = B (K x J): D1 count=I stride=0 (repeat col per i); D2 count=J stride=1; D3 count=K stride=J
            SsStaLdW(2, 2), SsApp(2, 0, 4, 0), SsApp(2, 0, 5, 7), SsEnd(2, 0, 6, 5),
            // u4 = C store (I x J): D1 count=I stride=J elems; D2 count=J stride=1
            SsStaStW(4, 3), SsApp(4, 0, 4, 5), SsEnd(4, 0, 5, 7),

            // .iLoop1:
            SoVDpW(0, 0), // u0 = 0 (accumulator)
            // .kloop1:
            SoAFp(UveFpOp.Mac, 0, 1, 2), // u0 += u1*u2
            SoBNdcD(2, 2, -4),           // so.b.ndc.3 u2, .kloop1
            SoAFp(UveFpOp.Adde, 4, 0, -1),
            SoBNc(2, -16), // so.b.nc u2, .iLoop1

            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(8000);

        for (var i = 0; i < expectedC.Length; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(cBase + (ulong)(i * 4), 4));
            Assert.Equal(expectedC[i], actual, 2);
        }
    }

    // ── Integration test: trmm via OoO pipeline ──────────────────────────────

    /// <summary>
    ///     Ports the <c>trmm</c> UVE2 reference benchmark (github.com/hpc-ulisboa/UVE2,
    ///     UVE-Testing/spike_test/benchmarks/trmm): <c>B[i,j] += sum_{k=i+1}^{M-1} A[k,i]</c> for all
    ///     <c>i</c> in <c>[0,M)</c>, <c>j</c> in <c>[0,N)</c> — a triangular access over <c>A</c> using
    ///     a static <c>ss.app.mod.siz.dec</c> modifier: the innermost (k) dimension's size shrinks by 1
    ///     each time the outer (i) wraps, going to 0 on the last row (a structurally-forced degenerate
    ///     case: the last row of a strict upper triangle has no valid k). The kernel's literal
    ///     <c>.dec.3</c>/<c>.ndc.3</c> suffixes are Spike-internal tdim numbering — NOT copied verbatim
    ///     here; the raw tdim passed to <see cref="SsAppMod" /> is rederived from the desired *engine*
    ///     target dimension (as for <c>spmv_ellpack_delimiters</c>).
    /// </summary>
    [Fact]
    public void Pipeline_Trmm_CorrectResult() {
        const int m = 4, n = 2;
        var a = new float[m * m];
        for (var i = 0; i < a.Length; i++) a[i] = i + 1;
        var b = new float[m * n];
        for (var i = 0; i < b.Length; i++) b[i] = 100 + i;

        // Independent oracle: mirrors the reference's own RUN_SIMPLE fallback.
        float[] bNew = b.ToArray();
        for (var i = 0; i < m; i++)
        for (var j = 0; j < n; j++)
        for (var k = i + 1; k < m; k++)
            bNew[i * n + j] += a[k * m + i];

        const ulong aBase = 0x0000, bBase = 0x0100;
        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < a.Length; i++) mem.Load(aBase + (ulong)(i * 4), BitConverter.GetBytes(a[i]));
        for (var i = 0; i < b.Length; i++) mem.Load(bBase + (ulong)(i * 4), BitConverter.GetBytes(b[i]));

        // Register plan: x1=A x2=B x3=M x4=M+1 x5=N x6=1 x7=M-1
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)aBase), Addi(2, 0, (int)bBase), Addi(3, 0, m), Addi(4, 0, m + 1),
            Addi(5, 0, n), Addi(6, 0, 1), Addi(7, 0, m - 1),

            // u3 = A (3-dim, triangular): D1(i) count=M stride=M+1; static Size-Dec modifier (disp=1)
            // targeting the innermost (k) dim; D2(j-repeat) count=N stride=0; D3(k) offset=M count=M-1
            // stride=M — activates with base offset by M elements (matches A[k*M+i]'s address algebra).
            SsStaLdW(3, 1), SsApp(3, 0, 3, 4), SsAppMod(3, 2, StreamModifierTarget.Size, StreamModifierBehavior.Dec, 6),
            SsApp(3, 0, 5, 0), SsEnd(3, 3, 7, 3),

            // u5 = B load: D1 count=M stride=N; D2 count=N stride=1
            SsStaLdW(5, 2), SsApp(5, 0, 3, 5), SsEnd(5, 0, 5, 6),
            // u1 = B store: same shape
            SsStaStW(1, 2), SsApp(1, 0, 3, 5), SsEnd(1, 0, 5, 6),

            // .SLOOP_1:
            SoVDpW(2, 0), // u2 = 0
            // .SLOOP_1_0_0:
            SoAFp(UveFpOp.AddeAcc, 2, 3, -1), // u2 += u3
            SoBNdcD(3, 2, -4),                // so.b.ndc.3 u3, .SLOOP_1_0_0
            SoAFp(UveFpOp.Add, 1, 2, 5),       // u1(store) = u2 + u5
            SoBNc(3, -16),                     // so.b.nc u3, .SLOOP_1

            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(10_000);

        for (var i = 0; i < b.Length; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(bBase + (ulong)(i * 4), 4));
            Assert.Equal(bNew[i], actual, 2);
        }
    }

    // ── Integration test: gemver (outer-product update) via OoO pipeline ────

    /// <summary>
    ///     Ports the first of <c>gemver</c>'s four chained sub-kernels in isolation
    ///     (github.com/hpc-ulisboa/UVE2, UVE-Testing/spike_test/benchmarks/gemver):
    ///     <c>A[i,j] += u1[i]*v1[j] + u2[i]*v2[j]</c>, a broadcast outer-product update. This is the
    ///     structurally distinctive one: four load streams each independently vary-per-i/repeat-per-j
    ///     or repeat-per-i/vary-per-j (two *simultaneous, independent* broadcast pairings feeding two
    ///     separate multiply-adds) — a genuinely new combination beyond 3mm's single broadcast-vs-vary
    ///     pairing. The other three sub-kernels (transposed matvec+reduction, vector add, matvec+
    ///     reduction) recombine patterns already exercised by mvt/3mm/jacobi-1d; see
    ///     <see cref="Pipeline_Gemver_FullKernel_CorrectResult" /> for the complete 4-stage chain.
    /// </summary>
    [Fact]
    public void Pipeline_Gemver_OuterProductUpdate_CorrectResult() {
        const int n = 2;
        float[] u1Vec = [2, 3,];
        float[] v1 = [5, 7,];
        float[] u2Vec = [4, 6,];
        float[] v2 = [8, 9,];
        float[] a = [1, 1, 1, 1,];

        // Independent oracle: mirrors the reference's own RUN_SIMPLE fallback.
        float[] aNew = a.ToArray();
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            aNew[i * n + j] += u1Vec[i] * v1[j] + u2Vec[i] * v2[j];

        const ulong aBase = 0x0000, v2Base = 0x0100, u2Base = 0x0200, v1Base = 0x0300, u1Base = 0x0400;
        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < a.Length; i++) mem.Load(aBase + (ulong)(i * 4), BitConverter.GetBytes(a[i]));
        for (var i = 0; i < n; i++) mem.Load(v2Base + (ulong)(i * 4), BitConverter.GetBytes(v2[i]));
        for (var i = 0; i < n; i++) mem.Load(u2Base + (ulong)(i * 4), BitConverter.GetBytes(u2Vec[i]));
        for (var i = 0; i < n; i++) mem.Load(v1Base + (ulong)(i * 4), BitConverter.GetBytes(v1[i]));
        for (var i = 0; i < n; i++) mem.Load(u1Base + (ulong)(i * 4), BitConverter.GetBytes(u1Vec[i]));

        // Register plan: x1=A x2=v2 x3=u2Vec x4=v1 x5=u1Vec x6=N x7=1
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)aBase), Addi(2, 0, (int)v2Base), Addi(3, 0, (int)u2Base),
            Addi(4, 0, (int)v1Base), Addi(5, 0, (int)u1Base), Addi(6, 0, n), Addi(7, 0, 1),

            // u1 = A store (2D): outer count=N stride=N; inner count=N stride=1
            SsStaStW(1, 1), SsApp(1, 0, 6, 6), SsEnd(1, 0, 6, 7),
            // u2 = v2 load: repeat-per-i (outer stride=0), vary-per-j (inner stride=1)
            SsStaLdW(2, 2), SsApp(2, 0, 6, 0), SsEnd(2, 0, 6, 7),
            // u3 = u2Vec load: vary-per-i (outer stride=1), repeat-per-j (inner stride=0)
            SsStaLdW(3, 3), SsApp(3, 0, 6, 7), SsEnd(3, 0, 6, 0),
            // u4 = v1 load: repeat-per-i, vary-per-j
            SsStaLdW(4, 4), SsApp(4, 0, 6, 0), SsEnd(4, 0, 6, 7),
            // u5 = u1Vec load: vary-per-i, repeat-per-j
            SsStaLdW(5, 5), SsApp(5, 0, 6, 7), SsEnd(5, 0, 6, 0),
            // u6 = A load (2D, same shape as the store)
            SsStaLdW(6, 1), SsApp(6, 0, 6, 6), SsEnd(6, 0, 6, 7),

            // .SLOOP_1:
            SoAFp(UveFpOp.Mul, 0, 5, 4), // u0 = u1Vec * v1
            SoAFp(UveFpOp.Add, 7, 6, 0), // u7 = Aload + u0
            SoAFp(UveFpOp.Mul, 0, 3, 2), // u0 = u2Vec * v2
            SoAFp(UveFpOp.Add, 1, 7, 0), // u1(store) = u7 + u0
            SoBNc(1, -16),               // so.b.nc u1, .SLOOP_1

            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(8000);

        for (var i = 0; i < aNew.Length; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(aBase + (ulong)(i * 4), 4));
            Assert.Equal(aNew[i], actual, 2);
        }
    }

    /// <summary>
    ///     Ports all four of <c>gemver</c>'s chained sub-kernels (github.com/hpc-ulisboa/UVE2,
    ///     UVE-Testing/spike_test/benchmarks/gemver), run back-to-back exactly as the reference
    ///     <c>core()</c> does: (1) the outer-product update from
    ///     <see cref="Pipeline_Gemver_OuterProductUpdate_CorrectResult" />; (2) a transposed
    ///     matvec+reduction into <c>x</c> (<c>x[i] += beta*A[j,i]*y[j]</c>, structurally identical to
    ///     <c>mvt</c>'s column-major pass); (3) a plain elementwise vector add (<c>x[i] += z[i]</c>,
    ///     identical shape to <c>memcpy</c>/<c>jacobi-1d</c>); (4) a non-transposed matvec+reduction
    ///     into <c>w</c> (<c>w[i] += alpha*A[i,j]*x[j]</c>, structurally identical to <c>mvt</c>'s
    ///     row-major pass, but consuming stage 2+3's updated <c>x</c>). u-registers 1-4 are reused
    ///     across all four stages (each prior stage's streams are fully exhausted before the next
    ///     stage reconfigures the same register), matching the reference's own register reuse; u10-u14
    ///     are pure arithmetic scratch/broadcast registers, never bound to a stream.
    /// </summary>
    [Fact]
    public void Pipeline_Gemver_FullKernel_CorrectResult() {
        const int n = 3;
        const float alpha = 2.0f, beta = 0.5f;
        float[] kernelU1 = [2, 3, 4,];
        float[] kernelV1 = [5, 6, 7,];
        float[] kernelU2 = [1, 2, 1,];
        float[] kernelV2 = [3, 1, 2,];
        float[] a = [1, 1, 1, 1, 1, 1, 1, 1, 1,];
        float[] y = [2, 1, 3,];
        float[] z = [10, 20, 30,];
        float[] x = [100, 200, 300,];
        float[] w = [1000, 2000, 3000,];

        // Independent oracle: mirrors the reference's own RUN_SIMPLE fallback, run in the same order
        // (each stage operates on the previous stage's updated arrays).
        float[] aNew = a.ToArray();
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            aNew[i * n + j] += kernelU1[i] * kernelV1[j] + kernelU2[i] * kernelV2[j];

        float[] xNew = x.ToArray();
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            xNew[i] += beta * aNew[j * n + i] * y[j];

        for (var i = 0; i < n; i++) xNew[i] += z[i];

        float[] wNew = w.ToArray();
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            wNew[i] += alpha * aNew[i * n + j] * xNew[j];

        // NOTE: all base addresses must stay within the 12-bit signed Addi immediate range
        // (-2048..2047) — 0x0800 (2048) would overflow and wrap to -2048.
        const ulong aBase = 0x0000, v1Base = 0x0080, v2Base = 0x0100, kernelU1Base = 0x0180,
            kernelU2Base = 0x0200, yBase = 0x0280, zBase = 0x0300, xBase = 0x0380, wBase = 0x0400;
        var mem = new FlatMemory(0x4000);
        for (var i = 0; i < a.Length; i++) mem.Load(aBase + (ulong)(i * 4), BitConverter.GetBytes(a[i]));
        for (var i = 0; i < n; i++) mem.Load(v1Base + (ulong)(i * 4), BitConverter.GetBytes(kernelV1[i]));
        for (var i = 0; i < n; i++) mem.Load(v2Base + (ulong)(i * 4), BitConverter.GetBytes(kernelV2[i]));
        for (var i = 0; i < n; i++) mem.Load(kernelU1Base + (ulong)(i * 4), BitConverter.GetBytes(kernelU1[i]));
        for (var i = 0; i < n; i++) mem.Load(kernelU2Base + (ulong)(i * 4), BitConverter.GetBytes(kernelU2[i]));
        for (var i = 0; i < n; i++) mem.Load(yBase + (ulong)(i * 4), BitConverter.GetBytes(y[i]));
        for (var i = 0; i < n; i++) mem.Load(zBase + (ulong)(i * 4), BitConverter.GetBytes(z[i]));
        for (var i = 0; i < n; i++) mem.Load(xBase + (ulong)(i * 4), BitConverter.GetBytes(x[i]));
        for (var i = 0; i < n; i++) mem.Load(wBase + (ulong)(i * 4), BitConverter.GetBytes(w[i]));

        // Register plan: x1=aBase x2=v2Base x3=kernelU2Base x4=v1Base x5=kernelU1Base x6=N x7=1
        //                x8=yBase x9=xBase x10=bits(beta) x11=zBase x12=bits(alpha) x13=wBase
        const ulong code = 0x1000;
        var words = new List<uint>();

        words.Add(Addi(1, 0, (int)aBase)); words.Add(Addi(2, 0, (int)v2Base));
        words.Add(Addi(3, 0, (int)kernelU2Base)); words.Add(Addi(4, 0, (int)v1Base));
        words.Add(Addi(5, 0, (int)kernelU1Base)); words.Add(Addi(6, 0, n)); words.Add(Addi(7, 0, 1));
        words.Add(Addi(8, 0, (int)yBase)); words.Add(Addi(9, 0, (int)xBase));
        words.Add(Lui(10, BitConverter.SingleToInt32Bits(beta) >> 12));
        words.Add(Addi(11, 0, (int)zBase));
        words.Add(Lui(12, BitConverter.SingleToInt32Bits(alpha) >> 12));
        words.Add(Addi(13, 0, (int)wBase));

        // ── STAGE 1: A[i,j] += kernelU1[i]*v1[j] + kernelU2[i]*v2[j] ──
        words.Add(SsStaStW(1, 1)); words.Add(SsApp(1, 0, 6, 6)); words.Add(SsEnd(1, 0, 6, 7));
        words.Add(SsStaLdW(2, 2)); words.Add(SsApp(2, 0, 6, 0)); words.Add(SsEnd(2, 0, 6, 7));
        words.Add(SsStaLdW(3, 3)); words.Add(SsApp(3, 0, 6, 7)); words.Add(SsEnd(3, 0, 6, 0));
        words.Add(SsStaLdW(4, 4)); words.Add(SsApp(4, 0, 6, 0)); words.Add(SsEnd(4, 0, 6, 7));
        words.Add(SsStaLdW(5, 5)); words.Add(SsApp(5, 0, 6, 7)); words.Add(SsEnd(5, 0, 6, 0));
        words.Add(SsStaLdW(6, 1)); words.Add(SsApp(6, 0, 6, 6)); words.Add(SsEnd(6, 0, 6, 7));

        var loopStart1 = words.Count;
        words.Add(SoAFp(UveFpOp.Mul, 0, 5, 4));
        words.Add(SoAFp(UveFpOp.Add, 7, 6, 0));
        words.Add(SoAFp(UveFpOp.Mul, 0, 3, 2));
        words.Add(SoAFp(UveFpOp.Add, 1, 7, 0));
        words.Add(SoBNc(1, (loopStart1 - words.Count) * 4));

        // ── STAGE 2: x[i] += beta * A[j,i] * y[j] (transposed matvec + reduction over j) ──
        words.Add(SsStaStW(1, 9)); words.Add(SsEnd(1, 0, 6, 7));
        words.Add(SsStaLdW(2, 1)); words.Add(SsApp(2, 0, 6, 7)); words.Add(SsEnd(2, 0, 6, 6));
        words.Add(SsStaLdW(3, 8)); words.Add(SsApp(3, 0, 6, 0)); words.Add(SsEnd(3, 0, 6, 7));
        words.Add(SsStaLdW(4, 9)); words.Add(SsEnd(4, 0, 6, 7));
        words.Add(SoVDpW(13, 10));

        var outerStart2 = words.Count;
        words.Add(SoVDpW(11, 0));
        var innerStart2 = words.Count;
        words.Add(SoAFp(UveFpOp.Mul, 12, 2, 13));
        words.Add(SoAFp(UveFpOp.Mul, 12, 12, 3));
        words.Add(SoAFp(UveFpOp.AddeAcc, 11, 12, -1));
        words.Add(SoBNdcD(2, 1, (innerStart2 - words.Count) * 4));
        words.Add(SoAFp(UveFpOp.Add, 1, 4, 11));
        words.Add(SoBNc(1, (outerStart2 - words.Count) * 4));

        // ── STAGE 3: x[i] += z[i] ──
        words.Add(SsStaStW(1, 9)); words.Add(SsEnd(1, 0, 6, 7));
        words.Add(SsStaLdW(2, 11)); words.Add(SsEnd(2, 0, 6, 7));
        words.Add(SsStaLdW(3, 9)); words.Add(SsEnd(3, 0, 6, 7));

        var loopStart3 = words.Count;
        words.Add(SoAFp(UveFpOp.Add, 1, 3, 2));
        words.Add(SoBNc(1, (loopStart3 - words.Count) * 4));

        // ── STAGE 4: w[i] += alpha * A[i,j] * x[j] (matvec + reduction over j) ──
        words.Add(SsStaStW(1, 13)); words.Add(SsEnd(1, 0, 6, 7));
        words.Add(SsStaLdW(2, 1)); words.Add(SsApp(2, 0, 6, 6)); words.Add(SsEnd(2, 0, 6, 7));
        words.Add(SsStaLdW(3, 9)); words.Add(SsApp(3, 0, 6, 0)); words.Add(SsEnd(3, 0, 6, 7));
        words.Add(SsStaLdW(4, 13)); words.Add(SsEnd(4, 0, 6, 7));
        words.Add(SoVDpW(14, 12));

        var outerStart4 = words.Count;
        words.Add(SoVDpW(11, 0));
        var innerStart4 = words.Count;
        words.Add(SoAFp(UveFpOp.Mul, 12, 2, 14));
        words.Add(SoAFp(UveFpOp.Mul, 12, 12, 3));
        words.Add(SoAFp(UveFpOp.AddeAcc, 11, 12, -1));
        words.Add(SoBNdcD(2, 1, (innerStart4 - words.Count) * 4));
        words.Add(SoAFp(UveFpOp.Add, 1, 4, 11));
        words.Add(SoBNc(1, (outerStart4 - words.Count) * 4));

        words.Add(EBreak());

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(30_000);

        for (var i = 0; i < aNew.Length; i++) {
            float actualA = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(aBase + (ulong)(i * 4), 4));
            Assert.Equal(aNew[i], actualA, 2);
        }
        for (var i = 0; i < n; i++) {
            float actualW = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(wBase + (ulong)(i * 4), 4));
            Assert.Equal(wNew[i], actualW, 2);
        }

        return;

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);
    }

    // ── Integration test: covariance via OoO pipeline ────────────────────────

    /// <summary>
    ///     Ports the <c>covariance</c> UVE2 reference benchmark (github.com/hpc-ulisboa/UVE2,
    ///     UVE-Testing/spike_test/benchmarks/covariance) in full: per-column mean (reduction + divide),
    ///     broadcast-subtract centering, then an upper-triangular covariance matrix with mirrored
    ///     <c>cov[i,j]=cov[j,i]</c> writes. The triangular stage introduces the <c>Offset</c> modifier
    ///     target (unused by every prior port, which only ever resized <c>Size</c>) — <c>cov</c>'s two
    ///     store streams each carry *two simultaneous* static modifiers (Offset-Inc making <c>j</c>
    ///     start at <c>i</c>; Size-Dec shrinking its count), both self-triggering on their own target
    ///     dimension's wrap (verified by tracing <c>StreamingEngine</c>'s reset/apply ordering, not
    ///     guessed). As with <c>trmm</c>/<c>spmv_ellpack_delimiters</c>, the kernel's literal
    ///     <c>.inc.2</c>/<c>.dec.2</c> suffixes are NOT copied into the <see cref="SsAppMod" /> calls —
    ///     every <c>tdim</c> here is rederived to target engine index 0 (the innermost/only-resized
    ///     dimension of each 2- or 3-dim stream involved).
    /// </summary>
    [Fact]
    public void Pipeline_Covariance_CorrectResult() => RunCovariance(2, 3, [1, 10, 2, 20, 6, 30,]);

    /// <summary>
    ///     Same kernel as <see cref="Pipeline_Covariance_CorrectResult" /> but scaled up (M=3, N=4,
    ///     asymmetric non-arithmetic-progression data) so the store-stream-modifier fix isn't only
    ///     validated at the original port's minimal 2x3 size — the triangular stage's Offset/Size
    ///     modifiers now actually shrink/grow across more than one step per store stream.
    /// </summary>
    [Fact]
    public void Pipeline_Covariance_Scaled3x4_CorrectResult() =>
        RunCovariance(3, 4, [1, 10, 100, 2, 7, 90, 5, 3, 40, 9, 20, 8,]);

    private static void RunCovariance(int m, int n, float[] data) {
        float datatN = n, datatNn = n - 1; // datatN - 1

        // Independent oracle: mirrors the reference's own RUN_SIMPLE fallback.
        var mean = new float[m];
        for (var j = 0; j < m; j++) {
            for (var i = 0; i < n; i++) mean[j] += data[i * m + j];
            mean[j] /= datatN;
        }

        float[] centered = data.ToArray();
        for (var i = 0; i < n; i++)
        for (var j = 0; j < m; j++)
            centered[i * m + j] -= mean[j];

        var cov = new float[m * m];
        for (var i = 0; i < m; i++)
        for (var j = i; j < m; j++) {
            float sum = 0;
            for (var k = 0; k < n; k++) sum += centered[k * m + i] * centered[k * m + j];
            cov[i * m + j] = sum / datatNn;
            cov[j * m + i] = cov[i * m + j];
        }

        const ulong dataBase = 0x0000, meanBase = 0x0100, covBase = 0x0200;
        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < data.Length; i++) mem.Load(dataBase + (ulong)(i * 4), BitConverter.GetBytes(data[i]));

        // Register plan: x1=data x2=mean x3=M x4=N x5=1 x6=bits(datatN) x7=cov x8=bits(datatNn)
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, (int)dataBase), Addi(2, 0, (int)meanBase), Addi(3, 0, m), Addi(4, 0, n),
            Addi(5, 0, 1), Lui(6, BitConverter.SingleToInt32Bits(datatN) >> 12), Addi(7, 0, (int)covBase),
            Lui(8, BitConverter.SingleToInt32Bits(datatNn) >> 12),

            // ── STAGE 1: mean[j] = sum_i data[i,j] / datatN ──
            // u1 = data load: D1(outer,"j") count=M stride=1; D2(final,"i") count=N stride=M
            SsStaLdW(1, 1), SsApp(1, 0, 3, 5), SsEnd(1, 0, 4, 3),
            // u2 = mean store: count=M stride=1
            SsStaStW(2, 2), SsEnd(2, 0, 3, 5),
            SoVMvsv(3, 6, 4), // u3 = broadcast datatN

            // .SLOOP_1:
            SoVMvsv(4, 0, 4), // u4 = 0.0 (accumulator)
            // .SLOOP_1_0:
            SoAFp(UveFpOp.AddeAcc, 4, 1, -1), // u4 += u1
            SoBNdcD(1, 1, -4),                // so.b.ndc.2 u1, .SLOOP_1_0
            SoAFp(UveFpOp.Div, 2, 4, 3),       // u2(store) = u4 / u3
            SoBNc(1, -16),                     // so.b.nc u1, .SLOOP_1

            // ── STAGE 2: data[i,j] -= mean[j] ──
            // u1 = data store: D1("i") count=N stride=M; D2(final,"j") count=M stride=1
            SsStaStW(1, 1), SsApp(1, 0, 4, 3), SsEnd(1, 0, 3, 5),
            // u2 = mean load: D1 repeat-per-i (stride=0); D2(final) vary-per-j (stride=1)
            SsStaLdW(2, 2), SsApp(2, 0, 4, 0), SsEnd(2, 0, 3, 5),
            // u3 = data load (same shape as u1's store)
            SsStaLdW(3, 1), SsApp(3, 0, 4, 3), SsEnd(3, 0, 3, 5),

            // .SLOOP_2:
            SoAFp(UveFpOp.Sub, 1, 3, 2), // u1(store) = u3(data) - u2(mean)
            SoBNc(1, -4),                 // so.b.nc u1, .SLOOP_2

            // ── STAGE 3: cov[i,j] = sum_k centered[k,i]*centered[k,j] / datatNn, for j>=i ──
            // u1 = data col-j (shrinking): D1("i") count=M stride=1; static Size-Dec (self-triggering,
            // self-targeting the same dim — tdim rederived to engine index 0, NOT the kernel's ".2");
            // D2("j", shrinking) count=M stride=1; D3(final,"k") count=N stride=M.
            SsStaLdW(1, 1), SsApp(1, 0, 3, 5), SsAppMod(1, 1, StreamModifierTarget.Size, StreamModifierBehavior.Dec, 5),
            SsApp(1, 0, 3, 5), SsEnd(1, 0, 4, 3),

            // u2 = data col-i (fixed): same D1/modifier; D2 repeat (stride=0) so its address never
            // varies with j; D3 same as u1.
            SsStaLdW(2, 1), SsApp(2, 0, 3, 5), SsAppMod(2, 1, StreamModifierTarget.Size, StreamModifierBehavior.Dec, 5),
            SsApp(2, 0, 3, 0), SsEnd(2, 0, 4, 3),

            // u3 = cov[i,j] store: D1("i") count=M stride=M (row shift); Offset-Inc (j starts at i) +
            // Size-Dec (j's count shrinks), both self-triggering/targeting D2; D2(final,"j") count=M
            // stride=1.
            SsStaStW(3, 7), SsApp(3, 0, 3, 3), SsAppMod(3, 1, StreamModifierTarget.Offset, StreamModifierBehavior.Inc, 5),
            SsAppMod(3, 1, StreamModifierTarget.Size, StreamModifierBehavior.Dec, 5), SsEnd(3, 0, 3, 5),

            // u4 = cov[j,i] store (mirror): D1("i") count=M stride=1 (column shift); Offset-Inc
            // disp=M (row-shift accumulation) + Size-Dec; D2(final) count=M stride=M (row shift).
            SsStaStW(4, 7), SsApp(4, 0, 3, 5), SsAppMod(4, 1, StreamModifierTarget.Offset, StreamModifierBehavior.Inc, 3),
            SsAppMod(4, 1, StreamModifierTarget.Size, StreamModifierBehavior.Dec, 5), SsEnd(4, 0, 3, 3),

            SoVMvsv(5, 8, 4), // u5 = broadcast datatNn

            // .SLOOP_3:
            SoVDpW(6, 0), // u6 = 0 (accumulator)
            // .SLOOP_3_0_0:
            SoAFp(UveFpOp.Mac, 6, 2, 1), // u6 += u2*u1
            SoBNdcD(2, 2, -4),           // so.b.ndc.3 u2, .SLOOP_3_0_0
            SoAFp(UveFpOp.Adde, 7, 6, -1), // u7 = u6 (reduce copy)
            SoAFp(UveFpOp.Div, 8, 7, 5),    // u8(scalar temp) = u7 / u5
            SoVMv(3, 8), SoVMv(4, 8),        // cov[i,j] = u8; cov[j,i] = u8
            SoBNc(1, -28),                    // so.b.nc u1, .SLOOP_3

            EBreak(),
        };

        for (var i = 0; i < words.Count; i++) mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(30_000);

        for (var i = 0; i < cov.Length; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(covBase + (ulong)(i * 4), 4));
            Assert.Equal(cov[i], actual, 2);
        }

        return;

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);
    }

    // ── ss.app.mod decode tests ──────────────────────────────────────────────

    [Fact]
    public void SsAppMod_DecodesCorrectly() {
        var mem = new FlatMemory(16);
        // tdim=0, target=Size, behavior=Inc, rs3Disp=x5
        mem.Load(0, BitConverter.GetBytes(SsAppMod(1, 0, StreamModifierTarget.Size, StreamModifierBehavior.Inc, 5)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSsAppMod>(tooth.Payload);
        Assert.Equal(1, op.Ud);
        Assert.Equal(0, op.TargetDimRaw);
        Assert.Equal(StreamModifierTarget.Size, op.Target);
        Assert.Equal(StreamModifierBehavior.Inc, op.Behavior);
        Assert.Equal(5, op.Rs3Disp);
    }

    [Fact]
    public void SsAppMod_StrideTarget_DecodesCorrectly() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SsAppMod(2, 1, StreamModifierTarget.Stride, StreamModifierBehavior.Dec, 7)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSsAppMod>(tooth.Payload);
        Assert.Equal(2, op.Ud);
        Assert.Equal(1, op.TargetDimRaw);
        Assert.Equal(StreamModifierTarget.Stride, op.Target);
        Assert.Equal(StreamModifierBehavior.Dec, op.Behavior);
        Assert.Equal(7, op.Rs3Disp);
    }

    // ── ss.app.mod integration tests ─────────────────────────────────────────

    private static float LowerTriangularExpected(int n) {
        var sum = 0f;
        for (var r = 0; r < n; r++)
        for (var c = 0; c <= r; c++)
            sum += r * n + c + 1;
        return sum;
    }

    // 2D stream with static Size modifier via ss.sta.ld.w → ss.app → ss.app.mod → ss.end.
    // The modifier grows the innermost count by 1 on each row wrap (lower-triangular access).
    private static float RunSsAppModLowerTriangular(int n) {
        const ulong matBase = 0x0200u;
        const ulong resultAddr = 0x0100u;
        const ulong codeBase = 0x1000u;

        var mem = new FlatMemory(0x4000);
        for (var r = 0; r < n; r++)
        for (var c = 0; c < n; c++) {
            float v = r * n + c + 1;
            mem.Load(matBase + (ulong)((r * n + c) * 4), BitConverter.GetBytes(v));
        }

        // Registers: x1=matBase, x2=N, x3=N (element stride for outer rows), x4=1 (element stride), x5=1(disp register)
        // Stream (config outermost-first): ss.sta.ld.w (base) → ss.app (rows: count=x2=N, stride=x3=N elems)
        //       → ss.app.mod (dimIndex=1 = innermost of 2, Size, Inc, disp=x5)
        //       → ss.end (row elements: count=x5=1, stride=x4=1 elem)
        // Loop: so.b.nc u1 (whole-stream done check)
        uint[] words = [
            Addi(1, 0, (int)matBase), // [0]
            Addi(2, 0, n), // [1] x2 = N
            Addi(3, 0, n), // [2] x3 = N (element stride for outer rows → N*4 bytes after scaling)
            Addi(4, 0, 1), // [3] x4 = 1 (element stride → 4 bytes after scaling)
            Addi(5, 0, 1), // [4] x5 = 1 (disp)
            SoVDpW(2, 0), // [5] u2 = 0.0f
            SsStaLdW(1, 1), // [6] base=x1
            SsApp(1, 0, 2, 3), // [7] outer rows: count=x2(N), stride=x3(N elems)
            SsAppMod(1, 1, StreamModifierTarget.Size, StreamModifierBehavior.Inc, 5), // [8] innermost.Size += x5
            SsEnd(1, 0, 5, 4), // [9] innermost: count=x5(1), stride=x4(1 elem); activate
            SoAFp(UveFpOp.Add, 2, 1, 2), // [10] u2 += elem
            SoBNc(1, -4), // [11] loop while stream active (back 1 instr)
            Addi(9, 0, (int)resultAddr), // [12]
            Addi(10, 0, 1), // [13]
            SsStaStW(3, 9), SsEnd(3, 0, 10, 4), // [14,15] 1D store stream, stride=x4(1 elem)
            SoAFp(UveFpOp.Add, 3, 2, 0), // [16] write u2 to result
            EBreak(), // [17]
        ];

        for (var i = 0; i < words.Length; i++) mem.Load(codeBase + (ulong)(i * 4), BitConverter.GetBytes(words[i]));
        new OooeTrain(new Rv32Mechanism(), mem, codeBase, streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32)
           .Run(20_000);
        return BitConverter.Int32BitsToSingle((int)(uint)mem.Read(resultAddr, 4));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SsAppMod_LowerTriangular_CorrectSum(int n) {
        Assert.Equal(LowerTriangularExpected(n), RunSsAppModLowerTriangular(n), 3);
    }

    // Single-variable-changed sibling of RunSsAppModLowerTriangular: identical dimensions/modifier
    // shape, but the stream is a STORE stream (ss.sta.st.w) driven by a tight write+so.b.nc loop
    // (same shape as Pipeline_SoBNc_OnStoreStream_LoopsUntilExhausted), instead of a load stream
    // read by SoAFp.Add. Writes a running counter 1,2,3,... in lower-triangular row order; checks
    // every written element AND that untouched cells stay at the sentinel, so a loop that dies
    // after 1 iteration (the covariance-motivated regression concern) is caught directly rather
    // than only being caught by the whole-kernel covariance test's more complex instruction mix.
    private static float[] RunSsAppModStoreLowerTriangular(int n) {
        const ulong matBase = 0x0200u;
        const ulong codeBase = 0x1000u;
        const float sentinel = -1f;

        var mem = new FlatMemory(0x4000);
        for (var i = 0; i < n * n; i++) mem.Load(matBase + (ulong)(i * 4), BitConverter.GetBytes(sentinel));

        // Registers: x1=matBase, x2=N (row count), x3=N (row stride elems), x4=1 (elem stride), x5=1 (disp)
        uint[] words = [
            Addi(1, 0, (int)matBase), // [0]
            Addi(2, 0, n), // [1] x2 = N
            Addi(3, 0, n), // [2] x3 = N (row stride, elems)
            Addi(4, 0, 1), // [3] x4 = 1 (elem stride)
            Addi(5, 0, 1), // [4] x5 = 1 (disp)
            Lui(6, BitConverter.SingleToInt32Bits(1.0f) >> 12), // [5] x6 = bits(1.0f)
            SsStaStW(1, 1), // [6] base=x1
            SsApp(1, 0, 2, 3), // [7] outer rows: count=x2(N), stride=x3(N elems)
            SsAppMod(1, 1, StreamModifierTarget.Size, StreamModifierBehavior.Inc, 5), // [8] innermost.Size += x5
            SsEnd(1, 0, 5, 4), // [9] innermost: count=x5(1), stride=x4(1 elem); activate
            SoVDpW(2, 6), // [10] u2 = 1.0f broadcast (increment constant)
            SoVDpW(3, 0), // [11] u3 = 0.0 running counter
            // .LOOP:
            SoAFp(UveFpOp.Add, 3, 3, 2), // [12] u3 += u2 (counter++)
            SoAFp(UveFpOp.Add, 1, 3, -1), // [13] u1(store) = u3 (unary copy)
            SoBNc(1, -8), // [14] loop while store stream active (back to [12])
            EBreak(), // [15]
        ];

        for (var i = 0; i < words.Length; i++) mem.Load(codeBase + (ulong)(i * 4), BitConverter.GetBytes(words[i]));
        new OooeTrain(new Rv32Mechanism(), mem, codeBase, streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32)
           .Run(20_000);

        var result = new float[n * n];
        for (var i = 0; i < result.Length; i++)
            result[i] = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(matBase + (ulong)(i * 4), 4));
        return result;

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void SsAppMod_StoreStream_LowerTriangular_WritesSequentialCounter(int n) {
        const float sentinel = -1f;
        var expected = new float[n * n];
        for (var i = 0; i < expected.Length; i++) expected[i] = sentinel;
        var counter = 0f;
        for (var r = 0; r < n; r++)
        for (var c = 0; c <= r; c++)
            expected[r * n + c] = ++counter;

        float[] actual = RunSsAppModStoreLowerTriangular(n);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], 3);
    }

    /// <summary>
    ///     ".L" (tdim=7) must target the *last configured dimension of the stream* (author-confirmed,
    ///     2026-07-22 — see SPEC_NOTES.md's ".L modifier suffix" entry), not "the dimension configured
    ///     right after the trigger" as previously implemented. The two readings only coincide when the
    ///     modifier triggers on the second-to-last configured dimension; here the modifier triggers on
    ///     the *outermost* of three dimensions, with two more dimensions (middle, innermost) configured
    ///     afterward, so the old ("trigger+1" → middle dimension, engine index 1) and correct
    ///     ("last configured" → innermost dimension, engine index 0) readings genuinely diverge.
    /// </summary>
    [Fact]
    public void SsAppMod_DotL_TargetsLastConfiguredDimension_NotTriggerPlusOne() {
        var state = new Rv32ArchState();
        var cfg = new PendingStreamConfig { BaseAddress = 0x1000, ElementBytes = 4, IsLoad = true, };
        state.UveState.PendingConfig[1] = cfg;

        // ss.sta.ld already ran (implicit above); ss.app configures the outermost dimension.
        cfg.Dimensions.Add(new StreamDimension(2, 16)); // outermost dim (Spike index 0), the trigger

        // ss.app.mod u1, .L, Size, Inc, x5 — triggers on the outermost dim just appended above.
        state.IntegerRegisters.Write(5, 1); // displacement (unused by this test beyond non-zero)
        ExecuteResult modResult = Exec(new RvUveSsAppMod(1, 7, StreamModifierTarget.Size, StreamModifierBehavior.Inc, 5), state);
        modResult.SideEffect?.Invoke(state);

        // Two more ss.app-style dimensions configured after the modifier: middle, then innermost via ss.end.
        cfg.Dimensions.Add(new StreamDimension(2, 4)); // middle dim (Spike index 1)
        state.IntegerRegisters.Write(2, 2); // innermost count
        state.IntegerRegisters.Write(3, 1); // innermost stride (1 elem = 4 bytes)
        ExecuteResult endResult = Exec(new RvUveSsEnd(1, 0, 2, 3), state);

        Assert.True(endResult.StreamConfig.HasValue);
        StreamDescriptor desc = endResult.StreamConfig!.Value.Descriptor;
        Assert.NotNull(desc.Modifiers);
        Assert.Single(desc.Modifiers!);
        // Engine order is innermost-first: index 0 = innermost dim (the true "last configured" one).
        // The old "trigger+1" bug would have resolved this to engine index 1 (the middle dimension).
        Assert.Equal(0, desc.Modifiers![0].TargetDim);
    }

    // ── ss.app.ind (dynamic indirect modifier) tests ─────────────────────────
    // Unlike ss.app.sgi/ss.end.sgi (StreamDescriptor.SgiMod), ss.app.ind attaches a general
    // StreamModifier with SourceStreamId set, reusing the same Target/Behavior/TriggerDim/TargetDim
    // machinery as the static ss.app.mod modifier. Had zero test coverage before this pair.

    [Fact]
    public void Decoder_SsAppInd_Roundtrip() {
        var mem = new FlatMemory(4);
        mem.Load(0, BitConverter.GetBytes(SsAppInd(2, 3, StreamModifierTarget.Offset, StreamModifierBehavior.Add, 7)));
        var op = Assert.IsType<RvUveSsAppInd>(new Rv32Decoder().Decode(0, mem).Payload);
        Assert.Equal(2, op.Ud);
        Assert.Equal(3, op.TargetDimRaw);
        Assert.Equal(StreamModifierTarget.Offset, op.Target);
        Assert.Equal(StreamModifierBehavior.Add, op.Behavior);
        Assert.Equal(7, op.SourceStreamId);
    }

    /// <summary>
    ///     Mirrors <see cref="SsAppMod_DotL_TargetsLastConfiguredDimension_NotTriggerPlusOne" /> but for
    ///     the dynamic (<c>ss.app.ind</c>) modifier family: attaches a <see cref="StreamModifier" /> with
    ///     <see cref="StreamModifier.SourceStreamId" /> set (rather than <see cref="StreamDescriptor.SgiMod" />),
    ///     and confirms ".L" (tdim=7) resolves to the last configured (innermost) dimension here too.
    /// </summary>
    [Fact]
    public void SsAppInd_DotL_AttachesSourceStreamModifier_TargetsLastConfiguredDimension() {
        var state = new Rv32ArchState();
        var cfg = new PendingStreamConfig { BaseAddress = 0x1000, ElementBytes = 4, IsLoad = true, };
        state.UveState.PendingConfig[1] = cfg;

        cfg.Dimensions.Add(new StreamDimension(2, 16)); // outermost dim (Spike index 0), the trigger

        // ss.app.ind u1, .L, Offset, Add, u9 — triggers on the outermost dim just appended above.
        ExecuteResult indResult = Exec(new RvUveSsAppInd(1, 7, StreamModifierTarget.Offset, StreamModifierBehavior.Add, 9), state);
        indResult.SideEffect?.Invoke(state);

        // One more dimension configured after the modifier, via ss.end (the innermost).
        state.IntegerRegisters.Write(2, 2); // innermost count
        state.IntegerRegisters.Write(3, 1); // innermost stride (1 elem = 4 bytes)
        ExecuteResult endResult = Exec(new RvUveSsEnd(1, 0, 2, 3), state);

        Assert.True(endResult.StreamConfig.HasValue);
        StreamDescriptor desc = endResult.StreamConfig!.Value.Descriptor;
        Assert.NotNull(desc.Modifiers);
        StreamModifier mod = Assert.Single(desc.Modifiers!);
        Assert.Equal(9, mod.SourceStreamId);
        Assert.Equal(StreamModifierTarget.Offset, mod.Target);
        Assert.Equal(StreamModifierBehavior.Add, mod.Behavior);
        // ndim=2: engine index 0 = innermost (the ss.end-appended dim) — the true "last configured" one.
        Assert.Equal(0, mod.TargetDim);
    }

    // ── FP extended ops (Min/Max/Abs/Inc/Dec) ─────────────────────────────────

    [Fact]
    public void SoAFp_Min_ReturnsSmaller() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, 3.0f);
        state.UveState.SetScalar(2, 7.0f);
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Min, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(3.0f, state.UveState.GetScalar(5));
    }

    [Fact]
    public void SoAFp_Max_ReturnsLarger() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, 3.0f);
        state.UveState.SetScalar(2, 7.0f);
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Max, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(7.0f, state.UveState.GetScalar(5));
    }

    [Fact]
    public void SoAFp_Abs_RemovesSign() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, -4.5f);
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Abs, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(4.5f, state.UveState.GetScalar(5));
    }

    [Fact]
    public void SoAFp_Inc_AddsOne() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, 9.0f);
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Inc, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(10.0f, state.UveState.GetScalar(5));
    }

    [Fact]
    public void SoAFp_Dec_SubtractsOne() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, 5.0f);
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Dec, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(4.0f, state.UveState.GetScalar(5));
    }

    // ── Integer arithmetic ops ─────────────────────────────────────────────────

    [Fact]
    public void SoAInt_Add_US_ComputesSum() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(10u));
        state.UveState.SetScalar(2, Ub(32u));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Add, false, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(42u, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAInt_Sub_SG_ComputesSignedDifference() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ib(5));
        state.UveState.SetScalar(2, Ib(8));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Sub, true, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(-3, Rib(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAInt_Mul_US_ComputesProduct() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(6u));
        state.UveState.SetScalar(2, Ub(7u));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Mul, false, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(42u, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAInt_Div_SG_ComputesQuotient() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ib(-20));
        state.UveState.SetScalar(2, Ib(4));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Div, true, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(-5, Rib(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAInt_Mac_AccumulatesResult() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(5, Ib(100)); // accumulator
        state.UveState.SetScalar(1, Ib(3));
        state.UveState.SetScalar(2, Ib(4));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Mac, true, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(112, Rib(state.UveState.GetScalar(5))); // 100 + 3*4
    }

    [Fact]
    public void SoAInt_Min_SG_ReturnsMinimum() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ib(-3));
        state.UveState.SetScalar(2, Ib(5));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Min, true, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(-3, Rib(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAInt_Max_US_ReturnsMaximum() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(0xFFFFFFF0u));
        state.UveState.SetScalar(2, Ub(0x00000010u));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Max, false, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(0xFFFFFFF0u, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAInt_Abs_SG_RemovesSign() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ib(-42));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Abs, true, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(42, Rib(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAInt_Inc_US_Increments() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(99u));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Inc, false, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(100u, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAInt_Dec_SG_Decrements() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ib(0));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Dec, true, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(-1, Rib(state.UveState.GetScalar(5)));
    }

    // ── Logic ops ─────────────────────────────────────────────────────────────

    [Fact]
    public void SoALogic_And_ComputesBitwiseAnd() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(0xFF00FF00u));
        state.UveState.SetScalar(2, Ub(0xF0F0F0F0u));
        ExecuteResult er = Exec(new RvUveSoALogic(UveLogicOp.And, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(0xF000F000u, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoALogic_Or_ComputesBitwiseOr() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(0xFF00FF00u));
        state.UveState.SetScalar(2, Ub(0x00FF00FFu));
        ExecuteResult er = Exec(new RvUveSoALogic(UveLogicOp.Or, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(0xFFFFFFFFu, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoALogic_Xor_ComputesBitwiseXor() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(0xAAAAAAAAu));
        state.UveState.SetScalar(2, Ub(0x55555555u));
        ExecuteResult er = Exec(new RvUveSoALogic(UveLogicOp.Xor, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(0xFFFFFFFFu, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoALogic_Not_InvertsBits() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(0xFFFF0000u));
        ExecuteResult er = Exec(new RvUveSoALogic(UveLogicOp.Not, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(0x0000FFFFu, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoALogic_Nand_ComputesNand() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(0xFFFFFFFFu));
        state.UveState.SetScalar(2, Ub(0xFFFFFFFFu));
        ExecuteResult er = Exec(new RvUveSoALogic(UveLogicOp.Nand, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(0u, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoALogic_Nor_ComputesNor() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(0u));
        state.UveState.SetScalar(2, Ub(0u));
        ExecuteResult er = Exec(new RvUveSoALogic(UveLogicOp.Nor, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(0xFFFFFFFFu, Rub(state.UveState.GetScalar(5)));
    }

    // ── Shift ops ─────────────────────────────────────────────────────────────

    [Fact]
    public void SoAShiftV_Sll_ShiftsLeft() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(1u));
        state.UveState.SetScalar(2, Ub(8u)); // shift amount
        ExecuteResult er = Exec(new RvUveSoAShiftV(UveShiftOp.Sll, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(256u, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAShiftV_Srl_ShiftsRightLogical() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(0x80000000u));
        state.UveState.SetScalar(2, Ub(1u));
        ExecuteResult er = Exec(new RvUveSoAShiftV(UveShiftOp.Srl, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(0x40000000u, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAShiftV_Sra_ShiftsRightArithmetic() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ib(-8));
        state.UveState.SetScalar(2, Ub(1u));
        ExecuteResult er = Exec(new RvUveSoAShiftV(UveShiftOp.Sra, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(-4, Rib(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAShiftS_Sll_UsesIntegerRegisterForAmount() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ub(1u));
        state.IntegerRegisters.Write(3, 4); // rs2=x3 holds shift amount 4
        ExecuteResult er = Exec(new RvUveSoAShiftS(UveShiftOp.Sll, 5, 1, 3), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(16u, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAShiftS_Sra_SignExtends() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ib(int.MinValue)); // 0x80000000
        state.IntegerRegisters.Write(3, 31);
        ExecuteResult er = Exec(new RvUveSoAShiftS(UveShiftOp.Sra, 5, 1, 3), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(-1, Rib(state.UveState.GetScalar(5)));
    }

    // ── Decoder round-trips for new ops ───────────────────────────────────────

    [Fact]
    public void Decoder_SoAInt_Add_US_Roundtrip() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoAInt(UveIntOp.Add, false, 5, 1, 2)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoAInt>(tooth.Payload);
        Assert.Equal(UveIntOp.Add, op.Op);
        Assert.False(op.Signed);
        Assert.Equal(5, op.Ud);
        Assert.Equal(1, op.Usrc1);
        Assert.Equal(2, op.Usrc2);
    }

    [Fact]
    public void Decoder_SoAInt_Mac_SG_Roundtrip() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoAInt(UveIntOp.Mac, true, 3, 1, 2)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoAInt>(tooth.Payload);
        Assert.Equal(UveIntOp.Mac, op.Op);
        Assert.True(op.Signed);
    }

    [Fact]
    public void Decoder_SoALogic_Xor_Roundtrip() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoALogic(UveLogicOp.Xor, 5, 1, 2)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoALogic>(tooth.Payload);
        Assert.Equal(UveLogicOp.Xor, op.Op);
        Assert.Equal(5, op.Ud);
        Assert.Equal(1, op.Usrc1);
        Assert.Equal(2, op.Usrc2);
    }

    [Fact]
    public void Decoder_SoALogic_Not_IsUnary() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoALogic(UveLogicOp.Not, 5, 1, -1)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoALogic>(tooth.Payload);
        Assert.Equal(UveLogicOp.Not, op.Op);
        Assert.Equal(-1, op.Usrc2);
    }

    [Fact]
    public void Decoder_SoAShiftV_Sll_Roundtrip() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoAShiftV(UveShiftOp.Sll, 5, 1, 2)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoAShiftV>(tooth.Payload);
        Assert.Equal(UveShiftOp.Sll, op.Op);
        Assert.Equal(5, op.Ud);
        Assert.Equal(2, op.Usrc2);
    }

    [Fact]
    public void Decoder_SoAShiftS_Sra_Roundtrip() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoAShiftS(UveShiftOp.Sra, 5, 1, 3)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoAShiftS>(tooth.Payload);
        Assert.Equal(UveShiftOp.Sra, op.Op);
        Assert.Equal(3, op.Rs2);
        // ShiftS lists integer rs2 as a source register
        Assert.Contains(3, tooth.SourceRegisters);
    }

    [Fact]
    public void Decoder_SoAFp_Min_Roundtrip() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoAFp(UveFpOp.Min, 5, 1, 2)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoAFp>(tooth.Payload);
        Assert.Equal(UveFpOp.Min, op.Op);
    }

    [Fact]
    public void Decoder_SoAFp_Abs_IsUnary() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoAFp(UveFpOp.Abs, 5, 1, -1)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoAFp>(tooth.Payload);
        Assert.Equal(UveFpOp.Abs, op.Op);
        Assert.Equal(-1, op.Usrc2);
    }

    [Fact]
    public void Decoder_SoAInt_Abs_IsSigned() {
        // Spike MATCH_SO_A_ABS_SG = 0x3000002b uses funct3=0 (type=0 slot) — always signed.
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoAInt(UveIntOp.Abs, true, 5, 1, -1)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoAInt>(tooth.Payload);
        Assert.Equal(UveIntOp.Abs, op.Op);
        Assert.True(op.Signed);
        Assert.Equal(-1, op.Usrc2);
    }

    [Fact]
    public void Decoder_SoVDpB_DecodesWidth() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoVDp(5, 1, 1)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoVDp>(tooth.Payload);
        Assert.Equal(1, op.ElementBytes);
    }

    [Fact]
    public void Decoder_SoVMvvs_Decodes() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoVMvvs(7, 3)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoVMvvs>(tooth.Payload);
        Assert.Equal(3, op.Us1);
        Assert.Equal(7, op.Rd);
    }

    [Fact]
    public void Decoder_SoVMvsvW_Decodes() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoVMvsv(6, 2, 4)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoVMvsv>(tooth.Payload);
        Assert.Equal(6, op.Ud);
        Assert.Equal(2, op.Rs1);
        Assert.Equal(4, op.ElementBytes);
    }

    // ── Reduction ops (adde / adde.acc / mine / maxe) ─────────────────────────

    [Fact]
    public void SoAFp_Adde_OverwritesUd() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(5, 999f); // existing accumulator
        state.UveState.SetScalar(1, 7.0f);
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Adde, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(7.0f, state.UveState.GetScalar(5));
    }

    [Fact]
    public void SoAFp_AddeAcc_AccumulatesIntoUd() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(5, 10f);
        state.UveState.SetScalar(1, 3.0f);
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.AddeAcc, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(13.0f, state.UveState.GetScalar(5));
    }

    [Fact]
    public void SoAFp_Mine_UpdatesRunningMin() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(5, 10f);  // current running min
        state.UveState.SetScalar(1, 3.0f); // new element, smaller
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Mine, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(3.0f, state.UveState.GetScalar(5));

        // Element larger than current min: does not update
        state.UveState.SetScalar(1, 99f);
        er = Exec(new RvUveSoAFp(UveFpOp.Mine, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(3.0f, state.UveState.GetScalar(5));
    }

    [Fact]
    public void SoAFp_Maxe_UpdatesRunningMax() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(5, 5f);
        state.UveState.SetScalar(1, 12.0f);
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Maxe, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(12.0f, state.UveState.GetScalar(5));
    }

    [Fact]
    public void SoAInt_AddeAcc_US_Accumulates() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(5, Ub(100u));
        state.UveState.SetScalar(1, Ub(42u));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.AddeAcc, false, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(142u, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAInt_Mine_SG_UpdatesRunningMin() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(5, Ib(10));
        state.UveState.SetScalar(1, Ib(-5));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Mine, true, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(-5, Rib(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void SoAInt_Maxe_US_UpdatesRunningMax() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(5, Ub(50u));
        state.UveState.SetScalar(1, Ub(200u));
        ExecuteResult er = Exec(new RvUveSoAInt(UveIntOp.Maxe, false, 5, 1, -1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(200u, Rub(state.UveState.GetScalar(5)));
    }

    [Fact]
    public void Decoder_SoAFp_AddeAcc_Roundtrip() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoAFp(UveFpOp.AddeAcc, 5, 1, -1)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoAFp>(tooth.Payload);
        Assert.Equal(UveFpOp.AddeAcc, op.Op);
        Assert.Equal(5, op.Ud);
        Assert.Equal(1, op.Usrc1);
        Assert.Equal(-1, op.Usrc2);
    }

    [Fact]
    public void Decoder_SoAFp_Adde_Roundtrip() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoAFp(UveFpOp.Adde, 5, 1, -1)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoAFp>(tooth.Payload);
        Assert.Equal(UveFpOp.Adde, op.Op);
        Assert.Equal(-1, op.Usrc2);
    }

    [Fact]
    public void Decoder_SoAFp_Mine_Roundtrip() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoAFp(UveFpOp.Mine, 5, 1, -1)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoAFp>(tooth.Payload);
        Assert.Equal(UveFpOp.Mine, op.Op);
    }

    [Fact]
    public void Decoder_SoAInt_AddeAcc_SG_Roundtrip() {
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SoAInt(UveIntOp.AddeAcc, true, 5, 1, -1)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoAInt>(tooth.Payload);
        Assert.Equal(UveIntOp.AddeAcc, op.Op);
        Assert.True(op.Signed);
        Assert.Equal(-1, op.Usrc2);
    }

    // ── sadde / fsadde — scalar-write reductions ──────────────────────────────

    [Fact]
    public void SoASadde_Int_OverwritesIntegerReg() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, Ib(42));
        ExecuteResult er = Exec(new RvUveSoASadde(false, false, 7, 1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(42u, (uint)state.IntegerRegisters.Read(7));
    }

    [Fact]
    public void SoASadde_Int_Acc_AccumulatesIntoIntegerReg() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(7, 10u);
        state.UveState.SetScalar(1, Ib(32));
        ExecuteResult er = Exec(new RvUveSoASadde(false, true, 7, 1), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(42u, (uint)state.IntegerRegisters.Read(7));
    }

    [Fact]
    public void SoASadde_Fp_OverwritesFpReg() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(1, 2.5f);
        ExecuteResult er = Exec(new RvUveSoASadde(true, false, 7 + 32, 1), state);
        er.SideEffect?.Invoke(state);
        ulong raw = state.IntegerRegisters.Read(7 + 32);
        Assert.Equal(0xFFFFFFFFu, (uint)(raw >> 32)); // NaN-boxed
        Assert.Equal(2.5f, BitConverter.Int32BitsToSingle((int)(uint)raw), 4);
    }

    [Fact]
    public void SoASadde_Fp_Acc_AccumulatesIntoFpReg() {
        var state = new Rv32ArchState();
        ulong init = 0xFFFFFFFF00000000UL | (uint)BitConverter.SingleToInt32Bits(1.5f);
        state.IntegerRegisters.Write(7 + 32, init);
        state.UveState.SetScalar(1, 1.0f);
        ExecuteResult er = Exec(new RvUveSoASadde(true, true, 7 + 32, 1), state);
        er.SideEffect?.Invoke(state);
        ulong raw = state.IntegerRegisters.Read(7 + 32);
        Assert.Equal(0xFFFFFFFFu, (uint)(raw >> 32)); // NaN-boxed
        Assert.Equal(2.5f, BitConverter.Int32BitsToSingle((int)(uint)raw), 4);
    }

    [Fact]
    public void Decoder_SoASadde_Int_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoASadde(false, false, 5, 1)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoASadde>(tooth.Payload);
        Assert.False(op.IsFp);
        Assert.False(op.Acc);
        Assert.Equal(5, op.Rd);
        Assert.Equal(1, op.Usrc1);
        Assert.Equal(5, tooth.DestinationRegister);
        Assert.Equal([1,], tooth.UveStreamSources);
    }

    [Fact]
    public void Decoder_SoASadde_Fp_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoASadde(true, false, 5, 1)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoASadde>(tooth.Payload);
        Assert.True(op.IsFp);
        Assert.False(op.Acc);
        Assert.Equal(5 + 32, op.Rd);
        Assert.Equal(1, op.Usrc1);
        Assert.Equal(5 + 32, tooth.DestinationRegister);
    }

    [Fact]
    public void Decoder_SoASadde_Int_Acc_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoASadde(false, true, 3, 2)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoASadde>(tooth.Payload);
        Assert.False(op.IsFp);
        Assert.True(op.Acc);
        Assert.Equal(3, op.Rd);
        Assert.Equal(2, op.Usrc1);
        Assert.Equal([3,], tooth.SourceRegisters); // rd read as accumulator
    }

    // ── SO_C: stream lifecycle and vector-length control ──────────────────────

    [Fact]
    public void SoCBreak_ClearsStreamAndMarksDone() {
        var state = new Rv32ArchState();
        state.UveState.RegKind[3] = UveRegKind.LoadStream;
        ExecuteResult er = Exec(new RvUveSoCBreak(3), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(UveRegKind.None, state.UveState.RegKind[3]);
        Assert.True(state.UveState.StreamDone[3]);
        Assert.False(state.UveState.Suspended[3]);
    }

    [Fact]
    public void SoCSuspd_SetsSuspendedFlag() {
        var state = new Rv32ArchState();
        ExecuteResult er = Exec(new RvUveSoCSuspd(5), state);
        er.SideEffect?.Invoke(state);
        Assert.True(state.UveState.Suspended[5]);
    }

    [Fact]
    public void SoCResum_ClearsSuspendedFlag() {
        var state = new Rv32ArchState();
        state.UveState.Suspended[5] = true;
        ExecuteResult er = Exec(new RvUveSoCResum(5), state);
        er.SideEffect?.Invoke(state);
        Assert.False(state.UveState.Suspended[5]);
    }

    [Fact]
    public void SoCGetvl_ReadsCurrentVl() {
        var state = new Rv32ArchState {
            UveState = {
                VectorLength = 16,
            },
        };
        ExecuteResult er = Exec(new RvUveSoCGetvl(7), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(16u, (uint)state.IntegerRegisters.Read(7));
    }

    [Fact]
    public void SoCSetvl_SetsVlAndReturnsOld() {
        var state = new Rv32ArchState {
            UveState = {
                VectorLength = 8,
            },
        };
        state.IntegerRegisters.Write(2, 32u); // new VL
        ExecuteResult er = Exec(new RvUveSoCSetvl(7, 2), state);
        er.SideEffect?.Invoke(state);
        Assert.Equal(32, state.UveState.VectorLength);
        Assert.Equal(8u, (uint)state.IntegerRegisters.Read(7)); // old VL returned
    }

    [Fact]
    public void Decoder_SoCBreak_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoCBreak(4)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoCBreak>(tooth.Payload);
        Assert.Equal(4, op.Ud);
        Assert.Equal(-1, tooth.DestinationRegister);
    }

    [Fact]
    public void Decoder_SoCSuspd_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoCSuspd(6)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoCSuspd>(tooth.Payload);
        Assert.Equal(6, op.Ud);
    }

    [Fact]
    public void Decoder_SoCGetvl_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoCGetvl(5)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoCGetvl>(tooth.Payload);
        Assert.Equal(5, op.Rd);
        Assert.Equal(5, tooth.DestinationRegister);
    }

    [Fact]
    public void Decoder_SoCSetvl_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoCSetvl(7, 3)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoCSetvl>(tooth.Payload);
        Assert.Equal(7, op.Rd);
        Assert.Equal(3, op.Rs1);
        Assert.Equal(7, tooth.DestinationRegister);
        Assert.Equal([3,], tooth.SourceRegisters);
    }

    // ── SO_P encode helpers ───────────────────────────────────────────────────

    // so.p.zero/one pd [.z] [, govPred=0]
    // group=8, funct3=000, bit11=0(zero)/1(one); bits[27:25]=govPred; bit24=zeroing
    private static uint SoPZero(int pd, int govPred = 0, bool zeroing = false) =>
        (0x80u << 24) | ((uint)govPred << 25) | (zeroing ? 1u << 24 : 0u) |
        (uint)((pd & 0xF) << 7) | 0x2Bu;

    private static uint SoPOne(int pd, int govPred = 0, bool zeroing = false) =>
        SoPZero(pd, govPred, zeroing) | (1u << 11);

    // so.p.not/mv/mvt pd, ps1 [.z] [, govPred=0]
    // not: funct3=001, bit11=1; mv: funct3=010, bit11=0; mvt: funct3=010, bit11=1
    private static uint SoPNot(int pd, int ps1, int govPred = 0, bool zeroing = false) =>
        (0x80u << 24) | ((uint)govPred << 25) | (zeroing ? 1u << 24 : 0u) |
        (1u << 12) | (1u << 11) | (uint)((ps1 & 0xF) << 15) | (uint)((pd & 0xF) << 7) | 0x2Bu;

    private static uint SoPMv(int pd, int ps1, int govPred = 0, bool zeroing = false) =>
        (0x80u << 24) | ((uint)govPred << 25) | (zeroing ? 1u << 24 : 0u) |
        (2u << 12) | (uint)((ps1 & 0xF) << 15) | (uint)((pd & 0xF) << 7) | 0x2Bu;

    private static uint SoPMvt(int pd, int ps1, int govPred = 0, bool zeroing = false) =>
        SoPMv(pd, ps1, govPred, zeroing) | (1u << 11);

    // so.p.ge.us/eq.us/lt.us pd, vs1, vs2
    // GE: group=8 (bits31-28=1000), funct3=100; EQ: group=9 (bits31-28=1001), funct3=000; LT: group=9, funct3=100
    private static uint SoPGeUs(int pd, int vs1, int vs2, int govPred = 0, bool zeroing = false) =>
        (0x80u << 24) | ((uint)govPred << 25) | (uint)((vs2 & 0x1F) << 20) |
        (uint)((vs1 & 0x1F) << 15) | (4u << 12) | (zeroing ? 1u << 11 : 0u) | (uint)((pd & 0xF) << 7) | 0x2Bu;

    private static uint SoPEqUs(int pd, int vs1, int vs2, int govPred = 0, bool zeroing = false) =>
        (0x90u << 24) | ((uint)govPred << 25) | (uint)((vs2 & 0x1F) << 20) |
        (uint)((vs1 & 0x1F) << 15) | (0u << 12) | (zeroing ? 1u << 11 : 0u) | (uint)((pd & 0xF) << 7) | 0x2Bu;

    private static uint SoPLtUs(int pd, int vs1, int vs2, int govPred = 0, bool zeroing = false) =>
        (0x90u << 24) | ((uint)govPred << 25) | (uint)((vs2 & 0x1F) << 20) |
        (uint)((vs1 & 0x1F) << 15) | (4u << 12) | (zeroing ? 1u << 11 : 0u) | (uint)((pd & 0xF) << 7) | 0x2Bu;

    // so.v.mv/mvt vd, vs1, predIdx — funct7=0x54; rs2 = (0<<3)|predIdx for mv, (1<<3)|predIdx for mvt
    private static uint SoVMv(int vd, int vs1, int predIdx = 0, bool transpose = false) =>
        (0x54u << 25) | (uint)((((transpose ? 1 : 0) << 3) | (predIdx & 7)) << 20) |
        (uint)((vs1 & 0x1F) << 15) | (uint)((vd & 0x1F) << 7) | 0x2Bu;

    // ── SO_P decoder round-trip tests ─────────────────────────────────────────

    [Fact]
    public void Decoder_SoPZero_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPZero(3, 2, true)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPSimple>(tooth.Payload);
        Assert.Equal(UveSoPSimpleOp.Zero, op.Op);
        Assert.Equal(3, op.Pd);
        Assert.Equal(2, op.GovPred);
        Assert.True(op.Zeroing);
    }

    [Fact]
    public void Decoder_SoPOne_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPOne(5)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPSimple>(tooth.Payload);
        Assert.Equal(UveSoPSimpleOp.One, op.Op);
        Assert.Equal(5, op.Pd);
        Assert.Equal(0, op.GovPred);
        Assert.False(op.Zeroing);
    }

    [Fact]
    public void Decoder_SoPNot_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPNot(1, 4)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPSimple>(tooth.Payload);
        Assert.Equal(UveSoPSimpleOp.Not, op.Op);
        Assert.Equal(1, op.Pd);
        Assert.Equal(4, op.Ps1);
    }

    [Fact]
    public void Decoder_SoPMv_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPMv(2, 6)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPSimple>(tooth.Payload);
        Assert.Equal(UveSoPSimpleOp.Mv, op.Op);
        Assert.Equal(2, op.Pd);
        Assert.Equal(6, op.Ps1);
    }

    [Fact]
    public void Decoder_SoPMvt_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPMvt(2, 6)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPSimple>(tooth.Payload);
        Assert.Equal(UveSoPSimpleOp.Mvt, op.Op);
    }

    [Fact]
    public void Decoder_SoPGeUs_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPGeUs(3, 4, 5, 1)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPCmp>(tooth.Payload);
        Assert.Equal(UveSoPCmpOp.Ge, op.Op);
        Assert.Equal(UveSoPCmpType.Us, op.CmpType);
        Assert.Equal(3, op.Pd);
        Assert.Equal(1, op.GovPred);
        Assert.Equal(4, op.Vs1);
        Assert.Equal(5, op.Vs2);
    }

    [Fact]
    public void Decoder_SoPEqUs_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPEqUs(2, 3, 4)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPCmp>(tooth.Payload);
        Assert.Equal(UveSoPCmpOp.Eq, op.Op);
        Assert.Equal(UveSoPCmpType.Us, op.CmpType);
    }

    [Fact]
    public void Decoder_SoPLtUs_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPLtUs(1, 0, 2)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPCmp>(tooth.Payload);
        Assert.Equal(UveSoPCmpOp.Lt, op.Op);
        Assert.Equal(UveSoPCmpType.Us, op.CmpType);
        Assert.False(op.Zeroing);
    }

    [Fact]
    public void Decoder_SoPGeUs_Z_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPGeUs(3, 4, 5, zeroing: true)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPCmp>(tooth.Payload);
        Assert.Equal(UveSoPCmpOp.Ge, op.Op);
        Assert.Equal(UveSoPCmpType.Us, op.CmpType);
        Assert.True(op.Zeroing);
    }

    [Fact]
    public void Decoder_SoPEqUs_Z_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPEqUs(2, 3, 4, zeroing: true)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPCmp>(tooth.Payload);
        Assert.Equal(UveSoPCmpOp.Eq, op.Op);
        Assert.True(op.Zeroing);
    }

    [Fact]
    public void Decoder_SoPLtUs_Z_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPLtUs(1, 0, 2, zeroing: true)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPCmp>(tooth.Payload);
        Assert.Equal(UveSoPCmpOp.Lt, op.Op);
        Assert.True(op.Zeroing);
    }

    [Fact]
    public void Decoder_SoVMv_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoVMv(3, 5, 2)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoVMv>(tooth.Payload);
        Assert.False(op.Transpose);
        Assert.Equal(3, op.Vd);
        Assert.Equal(5, op.Vs1);
        Assert.Equal(2, op.PredIdx);
    }

    [Fact]
    public void Decoder_SoVMvt_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoVMv(3, 5, 2, true)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoVMv>(tooth.Payload);
        Assert.True(op.Transpose);
    }

    // ── SO_P executor tests ───────────────────────────────────────────────────

    [Fact]
    public void SoP_Reg0_InitiallyAllTrue() {
        var state = new Rv32ArchState();
        Assert.All(state.UveState.PredicateRegs[0], Assert.True);
    }

    [Fact]
    public void SoP_OtherRegs_InitiallyAllFalse() {
        var state = new Rv32ArchState();
        for (var i = 1; i < UveState.PredCount; i++) Assert.All(state.UveState.PredicateRegs[i], Assert.False);
    }

    [Fact]
    public void SoP_Zero_ClearsActiveElements() {
        var state = new Rv32ArchState();
        // Governing pred is reg 0 (all-true); target is reg 1.
        Array.Fill(state.UveState.PredicateRegs[1], true);

        ExecuteResult er = Exec(new RvUveSoPSimple(UveSoPSimpleOp.Zero, 1, 0, false, -1, -1), state);
        er.SideEffect!(state);

        Assert.All(state.UveState.PredicateRegs[1], Assert.False);
    }

    [Fact]
    public void SoP_Zero_MergesMaskedElements() {
        var state = new Rv32ArchState();
        // Governing pred = reg 1 (all-false); target = reg 2 (all-true).
        Array.Fill(state.UveState.PredicateRegs[2], true);

        ExecuteResult er = Exec(new RvUveSoPSimple(UveSoPSimpleOp.Zero, 2, 1, false, -1, -1), state);
        er.SideEffect!(state);

        // All inactive → merge → still all-true
        Assert.All(state.UveState.PredicateRegs[2], Assert.True);
    }

    [Fact]
    public void SoP_Zero_ZeroingFlagClearsInactiveElements() {
        var state = new Rv32ArchState();
        // Governing pred = reg 1 (all-false); target = reg 2 (all-true).
        Array.Fill(state.UveState.PredicateRegs[2], true);

        ExecuteResult er = Exec(new RvUveSoPSimple(UveSoPSimpleOp.Zero, 2, 1, true, -1, -1), state);
        er.SideEffect!(state);

        // Zeroing mode: inactive → cleared to false
        Assert.All(state.UveState.PredicateRegs[2], Assert.False);
    }

    [Fact]
    public void SoP_One_SetsAllActive() {
        var state = new Rv32ArchState();

        ExecuteResult er = Exec(new RvUveSoPSimple(UveSoPSimpleOp.One, 3, 0, false, -1, -1), state);
        er.SideEffect!(state);

        Assert.All(state.UveState.PredicateRegs[3], Assert.True);
    }

    [Fact]
    public void SoP_Not_InvertsSource() {
        var state = new Rv32ArchState();
        // ps1 = reg 0 (all-true); pd = reg 2.
        ExecuteResult er = Exec(new RvUveSoPSimple(UveSoPSimpleOp.Not, 2, 0, false, 0, -1), state);
        er.SideEffect!(state);

        Assert.All(state.UveState.PredicateRegs[2], Assert.False);
    }

    [Fact]
    public void SoP_Mv_CopiesSource() {
        var state = new Rv32ArchState();
        // Source = reg 0 (all-true); dest = reg 3.
        ExecuteResult er = Exec(new RvUveSoPSimple(UveSoPSimpleOp.Mv, 3, 0, false, 0, -1), state);
        er.SideEffect!(state);

        Assert.All(state.UveState.PredicateRegs[3], Assert.True);
    }

    [Fact]
    public void SoP_Mvt_ReversesSource() {
        var state = new Rv32ArchState();
        // Source = reg 2 with only first byte set.
        state.UveState.PredicateRegs[2][0] = true;

        ExecuteResult er = Exec(new RvUveSoPSimple(UveSoPSimpleOp.Mvt, 3, 0, false, 2, -1), state);
        er.SideEffect!(state);

        // Reversed: the last byte of pd should be true.
        bool[] pd = state.UveState.PredicateRegs[3];
        Assert.True(pd[UveState.PredBytes - 1]);
        Assert.All(pd[..^1], Assert.False);
    }

    [Fact]
    public void SoP_Vr_SetsValidRange() {
        var state = new Rv32ArchState {
            UveState = {
                VectorLength = 4,
            },
        };

        ExecuteResult er = Exec(new RvUveSoPSimple(UveSoPSimpleOp.Vr, 2, 0, false, -1, 0), state);
        er.SideEffect!(state);

        bool[] pd = state.UveState.PredicateRegs[2];
        Assert.True(pd[0]);
        Assert.True(pd[1]);
        Assert.True(pd[2]);
        Assert.True(pd[3]);
        Assert.False(pd[4]);
    }

    [Fact]
    public void SoP_EqUs_SetsPredicateOnMatch() {
        var state = new Rv32ArchState();
        float v = BitConverter.Int32BitsToSingle(42);
        state.UveState.SetScalar(0, v);
        state.UveState.SetScalar(1, v);

        ExecuteResult er = Exec(new RvUveSoPCmp(UveSoPCmpOp.Eq, UveSoPCmpType.Us, 2, 0, 0, 1), state);
        er.SideEffect!(state);

        Assert.All(state.UveState.PredicateRegs[2], Assert.True);
    }

    [Fact]
    public void SoP_EqUs_ClearsPredicateOnMismatch() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(0, BitConverter.Int32BitsToSingle(1));
        state.UveState.SetScalar(1, BitConverter.Int32BitsToSingle(2));
        // Initialize pd to all-true first.
        Array.Fill(state.UveState.PredicateRegs[2], true);

        ExecuteResult er = Exec(new RvUveSoPCmp(UveSoPCmpOp.Eq, UveSoPCmpType.Us, 2, 0, 0, 1), state);
        er.SideEffect!(state);

        Assert.All(state.UveState.PredicateRegs[2], Assert.False);
    }

    [Fact]
    public void SoP_LtUs_SetsPredicateWhenLess() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(0, BitConverter.Int32BitsToSingle(1));
        state.UveState.SetScalar(1, BitConverter.Int32BitsToSingle(2));

        ExecuteResult er = Exec(new RvUveSoPCmp(UveSoPCmpOp.Lt, UveSoPCmpType.Us, 2, 0, 0, 1), state);
        er.SideEffect!(state);

        Assert.All(state.UveState.PredicateRegs[2], Assert.True);
    }

    [Fact]
    public void SoP_GovPred_MasksUpdate() {
        var state = new Rv32ArchState();
        // GovPred = reg 1 (all-false); no update expected on pd=2.
        Array.Fill(state.UveState.PredicateRegs[2], true);

        ExecuteResult er = Exec(new RvUveSoPSimple(UveSoPSimpleOp.Zero, 2, 1, false, -1, -1), state);
        er.SideEffect!(state);

        // No update: still all-true (merging)
        Assert.All(state.UveState.PredicateRegs[2], Assert.True);
    }

    [Fact]
    public void SoP_Reset_RestoresReg0AllTrue() {
        var state = new Rv32ArchState();
        Array.Clear(state.UveState.PredicateRegs[0]);
        state.UveState.Reset();
        Assert.All(state.UveState.PredicateRegs[0], Assert.True);
    }

    [Fact]
    public void SoP_Reset_ClearsPredZeroing() {
        var state = new Rv32ArchState();
        state.UveState.PredZeroing[3] = true;
        state.UveState.Reset();
        Assert.All(state.UveState.PredZeroing, Assert.False);
    }

    [Fact]
    public void SoP_EqUs_NonZ_SetsPredZeroingFalse() {
        var state = new Rv32ArchState();
        state.UveState.PredZeroing[2] = true; // pre-set to true
        float v = BitConverter.Int32BitsToSingle(7);
        state.UveState.SetScalar(0, v);
        state.UveState.SetScalar(1, v);

        ExecuteResult er = Exec(new RvUveSoPCmp(UveSoPCmpOp.Eq, UveSoPCmpType.Us, 2, 0, 0, 1), state);
        er.SideEffect!(state);

        Assert.False(state.UveState.PredZeroing[2]);
    }

    [Fact]
    public void SoP_EqUs_Z_SetsPredZeroingTrue() {
        var state = new Rv32ArchState();
        float v = BitConverter.Int32BitsToSingle(7);
        state.UveState.SetScalar(0, v);
        state.UveState.SetScalar(1, v);

        ExecuteResult er = Exec(new RvUveSoPCmp(UveSoPCmpOp.Eq, UveSoPCmpType.Us, 2, 0, 0, 1, true), state);
        er.SideEffect!(state);

        Assert.True(state.UveState.PredZeroing[2]);
    }

    [Fact]
    public void SoP_GeUs_Z_SetsPredZeroingTrue() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(0, BitConverter.Int32BitsToSingle(5));
        state.UveState.SetScalar(1, BitConverter.Int32BitsToSingle(3));

        ExecuteResult er = Exec(new RvUveSoPCmp(UveSoPCmpOp.Ge, UveSoPCmpType.Us, 3, 0, 0, 1, true), state);
        er.SideEffect!(state);

        Assert.True(state.UveState.PredZeroing[3]);
        // Comparison result unaffected by the _z flag.
        Assert.All(state.UveState.PredicateRegs[3], Assert.True);
    }

    [Fact]
    public void SoP_LtUs_Z_SetsPredZeroingTrue() {
        var state = new Rv32ArchState();
        state.UveState.SetScalar(0, BitConverter.Int32BitsToSingle(1));
        state.UveState.SetScalar(1, BitConverter.Int32BitsToSingle(2));

        ExecuteResult er = Exec(new RvUveSoPCmp(UveSoPCmpOp.Lt, UveSoPCmpType.Us, 4, 0, 0, 1, true), state);
        er.SideEffect!(state);

        Assert.True(state.UveState.PredZeroing[4]);
    }

    [Fact]
    public void SoP_CmpZ_OnlyTagsMode_DoesNotChangeInactiveElements() {
        var state = new Rv32ArchState();
        // GovPred=1 (all-false): no elements active → comparison body skips → old dest kept.
        Array.Fill(state.UveState.PredicateRegs[2], true); // pre-fill pd to all-true
        state.UveState.SetScalar(0, BitConverter.Int32BitsToSingle(1));
        state.UveState.SetScalar(1, BitConverter.Int32BitsToSingle(2));

        ExecuteResult er = Exec(new RvUveSoPCmp(UveSoPCmpOp.Eq, UveSoPCmpType.Us, 2, 1, 0, 1, true), state);
        er.SideEffect!(state);

        // Inactive elements merged (kept all-true); _z only tags the mode.
        Assert.All(state.UveState.PredicateRegs[2], Assert.True);
        Assert.True(state.UveState.PredZeroing[2]);
    }

    // ── so.v.mv / so.v.mvt executor tests ────────────────────────────────────

    [Fact]
    public void SoVMv_CopiesWhenPredicateActive() {
        var state = new Rv32ArchState();
        const float src = 3.14f;
        state.UveState.SetScalar(5, src);

        ExecuteResult er = Exec(new RvUveSoVMv(false, 3, 5, 0), state);
        er.SideEffect!(state);

        Assert.Equal(src, state.UveState.GetScalar(3));
    }

    [Fact]
    public void SoVMv_MergesWhenPredicateInactive() {
        var state = new Rv32ArchState();
        const float src = 3.14f;
        const float dst = 2.71f;
        state.UveState.SetScalar(5, src);
        state.UveState.SetScalar(3, dst);
        // PredIdx = 1, which is all-false.

        ExecuteResult er = Exec(new RvUveSoVMv(false, 3, 5, 1), state);
        er.SideEffect!(state);

        Assert.Equal(dst, state.UveState.GetScalar(3)); // unchanged
    }

    // ── Scatter-gather modifier (ss.app.sgi / ss.end.sgi) tests ─────────────────

    [Fact]
    public void Decoder_SsAppSgi_Add_Roundtrip() {
        var mem = new FlatMemory(4);
        mem.Load(0, BitConverter.GetBytes(SsAppSgi(3, 5, StreamModifierBehavior.Add)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSsAppSgi>(tooth.Payload);
        Assert.Equal(3, op.Ud);
        Assert.Equal(5, op.Rs1Source);
        Assert.Equal(StreamModifierBehavior.Add, op.Behavior);
    }

    [Fact]
    public void Decoder_SsEndSgi_Inc_Roundtrip() {
        var mem = new FlatMemory(4);
        mem.Load(0, BitConverter.GetBytes(SsEndSgi(2, 7, StreamModifierBehavior.Inc)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSsEndSgi>(tooth.Payload);
        Assert.Equal(2, op.Ud);
        Assert.Equal(7, op.Rs1Source);
        Assert.Equal(StreamModifierBehavior.Inc, op.Behavior);
    }

    [Fact]
    public void SsEndSgi_SetsDescriptorSgiMod() {
        var state = new Rv32ArchState();
        var cfg = new PendingStreamConfig { BaseAddress = 0x1000, ElementBytes = 4, IsLoad = true, };
        cfg.Dimensions.Add(new StreamDimension(4, 4));
        state.UveState.PendingConfig[0] = cfg;

        ExecuteResult er = Exec(new RvUveSsEndSgi(0, 3, StreamModifierBehavior.Set), state);

        Assert.True(er.StreamConfig.HasValue);
        StreamDescriptor desc = er.StreamConfig.Value.Descriptor;
        Assert.True(desc.SgiMod.HasValue);
        Assert.Equal(3, desc.SgiMod!.Value.SourceStreamId);
        Assert.Equal(StreamModifierBehavior.Set, desc.SgiMod.Value.Behavior);
    }

    [Fact]
    public void ScatterGather_SgiSet_GathersIndirectElements() {
        // IndSource stream 1: indices [2, 0, 1] at 0x100 (4-byte uint each).
        // Data array at 0x000: A = [10, 20, 30] (4 bytes each).
        // Gather stream 0: base=0x000, count=3, stride=0; sgi from stream 1 with behavior=Set.
        // sgi sets _fetchDimOffsets[0] = index * elementBytes before each fetch:
        //   idx=2 → offset=8 → A[2]=30; idx=0 → offset=0 → A[0]=10; idx=1 → offset=4 → A[1]=20.
        var mem = new FlatMemory(0x200);
        mem.Load(0x000, BitConverter.GetBytes(10u));
        mem.Load(0x004, BitConverter.GetBytes(20u));
        mem.Load(0x008, BitConverter.GetBytes(30u));
        mem.Load(0x100, BitConverter.GetBytes(2u)); // → A[2]=30
        mem.Load(0x104, BitConverter.GetBytes(0u)); // → A[0]=10
        mem.Load(0x108, BitConverter.GetBytes(1u)); // → A[1]=20

        var eng = new StreamingEngine(16);
        eng.Configure(1, new StreamDescriptor(0x100, 4, 3, 4)); // IndSource: 3 indices
        eng.Configure(
            0, new StreamDescriptor(
                0x000, 4, [new StreamDimension(3, 0),],
                SgiMod: (1, StreamModifierBehavior.Set)
            )
        );

        for (var i = 0; i < 20; i++) eng.Step(mem);

        Assert.Equal(30UL, eng.Consume(0));
        Assert.Equal(10UL, eng.Consume(0));
        Assert.Equal(20UL, eng.Consume(0));
        Assert.True(eng.IsExhausted(0));
    }

    // ── so.p.cv encode helper ─────────────────────────────────────────────────

    // so.p.cv.<srcW>.<destW>[.z] pd, ps1
    // group=8 (funct7=0x40..0x47), funct3=3, rs2[1:0]=srcWidthIdx, rs2[3:2]=destWidthIdx, rs2[4]=zeroing
    // rs1[3:0]=ps1, rd[3:0]=pd
    private static uint SoPCv(int pd, int ps1, int srcBytes, int destBytes, bool zeroing = false) {
        int srcIdx = srcBytes == 1   ? 0 : srcBytes == 2  ? 1 : srcBytes == 4  ? 2 : 3;
        int destIdx = destBytes == 1 ? 0 : destBytes == 2 ? 1 : destBytes == 4 ? 2 : 3;
        var rs2 = (uint)(srcIdx | (destIdx << 2) | (zeroing ? 0x10 : 0));
        return (0x40u << 25) | (rs2 << 20) | (uint)((ps1 & 0xF) << 15) | (3u << 12) |
               (uint)((pd & 0xF) << 7) | 0x2Bu;
    }

    // so.v.cv.{fp,sg,us}.<destW> vd, vs1
    // group=10 (funct7=0x50+cvType/2): funct3=destWidthIdx; rs2=0(US)/8(FP)/16(SG)
    private static uint SoVCvUs(int vd, int vs1, int destBytes) {
        int destIdx = destBytes == 1 ? 0 : destBytes == 2 ? 1 : destBytes == 4 ? 2 : 3;
        // US: rs2=0; funct7=0x55 (bits[31:25]); rs2 in bits[24:20]=0
        return (0x55u << 25) | (0u << 20) | (uint)((vs1 & 0x1F) << 15) | ((uint)destIdx << 12) |
               (uint)((vd & 0x1F) << 7) | 0x2Bu;
    }

    private static uint SoVCvFp(int vd, int vs1, int destBytes) {
        int destIdx = destBytes == 1 ? 0 : destBytes == 2 ? 1 : destBytes == 4 ? 2 : 3;
        return (0x55u << 25) | (8u << 20) | (uint)((vs1 & 0x1F) << 15) | ((uint)destIdx << 12) |
               (uint)((vd & 0x1F) << 7) | 0x2Bu;
    }

    private static uint SoVCvSg(int vd, int vs1, int destBytes) {
        int destIdx = destBytes == 1 ? 0 : destBytes == 2 ? 1 : destBytes == 4 ? 2 : 3;
        // SG: rs2=16 (bit24=1, bits[23:20]=0); funct7=bits[31:25]=0x55 (bit24 is part of rs2, not funct7)
        return (0x55u << 25) | (16u << 20) | (uint)((vs1 & 0x1F) << 15) | ((uint)destIdx << 12) |
               (uint)((vd & 0x1F) << 7) | 0x2Bu;
    }

    // ── so.p.cv decoder round-trip ────────────────────────────────────────────

    [Fact]
    public void Decoder_SoPCv_BH_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPCv(2, 5, 1, 2)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPCv>(tooth.Payload);
        Assert.Equal(2, op.Pd);
        Assert.Equal(5, op.Ps1);
        Assert.Equal(1, op.SrcBytes);
        Assert.Equal(2, op.DestBytes);
        Assert.False(op.Zeroing);
    }

    [Fact]
    public void Decoder_SoPCv_WB_Zeroing_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoPCv(3, 1, 4, 1, true)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoPCv>(tooth.Payload);
        Assert.Equal(3, op.Pd);
        Assert.Equal(1, op.Ps1);
        Assert.Equal(4, op.SrcBytes);
        Assert.Equal(1, op.DestBytes);
        Assert.True(op.Zeroing);
    }

    // ── so.p.cv semantics ─────────────────────────────────────────────────────

    [Fact]
    public void SoPCv_B_to_H_MapsActiveBits() {
        // Source predicate: element 0 active (byte 0 = true), elements 1..7 inactive.
        // B→H: nElems=8; elem i: src=(i+1)*1-1=i, dest=(i+1)*2-1=2i+1.
        // Only element 0 is active: dest[1] = src[0] = true, rest false.
        var state = new Rv32ArchState();
        state.UveState.PredicateRegs[1][0] = true; // element 0 active (byte-width active bit at index 0)
        ExecuteResult result = Exec(new RvUveSoPCv(2, 1, 1, 2, false), state);
        result.SideEffect!(state);
        bool[] pd = state.UveState.PredicateRegs[2];
        Assert.True(pd[1]);  // element 0 dest active bit at (0+1)*2-1 = 1
        Assert.False(pd[3]); // element 1 inactive
        Assert.False(pd[0]); // no spurious set
    }

    [Fact]
    public void SoPCv_H_to_B_MapsActiveBits() {
        // H→B: nElems=8; src=(i+1)*2-1=2i+1, dest=(i+1)*1-1=i.
        // Elements 0 and 2 of H are active: src[1]=true, src[5]=true.
        var state = new Rv32ArchState();
        state.UveState.PredicateRegs[3][1] = true; // elem 0 of H
        state.UveState.PredicateRegs[3][5] = true; // elem 2 of H
        ExecuteResult result = Exec(new RvUveSoPCv(4, 3, 2, 1, false), state);
        result.SideEffect!(state);
        bool[] pd = state.UveState.PredicateRegs[4];
        Assert.True(pd[0]);  // elem 0 → dest byte 0
        Assert.True(pd[2]);  // elem 2 → dest byte 2
        Assert.False(pd[1]); // elem 1 was not active
    }

    [Fact]
    public void SoPCv_Zeroing_SetsTag() {
        var state = new Rv32ArchState();
        Assert.False(state.UveState.PredZeroing[5]);
        ExecuteResult result = Exec(new RvUveSoPCv(5, 0, 1, 2, true), state);
        result.SideEffect!(state);
        Assert.True(state.UveState.PredZeroing[5]);
    }

    // ── so.v.cv decoder round-trip ────────────────────────────────────────────

    [Fact]
    public void Decoder_SoVCvUs_Word_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoVCvUs(1, 3, 4)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoVCv>(tooth.Payload);
        Assert.Equal(1, op.Vd);
        Assert.Equal(3, op.Vs1);
        Assert.Equal(4, op.DestBytes);
        Assert.False(op.IsFp);
        Assert.False(op.IsSigned);
    }

    [Fact]
    public void Decoder_SoVCvSg_Halfword_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoVCvSg(2, 4, 2)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoVCv>(tooth.Payload);
        Assert.Equal(2, op.Vd);
        Assert.Equal(4, op.Vs1);
        Assert.Equal(2, op.DestBytes);
        Assert.True(op.IsSigned);
        Assert.False(op.IsFp);
    }

    [Fact]
    public void Decoder_SoVCvFp_Word_Roundtrip() {
        var mem = new FlatMemory(256);
        mem.Load(0, BitConverter.GetBytes(SoVCvFp(5, 6, 4)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSoVCv>(tooth.Payload);
        Assert.Equal(5, op.Vd);
        Assert.Equal(6, op.Vs1);
        Assert.Equal(4, op.DestBytes);
        Assert.True(op.IsFp);
        Assert.False(op.IsSigned);
    }

    // ── so.v.cv semantics ─────────────────────────────────────────────────────

    [Fact]
    public void SoVCv_Us_ByteToWord_ZeroExtends() {
        // Source: 2 byte-wide elements: 0xFF (-1 as signed byte) and 0x7F.
        // US (zero-extend) → 0x000000FF and 0x0000007F.
        var state = new Rv32ArchState();
        state.UveState.SetLane32(1, 0, 0xFF);
        state.UveState.SetLane32(1, 1, 0x7F);
        state.UveState.ValidElements[1] = 2;
        state.UveState.RegElemBytes[1] = 1;
        ExecuteResult result = Exec(new RvUveSoVCv(2, 1, 4, false, false), state);
        result.SideEffect!(state);
        Assert.Equal(0x000000FFu, state.UveState.GetLane32(2, 0));
        Assert.Equal(0x0000007Fu, state.UveState.GetLane32(2, 1));
        Assert.Equal(2, state.UveState.ValidElements[2]);
        Assert.Equal(4, state.UveState.RegElemBytes[2]);
    }

    [Fact]
    public void SoVCv_Sg_ByteToWord_SignExtends() {
        // Source: 2 byte-wide elements: 0xFF (-1 signed) and 0x01.
        // SG (sign-extend) → 0xFFFFFFFF and 0x00000001.
        var state = new Rv32ArchState();
        state.UveState.SetLane32(3, 0, 0xFF);
        state.UveState.SetLane32(3, 1, 0x01);
        state.UveState.ValidElements[3] = 2;
        state.UveState.RegElemBytes[3] = 1;
        ExecuteResult result = Exec(new RvUveSoVCv(4, 3, 4, false, true), state);
        result.SideEffect!(state);
        Assert.Equal(0xFFFFFFFFu, state.UveState.GetLane32(4, 0));
        Assert.Equal(0x00000001u, state.UveState.GetLane32(4, 1));
    }

    [Fact]
    public void SoVCv_Us_WordToByte_Truncates() {
        // Source: word 0x12345678 → byte 0x78 (low byte).
        var state = new Rv32ArchState();
        state.UveState.SetLane32(5, 0, 0x12345678u);
        state.UveState.ValidElements[5] = 1;
        state.UveState.RegElemBytes[5] = 4;
        ExecuteResult result = Exec(new RvUveSoVCv(6, 5, 1, false, false), state);
        result.SideEffect!(state);
        Assert.Equal(0x78u, state.UveState.GetLane32(6, 0));
    }

    [Fact]
    public void SoVCv_Fp_Float32ToFloat16_Converts() {
        // Source: float32 1.0f → float16 representation.
        const float src = 1.0f;
        var srcBits = (uint)BitConverter.SingleToInt32Bits(src);
        ushort expected = BitConverter.HalfToUInt16Bits((Half)src);
        var state = new Rv32ArchState();
        state.UveState.SetLane32(7, 0, srcBits);
        state.UveState.ValidElements[7] = 1;
        state.UveState.RegElemBytes[7] = 4;
        ExecuteResult result = Exec(new RvUveSoVCv(8, 7, 2, true, false), state);
        result.SideEffect!(state);
        Assert.Equal(expected, state.UveState.GetLane32(8, 0));
        Assert.Equal(2, state.UveState.RegElemBytes[8]);
    }
}