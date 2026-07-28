namespace Orrery.Cache;

/// <summary>Compressed-cache-line encodings, matching Table 2 of Pekhimenko et al., PACT 2012.</summary>
public enum BdiEncoding {
    Zeros,
    RepValues,
    Base8Delta1,
    Base8Delta2,
    Base8Delta4,
    Base4Delta1,
    Base4Delta2,
    Base2Delta1,
    NoCompr,
}

/// <summary>
///     <paramref name="Data" />.Length is the compressed size in bytes (what the paper's Table 2
///     calls the encoding's "Size" — used directly for capacity/eviction accounting by
///     <see cref="BdiCache" />).
/// </summary>
public readonly record struct BdiResult(BdiEncoding Encoding, byte[] Data);

/// <summary>
///     Base-Delta-Immediate (BΔI) compression (Pekhimenko, Seshadri, Mutlu, Kozuch, Gibbons &amp;
///     Mowry, PACT 2012). Views a cache line as <c>n = C/k</c> signed elements of size
///     <c>k ∈ {8, 4, 2}</c> bytes and looks for the smallest representation among: an all-zero
///     line (1 byte), a single repeated 8-byte value (8 bytes), or a base+delta encoding for each
///     candidate (k, Δ) pair with Δ &lt; k (Δ ∈ {1, 2, 4} as applicable) — falling back to storing
///     the line uncompressed.
///     <para>
///         <strong>Two-base (BΔI) refinement over plain Base+Delta (Section 4.2):</strong> for a
///         given (k, Δ), each element is first tested against an <em>implicit zero base</em>
///         (does the raw value itself fit in Δ signed bytes?). Only elements that fail this get
///         tested against a second, <em>real</em> base — the first element that failed the
///         zero-base test — computing Δᵢ = valueᵢ − base. The line compresses at this (k, Δ) iff
///         every element passes one test or the other.
///     </para>
///     <para>
///         <strong>Deliberate deviation from Table 2's literal byte counts</strong>: Table 2 gives
///         encoding sizes as exactly <c>k + n·Δ</c> with no room for a per-element base-selection
///         bit, even though Section 5.1 states the two-base design "stores a bit mask, 1-bit per
///         element indicating whether or not the corresponding base is zero." When every element
///         in a line resolves to the <em>same</em> base (all-zero-base, as in the paper's own
///         Figure 3 h264ref example, or all-real-base, as in its Figure 4 perlbench example) no
///         mask is needed and this implementation reproduces Table 2's number exactly — both
///         worked examples in the paper happen to be uniform-base lines, which is why they're
///         used here as byte-exact oracle tests. When a line genuinely mixes zero-base and
///         real-base elements, this implementation adds <c>⌈n/8⌉</c> mask bytes so
///         <see cref="Decompress" /> can actually reconstruct it — a size Table 2 doesn't itemize
///         at all. <see cref="Decompress" /> infers mask presence from <c>Data.Length</c> alone
///         (it's exactly one of two possible values for a given (encoding, line size)), rather
///         than a separate stored flag.
///     </para>
///     <para>
///         The mcf example in Figure 5 needs a <em>second arbitrary</em> base (not zero) to
///         compress — the paper's own "B+Δ with two arbitrary bases" variant, which it explicitly
///         does <em>not</em> adopt for BΔI (Section 4.2: two arbitrary bases cost more storage for
///         only a marginal average-case win). This compressor therefore correctly reports that
///         line as <see cref="BdiEncoding.NoCompr" /> at k=4 — a documented non-goal, not a bug.
///     </para>
/// </summary>
public static class BdiCompressor {
    private static readonly (int K, int Delta, BdiEncoding Encoding)[] BaseDeltaCandidates = [
        (8, 1, BdiEncoding.Base8Delta1), (8, 2, BdiEncoding.Base8Delta2), (8, 4, BdiEncoding.Base8Delta4),
        (4, 1, BdiEncoding.Base4Delta1), (4, 2, BdiEncoding.Base4Delta2),
        (2, 1, BdiEncoding.Base2Delta1),
    ];

    /// <summary>Compresses <paramref name="line" /> (length must be a multiple of 8) to its smallest valid encoding.</summary>
    public static BdiResult Compress(ReadOnlySpan<byte> line) {
        if (line.Length == 0 || line.Length % 8 != 0)
            throw new ArgumentException("Line length must be a positive multiple of 8.", nameof(line));

        if (IsAllZero(line)) return new BdiResult(BdiEncoding.Zeros, new byte[1]);

        BdiResult best = new(BdiEncoding.NoCompr, line.ToArray());

        if (TryRepeatedValue(line, out byte[]? repData) && repData!.Length < best.Data.Length)
            best = new BdiResult(BdiEncoding.RepValues, repData);

        foreach ((int k, int delta, BdiEncoding encoding) in BdiCompressor.BaseDeltaCandidates)
            if (TryBaseDelta(line, k, delta, out byte[]? data) && data!.Length < best.Data.Length)
                best = new BdiResult(encoding, data);

        return best;
    }

