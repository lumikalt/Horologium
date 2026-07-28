#region

using Orrery.Cache;

#endregion

namespace Tests.Orrery;

/// <summary>
///     Unit tests for BdiCompressor (Pekhimenko et al., PACT 2012). Includes byte-exact
///     reproductions of the paper's own worked examples (Figures 3 and 4) as oracle tests.
/// </summary>
public sealed class BdiCompressorTests {
    private static byte[] BuildLine(params long[] values32) {
        var line = new byte[values32.Length * 4];
        for (var i = 0; i < values32.Length; i++) {
            uint v = unchecked((uint)values32[i]);
            for (var b = 0; b < 4; b++) line[i * 4 + b] = (byte)(v >> (8 * b));
        }

        return line;
    }

    // ── Basic patterns ────────────────────────────────────────────────────────

    [Fact]
    public void AllZeroLine_CompressesToZeros() {
        var line = new byte[32];
        BdiResult result = BdiCompressor.Compress(line);
        Assert.Equal(BdiEncoding.Zeros, result.Encoding);
        Assert.Single(result.Data);

        var restored = new byte[32];
        BdiCompressor.Decompress(result.Encoding, result.Data, restored);
        Assert.Equal(line, restored);
    }

    [Fact]
    public void RepeatedEightByteValue_CompressesToRepValues() {
        var line = new byte[32];
        for (var off = 0; off < 32; off += 8)
            for (var b = 0; b < 8; b++)
                line[off + b] = (byte)(0xA0 + b);
        BdiResult result = BdiCompressor.Compress(line);
        Assert.Equal(BdiEncoding.RepValues, result.Encoding);
        Assert.Equal(8, result.Data.Length);

        var restored = new byte[32];
        BdiCompressor.Decompress(result.Encoding, result.Data, restored);
        Assert.Equal(line, restored);
    }

    [Fact]
    public void FourDistinctWideValues_Incompressible() {
        // Hand-verified: four 8-byte values (0x1111.., 0x2222.., 0x3333.., 0x4444..) each fail
        // every (k, Δ) candidate — as 8-byte elements the deltas (~0x11111111) exceed the 4-byte
        // Δ max; as 4-/2-byte elements the repeating-nibble halves still diverge from any shared
        // base by more than the Δ range allows. Not RepValues (chunks differ) or Zeros.
        var line = new byte[32];
        for (var block = 0; block < 4; block++) {
            byte nibble = (byte)(0x11 * (block + 1));
            for (var b = 0; b < 8; b++) line[block * 8 + b] = nibble;
        }

        BdiResult result = BdiCompressor.Compress(line);
        Assert.Equal(BdiEncoding.NoCompr, result.Encoding);
        Assert.Equal(32, result.Data.Length);
    }

    // ── Paper's own worked examples (byte-exact oracles) ─────────────────────

    [Fact]
    public void Figure3_H264ref_CompressesExactlyAsInThePaper() {
        // Base=0 (the first element already is zero), 8 elements all fit within 1 signed byte
        // of zero — a uniform zero-base line, so no mask is needed and the size matches Table 2
        // exactly: 4 (base) + 8*1 (deltas) = 12 bytes.
        byte[] line = BdiCompressorTests.BuildLine(0, 0xB, 3, 1, 4, 0, 3, 4);
        BdiResult result = BdiCompressor.Compress(line);
        Assert.Equal(BdiEncoding.Base4Delta1, result.Encoding);
        Assert.Equal(12, result.Data.Length);
        Assert.Equal(
            new byte[] { 0, 0, 0, 0, 0x00, 0x0B, 0x03, 0x01, 0x04, 0x00, 0x03, 0x04, },
            result.Data
        );

        var restored = new byte[32];
        BdiCompressor.Decompress(result.Encoding, result.Data, restored);
        Assert.Equal(line, restored);
    }

