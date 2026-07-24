#region

using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Sparse·dense dot product (one CSR row): result = Σ val[k] · x[col[k]].
///     <para>
///         The gather x[col[k]] is expressed entirely in stream descriptors via an
///         indirect dimension modifier (ss.app.ind): an IndSource stream (u2) delivers
///         the column byte-offsets, and each element of the gathered stream (u3) is
///         fetched at xBase + offset with {Offset, Set} applied per inner-dim wrap.
///         The scalar core never sees an index — no load of col[k], no shift, no add,
///         no dependent load. The loop body is the same two instructions as any dense
///         UVE kernel:
///     </para>
///     <para>
///         loop: so.a.mac.fp u4, u1, u3   ; acc += val[k] * x[col[k]]
///         so.b.nc     u1, loop
///     </para>
///     <para>
///         This is the irregular-access pattern the UVE paper motivates indirect
///         stream modifiers with: the memory-indirection chain (load index → compute
///         address → load data) moves off the critical path into the streaming engine,
///         which resolves it ahead of the consuming MAC.
///         IndSource values are element indices; the engine scales them by the element
///         width (col[k] · 4 bytes for floats), so the index array holds plain col[k].
///     </para>
/// </summary>
public class SparseDotProductTests {
    // ── Encode helpers ────────────────────────────────────────────────────────

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | (0 << 12) | ((rd & 0x1F) << 7) | 0x13u);

    private static uint EBreak() => 0x00100073u;

    // SS.STA.LD.W ud, rs1 — funct2=0, funct3=0b110 (load, ew=4)
    private static uint SsStaLdW(int ud, int rs1) =>
        (uint)(((rs1 & 0x1F) << 15) | (0x6u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // SS.STA.LD.W_INDS ud, rs1 — IndSource stream: rs2 field bit[4]=1
    private static uint SsStaLdWInds(int ud, int rs1) =>
        (0x10u << 20) | ((uint)(rs1 & 0x1F) << 15) | (0x6u << 12) | ((uint)(ud & 0x1F) << 7) | 0x0Bu;

    // SS.STA.ST.W ud, rs1 — funct2=0, funct3=0b010 (store, ew=4)
    private static uint SsStaStW(int ud, int rs1) =>
        (uint)(((rs1 & 0x1F) << 15) | (0x2u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // SS.APP ud, rs1Offset, rs2Count, rs3Stride — funct2=1, funct3=0
    private static uint SsApp(int ud, int rs1Offset, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | (0x1u << 25) | (uint)((rs2 & 0x1F) << 20)
             | (uint)((rs1Offset & 0x1F) << 15) | (0x0u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // SS.APP.IND ud, indSrcUd — funct2=1, funct3=6.
    // rs1 field = IndSource UVE register number; rs2 bits[1:0]=target (2=Offset),
    // rs2 bits[4:2]=behavior (4=Set); rs3 bits[4:1]=Spike dim index (outermost=0).
    private static uint SsAppInd(int ud, int indSrcUd, int spikeDim) {
        const uint rs2 = (4u << 2) | 2u; // behavior=Set, target=Offset
        uint rs3 = (uint)(spikeDim & 0xF) << 1;
        return (rs3 << 27) | (0x1u << 25) | (rs2 << 20)
             | ((uint)(indSrcUd & 0x1F) << 15) | (0x6u << 12) | ((uint)(ud & 0x1F) << 7) | 0x0Bu;
    }

    // SS.END ud, rs1Offset, rs2Count, rs3Stride — funct2=2, funct3=0
    private static uint SsEnd(int ud, int rs1Offset, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | (0x2u << 25) | (uint)((rs2 & 0x1F) << 20)
             | (uint)((rs1Offset & 0x1F) << 15) | (0x0u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // SO.V.DP.W ud, rs1 — custom-1, funct7=0x56, funct3=2
    private static uint SoVDpW(int ud, int rs1) =>
        (uint)((0x56u << 25) | ((rs1 & 0x1F) << 15) | (0x2u << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu);

    // SO.A.ADD.FP ud, usrc1, usrc2 — (funct7>>3, funct3)=(0,1)
    private static uint SoAAddFp(int ud, int usrc1, int usrc2) =>
        (uint)(((usrc2 & 0x1F) << 20) | ((usrc1 & 0x1F) << 15)
                                      | (0x1u << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu);

    // SO.A.MAC.FP ud, usrc1, usrc2 — (funct7>>3, funct3)=(3,5)
    private static uint SoAMacFp(int ud, int usrc1, int usrc2) =>
        (3u << 3 << 25) | ((uint)(usrc2 & 0x1F) << 20) | ((uint)(usrc1 & 0x1F) << 15)
      | (0x5u << 12) | ((uint)(ud & 0x1F) << 7) | 0x2Bu;

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

    // ── Test ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new[] { 5, 0, 3, 6, }, new[] { 2.0f, 3.0f, 0.5f, 1.25f, })]
    [InlineData(new[] { 7, 7, 7, }, new[] { 1.0f, 1.0f, 1.0f, })] // repeated column
    [InlineData(new[] { 0, }, new[] { 4.0f, })]                   // single element
    [InlineData(new[] { 3, 1, 4, 1, 5, 2, 6, 0, }, new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, })]
    public void SparseDot_IndirectGatherStream_CorrectResult(int[] cols, float[] vals) {
        const ulong xBase = 0x0100u;   // dense vector (8 floats)
        const ulong valBase = 0x0200u; // sparse values
        const ulong colBase = 0x0300u; // column indices (element-scaled by the engine)
        const ulong resultAddr = 0x0400u;
        const ulong codeBase = 0x1000u;

        int m = cols.Length;
        float[] x = [10f, 20f, 30f, 40f, 50f, 60f, 70f, 80f,];
        float expected = cols.Zip(vals, (c, v) => v * x[c]).Sum();

        var mem = new FlatMemory(0x2000);
        for (var i = 0; i < x.Length; i++) mem.Load(xBase + (ulong)(i * 4), BitConverter.GetBytes(x[i]));
        for (var k = 0; k < m; k++) {
            mem.Load(valBase + (ulong)(k * 4), BitConverter.GetBytes(vals[k]));
            mem.Load(colBase + (ulong)(k * 4), BitConverter.GetBytes((uint)cols[k]));
        }

        // Register plan: x1=valBase  x2=colBase  x3=xBase  x4=M  x5=4  x7=1  x9=resultAddr
        //
        // UVE register plan:
        //   u1 = val load stream (1D)
        //   u2 = IndSource stream over the column offsets (consumed by the engine only)
        //   u3 = gathered x stream: outer dim count=M over an innermost dim count=1
        //        carrying an {Offset, Set} indirect modifier — each element fetched
        //        at xBase + col-offset
        //   u4 = accumulator   u5 = 1-element result store stream

        uint[] words = [
            Addi(1, 0, (int)valBase),    // [0]
            Addi(2, 0, (int)colBase),    // [1]
            Addi(3, 0, (int)xBase),      // [2]
            Addi(4, 0, m),               // [3]  x4 = M
            Addi(5, 0, 1),               // [4]  x5 = 1 (element stride → 4 bytes after scaling)
            Addi(7, 0, 1),               // [5]  x7 = 1
            Addi(9, 0, (int)resultAddr), // [6]

            // u2: IndSource over col offsets — configured first so it primes ahead
            SsStaLdWInds(2, 2), // [7]
            SsEnd(2, 0, 4, 5),  // [8]  count=M, stride=1 elem; activate

            // u3: gathered x — 2D (config outermost-first), innermost count=1 re-based
            // per element from u2
            SsStaLdW(3, 3),    // [9]  base = xBase
            SsApp(3, 0, 4, 0), // [10] outer: count=M, stride=0
            SsAppInd(3, 2, 1), // [11] {Offset, Set} from u2 on the innermost (Spike dim 1 of 2)
            SsEnd(3, 0, 7, 0), // [12] innermost: count=1, stride=0; activate

            // u1: sparse values, plain 1D
            SsStaLdW(1, 1),    // [13]
            SsEnd(1, 0, 4, 5), // [14] count=M, stride=1 elem; activate

            // ── The entire kernel ─────────────────────────────────────────────
            SoVDpW(4, 0),      // [15] u4 = 0.0
            SoAMacFp(4, 1, 3), // [16] loop: u4 += val[k] * x[col[k]]
            SoBNc(1, -4),      // [17] until exhausted → [16]

            // Result: 1-element store stream
            SsStaStW(5, 9),    // [18]
            SsEnd(5, 0, 7, 5), // [19] count=1, stride=1 elem; activate
            SoAAddFp(5, 4, 0), // [20] u5 ← u4 + 0 → writes result
            EBreak(),          // [21]
        ];

        for (var i = 0; i < words.Length; i++) mem.Load(codeBase + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooTrain(
            new Rv32Mechanism(), mem, codeBase,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(10_000);

        float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(resultAddr, 4));
        Assert.Equal(expected, actual, 3);
    }
}