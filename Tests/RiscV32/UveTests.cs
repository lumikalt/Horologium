using Mechanism;
using Orrery.Streaming;
using Pipeline;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.State;

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32;

/// <summary>
/// Tests for the UVE extension: stream setup, scalar broadcast, arithmetic ops, and branches.
/// Also includes a SAXPY-style integration test through the OoO pipeline.
/// </summary>
public class UveTests {
    // ── Helpers ───────────────────────────────────────────────────────────────

    // Execute a single UVE instruction directly via the executor.
    private static ExecuteResult Exec(RvOp payload, Rv32ArchState state, IMemory? memory = null) {
        var instr = new RvInstruction(0x1000, 0xDEADBEEF, -1, [], ToothClass.Uve, payload);
        return new Rv32Executor().Execute(instr, state, memory ?? new FlatMemory(256));
    }

    // ── Encode helpers ────────────────────────────────────────────────────────

    // so.v.dp.w ud, rs1 — custom-1, funct7=0x56, funct3=0x2
    private static uint SoVDpW(int ud, int rs1) =>
        (uint)((0x56u << 25) | ((rs1 & 0x1F) << 15) | (0x2u << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu);

    // so.a.fp ud, usrc1, usrc2 — custom-1; (funct7>>3, funct3) encodes the operation
    private static uint SoAFp(UveFpOp op, int ud, int usrc1, int usrc2) {
        (uint funct3, uint top4) = op switch {
            UveFpOp.Add => (1u, 0u),
            UveFpOp.Sub => (5u, 0u),
            UveFpOp.Mul => (1u, 1u),
            UveFpOp.Div => (5u, 1u),
            UveFpOp.Mac => (5u, 3u),
            _           => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        uint funct7 = top4 << 3;
        return (funct7 << 25) | (uint)((usrc2 & 0x1F) << 20) | (uint)((usrc1 & 0x1F) << 15)
             | (funct3 << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu;
    }

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

    // so.b.nc urs, imm — funct3=0, bit20=1 (notDone)
    private static uint SoBNc(int urs, int imm) => UveBTypeImm(imm, (uint)urs, 0b00001u, 0x0);

    // so.b.c urs, imm — funct3=0, bit20=0 (done)
    private static uint SoBc(int urs, int imm) => UveBTypeImm(imm, (uint)urs, 0b00000u, 0x0);

    // so.b.ndc.D urs, imm — funct3=D, bit20=1 (notDone)
    private static uint SoBNdcD(int urs, int dim, int imm) => UveBTypeImm(imm, (uint)urs, 0b00001u, (uint)dim);

    // so.b.dc.D urs, imm — funct3=D, bit20=0 (done)
    private static uint SoBdcD(int urs, int dim, int imm) => UveBTypeImm(imm, (uint)urs, 0b00000u, (uint)dim);

    // ss.sta.ld.w ud, rs1 — funct2=0, funct3=0b110 (isLoad=1, ew=4)
    private static uint SsStaLdW(int ud, int rs1) =>
        (uint)(((rs1 & 0x1F) << 15) | (0x6u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

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

    // ss.app.mod: funct2=1, funct3=4; rs1=dimIndex literal, rs2=behavior<<2|spikeTarget, rs3=disp reg
    // Spike target encoding: Size=0, Stride=1, Offset=2 (differs from Horologium: Size=0, Offset=1, Stride=2)
    private static uint SsAppMod(
        int ud,
        int dimIndex,
        StreamModifierTarget target,
        StreamModifierBehavior behavior,
        int rs3Disp
    ) {
        int spikeTarget = target switch {
            StreamModifierTarget.Size   => 0,
            StreamModifierTarget.Stride => 1,
            StreamModifierTarget.Offset => 2,
            _                           => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        int rs2Fixed = ((int)behavior << 2) | spikeTarget;
        return (uint)(((rs3Disp & 0x1F) << 27) | (0x1u << 25) | (uint)((rs2Fixed & 0x1F) << 20)
                    | (uint)((dimIndex & 0x1F) << 15) | (0x4u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);
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

        ExecuteResult er = Exec(new RvUveSoVDpW(4, 5), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(3.14f, state.UveState.Scalars[4], 4);
        Assert.Equal(UveRegKind.Scalar, state.UveState.RegKind[4]);
    }

    [Fact]
    public void SoAFp_Mul_ComputesProduct() {
        var state = new Rv32ArchState();
        // Pipeline would inject source values; simulate by pre-setting scalars
        state.UveState.Scalars[1] = 3.0f;
        state.UveState.Scalars[2] = 4.0f;

        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Mul, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(12.0f, state.UveState.Scalars[5], 4);
    }

    [Fact]
    public void SoAFp_Add_ComputesSum() {
        var state = new Rv32ArchState();
        state.UveState.Scalars[1] = 2.5f;
        state.UveState.Scalars[2] = 7.5f;

        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Add, 0, 1, 2), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(10.0f, state.UveState.Scalars[0], 4);
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

        state.UveState.Scalars[1] = 5.0f;
        state.UveState.Scalars[2] = 3.0f;

        // so.a.mul.fp u3, u1, u2 → should write 5*3=15 to address 0 and advance cursor
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Mul, 3, 1, 2), state, mem);
        er.SideEffect?.Invoke(state);

        float written = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(0, 4));
        Assert.Equal(15.0f, written, 4);
        Assert.Equal(4UL, state.UveState.StoreStreams[3]!.CurrentAddress); // cursor advanced to next element
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

        state.IntegerRegisters.Write(2, 3);  // outer count
        state.IntegerRegisters.Write(3, 32); // outer stride

        ExecuteResult er = Exec(new RvUveSsApp(3, 0, 2, 3), state);
        er.SideEffect?.Invoke(state);

        Assert.Single(state.UveState.PendingConfig[3]!.Dimensions);
        Assert.Equal(3L, state.UveState.PendingConfig[3]!.Dimensions[0].Count);
        Assert.Equal(32L, state.UveState.PendingConfig[3]!.Dimensions[0].Stride);
    }

    [Fact]
    public void SsEnd_ActivatesMultiDimLoadStream() {
        var state = new Rv32ArchState();
        var cfg = new PendingStreamConfig { BaseAddress = 0x3000, ElementBytes = 4, IsLoad = true, };
        cfg.Dimensions.Add(new StreamDimension(4, 4));  // inner dim
        cfg.Dimensions.Add(new StreamDimension(3, 32)); // middle dim
        state.UveState.PendingConfig[1] = cfg;

        state.IntegerRegisters.Write(2, 2); // outermost count
        state.IntegerRegisters.Write(3, 0); // outermost stride (unused here)

        ExecuteResult er = Exec(new RvUveSsEnd(1, 0, 2, 3), state);

        // Should produce a StreamConfig with 3 dimensions
        Assert.True(er.StreamConfig.HasValue);
        Assert.Equal(1, er.StreamConfig!.Value.StreamId);
        StreamDescriptor desc = er.StreamConfig.Value.Descriptor;
        Assert.Equal(0x3000UL, desc.BaseAddress);
        Assert.Equal(3, desc.Dimensions.Length);
        Assert.Equal(4L, desc.Dimensions[0].Count);
        Assert.Equal(3L, desc.Dimensions[1].Count);
        Assert.Equal(2L, desc.Dimensions[2].Count);

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
        // A stream of 4 bytes read one-at-a-time from a tightly-packed array
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

    // ── Integration test: SAXPY via OoO pipeline ──────────────────────────────

    /// <summary>
    /// Runs a SAXPY computation (Y = A*X + Y) through the OoO pipeline using UVE streams.
    /// <para>
    /// Memory layout:
    ///   [0x0000..0x003F]  source X array: 8 floats
    ///   [0x0040..0x007F]  destination Y array: 8 floats (output overwrites in-place)
    ///   [0x1000..]        code
    /// </para>
    /// <para>
    /// Register assignments in setup ADDI sequence:
    ///   x1 = 0x0000  (base of X)
    ///   x2 = 0x0040  (base of Y)
    ///   x3 = 8       (element count)
    ///   x4 = 4       (stride in bytes, = element width)
    ///   x5 = bits(A) (scalar multiplier, as float32 raw bits)
    /// </para>
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
            Addi(4, 0, 4),     // x4 = 4 (stride)
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
    /// Verifies multidimensional stream access and so.b.ndc.D loop control.
    /// <para>
    /// Memory layout (data region at 0x0000):
    ///   A 3×4 matrix stored in row-major order in an 8-float-wide (32 byte) row buffer.
    ///   Only the first 4 floats of each row are part of the matrix; the trailing 4 are padding.
    ///   Row 0: A[0][0..3] at 0x0000–0x000F, padding 0x0010–0x001F
    ///   Row 1: A[1][0..3] at 0x0020–0x002F, padding 0x0030–0x003F
    ///   Row 2: A[2][0..3] at 0x0040–0x004F, padding 0x0050–0x005F
    ///   Output: 12 floats at 0x0200 (linearized, row-major).
    /// </para>
    /// <para>
    /// Stream u1 configured as a 2D load stream:
    ///   ss.sta.ld.w u1, x1, x3, x4   — base=0x0000, inner count=4, inner stride=4
    ///   ss.app      u1, x5, x6        — outer count=3, outer stride=32
    ///   ss.end      u1, x0, x0        — no additional dimension (0-count dim ignored? No —
    ///                                   we use a 2-dim stream: ss.sta provides dim0, ss.end provides dim1)
    /// </para>
    /// <para>
    /// Loop structure:
    ///   outer: so.b.ndc.1 u1, outer  — outer dim (dim1) loop
    ///     inner: so.a.mul.fp u2, u1, u4   — u2 = elem * scalar
    ///            so.b.ndc.0 u1, inner     — inner dim (dim0) loop
    ///   ebreak
    /// </para>
    /// <para>Expected output[i*4+j] = A[i][j] * scalar.</para>
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
        //   x4 = 4            inner stride (bytes per float)
        //   x5 = 3            outer count (rows)
        //   x6 = RowBytes=32  outer stride (bytes per row)
        //   x7 = bits(Scalar) scalar multiplier raw bits
        //   x8 = 12           output element count
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, 0x000),       // x1 = 0 (matrix base)
            Addi(2, 0, 0x200),       // x2 = 0x200 (output base)
            Addi(3, 0, cols),        // x3 = 4
            Addi(4, 0, 4),           // x4 = 4 (byte stride)
            Addi(5, 0, rows),        // x5 = 3
            Addi(6, 0, rowBytes),    // x6 = 32
            Addi(8, 0, rows * cols), // x8 = 12
            Lui(7, 0x40400),         // x7 = bits(3.0f)
            // 2D load stream u1: ss.sta.ld.w (base) + ss.app (inner dim) + ss.end (outer dim, activate)
            SsStaLdW(1, 1),    // base=x1
            SsApp(1, 0, 3, 4), // inner dim: count=x3(4), stride=x4(4)
            SsEnd(1, 0, 5, 6), // outer dim: count=x5(3), stride=x6(32); activate
            // 1D store stream u2: ss.sta.st.w + ss.end
            SsStaStW(2, 2), SsEnd(2, 0, 8, 4), // count=x8(12), stride=x4(4)
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

        // Verify output = A[r][c] * Scalar for every element, linearised row-major
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
    /// Copies 12 floats from a 1D source to a 3×4 matrix stored with padded rows (8 floats wide
    /// = 32 bytes per row). Uses a 1D load stream (ss.ld.w) as source and a 2D store stream
    /// (ss.sta.st.w → ss.end) as destination. Verifies that UveStoreStream advances its inner/outer
    /// indices correctly, skipping the 4-element padding gap between rows.
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
        //   x4 = 4            inner byte stride (sizeof float)
        //   x5 = 4            inner col count
        //   x6 = 3            outer row count
        //   x7 = bits(1.0f)   scalar multiplier (copy via mul)
        //   x8 = 32           outer row stride (RowBytes)
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, 0x000),       // x1 = 0
            Addi(2, 0, 0x400),       // x2 = 0x400
            Addi(3, 0, rows * cols), // x3 = 12
            Addi(4, 0, 4),           // x4 = 4
            Addi(5, 0, cols),        // x5 = 4
            Addi(6, 0, rows),        // x6 = 3
            Addi(8, 0, rowBytes),    // x8 = 32
            Lui(7, 0x3F800),         // x7 = bits(1.0f)
            // 1D load stream u1: ss.sta.ld.w + ss.end
            SsStaLdW(1, 1), SsEnd(1, 0, 3, 4), // count=x3(12), stride=x4(4)
            // 2D store stream u2: ss.sta.st.w + ss.app (inner) + ss.end (outer)
            SsStaStW(2, 2),              // base=x2
            SsApp(2, 0, 5, 4),           // inner dim: count=x5(4 cols), stride=x4(4)
            SsEnd(2, 0, 6, 8),           // outer dim: count=x6(3 rows), stride=x8(32); activate
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

    // ── ss.app.mod / ss.end.mod decode tests ─────────────────────────────────

    [Fact]
    public void SsAppMod_DecodesCorrectly() {
        var mem = new FlatMemory(16);
        // dimIndex=0, target=Size, behavior=Inc, rs3Disp=x5
        mem.Load(0, BitConverter.GetBytes(SsAppMod(1, 0, StreamModifierTarget.Size, StreamModifierBehavior.Inc, 5)));
        ITooth tooth = new Rv32Decoder().Decode(0, mem);
        var op = Assert.IsType<RvUveSsAppMod>(tooth.Payload);
        Assert.Equal(1, op.Ud);
        Assert.Equal(0, op.DimIndex);
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
        Assert.Equal(1, op.DimIndex);
        Assert.Equal(StreamModifierTarget.Stride, op.Target);
        Assert.Equal(StreamModifierBehavior.Dec, op.Behavior);
        Assert.Equal(7, op.Rs3Disp);
    }

    // ── ss.app.mod / ss.end.mod integration tests ────────────────────────────

    private static float LowerTriangularExpected(int n) {
        var sum = 0f;
        for (var r = 0; r < n; r++)
        for (var c = 0; c <= r; c++)
            sum += r * n + c + 1;
        return sum;
    }

    // 2D stream with static Size modifier via ss.sta.ld.w → ss.app.mod → ss.app → ss.end.
    // The modifier grows D0's count by 1 on each D1 iteration (lower-triangular access).
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

        // Registers: x1=matBase, x2=N, x3=N*4, x4=4, x5=1(disp register)
        // Stream: ss.sta.ld.w (base) → ss.app (D0: count=x5=1, stride=x4=4)
        //       → ss.app.mod (dimIndex=0, Size, Inc, disp=x5) → ss.end (D1: count=x2=N, stride=x3=N*4)
        // Loop: so.b.nc u1 (whole-stream done check; ndc_1/dim=0 doesn't exist in Spike encoding)
        uint[] words = [
            Addi(1, 0, (int)matBase), // [0]
            Addi(2, 0, n), // [1] x2 = N
            Addi(3, 0, n * 4), // [2] x3 = N*4
            Addi(4, 0, 4), // [3] x4 = 4
            Addi(5, 0, 1), // [4] x5 = 1 (disp)
            SoVDpW(2, 0), // [5] u2 = 0.0f
            SsStaLdW(1, 1), // [6] base=x1
            SsApp(1, 0, 5, 4), // [7] D0: count=x5(1), stride=x4(4)
            SsAppMod(1, 0, StreamModifierTarget.Size, StreamModifierBehavior.Inc, 5), // [8] mod D0.Size += x5
            SsEnd(1, 0, 2, 3), // [9] D1: count=x2(N), stride=x3(N*4); activate
            SoAFp(UveFpOp.Add, 2, 1, 2), // [10] u2 += elem
            SoBNc(1, -4), // [11] loop while stream active (back 1 instr)
            Addi(9, 0, (int)resultAddr), // [12]
            Addi(10, 0, 1), // [13]
            SsStaStW(3, 9), SsEnd(3, 0, 10, 4), // [14,15] 1D store stream
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
}