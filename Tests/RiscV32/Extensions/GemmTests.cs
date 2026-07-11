using Pipeline;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Memory;

// ReSharper disable ShiftExpressionZeroLeftOperand

namespace Tests.RiscV32.Extensions;

/// <summary>
/// Dense GEMM (C = A·B, all N×N) with every stream configured exactly once.
/// <para>
/// This is the flagship UVE idiom: the full three-deep loop nest lives in the
/// stream descriptors, not in scalar code. All three induction variables (i, j, k)
/// exist only inside the streaming engine — the loop body is five instructions with
/// zero address arithmetic, zero loop counters, and zero per-iteration reconfiguration:
/// </para>
/// <para>
///   acc:   so.v.dp.w   u3, x0        ; acc = 0
///   kloop: so.a.mac.fp u3, u1, u2    ; acc += A[i][k] * B[k][j]
///          so.b.ndc.3  u1, kloop     ; while the k-pass (innermost dim) is not complete
///          so.a.add.fp u4, u3, u0    ; C[i][j] = acc   (u4 = store stream)
///          so.b.nc     u1, acc       ; while A stream not exhausted
/// </para>
/// <para>
/// The traversal orders are expressed with stride-0 "repeat" dimensions
/// (configured outermost-first, Spike style; ss.end adds the innermost):
///   u1 (A): i (count N, stride 4N) · j (count N, stride 0 — replay the row N times)
///           · k (count N, stride 4, innermost)
///   u2 (B): i (count N, stride 0 — replay the whole matrix) · j (count N, stride 4)
///           · k (count N, stride 4N — walk a column, innermost)
///   u4 (C): i (count N, stride 4N) · j (count N, stride 4, innermost), store stream
/// </para>
/// <para>
/// Contrast with <see cref="TrisolvTests"/> / <see cref="LowerTriangularSumTests"/>,
/// which rebuild their streams from scalar code on every outer iteration.
/// </para>
/// </summary>
public class GemmTests {
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

