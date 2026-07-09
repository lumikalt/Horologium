using Pipeline;
using RiscV32;
using RiscV32.Memory;

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32.Extensions;

/// <summary>
/// PolyBench trisolv: solve L·x = b where L is lower-triangular.
/// <para>
/// Algorithm:
///   for i in 0..N-1:
///     x[i] = (b[i] - sum(L[i][j]*x[j] for j in 0..i-1)) / L[i][i]
/// </para>
/// <para>
/// UVE mapping:
///   Inner loop: u3 += u1[j] * u2[j]   (MAC; u1=L row, u2=x prefix)
///   Final:      u6 = b[i] - u3;  x[i] = u6 / L[i][i]
/// </para>
/// <para>
/// Tests that UVE store-stream writes to x[] are visible to subsequent load-stream
/// prefetches (both use DLayers.Accessor, so writes in row i are seen in row i+1).
/// </para>
/// </summary>
public class TrisolvTests {
    // ── Encode helpers ────────────────────────────────────────────────────────

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | (0 << 12) | ((rd & 0x1F) << 7) | 0x13u);

    private static uint Slli(int rd, int rs1, int shamt) =>
        (uint)((0 << 25) | ((shamt & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (1 << 12) | ((rd & 0x1F) << 7) | 0x13u);

    private static uint Add(int rd, int rs1, int rs2) =>
        (uint)(((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (0 << 12) | ((rd & 0x1F) << 7) | 0x33u);

    private static uint Lw(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | (0x2 << 12) | ((rd & 0x1F) << 7) | 0x03u);

    private static uint Jal(int rd, int imm) {
        var i = (uint)imm;
        return (((i >> 20) & 1) << 31) | (((i >> 1) & 0x3FF) << 21) | (((i >> 11) & 1) << 20)
             | (((i >> 12) & 0xFF) << 12) | ((uint)(rd & 0x1F) << 7) | 0x6Fu;
    }

    private static uint BranchInstr(int imm, int rs1, int rs2, int funct3, uint opcode = 0x63u) {
        var i = (uint)imm;
        return (((i >> 12) & 1) << 31) | (((i >> 5) & 0x3F) << 25) | ((uint)(rs2 & 0x1F) << 20)
             | ((uint)(rs1 & 0x1F) << 15) | ((uint)(funct3 & 7) << 12)
             | (((i >> 1) & 0xF) << 8) | (((i >> 11) & 1) << 7) | opcode;
    }

    private static uint Bge(int rs1, int rs2, int imm) => BranchInstr(imm, rs1, rs2, 0x5);
    private static uint Beq(int rs1, int rs2, int imm) => BranchInstr(imm, rs1, rs2, 0x0);
    private static uint EBreak() => 0x00100073u;

    // SS.STA.LD.W ud, rs1 — funct2=0, funct3=0b110 (load, ew=4)
    private static uint SsStaLdW(int ud, int rs1) =>
        (uint)(((rs1 & 0x1F) << 15) | (0x6u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // SS.STA.ST.W ud, rs1 — funct2=0, funct3=0b010 (store, ew=4)
    private static uint SsStaStW(int ud, int rs1) =>
        (uint)(((rs1 & 0x1F) << 15) | (0x2u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // SS.END ud, rs1Offset, rs2, rs3 — funct2=2, funct3=0
    private static uint SsEnd(int ud, int rs1Offset, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | (0x2u << 25) | (uint)((rs2 & 0x1F) << 20)
             | (uint)((rs1Offset & 0x1F) << 15) | (0x0u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // SO.V.DP.W ud, rs1 — custom-1, funct7=0x56, funct3=2
    private static uint SoVDpW(int ud, int rs1) =>
        (uint)((0x56u << 25) | ((rs1 & 0x1F) << 15) | (0x2u << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu);

    // SO.A.FP ud, usrc1, usrc2 — opCode uses UveFpOp int values: Mul=0,Add=1,Mac=2,Sub=3,Div=4
    private static uint SoAFp(int opCode, int ud, int usrc1, int usrc2) {
        (uint funct3, uint top4) = opCode switch {
            0 => (1u, 1u), // Mul
            1 => (1u, 0u), // Add
            2 => (5u, 3u), // Mac
            3 => (5u, 0u), // Sub
            4 => (5u, 1u), // Div
            _ => throw new ArgumentOutOfRangeException(nameof(opCode)),
        };
        uint funct7 = top4 << 3;
        return (funct7 << 25) | (uint)((usrc2 & 0x1F) << 20) | (uint)((usrc1 & 0x1F) << 15)
             | (funct3 << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu;
    }

    private static uint SoAMacFp(int ud, int usrc1, int usrc2) => SoAFp(2, ud, usrc1, usrc2);
    private static uint SoASubFp(int ud, int usrc1, int usrc2) => SoAFp(3, ud, usrc1, usrc2);
    private static uint SoADivFp(int ud, int usrc1, int usrc2) => SoAFp(4, ud, usrc1, usrc2);

    // SO.B.NC urs, imm — UVE B-type: bits[31:29]=111, bit28=imm[12], bit20=1(notDone), funct3=0
    private static uint SoBNc(int urs, int imm) {
        var i = (uint)imm;
        uint bit12 = (i >> 12) & 1,
             bit11 = (i >> 11) & 1,
             bits10To5 = (i >> 5) & 0x3F,
             bits4To1 = (i >> 1) & 0xF;
        return (0b111u << 29) | (bit12 << 28) | (bits10To5 << 22) | (0b00001u << 20)
             | ((uint)(urs & 0x1F) << 15) | (bits4To1 << 8) | (bit11 << 7) | 0x2Bu;
    }

    // ── Reference solution ────────────────────────────────────────────────────

    private static float[] ReferenceSolve(int n, float[] l, float[] b) {
        var x = new float[n];
        for (var i = 0; i < n; i++) {
            var sum = 0f;
            for (var j = 0; j < i; j++) sum += l[i * n + j] * x[j];
            x[i] = (b[i] - sum) / l[i * n + i];
        }

        return x;
    }

    // ── Instruction builder ───────────────────────────────────────────────────

    // Builds the trisolv instruction sequence for N×N system.
    //
    // Register plan:
    //   x1  = lBase              x8  = xBase
    //   x2  = N                  x9  = bBase
    //   x3  = i (outer index)    x10 = element byte offset (i*4)
    //   x4  = row byte offset    x11 = temp (float bits from LW)
    //         (i * N * 4)        x12 = &x[i]
    //   x5  = N*4 (row stride)   x13 = &b[i]
    //   x6  = 4 (elem stride)    x14 = 1 (1-element count)
    //   x7  = &L[i][0]           x15 = &L[i][i]
    //
    // UVE register plan:
    //   u1 = load stream for L[i][0..i-1]  (count=i, stride=4)
    //   u2 = load stream for x[0..i-1]     (count=i, stride=4)
    //   u3 = MAC accumulator (sum of L[i][j]*x[j])
    //   u4 = scalar L[i][i]
    //   u5 = scalar b[i]
    //   u6 = b[i] - u3
    //   u7 = 1-element store stream at &x[i]
    //
    // Word layout:
    //   [0..9]   setup
    //   [10]     outer_loop: bge x3, x2, done
    //   [11..15] configure streams, reset accumulator, gate inner loop
    //   [16..17] inner_loop: mac + so.b.nc
    //   [18..27] post-inner: load b[i]/L[i][i], sub, div, write x[i]
    //   [28..31] outer loop tail + jal
    //   [32]     unreachable ebreak
    //   [33]     done: ebreak
    private static uint[] BuildWords(int n) {
        const ulong lBase = 0x0100u;
        const ulong bBase = 0x0300u;
        const ulong xBase = 0x0400u;

        const int outerIdx = 10;
        const int innerIdx = 18;
        const int skipIdx = 20;
        const int doneIdx = 36;

        return [
            Addi(1, 0, (int)lBase), // [0]  x1 = lBase
            Addi(2, 0, n),          // [1]  x2 = N
            Slli(5, 2, 2),          // [2]  x5 = N*4
            Addi(3, 0, 0),          // [3]  i = 0
            Addi(4, 0, 0),          // [4]  row_byte_offset = 0
            Addi(6, 0, 4),          // [5]  x6 = 4 (elem stride)
            Addi(8, 0, (int)xBase), // [6]  x8 = xBase
            Addi(9, 0, (int)bBase), // [7]  x9 = bBase
            Addi(10, 0, 0),         // [8]  element_byte_offset = 0
            Addi(14, 0, 1),         // [9]  x14 = 1

            Bge(3, 2, (doneIdx - outerIdx) * 4), // [10] if i >= N → done
            Add(7, 1, 4),                        // [11] x7 = &L[i][0]
            SsStaLdW(1, 7),                      // [12] u1 base = &L[i][0]
            SsEnd(1, 0, 3, 6),                   // [13] u1: count=i, stride=4; activate
            SsStaLdW(2, 8),                      // [14] u2 base = xBase
            SsEnd(2, 0, 3, 6),                   // [15] u2: count=i, stride=4; activate
            SoVDpW(3, 0),                        // [16] u3 = 0.0f
            Beq(3, 0, (skipIdx - 17) * 4),       // [17] if i==0 → skip (count=0 deadlock guard)

            SoAMacFp(3, 1, 2),             // [18] u3 += u1[j] * u2[j]
            SoBNc(1, (innerIdx - 19) * 4), // [19] while u1 not done → [18]

            Add(13, 9, 10),     // [20] x13 = &b[i]
            Lw(11, 13, 0),      // [21] x11 = bits(b[i])
            SoVDpW(5, 11),      // [22] u5 = b[i]
            SoASubFp(6, 5, 3),  // [23] u6 = b[i] - u3
            Add(15, 7, 10),     // [24] x15 = &L[i][i]  (= &L[i][0] + i*4)
            Lw(11, 15, 0),      // [25] x11 = bits(L[i][i])
            SoVDpW(4, 11),      // [26] u4 = L[i][i]
            Add(12, 8, 10),     // [27] x12 = &x[i]
            SsStaStW(7, 12),    // [28] u7 base = &x[i]
            SsEnd(7, 0, 14, 6), // [29] u7: count=1, stride=4; activate
            SoADivFp(7, 6, 4),  // [30] u7 ← u6/u4 → writes x[i]

            Addi(3, 3, 1),               // [31] i++
            Add(4, 4, 5),                // [32] row_offset += N*4
            Addi(10, 10, 4),             // [33] elem_offset += 4
            Jal(0, (outerIdx - 34) * 4), // [34] → outer_loop

            EBreak(), // [35] unreachable
            EBreak(), // [36] done (BGE target)
        ];
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Trisolv_CorrectResult(int n) {
        const ulong lBase = 0x0100u;
        const ulong bBase = 0x0300u;
        const ulong xBase = 0x0400u;
        const ulong codeBase = 0x1000u;

        var mem = new FlatMemory(0x2000);

        var lFlat = new float[n * n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++) {
            lFlat[i * n + j] = i * n + j + 1.0f;
            mem.Load(lBase + (ulong)((i * n + j) * 4), BitConverter.GetBytes(lFlat[i * n + j]));
        }

        var bVec = new float[n];
        for (var i = 0; i < n; i++) {
            bVec[i] = (i + 1) * 10.0f;
            mem.Load(bBase + (ulong)(i * 4), BitConverter.GetBytes(bVec[i]));
        }

        uint[] words = BuildWords(n);
        for (var i = 0; i < words.Length; i++) mem.Load(codeBase + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, codeBase,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(20_000);

        float[] expected = ReferenceSolve(n, lFlat, bVec);
        for (var i = 0; i < n; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(xBase + (ulong)(i * 4), 4));
            Assert.Equal(expected[i], actual, 3);
        }
    }
}