    [Fact]
    public void Figure4_Perlbench_CompressesExactlyAsInThePaper() {
        // Base=0xC04039C0 (the first element; none fit near zero), all 8 fit within 1 signed
        // byte of that base — a uniform real-base line, so again no mask: 4 + 8 = 12 bytes.
        byte[] line = BdiCompressorTests.BuildLine(
            0xC04039C0, 0xC04039C8, 0xC04039D0, 0xC04039D8, 0xC04039E0, 0xC04039E8, 0xC04039F0, 0xC04039F8
        );
        BdiResult result = BdiCompressor.Compress(line);
        Assert.Equal(BdiEncoding.Base4Delta1, result.Encoding);
        Assert.Equal(12, result.Data.Length);
        Assert.Equal(
            new byte[] {
                0xC0, 0x39, 0x40, 0xC0, 0x00, 0x08, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38,
            },
            result.Data
        );

        var restored = new byte[32];
        BdiCompressor.Decompress(result.Encoding, result.Data, restored);
        Assert.Equal(line, restored);
    }

    [Fact]
    public void Figure5_Mcf_RoundTripsRegardlessOfEncoding() {
        // This line needs TWO ARBITRARY bases to reach the paper's cited 19-byte compression
        // (Section 4.1) — the "B+Δ with two arbitrary bases" variant BΔI deliberately does not
        // implement (Section 4.2 keeps the second base fixed at zero, trading a little
        // compressibility for much less hardware complexity). We don't assert BΔI reproduces
        // that 19-byte result, or even that it finds any encoding at all for this specific line —
        // only that whatever Compress() decides is faithfully reversible.
        byte[] line = BdiCompressorTests.BuildLine(
            0, 0x09A40178, 0xB, 1, 0x09A4A838, 0xA, 0xB, 0x09A4C2F0
        );
        BdiResult result = BdiCompressor.Compress(line);
        var restored = new byte[32];
        BdiCompressor.Decompress(result.Encoding, result.Data, restored);
        Assert.Equal(line, restored);
    }

    // ── Mixed zero-base / real-base line (needs the per-element mask) ───────

    [Fact]
    public void MixedBaseLine_UsesMaskAndRoundTrips() {
        // Element0 and element2 are small enough to compress against an implicit zero base;
        // element1 (huge) becomes the real base; element3 compresses against that real base.
        // A uniform-base encoding cannot represent this line — the mask is load-bearing.
        byte[] line = BdiCompressorTests.BuildLine(2, 0xC0403000, 5, 0xC0403008);
        BdiResult result = BdiCompressor.Compress(line);
        Assert.Equal(BdiEncoding.Base4Delta1, result.Encoding);
        // 4 (base) + 4*1 (deltas) + 1 (mask, ⌈4/8⌉) = 9 bytes — one more than Table 2's
        // undifferentiated k+nΔ formula, because this line genuinely needs the mask.
        Assert.Equal(9, result.Data.Length);

        var restored = new byte[16];
        BdiCompressor.Decompress(result.Encoding, result.Data, restored);
        Assert.Equal(line, restored);
    }

    // ── Round-trip property, fuzzed ───────────────────────────────────────────

    [Fact]
    public void RandomLines_RoundTripWheneverNotNoCompr() {
        ulong x = 88172645463325252UL; // xorshift seed
        for (var trial = 0; trial < 5000; trial++) {
            var line = new byte[32];
            for (var i = 0; i < 32; i++) {
                x ^= x << 13;
                x ^= x >> 7;
                x ^= x << 17;
                line[i] = (byte)x;
            }

            BdiResult result = BdiCompressor.Compress(line);
            var restored = new byte[32];
            BdiCompressor.Decompress(result.Encoding, result.Data, restored);
            Assert.True(
                line.AsSpan().SequenceEqual(restored),
                $"round-trip failed for encoding {result.Encoding} on trial {trial}"
            );
            Assert.True(result.Data.Length <= 32, $"compressed size {result.Data.Length} exceeds line size");
        }
    }

    [Fact]
    public void Compress_WrongLength_Throws() {
        Assert.Throws<ArgumentException>(() => BdiCompressor.Compress(new byte[7]));
        Assert.Throws<ArgumentException>(() => BdiCompressor.Compress(Array.Empty<byte>()));
    }
}