    // SO.V.DP.W ud, rs1 — custom-1, funct7=0x56, funct3=2
    private static uint SoVDpW(int ud, int rs1) =>
        (uint)((0x56u << 25) | ((rs1 & 0x1F) << 15) | (0x2u << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu);

    // SO.A.FP ud, usrc1, usrc2 — (funct7>>3, funct3) encodes the operation
    private static uint SoAFp(UveFpOp op, int ud, int usrc1, int usrc2) {
        (uint funct3, uint top4) = op switch {
            UveFpOp.Add => (1u, 0u),
            UveFpOp.Mac => (5u, 3u),
            _           => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        return (top4 << 3 << 25) | (uint)((usrc2 & 0x1F) << 20) | (uint)((usrc1 & 0x1F) << 15)
             | (funct3 << 12) | (uint)((ud & 0x1F) << 7) | 0x2Bu;
    }

    // UVE non-standard B-type: bits[31:29]=111, bit28=imm[12], bits[27:22]=imm[10:5],
    // bit7=imm[11], bits[11:8]=imm[4:1]. rs2=0b00001 → notDone.
    private static uint UveBTypeImm(int imm, uint rs1, uint rs2, uint funct3) {
        var i = (uint)imm;
        uint bit12 = (i >> 12) & 1,
             bit11 = (i >> 11) & 1,
             bits10To5 = (i >> 5) & 0x3F,
             bits4To1 = (i >> 1) & 0xF;
        return (0b111u << 29) | (bit12 << 28) | (bits10To5 << 22) | (rs2 << 20) | (rs1 << 15)
             | (funct3 << 12) | (bits4To1 << 8) | (bit11 << 7) | 0x2Bu;
    }

    // SO.B.NC urs, imm — branch while stream not exhausted
    private static uint SoBNc(int urs, int imm) => UveBTypeImm(imm, (uint)urs, 0b00001u, 0x0);

    // SO.B.NDC.D urs, imm — branch while dimension pass not complete.
    // funct3 = D-1 counts from the OUTERMOST dim; innermost of an N-dim stream is D=N.
    private static uint SoBNdcD(int urs, int d, int imm) => UveBTypeImm(imm, (uint)urs, 0b00001u, (uint)(d - 1));

    // ── Reference ─────────────────────────────────────────────────────────────

    private static float[] ReferenceGemm(int n, float[] a, float[] b) {
        var c = new float[n * n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++) {
            var acc = 0f;
            for (var k = 0; k < n; k++) acc += a[i * n + k] * b[k * n + j];
            c[i * n + j] = acc;
        }

        return c;
    }

    // ── Test ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Gemm_ConfigureOnce_CorrectResult(int n) {
        const ulong aBase = 0x0100u;
        const ulong bBase = 0x0300u;
        const ulong cBase = 0x0500u;
        const ulong codeBase = 0x1000u;

        var mem = new FlatMemory(0x2000);

        var a = new float[n * n];
        var b = new float[n * n];
        for (var i = 0; i < n * n; i++) {
            a[i] = i + 1.0f;
            b[i] = (i + 1) * 0.5f;
            mem.Load(aBase + (ulong)(i * 4), BitConverter.GetBytes(a[i]));
            mem.Load(bBase + (ulong)(i * 4), BitConverter.GetBytes(b[i]));
        }

        // Register plan: x1=aBase  x2=bBase  x3=cBase  x4=N  x5=4  x6=N*4
        // These integer registers exist only to feed the stream-config instructions;
        // after word [16] nothing reads them again.
        //
        // UVE register plan:
        //   u1 = A load stream (3D)   u3 = dot-product accumulator (scalar)
        //   u2 = B load stream (3D)   u4 = C store stream (2D)
        //
        // Word layout:
        //   [0..5]   scalar setup — addresses, N, element stride (4), row stride (4N)
        //   [6..16]  stream configuration — runs ONCE
        //   [17..21] the whole GEMM loop nest
        //   [22]     ebreak
        //
        // Stream configuration ([6..16]). Each stream is opened by ss.sta.{ld,st}.w
        // (base address, pending config), extended by ss.app (append next dimension:
        // count register, stride register), and closed by ss.end (append the last
        // dimension + activate). Dimensions are configured OUTERMOST-FIRST (Spike
        // convention): ss.end adds the innermost dimension.
        //
        //   u1 = A in the order  for i { for j { for k { A[i][k] } } }:
        //     i: count=N, stride=N elems  — advance one row
        //     j: count=N, stride=0        — j is absent from A's index: when k wraps,
        //                                   move 0 bytes and REPLAY the same row N times
        //     k: count=N, stride=1 elem   — one row, consecutive floats (innermost, via ss.end)
        //   u2 = B in the order  for i { for j { for k { B[k][j] } } }:
        //     i: count=N, stride=0        — replay the entire matrix for each i
        //     j: count=N, stride=1 elem   — next column
        //     k: count=N, stride=N elems  — stepping k jumps a whole row = walk a COLUMN (innermost)
        //   u4 = C store, row-major, one write per (i,j):
        //     i: count=N, stride=N elems;  j: count=N, stride=1 elem (innermost)
        //
        // The loop ([17..21]) — all three induction variables live in the streaming
        // engine as dimension odometers; no loads, no address math, no counters:
        //   [17] so.v.dp.w u3, x0     — reset accumulator (broadcast 0); top of (i,j) loop
        //   [18] so.a.mac.fp u3,u1,u2 — u3 += A·B; reading u1/u2 CONSUMES one element
        //                               from each stream and advances their odometers
        //   [19] so.b.ndc.3 u1        — loop to [18] while the innermost (k) pass is not
        //                               complete; falls through after exactly N MACs
        //   [20] so.a.add.fp u4,u3,u0 — u3 + 0.0 with a store-stream destination: writes
        //                               C[i][j] at u4's cursor and advances it
        //   [21] so.b.nc u1           — loop to [17] while u1 is not exhausted; the
        //                               engine, not a scalar counter, ends the kernel

        const int accIdx = 17;
        const int kloopIdx = 18;

        uint[] words = [
            Addi(1, 0, (int)aBase), // [0]  x1 = aBase
            Addi(2, 0, (int)bBase), // [1]  x2 = bBase
            Addi(3, 0, (int)cBase), // [2]  x3 = cBase
            Addi(4, 0, n),          // [3]  x4 = N
            Addi(5, 0, 1),          // [4]  x5 = 1 (element stride → 4 bytes after scaling)
            Addi(6, 4, 0),          // [5]  x6 = N (element stride for rows → N*4 bytes after scaling)

            // u1 = A, outermost-first: i (N,N elems) · j (N,0) · k (N,1 elem)
            SsStaLdW(1, 1),    // [6]
            SsApp(1, 0, 4, 6), // [7]  i: count=N, stride=N elems
            SsApp(1, 0, 4, 0), // [8]  j: count=N, stride=0 (replay row)
            SsEnd(1, 0, 4, 5), // [9]  k (innermost): count=N, stride=1 elem; activate

            // u2 = B, outermost-first: i (N,0) · j (N,1 elem) · k (N,N elems)
            SsStaLdW(2, 2),    // [10]
            SsApp(2, 0, 4, 0), // [11] i: count=N, stride=0 (replay matrix)
            SsApp(2, 0, 4, 5), // [12] j: count=N, stride=1 elem
            SsEnd(2, 0, 4, 6), // [13] k (innermost): count=N, stride=N elems (walk column); activate

            // u4 = C store, outermost-first: i (N,N elems) · j (N,1 elem)
            SsStaStW(4, 3),    // [14]
            SsApp(4, 0, 4, 6), // [15] i: count=N, stride=N elems
            SsEnd(4, 0, 4, 5), // [16] j (innermost): count=N, stride=1 elem; activate

            // ── The entire GEMM loop nest ─────────────────────────────────────
            SoVDpW(3, 0),                       // [17] acc: u3 = 0.0
            SoAFp(UveFpOp.Mac, 3, 1, 2),        // [18] kloop: u3 += a*b
            SoBNdcD(1, 3, (kloopIdx - 19) * 4), // [19] so.b.ndc.3: while k (innermost of 3D) not complete → [18]
            SoAFp(UveFpOp.Add, 4, 3, 0),        // [20] C[i][j] = u3 + 0
            SoBNc(1, (accIdx - 21) * 4),        // [21] while A not exhausted → [17]
            EBreak(),                           // [22]
        ];

        for (var i = 0; i < words.Length; i++) mem.Load(codeBase + (ulong)(i * 4), BitConverter.GetBytes(words[i]));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem, codeBase,
            streamPrefetchDepth: 8, robCapacity: 64, iqCapacity: 32
        );
        train.Run(50_000);

        float[] expected = ReferenceGemm(n, a, b);
        for (var i = 0; i < n * n; i++) {
            float actual = BitConverter.Int32BitsToSingle((int)(uint)mem.Read(cBase + (ulong)(i * 4), 4));
            Assert.Equal(expected[i], actual, 2);
        }
    }
}