using Mechanism;
using Orrery.Streaming;
using Pipeline;
using RiscV;
using RiscV.Decode;
using RiscV.Execute;
using RiscV.Memory;
using RiscV.State;

namespace Tests.RiscV;

/// <summary>
/// Tests for the UVE extension: stream setup, scalar broadcast, arithmetic ops, and branches.
/// Also includes a SAXPY-style integration test through the OoO pipeline.
/// </summary>
public class UveTests {
    // ── Helpers ───────────────────────────────────────────────────────────────

    // Build a FlatMemory pre-loaded with N floats at addresses 0, 4, 8, ...
    private static FlatMemory FloatMemory(params float[] values) {
        var mem = new FlatMemory(values.Length * 4);
        for (var i = 0; i < values.Length; i++)
            mem.Load((ulong)(i * 4), BitConverter.GetBytes(values[i]));
        return mem;
    }

    // Execute a single UVE instruction directly via the executor.
    private static ExecuteResult Exec(RvOp payload, RvArchState state, IMemory? memory = null) {
        var instr = new RvInstruction(0x1000, 0xDEADBEEF, -1, [], ToothClass.Uve, payload);
        return new RvExecutor().Execute(instr, state, memory ?? new FlatMemory(256));
    }

    // Build an OooeTrain running a hand-assembled UVE instruction sequence.
    // Instructions are encoded as raw 32-bit words and loaded starting at PC=0.
    private static OooeTrain BuildTrain(FlatMemory mem, params uint[] words) {
        for (var i = 0; i < words.Length; i++)
            mem.Load((ulong)(i * 4), BitConverter.GetBytes(words[i]));
        return new OooeTrain(new RvMechanism(), mem, entryPoint: 0, streamPrefetchDepth: 8);
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
        (uint)((((int)op << 4) << 25) | ((usrc2 & 0x1F) << 20) | ((usrc1 & 0x1F) << 15)
             | (0x1 << 12) | ((ud & 0x1F) << 7) | 0x2B);

    // so.b.nc urs, imm — B-type, opcode=0x2B, funct3=0x4
    private static uint SoBNc(int urs, int imm) {
        // B-type: imm[12|10:5] | rs2 | rs1 | funct3 | imm[4:1|11] | opcode
        int i = imm;
        uint bit12 = (uint)((i >> 12) & 1);
        uint bit11 = (uint)((i >> 11) & 1);
        uint bits10_5 = (uint)((i >> 5) & 0x3F);
        uint bits4_1 = (uint)((i >> 1) & 0xF);
        return (bit12 << 31) | (bits10_5 << 25) | (0u << 20) | ((uint)(urs & 0x1F) << 15)
             | (0x4u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0x2Bu;
    }

    // EBREAK — halts the pipeline
    private static uint EBreak() => 0x00100073u;

    // ADDI rd, rs1, imm — used to set up integer registers in integration tests
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)((imm & 0xFFF) << 20 | (rs1 & 0x1F) << 15 | 0x0 << 12 | (rd & 0x1F) << 7 | 0x13);

    // LI rd, imm — pseudo-instruction, maps to addi rd, x0, imm (for small immediates)
    private static uint Li(int rd, int imm) => Addi(rd, 0, imm);

    // ── Executor unit tests ───────────────────────────────────────────────────

    [Fact]
    public void SsLdW_ReturnsStreamConfig() {
        var state = new RvArchState();
        state.IntegerRegisters.Write(1, 0x1000); // base
        state.IntegerRegisters.Write(2, 4);       // count
        state.IntegerRegisters.Write(3, 4);       // stride (word)

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
        var state = new RvArchState();
        state.IntegerRegisters.Write(1, 0x1000);
        state.IntegerRegisters.Write(2, 4);
        state.IntegerRegisters.Write(3, 4);

        ExecuteResult er = Exec(new RvUveSsLdW(2, 1, 2, 3), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(UveRegKind.LoadStream, state.UveState.RegKind[2]);
    }

    [Fact]
    public void SsStW_SideEffect_ConfiguresStoreStream() {
        var state = new RvArchState();
        state.IntegerRegisters.Write(1, 0x2000); // base
        state.IntegerRegisters.Write(2, 8);       // count
        state.IntegerRegisters.Write(3, 4);       // stride

        ExecuteResult er = Exec(new RvUveSsStW(3, 1, 2, 3), state);
        er.SideEffect?.Invoke(state);

        UveStoreStream? ss = state.UveState.StoreStreams[3];
        Assert.NotNull(ss);
        Assert.Equal(0x2000UL, ss!.BaseAddress);
        Assert.Equal(8L, ss.Count);
        Assert.Equal(4L, ss.Stride);
        Assert.Equal(UveRegKind.StoreStream, state.UveState.RegKind[3]);
    }

    [Fact]
    public void SoVDpW_WritesBroadcastScalar() {
        var state = new RvArchState();
        state.IntegerRegisters.Write(5, (uint)BitConverter.SingleToInt32Bits(3.14f));

        ExecuteResult er = Exec(new RvUveSoVDpW(4, 5), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(3.14f, state.UveState.Scalars[4], 4);
        Assert.Equal(UveRegKind.Scalar, state.UveState.RegKind[4]);
    }

    [Fact]
    public void SoAFp_Mul_ComputesProduct() {
        var state = new RvArchState();
        // Pipeline would inject source values; simulate by pre-setting scalars
        state.UveState.Scalars[1] = 3.0f;
        state.UveState.Scalars[2] = 4.0f;

        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Mul, 5, 1, 2), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(12.0f, state.UveState.Scalars[5], 4);
    }

    [Fact]
    public void SoAFp_Add_ComputesSum() {
        var state = new RvArchState();
        state.UveState.Scalars[1] = 2.5f;
        state.UveState.Scalars[2] = 7.5f;

        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Add, 0, 1, 2), state);
        er.SideEffect?.Invoke(state);

        Assert.Equal(10.0f, state.UveState.Scalars[0], 4);
    }

