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

    // ss.ld.w ud, rs1, rs2, rs3 — R4-type, opcode=0x0B, funct3=0x0
    private static uint SsLdW(int ud, int rs1, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | (0 << 25) | ((rs2 & 0x1F) << 20)
             | ((rs1 & 0x1F) << 15) | (0x0 << 12) | ((ud & 0x1F) << 7) | 0x0B);

    // ss.st.w ud, rs1, rs2, rs3 — R4-type, opcode=0x0B, funct3=0x1
    private static uint SsStW(int ud, int rs1, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | (0 << 25) | ((rs2 & 0x1F) << 20)
             | ((rs1 & 0x1F) << 15) | (0x1 << 12) | ((ud & 0x1F) << 7) | 0x0B);

    // so.v.dp.w ud, rs1 — R-type, opcode=0x2B, funct3=0x0, funct7=0x00
    private static uint SoVDpW(int ud, int rs1) =>
        (uint)((0 << 25) | ((rs1 & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (0x0 << 12) | ((ud & 0x1F) << 7) | 0x2B);

    // so.a.fp ud, usrc1, usrc2 — R-type, opcode=0x2B, funct3=0x1, funct7[6:4]=op
    private static uint SoAFp(UveFpOp op, int ud, int usrc1, int usrc2) =>
        (uint)(((int)op << 4 << 25) | ((usrc2 & 0x1F) << 20) | ((usrc1 & 0x1F) << 15)
             | (0x1 << 12) | ((ud & 0x1F) << 7) | 0x2B);

    // B-type immediate encoding helper used by so.b.nc and so.b.ndc.D
    private static uint BTypeImm(int imm, uint rs1, uint rs2, uint funct3) {
        var i = (uint)imm;
        uint bit12 = (i >> 12) & 1;
        uint bit11 = (i >> 11) & 1;
        uint bits10To5 = (i >> 5) & 0x3F;
        uint bits4To1 = (i >> 1) & 0xF;
        return (bit12 << 31) | (bits10To5 << 25) | (rs2 << 20) | (rs1 << 15)
             | (funct3 << 12) | (bits4To1 << 8) | (bit11 << 7) | 0x2Bu;
    }

    // so.b.nc urs, imm — B-type, opcode=0x2B, funct3=0x4
    private static uint SoBNc(int urs, int imm) => BTypeImm(imm, (uint)urs, 0, 0x4);

    // so.b.ndc.D urs, imm — B-type, opcode=0x2B, funct3=0x5; dim D encoded in rs2 field
    private static uint SoBNdcD(int urs, int dim, int imm) => BTypeImm(imm, (uint)urs, (uint)dim, 0x5);

    // sb.c urs, imm — B-type, opcode=0x2B, funct3=0x6 (complete polarity)
    private static uint SoBc(int urs, int imm) => BTypeImm(imm, (uint)urs, 0, 0x6);

    // sb.dc.D urs, imm — B-type, opcode=0x2B, funct3=0x7; dim D encoded in rs2 field
    private static uint SoBdcD(int urs, int dim, int imm) => BTypeImm(imm, (uint)urs, (uint)dim, 0x7);

    // so.v.dup.fp.w ud, fs1 — R-type, opcode=0x2B, funct3=0x0, funct7[0]=1
    private static uint SoVDupFpW(int ud, int fs1) =>
        (uint)((1 << 25) | ((fs1 & 0x1F) << 20) | ((fs1 & 0x1F) << 15) | (0x0 << 12) | ((ud & 0x1F) << 7) | 0x2B);

    // ss.ld.b/h/d — R4-type, opcode=0x0B, funct3=0x0; funct2 encodes element width
    private static uint SsLdBytes(int ud, int rs1, int rs2, int rs3, int funct2) =>
        (uint)(((rs3 & 0x1F) << 27) | ((funct2 & 0x3) << 25) | ((rs2 & 0x1F) << 20)
             | ((rs1 & 0x1F) << 15) | (0x0 << 12) | ((ud & 0x1F) << 7) | 0x0B);

    // ss.sta.ld.w ud, rs1, rs2, rs3 — R4-type, opcode=0x0B, funct3=0x2
    private static uint SsStaLdW(int ud, int rs1, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | (0x2 << 12) | ((ud & 0x1F) << 7) | 0x0B);

    // ss.sta.st.w ud, rs1, rs2, rs3 — R4-type, opcode=0x0B, funct3=0x3
    private static uint SsStaStW(int ud, int rs1, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | (0x3 << 12) | ((ud & 0x1F) << 7) | 0x0B);

    // ss.end ud, rs2, rs3 — R4-type, opcode=0x0B, funct3=0x5; rs1=x0 (ignored)
    private static uint SsEnd(int ud, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | ((rs2 & 0x1F) << 20) | (0x5 << 12) | ((ud & 0x1F) << 7) | 0x0B);

    // EBREAK — halts the pipeline
    private static uint EBreak() => 0x00100073u;

    // ADDI rd, rs1, imm — used to set up integer registers in integration tests
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | (0x0 << 12) | ((rd & 0x1F) << 7) | 0x13);

    // ── Executor unit tests ───────────────────────────────────────────────────

    [Fact]
    public void SsLdW_ReturnsStreamConfig() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(1, 0x1000); // base
        state.IntegerRegisters.Write(2, 4);      // count
        state.IntegerRegisters.Write(3, 4);      // stride (word)

        ExecuteResult er = Exec(new RvUveSsLdW(1, 1, 2, 3), state);

        Assert.True(er.StreamConfig.HasValue);
        Assert.Equal(1, er.StreamConfig!.Value.StreamId);
        Assert.Equal(0x1000UL, er.StreamConfig.Value.Descriptor.BaseAddress);
        Assert.Equal(4, er.StreamConfig.Value.Descriptor.ElementBytes);
        Assert.Equal(4L, er.StreamConfig.Value.Descriptor.Count);
        Assert.Equal(4L, er.StreamConfig.Value.Descriptor.Stride);
    }

    [Fact]
    public void SsLdW_SideEffect_SetsLoadStreamKind() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(1, 0x1000);
        state.IntegerRegisters.Write(2, 4);
        state.IntegerRegisters.Write(3, 4);

        ExecuteResult er = Exec(new RvUveSsLdW(2, 1, 2, 3), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(UveRegKind.LoadStream, state.UveState.RegKind[2]);
    }

    [Fact]
    public void SsStW_SideEffect_ConfiguresStoreStream() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(1, 0x2000); // base
        state.IntegerRegisters.Write(2, 8);      // count
        state.IntegerRegisters.Write(3, 4);      // stride

        ExecuteResult er = Exec(new RvUveSsStW(3, 1, 2, 3), state);
        er.SideEffect?.Invoke(state);

        UveStoreStream? ss = state.UveState.StoreStreams[3];
        Assert.NotNull(ss);
        Assert.Equal(0x2000UL, ss.BaseAddress);
        Assert.Equal(8L, ss.Dimensions[0].Count);
        Assert.Equal(4L, ss.Dimensions[0].Stride);
        Assert.Equal(UveRegKind.StoreStream, state.UveState.RegKind[3]);
    }

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
    public void Decoder_SsLdW_Roundtrip() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        uint enc = SsLdW(2, 1, 3, 4);
        mem.Load(0, BitConverter.GetBytes(enc));

        ITooth tooth = dec.Decode(0, mem);

        Assert.IsType<RvUveSsLdW>(tooth.Payload);
        var op = (RvUveSsLdW)tooth.Payload!;
        Assert.Equal(2, op.Ud);
        Assert.Equal(1, op.Rs1Base);
        Assert.Equal(3, op.Rs2Count);
        Assert.Equal(4, op.Rs3Stride);
        Assert.Equal(ToothClass.Uve, tooth.Class);
    }

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
    public void SsStaLdW_CreatesPendingConfig_WithFirstDimension() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(1, 0x1000); // base
        state.IntegerRegisters.Write(2, 4);      // inner count
        state.IntegerRegisters.Write(3, 4);      // inner stride

        ExecuteResult er = Exec(new RvUveSsStaLdW(2, 1, 2, 3), state);
        er.SideEffect?.Invoke(state);

        PendingStreamConfig? cfg = state.UveState.PendingConfig[2];
        Assert.NotNull(cfg);
        Assert.Equal(0x1000UL, cfg.BaseAddress);
        Assert.True(cfg.IsLoad);
        Assert.Single(cfg.Dimensions);
        Assert.Equal(4L, cfg.Dimensions[0].Count);
        Assert.Equal(4L, cfg.Dimensions[0].Stride);
    }

    [Fact]
    public void SsApp_AppendsDimensionToPendingConfig() {
        var state = new Rv32ArchState();
        // Pre-populate a pending config (as ss.sta would have done)
        var cfg = new PendingStreamConfig { BaseAddress = 0x2000, ElementBytes = 4, IsLoad = true, };
        cfg.Dimensions.Add(new StreamDimension(4, 4)); // first dim
        state.UveState.PendingConfig[3] = cfg;

        state.IntegerRegisters.Write(2, 3);  // outer count
        state.IntegerRegisters.Write(3, 32); // outer stride

        ExecuteResult er = Exec(new RvUveSsApp(3, 2, 3), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(2, state.UveState.PendingConfig[3]!.Dimensions.Count);
        Assert.Equal(3L, state.UveState.PendingConfig[3]!.Dimensions[1].Count);
        Assert.Equal(32L, state.UveState.PendingConfig[3]!.Dimensions[1].Stride);
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

        ExecuteResult er = Exec(new RvUveSsEnd(1, 2, 3), state);

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

    // ── so.v.dup.fp.w tests ───────────────────────────────────────────────────

    [Fact]
    public void SoVDupFpW_BroadcastsFromFpReg() {
        var state = new Rv32ArchState();
        // FP register f2 lives at unified-RF index 34 (= 2 + 32)
        state.IntegerRegisters.Write(34, (uint)BitConverter.SingleToInt32Bits(1.5f));

        ExecuteResult er = Exec(new RvUveSoVDupFpW(6, 34), state); // fs1=34 = f2
        er.SideEffect?.Invoke(state);

        Assert.Equal(1.5f, state.UveState.Scalars[6], 4);
        Assert.Equal(UveRegKind.Scalar, state.UveState.RegKind[6]);
    }

    [Fact]
    public void Decoder_SoVDupFpW_Roundtrip() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        // fs1 = f3 → rs1=3 in the encoding; unified index is 3+32=35
        mem.Load(0, BitConverter.GetBytes(SoVDupFpW(7, 3)));

        ITooth tooth = dec.Decode(0, mem);

        Assert.IsType<RvUveSoVDupFpW>(tooth.Payload);
        var op = (RvUveSoVDupFpW)tooth.Payload!;
        Assert.Equal(7, op.Ud);
        Assert.Equal(35, op.Fs1); // rs1=3 → unified index 3+32=35
    }

    // ── Non-word element widths tests ─────────────────────────────────────────

    [Fact]
    public void Decoder_SsLdB_DecodesElementBytes1() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SsLdBytes(1, 2, 3, 4, 1))); // .b = 1 byte

        ITooth tooth = dec.Decode(0, mem);

        var op = Assert.IsType<RvUveSsLdW>(tooth.Payload);
        Assert.Equal(1, op.ElementBytes);
    }

    [Fact]
    public void Decoder_SsLdH_DecodesElementBytes2() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SsLdBytes(1, 2, 3, 4, 2))); // .h = 2 bytes

        ITooth tooth = dec.Decode(0, mem);

        var op = Assert.IsType<RvUveSsLdW>(tooth.Payload);
        Assert.Equal(2, op.ElementBytes);
    }

    [Fact]
    public void Decoder_SsLdD_DecodesElementBytes8() {
        var dec = new Rv32Decoder();
        var mem = new FlatMemory(16);
        mem.Load(0, BitConverter.GetBytes(SsLdBytes(1, 2, 3, 4, 3))); // .d = 8 bytes

        ITooth tooth = dec.Decode(0, mem);

        var op = Assert.IsType<RvUveSsLdW>(tooth.Payload);
        Assert.Equal(8, op.ElementBytes);
    }

    [Fact]
    public void SsLdB_StreamConfigCarriesElementBytes() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(1, 0x1000); // base
        state.IntegerRegisters.Write(2, 8);      // count
        state.IntegerRegisters.Write(3, 1);      // stride (1 byte)

        ExecuteResult er = Exec(new RvUveSsLdW(0, 1, 2, 3, 1), state);

        Assert.True(er.StreamConfig.HasValue);
        Assert.Equal(1, er.StreamConfig!.Value.Descriptor.ElementBytes);
    }

    [Fact]
    public void SsLdD_StreamConfigCarriesElementBytes() {
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(1, 0x2000); // base
        state.IntegerRegisters.Write(2, 4);      // count
        state.IntegerRegisters.Write(3, 8);      // stride (8 bytes)

        ExecuteResult er = Exec(new RvUveSsLdW(0, 1, 2, 3, 8), state);

        Assert.True(er.StreamConfig.HasValue);
        Assert.Equal(8, er.StreamConfig!.Value.Descriptor.ElementBytes);
    }

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
        uint enc = SsStaLdW(2, 1, 3, 5);
        mem.Load(0, BitConverter.GetBytes(enc));

        ITooth tooth = dec.Decode(0, mem);

        Assert.IsType<RvUveSsStaLdW>(tooth.Payload);
        var op = (RvUveSsStaLdW)tooth.Payload!;
        Assert.Equal(2, op.Ud);
        Assert.Equal(1, op.Rs1Base);
        Assert.Equal(3, op.Rs2Count);
        Assert.Equal(5, op.Rs3Stride);
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

        // Register allocation for the instruction sequence:
        // x1=base_x, x2=base_y, x3=N, x4=stride(4), x5=bits(A)
        // We use LUI/ADDI to load constants.
        // For addresses and small constants, ADDI x0 is enough.

        // Code at address 0x1000:
        //   addi x1, x0, 0       → x1 = 0 (base X — but 0 is default, so skip? No, use for clarity)
        //   addi x2, x0, 0x100   → x2 = 0x100 (base Y)
        //   addi x3, x0, N       → x3 = 4 (count)
        //   addi x4, x0, 4       → x4 = 4 (stride)
        //   addi x5, x0, bits(A) → won't work for large bits, need alternate approach

        // Problem: scalarBits for A=2.0 = 0x40000000. That's larger than 12-bit immediate.
        // For A=2.0, use LUI x5, 0x40000 + addi x5, x5, 0
        // LUI rd, imm: puts imm in upper 20 bits, rd[11:0]=0
        // bits: 0x40000000 = 0b0100_0000_0000_0000_0000_0000_0000_0000
        // LUI x5, 0x40000 (20-bit imm placed in bits[31:12])

        // A=2.0f bits = 0x40000000 → upper 20 bits = 0x40000
        const ulong code = 0x1000;
        var words = new List<uint> {
            // Setup integer registers
            Addi(1, 0, 0),     // x1 = 0 (base X)
            Addi(2, 0, 0x100), // x2 = 0x100 (base Y)
            Addi(3, 0, n),     // x3 = N (count)
            Addi(4, 0, 4),     // x4 = 4 (stride)
            Lui(5, 0x40000),   // x5 = 0x40000000 (A=2.0 raw bits, lower 12 = 0)
            // Configure streams:
            //   u1 = load from X (base=x1, count=x3, stride=x4)
            //   u2 = load from Y (base=x2, count=x3, stride=x4)
            //   u3 = store to Y  (base=x2, count=x3, stride=x4)
            SsLdW(1, 1, 3, 4), // u1 = load stream X
            SsLdW(2, 2, 3, 4), // u2 = load stream Y
            SsStW(3, 2, 3, 4), // u3 = store stream Y
            // Broadcast scalar A into u4
            SoVDpW(4, 5),
            // Loop body:  so.b.nc u1, loop_back
            // Loop: so.a.mul.fp u5, u1, u4  — u5 = x[i] * A
            //        so.a.add.fp u3, u2, u5  — u3(y) = y[i] + u5
            //        so.b.nc u1, -8          — branch back -8 bytes (2 instructions × 4 bytes)
            SoAFp(UveFpOp.Mul, 5, 1, 4), // u5 = u1[i] * u4
            SoAFp(UveFpOp.Add, 3, 2, 5), // u3[i] = u2[i] + u5
            SoBNc(1, -8),                // loop while u1 not done (-2 instructions)
            EBreak(),                    // u4 = broadcast A
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
        //   x8 = 12           output count
        const ulong code = 0x1000;
        var words = new List<uint> {
            Addi(1, 0, 0x000),       // x1 = 0 (matrix base)
            Addi(2, 0, 0x200),       // x2 = 0x200 (output base)
            Addi(3, 0, cols),        // x3 = 4
            Addi(4, 0, 4),           // x4 = 4 (byte stride)
            Addi(5, 0, rows),        // x5 = 3
            Addi(6, 0, rowBytes),    // x6 = 32
            Addi(8, 0, rows * cols), // x8 = 12 (output element count)
            // Scalar: 3.0f = 0x40400000. LUI x7, 0x40400 puts 0x40400000 in x7 (lower 12=0). ✓
            Lui(7, 0x40400),
            // 2D load stream on u1: dim0=inner(count=4,stride=4), dim1=outer(count=3,stride=32)
            // ss.sta provides the innermost dimension; ss.end provides the outermost and activates.
            SsStaLdW(1, 1, 3, 4), // ss.sta.ld.w u1, x1, x3, x4  — dim0: inner
            SsEnd(1, 5, 6),       // ss.end      u1, x5, x6       — dim1: outer, finalize
            // 1D store stream on u2: 12 elements at 0x0200, stride=4
            SsStW(2, 2, 8, 4), // ss.st.w u2, x2, x8, x4
            // Broadcast scalar into u4
            SoVDpW(4, 7), // u4 = broadcast Scalar
        };

        // Loop:
        //   outer: (check dim1 not complete at bottom)
        //     inner: so.a.mul.fp u2, u1, u4  — writes to store stream
        //            so.b.ndc.0 u1, -4       — branch while inner dim not complete (-1 instr)
        //   so.b.ndc.1 u1, outer_offset      — branch while outer dim not complete

        // Instruction layout relative to code base:
        //   [0..7]  setup addi/lui  (8 words)
        //   [8]     SsStaLdW
        //   [9]     SsEnd
        //   [10]    SsStW
        //   [11]    SoVDpW
        //   [12]    inner_loop_start: SoAFp (mul)
        //   [13]    SoBNdcD dim0 (branch -4 bytes = -1 instr back to [12])
        //   [14]    SoBNdcD dim1 (branch to [12] = -8 bytes)
        //   [15]    EBreak

        int innerLoopWord = words.Count; // will be word index 13

        words.Add(SoAFp(UveFpOp.Mul, 2, 1, 4)); // u2 = u1[elem] * u4
        words.Add(SoBNdcD(1, 0, -4));           // so.b.ndc.0 u1, -4 (inner loop back 1 instr)

        // Outer loop branch: target = innerLoopWord instruction, from current position = innerLoopWord+2
        int outerBranchWord = words.Count;                          // word index 15
        int outerBranchImm = (innerLoopWord - outerBranchWord) * 4; // negative offset
        words.Add(SoBNdcD(1, 1, outerBranchImm));                   // so.b.ndc.1 u1, outer

        words.Add(EBreak());

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
            // 1.0f = 0x3F800000; LUI x7, 0x3F800 gives 0x3F800000 (lower 12 bits = 0). ✓
            Lui(7, 0x3F800), // x7 = bits(1.0f)
            // Load stream: u1 reads all 12 source elements in order (1D)
            SsLdW(1, 1, 3, 4), // ss.ld.w u1, x1, x3, x4
            // Store stream: u2 writes to a 3×4 matrix with 32-byte rows (multi-dim)
            SsStaStW(2, 2, 5, 4), // ss.sta.st.w u2, x2, x5, x4  (dim0: 4 cols, stride 4)
            SsEnd(2, 6, 8),       // ss.end u2, x6, x8            (dim1: 3 rows, stride 32)
            // Broadcast scalar 1.0 into u4
            SoVDpW(4, 7), // u4 = 1.0f
            // Loop: copy each element (u1 elem × 1.0 = u1 elem), write to u2 store stream
            //   [loop]: so.a.mul.fp u2, u1, u4   — writes dst[row][col], advances 2D cursor
            //           so.b.nc u1, -4           — branch while load stream not exhausted
            SoAFp(UveFpOp.Mul, 2, 1, 4), // u2 = u1[i] * u4
            SoBNc(1, -4),                // so.b.nc u1, -4
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
}