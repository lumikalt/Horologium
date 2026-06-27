using Pipeline;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Memory;

namespace Tests.RiscV;

/// <summary>
/// Computes the lower-triangular sum of an N×N float matrix using UVE streams.
/// The UVE paper cites triangular matrix access as a first-class motivation for
/// per-row stream reconfiguration: the outer loop reconfigures the load stream
/// with count = r+1 on each iteration so only the live triangle is streamed.
///
/// Program structure:
///   outer (r = 0..N-1):
///     ss.ld.w u1, &matrix[r][0], count=(r+1), stride=4   — reconfigure per row
///     inner: so.a.add.fp u2, u1, u2 ; so.b.nc u1, -4    — accumulate into u2
///   final: write u2 to resultAddr via a 1-element store stream
/// </summary>
public class LowerTriangularSumTests {

    // ── Encode helpers ────────────────────────────────────────────────────────

    // ADDI rd, rs1, imm12 — I-type, funct3=000, opcode=0x13
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | (0 << 12) | ((rd & 0x1F) << 7) | 0x13u);

    // SLLI rd, rs1, shamt — I-type, funct3=001, funct7=0000000, opcode=0x13
    private static uint Slli(int rd, int rs1, int shamt) =>
        (uint)((0 << 25) | ((shamt & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (1 << 12) | ((rd & 0x1F) << 7) | 0x13u);

    // ADD rd, rs1, rs2 — R-type, funct7=0x00, funct3=000, opcode=0x33
    private static uint Add(int rd, int rs1, int rs2) =>
        (uint)((rs2 & 0x1F) << 20 | (rs1 & 0x1F) << 15 | 0 << 12 | (rd & 0x1F) << 7 | 0x33u);

    // BGE rs1, rs2, imm — B-type, funct3=101, opcode=0x63; branch if rs1 >= rs2 (signed)
    private static uint Bge(int rs1, int rs2, int imm) => BranchInstr(imm, rs1, rs2, 0x5);

    // JAL rd, imm — J-type, opcode=0x6F
    private static uint Jal(int rd, int imm) {
        uint i = (uint)imm;
        return ((i >> 20 & 1) << 31) | ((i >> 1 & 0x3FF) << 21) | ((i >> 11 & 1) << 20)
             | ((i >> 12 & 0xFF) << 12) | ((uint)(rd & 0x1F) << 7) | 0x6Fu;
    }

    // B-type helper shared by BGE and SO.B.NC — funct3 distinguishes them
    private static uint BranchInstr(int imm, int rs1, int rs2, int funct3, uint opcode = 0x63u) {
        uint i = (uint)imm;
        return ((i >> 12 & 1) << 31) | ((i >> 5 & 0x3F) << 25) | ((uint)(rs2 & 0x1F) << 20)
             | ((uint)(rs1 & 0x1F) << 15) | ((uint)(funct3 & 7) << 12)
             | ((i >> 1 & 0xF) << 8) | ((i >> 11 & 1) << 7) | opcode;
    }

    // EBREAK
    private static uint EBreak() => 0x00100073u;

    // SS.LD.W ud, rs1_base, rs2_count, rs3_stride — R4-type, opcode=0x0B, funct3=0x0
    private static uint SsLdW(int ud, int rs1, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | (0x0 << 12) | ((ud & 0x1F) << 7) | 0x0Bu);

    // SS.ST.W ud, rs1_base, rs2_count, rs3_stride — R4-type, opcode=0x0B, funct3=0x1
    private static uint SsStW(int ud, int rs1, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | ((rs2 & 0x1F) << 20) | ((rs1 & 0x1F) << 15)
             | (0x1 << 12) | ((ud & 0x1F) << 7) | 0x0Bu);

    // SO.V.DP.W ud, rs1 — broadcast float bits from integer register into u-reg scalar
    private static uint SoVDpW(int ud, int rs1) =>
        (uint)(((rs1 & 0x1F) << 20) | ((rs1 & 0x1F) << 15) | (0x0 << 12) | ((ud & 0x1F) << 7) | 0x2Bu);

    // SO.A.ADD.FP ud, usrc1, usrc2 — element-wise FP add (UveFpOp.Add = 1)
    private static uint SoAAddFp(int ud, int usrc1, int usrc2) =>
        (uint)(((1 << 4) << 25) | ((usrc2 & 0x1F) << 20) | ((usrc1 & 0x1F) << 15)
             | (0x1 << 12) | ((ud & 0x1F) << 7) | 0x2Bu);

    // SO.B.NC urs, imm — B-type, opcode=0x2B, funct3=0x4; branch while stream not exhausted
    private static uint SoBNc(int urs, int imm) => BranchInstr(imm, urs, 0, 0x4, 0x2Bu);

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Expected lower-triangular sum for an N×N matrix whose [r][c] = r*N + c + 1.
    private static float ExpectedSum(int n) {
        float sum = 0f;
        for (int r = 0; r < n; r++)
            for (int c = 0; c <= r; c++)
                sum += r * n + c + 1;
        return sum;
    }

    // Write N×N float matrix at `matBase` in `mem`. Element [r][c] = r*N + c + 1.
    private static void WriteMatrix(FlatMemory mem, ulong matBase, int n) {
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++) {
                float val = r * n + c + 1;
                mem.Load(matBase + (ulong)((r * n + c) * 4), BitConverter.GetBytes(val));
            }
    }

    // Build and run the lower-triangular-sum program, returning the float result.
    private static float RunLowerTriangularSum(int n) {
        const ulong codeBase   = 0x1000u;
        const ulong matBase    = 0x0200u;  // matrix data (before code, no overlap)
        const ulong resultAddr = 0x0100u;  // output float

        var mem = new FlatMemory(0x4000);
        WriteMatrix(mem, matBase, n);

        // Register plan:
        //   x1 = matBase       x5 = N*4 (row stride bytes)   x9  = resultAddr
        //   x2 = N             x6 = r+1 (inner count)        x10 = 1 (store count)
        //   x3 = r             x7 = &matrix[r][0]
        //   x4 = row_offset    x8 = 4 (element stride bytes)
        //
        // u0 = 0.0f (zero, default — used as add-zero in final store)
        // u1 = 1D load stream, reconfigured each outer iteration
        // u2 = float accumulator (scalar, updated in place)
        // u3 = 1-element output store stream at resultAddr

        // Instruction layout (word indices):
        //   [0..6]   setup
        //   [7]      outer_loop: BGE exit
        //   [8..10]  configure row
        //   [11]     inner_loop: so.a.add.fp  ← inner loop back-edge target
        //   [12]     so.b.nc (imm=-4)
        //   [13..15] advance outer loop + jump back to [7]
        //   [16..20] done: store result + ebreak

        const int outerLoopIdx = 7;
        const int innerLoopIdx = 11;
        const int doneIdx      = 16;

        // Offset of done from BGE (at index 7):  (16-7)*4 = 36
        // Offset of outer from JAL (at index 15): (7-15)*4 = -32
        uint[] words = [
            // ── Setup ────────────────────────────────────────────────────────
            Addi(1, 0, (int)matBase),    // [0]  x1 = matBase
            Addi(2, 0, n),               // [1]  x2 = N
            Slli(5, 2, 2),               // [2]  x5 = N*4
            Addi(3, 0, 0),               // [3]  r = 0
            Addi(4, 0, 0),               // [4]  row_offset = 0
            Addi(8, 0, 4),               // [5]  x8 = 4 (element stride)
            SoVDpW(2, 0),               // [6]  u2 = 0.0f  (x0 = 0 = bits(+0.0f))

            // ── Outer loop ────────────────────────────────────────────────────
            Bge(3, 2, (doneIdx - outerLoopIdx) * 4),   // [7]  if r >= N → done
            Add(7, 1, 4),                               // [8]  x7 = &matrix[r][0]
            Addi(6, 3, 1),                              // [9]  x6 = r+1
            SsLdW(1, 7, 6, 8),                         // [10] ss.ld.w u1, x7, x6, x8

            // ── Inner loop ────────────────────────────────────────────────────
            SoAAddFp(2, 1, 2),                          // [11] u2 += stream-element
            SoBNc(1, (innerLoopIdx - 12) * 4),         // [12] while !done → [11]

            // ── Outer loop tail ───────────────────────────────────────────────
            Addi(3, 3, 1),                              // [13] r++
            Add(4, 4, 5),                               // [14] row_offset += N*4
            Jal(0, (outerLoopIdx - 15) * 4),            // [15] → outer_loop [7]

            // ── Epilogue: store accumulator to result address ──────────────────
            Addi(9,  0, (int)resultAddr),               // [16] x9 = resultAddr
            Addi(10, 0, 1),                             // [17] x10 = 1
            SsStW(3, 9, 10, 8),                        // [18] ss.st.w u3, x9, x10, x8
            SoAAddFp(3, 2, 0),                          // [19] u3 = u2 + u0 → writes sum
            EBreak(),                                   // [20] halt
        ];

        for (var i = 0; i < words.Length; i++)
            mem.Load(codeBase + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(new Rv32Mechanism(), mem, entryPoint: codeBase,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32);
        train.Run(maxTicks: 10_000);

        uint raw = (uint)mem.Read(resultAddr, 4);
        return BitConverter.Int32BitsToSingle((int)raw);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void LowerTriangularSum_CorrectResult(int n) {
        float expected = ExpectedSum(n);
        float actual   = RunLowerTriangularSum(n);
        Assert.Equal(expected, actual, precision: 3);
    }
}