    [Fact]
    public void SoAFp_ToStoreStream_WritesMemory() {
        var state = new RvArchState();
        var mem = new FlatMemory(64);

        // Configure u3 as a store stream starting at address 0
        state.UveState.StoreStreams[3] = new UveStoreStream { BaseAddress = 0, ElementBytes = 4, Count = 4, Stride = 4 };
        state.UveState.RegKind[3] = UveRegKind.StoreStream;

        state.UveState.Scalars[1] = 5.0f;
        state.UveState.Scalars[2] = 3.0f;

        // so.a.mul.fp u3, u1, u2 → should write 5*3=15 to address 0 and advance cursor
        ExecuteResult er = Exec(new RvUveSoAFp(UveFpOp.Mul, 3, 1, 2), state, mem);
        er.SideEffect?.Invoke(state);

        float written = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(0, 4));
        Assert.Equal(15.0f, written, 4);
        Assert.Equal(1L, state.UveState.StoreStreams[3]!.NextIndex); // cursor advanced
    }

    [Fact]
    public void SoBNc_TakenWhenNotDone() {
        var state = new RvArchState();
        state.UveState.StreamDone[1] = false; // stream not exhausted

        // so.b.nc u1, -12 — should take branch back by 12 bytes
        ExecuteResult er = Exec(new RvUveSoBNc(1, -12), state);

        Assert.True(er.BranchTaken);
        Assert.Equal(0x1000UL - 12UL, er.BranchTarget); // PC was 0x1000 in Exec helper
    }

    [Fact]
    public void SoBNc_NotTakenWhenDone() {
        var state = new RvArchState();
        state.UveState.StreamDone[1] = true; // stream exhausted

        ExecuteResult er = Exec(new RvUveSoBNc(1, -12), state);

        Assert.False(er.BranchTaken);
    }

    // ── Decoder tests ─────────────────────────────────────────────────────────

    [Fact]
    public void Decoder_SsLdW_Roundtrip() {
        var dec = new RvDecoder();
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
        var dec = new RvDecoder();
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
        var dec = new RvDecoder();
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

    // ── Integration test: SAXPY via OoO pipeline ──────────────────────────────

    /// <summary>
    /// Runs a SAXPY computation (Y = A*X + Y) through the OoO pipeline using UVE streams.
    ///
    /// Memory layout:
    ///   [0x0000..0x003F]  source X array: 8 floats
    ///   [0x0040..0x007F]  destination Y array: 8 floats (output overwrites in-place)
    ///   [0x1000..]        code
    ///
    /// Register assignments in setup ADDI sequence:
    ///   x1 = 0x0000  (base of X)
    ///   x2 = 0x0040  (base of Y)
    ///   x3 = 8       (element count)
    ///   x4 = 4       (stride in bytes, = element width)
    ///   x5 = bits(A) (scalar multiplier, as float32 raw bits)
    /// </summary>
    [Fact]
    public void Pipeline_Saxpy_CorrectResult() {
        const int N = 4; // keep small so test runs fast
        const float A = 2.0f;

        float[] x = [1.0f, 2.0f, 3.0f, 4.0f,];
        float[] y = [10.0f, 20.0f, 30.0f, 40.0f,];
        float[] expected = x.Zip(y, (xi, yi) => A * xi + yi).ToArray();

        var mem = new FlatMemory(0x2000);

        // Write X at 0x0000 and Y at 0x0100
        for (var i = 0; i < N; i++) {
            mem.Load((ulong)(i * 4), BitConverter.GetBytes(x[i]));
            mem.Load((ulong)(0x100 + i * 4), BitConverter.GetBytes(y[i]));
        }

        uint scalarBits = (uint)BitConverter.SingleToInt32Bits(A);

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

        uint Lui(int rd, int imm20) =>
            (uint)(((imm20 & 0xFFFFF) << 12) | ((rd & 0x1F) << 7) | 0x37);

        // A=2.0f bits = 0x40000000 → upper 20 bits = 0x40000
        ulong code = 0x1000;
        var words = new List<uint>();

        // Setup integer registers
        words.Add(Addi(1, 0, 0));         // x1 = 0 (base X)
        words.Add(Addi(2, 0, 0x100));     // x2 = 0x100 (base Y)
        words.Add(Addi(3, 0, N));         // x3 = N (count)
        words.Add(Addi(4, 0, 4));         // x4 = 4 (stride)
        words.Add(Lui(5, 0x40000));       // x5 = 0x40000000 (A=2.0 raw bits, lower 12 = 0)

        // Configure streams:
        //   u1 = load from X (base=x1, count=x3, stride=x4)
        //   u2 = load from Y (base=x2, count=x3, stride=x4)
        //   u3 = store to Y  (base=x2, count=x3, stride=x4)
        words.Add(SsLdW(1, 1, 3, 4));    // u1 = load stream X
        words.Add(SsLdW(2, 2, 3, 4));    // u2 = load stream Y
        words.Add(SsStW(3, 2, 3, 4));    // u3 = store stream Y

        // Broadcast scalar A into u4
        words.Add(SoVDpW(4, 5));          // u4 = broadcast A

        // Loop body:  so.b.nc u1, loop_back
        // Loop: so.a.mul.fp u5, u1, u4  — u5 = x[i] * A
        //        so.a.add.fp u3, u2, u5  — u3(y) = y[i] + u5
        //        so.b.nc u1, -8          — branch back -8 bytes (2 instructions × 4 bytes)
        uint loopStart = (uint)words.Count;

        words.Add(SoAFp(UveFpOp.Mul, 5, 1, 4)); // u5 = u1[i] * u4
        words.Add(SoAFp(UveFpOp.Add, 3, 2, 5)); // u3[i] = u2[i] + u5
        words.Add(SoBNc(1, -8));                  // loop while u1 not done (-2 instructions)

        words.Add(EBreak());

        // Load code at 0x1000
        for (var i = 0; i < words.Count; i++)
            mem.Load(code + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(new RvMechanism(), mem, entryPoint: code,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32);
        train.Run(maxTicks: 2000);

        // Verify Y array was overwritten with A*X + Y
        for (var i = 0; i < N; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read((ulong)(0x100 + i * 4), 4));
            Assert.Equal(expected[i], actual, 2);
        }
    }
}
