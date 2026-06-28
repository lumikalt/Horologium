using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// PolyBench trisolv: solve L·x = b where L is lower-triangular.
///
/// Algorithm:
///   for i in 0..N-1:
///     x[i] = (b[i] - sum(L[i][j]*x[j] for j in 0..i-1)) / L[i][i]
///
/// UVE mapping:
///   Inner loop: u3 += u1[j] * u2[j]   (MAC; u1=L row, u2=x prefix)
///   Final:      u6 = b[i] - u3;  x[i] = u6 / L[i][i]
///
/// Tests that UVE store-stream writes to x[] are visible to subsequent load-stream
/// prefetches (both use DLayers.Accessor, so writes in row i are seen in row i+1).
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

    // SS.LD.W ud, rs1_base, rs2_count, rs3_stride
    private static uint SsLdW(int ud, int rs1, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | (0x0 << 12) | ((ud & 0x1F) << 7) | 0x0Bu);

    // SS.ST.W ud, rs1_base, rs2_count, rs3_stride
    private static uint SsStW(int ud, int rs1, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | (0x1 << 12) | ((ud & 0x1F) << 7) | 0x0Bu);

    // SO.V.DP.W ud, rs1 — broadcast float bits from integer register into u-reg scalar
    private static uint SoVDpW(int ud, int rs1) =>
        (uint)(((rs1 & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (0x0 << 12) | ((ud & 0x1F) << 7) | 0x2Bu);

    // SO.A.FP ud, usrc1, usrc2 — FP op; funct7[6:4] = op code (Mul=0,Add=1,Mac=2,Sub=3,Div=4)
    private static uint SoAFp(int opCode, int ud, int usrc1, int usrc2) =>
        (uint)(((opCode & 7) << 4 << 25) | ((usrc2 & 0x1F) << 20) | ((usrc1 & 0x1F) << 15)
             | (0x1 << 12) | ((ud & 0x1F) << 7) | 0x2Bu);

    private static uint SoAMacFp(int ud, int usrc1, int usrc2) => SoAFp(2, ud, usrc1, usrc2);
    private static uint SoASubFp(int ud, int usrc1, int usrc2) => SoAFp(3, ud, usrc1, usrc2);
    private static uint SoADivFp(int ud, int usrc1, int usrc2) => SoAFp(4, ud, usrc1, usrc2);

    // SO.B.NC urs, imm — B-type, opcode=0x2B, funct3=0x4
    private static uint SoBNc(int urs, int imm) => BranchInstr(imm, urs, 0, 0x4, 0x2Bu);

    // ── Reference solution ────────────────────────────────────────────────────

    private static float[] ReferenceSolve(int n, float[] L, float[] b) {
        var x = new float[n];
        for (var i = 0; i < n; i++) {
            var sum = 0f;
            for (var j = 0; j < i; j++) sum += L[i * n + j] * x[j];
            x[i] = (b[i] - sum) / L[i * n + i];
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
        const int innerIdx = 16;
        const int skipIdx = 18;
        const int doneIdx = 33;

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
            SsLdW(1, 7, 3, 6),                   // [12] u1 = L[i][0..i-1], count=i
            SsLdW(2, 8, 3, 6),                   // [13] u2 = x[0..i-1],    count=i
            SoVDpW(3, 0),                        // [14] u3 = 0.0f
            Beq(3, 0, (skipIdx - 15) * 4),       // [15] if i==0 → skip (count=0 deadlock guard)

            SoAMacFp(3, 1, 2),             // [16] u3 += u1[j] * u2[j]
            SoBNc(1, (innerIdx - 17) * 4), // [17] while u1 not done → [16]

            Add(13, 9, 10),      // [18] x13 = &b[i]
            Lw(11, 13, 0),       // [19] x11 = bits(b[i])
            SoVDpW(5, 11),       // [20] u5 = b[i]
            SoASubFp(6, 5, 3),   // [21] u6 = b[i] - u3
            Add(15, 7, 10),      // [22] x15 = &L[i][i]  (= &L[i][0] + i*4)
            Lw(11, 15, 0),       // [23] x11 = bits(L[i][i])
            SoVDpW(4, 11),       // [24] u4 = L[i][i]
            Add(12, 8, 10),      // [25] x12 = &x[i]
            SsStW(7, 12, 14, 6), // [26] u7 = 1-elem store stream at &x[i]
            SoADivFp(7, 6, 4),   // [27] u7 ← u6/u4 → writes x[i]

            Addi(3, 3, 1),               // [28] i++
            Add(4, 4, 5),                // [29] row_offset += N*4
            Addi(10, 10, 4),             // [30] elem_offset += 4
            Jal(0, (outerIdx - 31) * 4), // [31] → outer_loop

            EBreak(), // [32] unreachable
            EBreak(), // [33] done (BGE target)
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