using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Lower-triangular sum of an N×N float matrix — the same kernel as
///     <see cref="LowerTriangularSumTests" />, but idiomatic UVE: the triangle is
///     described by a single stream configured once, using a static dimension
///     modifier (ss.app.mod) that grows the inner count by 1 each time the row
///     dimension wraps.
///     <para>
///         The existing example rebuilds the stream from scalar code on every row
///         (~10 scalar instructions per outer iteration). Here the whole kernel after
///         configuration is two instructions:
///     </para>
///     <para>
///         loop: so.a.add.fp u2, u1, u2   ; acc += next triangle element
///         so.b.nc     u1, loop     ; until the stream is exhausted
///     </para>
///     <para>
///         Stream shape (configured outermost-first): rows: count=N, stride=N (elements); row
///         elements (innermost, added by ss.end): count=1 initially, stride=1 (element), with
///         modifier {Size, Inc, +1} → rows deliver 1, 2, 3, …, N elements. This is
///         precisely the pattern the UVE paper cites as motivation for descriptor
///         modifiers.
///     </para>
/// </summary>
public class TriangularSumModifierTests {
    // ── Encode helpers ────────────────────────────────────────────────────────

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | (0 << 12) | ((rd & 0x1F) << 7) | 0x13u);

    private static uint EBreak() => 0x00100073u;

    // SS.STA.LD.W ud, rs1 — funct2=0, funct3=0b110 (load, ew=4)
    private static uint SsStaLdW(int ud, int rs1) =>
        (uint)(((rs1 & 0x1F) << 15) | (0x6u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // SS.STA.ST.W ud, rs1 — funct2=0, funct3=0b010 (store, ew=4)
    private static uint SsStaStW(int ud, int rs1) =>
        (uint)(((rs1 & 0x1F) << 15) | (0x2u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // SS.APP ud, rs1Offset, rs2Count, rs3Stride — funct2=1, funct3=0
    private static uint SsApp(int ud, int rs1Offset, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | (0x1u << 25) | (uint)((rs2 & 0x1F) << 20)
             | (uint)((rs1Offset & 0x1F) << 15) | (0x0u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // SS.END ud, rs1Offset, rs2Count, rs3Stride — funct2=2, funct3=0
    private static uint SsEnd(int ud, int rs1Offset, int rs2, int rs3) =>
        (uint)(((rs3 & 0x1F) << 27) | (0x2u << 25) | (uint)((rs2 & 0x1F) << 20)
             | (uint)((rs1Offset & 0x1F) << 15) | (0x0u << 12) | (uint)((ud & 0x1F) << 7) | 0x0Bu);

    // SS.APP.MOD ud (UVE2) — funct2=1 (APP), funct3=4 (MOD), b[24:22]=behavior, ta[21:20]=target
    // (Size=0, Stride=1, Offset=2), tdim[17:15]=target dim (outermost-first), rs3=displacement register.
    // The trigger dimension is positional: the most recently appended dimension.
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

    // SO.V.DP.W ud, rs1 — custom-1, funct7=0x56, funct3=2
    private static uint SoVDpW(int ud, int rs1) =>
        (uint)((0x56u << 25) | ((rs1 & 0x1F) << 15) | (0x2u << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu);

    // SO.A.ADD.FP ud, usrc1, usrc2 — (funct7>>3, funct3)=(0,1)
    private static uint SoAAddFp(int ud, int usrc1, int usrc2) =>
        (uint)(((usrc2 & 0x1F) << 20) | ((usrc1 & 0x1F) << 15)
                                      | (0x1u << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu);

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Expected lower-triangular sum for an N×N matrix whose [r][c] = r*N + c + 1.
    private static float ExpectedSum(int n) {
        var sum = 0f;
        for (var r = 0; r < n; r++)
        for (var c = 0; c <= r; c++)
            sum += r * n + c + 1;
        return sum;
    }

    // ── Test ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    public void TriangularSum_SingleStreamWithModifier_CorrectResult(int n) {
        const ulong matBase = 0x0200u;
        const ulong resultAddr = 0x0100u;
        const ulong codeBase = 0x1000u;

        var mem = new FlatMemory(0x2000);
        for (var r = 0; r < n; r++)
        for (var c = 0; c < n; c++)
            mem.Load(matBase + (ulong)((r * n + c) * 4), BitConverter.GetBytes((float)(r * n + c + 1)));

        // Register plan: x1=matBase  x2=N  x5=1(elem stride)  x6=N(elem stride for rows)  x7=1  x9=resultAddr
        //
        // UVE register plan:
        //   u1 = triangle load stream (2D + Size/Inc modifier) — configured ONCE
        //   u2 = accumulator (scalar)
        //   u3 = 1-element result store stream

        uint[] words = [
            Addi(1, 0, (int)matBase),    // [0]  x1 = matBase
            Addi(2, 0, n),               // [1]  x2 = N
            Addi(5, 0, 1),               // [2]  x5 = 1 (element stride → 4 bytes after scaling)
            Addi(6, 2, 0),               // [3]  x6 = N (element stride for rows → N*4 bytes after scaling)
            Addi(7, 0, 1),               // [4]  x7 = 1
            Addi(9, 0, (int)resultAddr), // [5]  x9 = resultAddr

            // u1: the whole triangle in one descriptor (config outermost-first)
            SsStaLdW(1, 1),    // [6]  base = matBase
            SsApp(1, 0, 2, 6), // [7]  rows: count=N, stride=N elems
            SsAppMod(
                1, 1, StreamModifierTarget.Size, StreamModifierBehavior.Inc, 7
            ),                 // [8] innermost.count += 1 per row wrap
            SsEnd(1, 0, 7, 5), // [9]  row elements (innermost): count=1 (grows), stride=1 elem; activate

            // ── The entire kernel ─────────────────────────────────────────────
            SoVDpW(2, 0),      // [10] u2 = 0.0
            SoAAddFp(2, 1, 2), // [11] loop: u2 += triangle element
            SoBNc(1, -4),      // [12] until exhausted → [11]

            // Result: 1-element store stream
            SsStaStW(3, 9),    // [13]
            SsEnd(3, 0, 7, 5), // [14] count=1, stride=4; activate
            SoAAddFp(3, 2, 0), // [15] u3 ← u2 + 0 → writes result
            EBreak(),          // [16]
        ];

        for (var i = 0; i < words.Length; i++) mem.Load(codeBase + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, codeBase,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(10_000);

        float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(resultAddr, 4));
        Assert.Equal(ExpectedSum(n), actual, 3);
    }
}