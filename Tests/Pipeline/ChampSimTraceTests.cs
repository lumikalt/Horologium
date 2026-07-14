using System.IO.Compression;
using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
using RiscV32.Trace;

namespace Tests.Pipeline;

/// <summary>
///     ChampSim binary trace import: record parsing (raw + gzip), branch-predictor replay, and
///     cache replay.
/// </summary>
public class ChampSimTraceTests {
    // Builds one raw 64-byte ChampSim `input_instr` record.
    private static byte[] RawRecord(
        ulong ip,
        bool isBranch,
        bool taken,
        ulong[]? srcMem = null,
        ulong[]? dstMem = null
    ) {
        srcMem ??= new ulong[4];
        dstMem ??= new ulong[2];
        var buf = new byte[64];
        BitConverter.TryWriteBytes(buf.AsSpan(0), ip);
        buf[8] = (byte)(isBranch ? 1 : 0);
        buf[9] = (byte)(taken ? 1 : 0);
        // bytes 10..15 = dest/src register indices — irrelevant to replay, left zero
        for (var i = 0; i < 2; i++) BitConverter.TryWriteBytes(buf.AsSpan(16 + i * 8), dstMem[i]);
        for (var i = 0; i < 4; i++) BitConverter.TryWriteBytes(buf.AsSpan(32 + i * 8), srcMem[i]);
        return buf;
    }

    private static byte[] Concat(params byte[][] chunks) {
        int total = chunks.Sum(c => c.Length);
        var outBuf = new byte[total];
        var pos = 0;
        foreach (byte[] c in chunks) {
            Array.Copy(c, 0, outBuf, pos, c.Length);
            pos += c.Length;
        }

        return outBuf;
    }

    // ── Reader ────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadAll_ParsesIpAndBranchFields() {
        byte[] trace = Concat(
            RawRecord(0x1000, true, true),
            RawRecord(0x1004, false, false)
        );
        using var ms = new MemoryStream(trace);
        using var reader = new ChampSimTraceReader(ms);
        ChampSimTraceRecord[] recs = [.. reader.ReadAll(),];