    /// <summary>
    ///     Reconstructs the original line into <paramref name="destination" /> (its length fixes
    ///     the original line size). Exact inverse of <see cref="Compress" /> for any non-NoCompr
    ///     result it can produce.
    /// </summary>
    public static void Decompress(BdiEncoding encoding, ReadOnlySpan<byte> data, Span<byte> destination) {
        switch (encoding) {
            case BdiEncoding.Zeros:
                destination.Clear();
                return;
            case BdiEncoding.RepValues:
                for (var off = 0; off < destination.Length; off += 8) data[..8].CopyTo(destination[off..]);
                return;
            case BdiEncoding.NoCompr:
                data.CopyTo(destination);
                return;
        }

        (int k, int delta) = EncodingParams(encoding);
        int n = destination.Length / k;
        int maskBytes = (n + 7) / 8;
        bool hasMask = data.Length == k + n * delta + maskBytes;

        long baseVal = ReadSigned(data, 0, k);
        for (var i = 0; i < n; i++) {
            long d = ReadSigned(data, k + i * delta, delta);
            bool zeroBase = hasMask && (data[k + n * delta + i / 8] & (1 << (i % 8))) != 0;
            long value = zeroBase ? d : baseVal + d;
            WriteSigned(destination[(i * k)..], value, k);
        }
    }

    private static (int K, int Delta) EncodingParams(BdiEncoding encoding) => encoding switch {
        BdiEncoding.Base8Delta1 => (8, 1),
        BdiEncoding.Base8Delta2 => (8, 2),
        BdiEncoding.Base8Delta4 => (8, 4),
        BdiEncoding.Base4Delta1 => (4, 1),
        BdiEncoding.Base4Delta2 => (4, 2),
        BdiEncoding.Base2Delta1 => (2, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };

    // ── Base+Delta with an implicit zero base (BΔI's two-base refinement, Section 4.2) ─────────

    private static bool TryBaseDelta(ReadOnlySpan<byte> line, int k, int delta, out byte[]? data) {
        int n = line.Length / k;
        Span<long> values = stackalloc long[n];
        Span<bool> zeroOk = stackalloc bool[n];
        var allZeroOk = true;
        for (var i = 0; i < n; i++) {
            values[i] = ReadSigned(line, i * k, k);
            zeroOk[i] = FitsSigned(values[i], delta);
            if (!zeroOk[i]) allZeroOk = false;
        }

        if (allZeroOk) {
            data = new byte[k + n * delta];
            WriteSigned(data, 0L, k);
            for (var i = 0; i < n; i++) WriteSigned(data.AsSpan(k + i * delta), values[i], delta);
            return true;
        }

        var baseIdx = 0;
        while (zeroOk[baseIdx]) baseIdx++;
        long baseVal = values[baseIdx];

        var anyZeroOk = false;
        for (var i = 0; i < n; i++) {
            if (zeroOk[i]) {
                anyZeroOk = true;
                continue;
            }

            if (!FitsSigned(values[i] - baseVal, delta)) {
                data = null;
                return false;
            }
        }

        if (!anyZeroOk) {
            // Uniform real-base line (e.g. the paper's Figure 4): no mask needed.
            data = new byte[k + n * delta];
            WriteSigned(data, baseVal, k);
            for (var i = 0; i < n; i++) WriteSigned(data.AsSpan(k + i * delta), values[i] - baseVal, delta);
            return true;
        }

        int maskBytes = (n + 7) / 8;
        data = new byte[k + n * delta + maskBytes];
        WriteSigned(data, baseVal, k);
        for (var i = 0; i < n; i++) {
            long d = zeroOk[i] ? values[i] : values[i] - baseVal;
            WriteSigned(data.AsSpan(k + i * delta), d, delta);
            if (zeroOk[i]) data[k + n * delta + i / 8] |= (byte)(1 << (i % 8));
        }

        return true;
    }

    private static bool TryRepeatedValue(ReadOnlySpan<byte> line, out byte[]? data) {
        ReadOnlySpan<byte> first = line[..8];
        for (var off = 8; off < line.Length; off += 8)
            if (!line.Slice(off, 8).SequenceEqual(first)) {
                data = null;
                return false;
            }

        data = first.ToArray();
        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> line) {
        foreach (byte b in line)
            if (b != 0)
                return false;
        return true;
    }

    // ── Sign-extended little-endian read/write over 1/2/4/8-byte fields ─────────────────────────

    private static long ReadSigned(ReadOnlySpan<byte> src, int offset, int size) {
        long v = 0;
        for (var i = 0; i < size; i++) v |= (long)src[offset + i] << (8 * i);
        int shift = 64 - size * 8;
        return (v << shift) >> shift;
    }

    private static void WriteSigned(Span<byte> dst, long value, int size) {
        for (var i = 0; i < size; i++) dst[i] = (byte)(value >> (8 * i));
    }

    /// <summary>True if the low <paramref name="size" /> bytes of <paramref name="value" />, sign-extended, reproduce it.</summary>
    private static bool FitsSigned(long value, int size) {
        int shift = 64 - size * 8;
        return (value << shift) >> shift == value;
    }
}
