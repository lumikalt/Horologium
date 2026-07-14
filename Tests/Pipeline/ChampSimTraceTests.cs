using System.IO.Compression;
using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;
using RiscV32.Config;
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

    // ── Coverage of every built-in --champsim-predictor ─────────────────────────

    public static TheoryData<string> AllChampSimPredictorNames => [
        "always_taken", "always_not_taken", "n_bit", "correlated", "gselect", "gshare", "l_tage",
        "perceptron", "tournament", "tage_sc_l", "hashed_perceptron", "ittage", "batage", "imli",
    ];

    public static TheoryData<string> AdaptiveChampSimPredictorNames => [
        "n_bit", "correlated", "gselect", "gshare", "l_tage", "perceptron", "tournament",
        "tage_sc_l", "hashed_perceptron", "ittage", "batage", "imli",
    ];

    // Mirrors src/Apps/Runner/Program.cs's ResolveChampSimPredictor mapping for --champsim-predictor;
    // kept in sync manually since Program.cs's top-level-statement local function isn't a public API.
    private static IBranchPredictor ResolveChampSimPredictor(string name) => (name switch {
        "always_taken"      => BranchPredictorConfig.AlwaysTaken(),
        "always_not_taken"  => BranchPredictorConfig.AlwaysNotTaken(),
        "n_bit"             => BranchPredictorConfig.NBit(),
        "correlated"        => BranchPredictorConfig.Correlated(),
        "gselect"           => BranchPredictorConfig.Gselect(),
        "gshare"            => BranchPredictorConfig.Gshare(),
        "l_tage"            => BranchPredictorConfig.LTage(),
        "perceptron"        => BranchPredictorConfig.Perceptron(),
        "tournament"        => BranchPredictorConfig.Tournament(),
        "tage_sc_l"         => BranchPredictorConfig.TageScL(),
        "hashed_perceptron" => BranchPredictorConfig.HashedPerceptron(),
        "ittage"            => BranchPredictorConfig.Ittage(),
        "batage"            => BranchPredictorConfig.Batage(),
        "imli"              => BranchPredictorConfig.Imli(),
        _                   => throw new ArgumentException($"Unknown predictor '{name}'."),
    }).Build();

    // A single-instruction countdown loop (branch at a fixed PC, taken tripCount-1 times then not
    // taken to exit), repeated `invocations` times with a non-branch filler after each exit so every
    // branch — including the trace's very last one — is resolved. Simple and highly regular on
    // purpose: this checks that the replay wiring works for every predictor, not predictor accuracy.
    // tripCount is kept below gshare's default 8-bit history length: a period >= the history length
    // makes even a perfectly-learned gshare alias two loop positions onto the same history bucket
    // (e.g. period 10 aliases the 9th and 10th iteration's "all taken so far" histories), which is
    // an inherent capacity limit of that predictor, not something this test should be probing.
    private static ChampSimTraceRecord[] LoopTrace(int invocations = 30, int tripCount = 5) {
        const ulong loopPc = 0x2000;
        const ulong exitPc = 0x2004;
        var recs = new List<ChampSimTraceRecord>();
        for (var inv = 0; inv < invocations; inv++) {
            for (var i = 0; i < tripCount - 1; i++)
                recs.Add(new ChampSimTraceRecord(loopPc, true, true, [], [], [], []));
            recs.Add(new ChampSimTraceRecord(loopPc, true, false, [], [], [], []));
            recs.Add(new ChampSimTraceRecord(exitPc, false, false, [], [], [], []));
        }

        return [.. recs,];
    }

    [Theory]
    [MemberData(nameof(AllChampSimPredictorNames))]
    public void Replay_AllChampSimPredictors_ProduceWellFormedStats(string name) {
        IBranchPredictor predictor = ResolveChampSimPredictor(name);
        ChampSimTraceRecord[] recs = LoopTrace();

        ChampSimReplayResult result = ChampSimTraceReplayer.Replay(recs, predictor);

        Assert.Equal(recs.Length, result.Instructions);
        Assert.Equal(150, result.Branches); // 30 invocations * 5 branches/invocation
        Assert.InRange(result.Mispredictions, 0, result.Branches);
        Assert.InRange(result.MispredictionRate, 0.0, 1.0);
        Assert.True(double.IsFinite(result.Mpki) && result.Mpki >= 0.0);
    }

    [Theory]
    [MemberData(nameof(AdaptiveChampSimPredictorNames))]
    public void Replay_AdaptiveChampSimPredictors_LearnSimpleLoopPattern(string name) {
        IBranchPredictor predictor = ResolveChampSimPredictor(name);
        ChampSimTraceRecord[] recs = LoopTrace();

        ChampSimReplayResult result = ChampSimTraceReplayer.Replay(recs, predictor);

        // Loose bound rather than a tight convergence target: some of these predictors have simple
        // shared-slot BTB designs (e.g. NBitPredictor.Update overwrites its one target slot on every
        // outcome, taken or not) that make a single self-looping branch PC with two distinct actual
        // targets a legitimately harder case than the direction-only pattern suggests — asserting a
        // tight numeric threshold here would be testing 14 different architectures' quirks rather
        // than the ChampSim replay wiring. This only checks that the predictor is doing *something*
        // adaptive, well below the naive ~80% (wrong on all 4 taken branches every cycle) that a
        // static not-taken predictor gets on this trace.
        Assert.True(
            result.MispredictionRate < 0.5,
            $"{name}: misprediction rate {result.MispredictionRate:P1} too high for a simple repeating loop"
        );
    }
}