        Assert.Equal(2, recs.Length);
        Assert.Equal(0x1000UL, recs[0].Ip);
        Assert.True(recs[0].IsBranch);
        Assert.True(recs[0].BranchTaken);
        Assert.Equal(0x1004UL, recs[1].Ip);
        Assert.False(recs[1].IsBranch);
    }

    [Fact]
    public void ReadAll_ParsesSourceAndDestinationMemory() {
        byte[] trace = RawRecord(
            0x2000, false, false,
            [0x8000, 0x8040, 0, 0,], [0x9000, 0,]
        );
        using var ms = new MemoryStream(trace);
        using var reader = new ChampSimTraceReader(ms);
        ChampSimTraceRecord rec = reader.ReadAll().Single();

        Assert.Equal((ulong[])[0x8000, 0x8040, 0, 0,], rec.SourceMemory);
        Assert.Equal((ulong[])[0x9000, 0,], rec.DestinationMemory);
    }

    [Fact]
    public void ReadAll_TransparentlyDecompressesGzip() {
        byte[] raw = Concat(
            RawRecord(0x100, true, true),
            RawRecord(0x104, false, false)
        );
        using var gz = new MemoryStream();
        using (var gzWriter = new GZipStream(gz, CompressionLevel.Fastest, true)) { gzWriter.Write(raw); }

        gz.Position = 0;

        using var reader = new ChampSimTraceReader(gz);
        ChampSimTraceRecord[] recs = [.. reader.ReadAll(),];

        Assert.Equal(2, recs.Length);
        Assert.Equal(0x100UL, recs[0].Ip);
        Assert.Equal(0x104UL, recs[1].Ip);
    }

    [Fact]
    public void ReadAll_TruncatedTrailingRecord_Throws() {
        byte[] trace = RawRecord(0x100, false, false);
        using var ms = new MemoryStream(trace[..40]); // chop a record short
        using var reader = new ChampSimTraceReader(ms);

        Assert.Throws<InvalidDataException>(() => reader.ReadAll().ToList());
    }

    [Fact]
    public void ReadAll_EmptyStream_YieldsNoRecords() {
        using var ms = new MemoryStream();
        using var reader = new ChampSimTraceReader(ms);
        Assert.Empty(reader.ReadAll());
    }

    // ── Predictor replay ─────────────────────────────────────────────────────

    [Fact]
    public void Replay_AlwaysNotTakenPredictor_MispredictsOnlyTakenBranches() {
        // Two taken branches interleaved with two not-taken, then a trailing instruction to
        // resolve the last one. AlwaysNotTakenPredictor is direction-only and keeps no state,
        // so only the taken branches (direction mismatch) count as mispredictions.
        ChampSimTraceRecord[] recs = [
            new(0x00, true, true, [], [], [], []),
            new(0x04, true, false, [], [], [], []),
            new(0x08, true, true, [], [], [], []),
            new(0x0C, true, false, [], [], [], []),
            new(0x10, false, false, [], [], [], []),
        ];

        ChampSimReplayResult result = ChampSimTraceReplayer.Replay(recs, new AlwaysNotTakenPredictor());

        Assert.Equal(5, result.Instructions);
        Assert.Equal(4, result.Branches);
        Assert.Equal(2, result.Mispredictions); // the two taken branches are wrong
    }

    [Fact]
    public void Replay_TakenBranch_TargetIsNextRecordsIp() {
        // A predictor that always predicts taken but to the wrong target (pc+4) should mispredict
        // even though direction is correct, because the actual next ip (0x100) differs.
        var wrongTargetPredictor = new FixedTargetPredictor(0x100 + 4);
        ChampSimTraceRecord[] recs = [
            new(0x100, true, true, [], [], [], []),
            new(0x200, false, false, [], [], [], []), // actual target: taken branch jumps here
        ];

        ChampSimReplayResult result = ChampSimTraceReplayer.Replay(recs, wrongTargetPredictor);
        Assert.Equal(1, result.Mispredictions);

        var rightTargetPredictor = new FixedTargetPredictor(0x200);
        result = ChampSimTraceReplayer.Replay(recs, rightTargetPredictor);
        Assert.Equal(0, result.Mispredictions);
    }

    [Fact]
    public void Replay_LastBranchInTrace_IsNotScored() {
        // No trailing record to reveal the actual target/outcome of the final branch.
        ChampSimTraceRecord[] recs = [new(0x00, true, true, [], [], [], []),];
        ChampSimReplayResult result = ChampSimTraceReplayer.Replay(recs, new AlwaysNotTakenPredictor());

        Assert.Equal(1, result.Branches);
        Assert.Equal(0, result.Mispredictions);
    }

    [Fact]
    public void Replay_NoBranches_MpkiIsZero() {
        ChampSimTraceRecord[] recs = [new(0x00, false, false, [], [], [], []),];
        ChampSimReplayResult result = ChampSimTraceReplayer.Replay(recs, new AlwaysTakenPredictor());
        Assert.Equal(0.0, result.Mpki);
        Assert.Equal(0.0, result.MispredictionRate);
    }

    private sealed class FixedTargetPredictor(ulong target) : IBranchPredictor {
        public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) =>
            BranchPrediction.Taken(target);

        public void Update(ulong pc, bool taken, ulong actualTarget) { }
    }

    // ── Cache replay ──────────────────────────────────────────────────────────

    [Fact]
    public void Replay_RepeatedAddress_HitsAfterFirstMiss() {
        var cache = new SetAssociativeCache(
            new ChampSimBackingMemory(), 256, 4, 16, 1
        );
        ChampSimTraceRecord[] recs = [
            new(0x00, false, false, [], [], [], [0x8000, 0, 0, 0,]),
            new(0x04, false, false, [], [], [], [0x8000, 0, 0, 0,]),
            new(0x08, false, false, [], [], [], [0x8000, 0, 0, 0,]),
        ];

        ChampSimReplayResult result = ChampSimTraceReplayer.Replay(recs, cache: cache);

        Assert.Equal(3, result.Loads);
        Assert.Equal(0, result.Stores);
        Assert.Equal(1, result.CacheMisses);
        Assert.Equal(2, result.CacheHits);
    }

    [Fact]
    public void Replay_ZeroAddressSlots_AreSkipped() {
        var cache = new SetAssociativeCache(
            new ChampSimBackingMemory(), 256, 4, 16, 1
        );
        // Only the first source-memory slot is used; the other three (0) must not count as accesses.
        ChampSimTraceRecord[] recs = [new(0x00, false, false, [], [], [], [0x8000, 0, 0, 0,]),];

        ChampSimReplayResult result = ChampSimTraceReplayer.Replay(recs, cache: cache);

        Assert.Equal(1, result.Loads);
        Assert.Equal(1, result.CacheMisses + result.CacheHits);
    }

    [Fact]
    public void Replay_StoreAddress_CountsAsStoreNotLoad() {
        var cache = new SetAssociativeCache(
            new ChampSimBackingMemory(), 256, 4, 16, 1
        );
        ChampSimTraceRecord[] recs = [new(0x00, false, false, [], [], [0x9000, 0,], []),];

        ChampSimReplayResult result = ChampSimTraceReplayer.Replay(recs, cache: cache);

        Assert.Equal(0, result.Loads);
        Assert.Equal(1, result.Stores);
    }
}