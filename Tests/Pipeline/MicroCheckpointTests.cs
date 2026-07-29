#region

using System.Text;
using Mechanism;
using Mechanism.BranchPred;
using Mechanism.ValuePred;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;
using RiscV32.State;

// ReSharper disable ShiftExpressionZeroLeftOperand

#endregion

namespace Tests.Pipeline;

/// <summary>
///     Tests for the microarchitectural checkpoint — caches, TLBs,
///     branch predictor, and RAS, layered on top of the architectural checkpoint (Option A,
///     see <see cref="CheckpointTests" />) and only valid at a drained pipeline boundary.
/// </summary>
public class MicroCheckpointTests {
    // addi x1,x0,100; addi x2,x0,0; addi x3,x0,20; loop: sw x2,0(x1); lw x4,0(x1);
    // addi x2,x4,1; addi x3,x3,-1; bne x3,x0,loop; ebreak
    // Same store/reload loop as SmbBypassTests — 20 iterations touching one address repeatedly,
    // enough to warm the D-cache, TLB, and branch predictor before a mid-run checkpoint.
    private static readonly uint[] LoopProgram = [
        0x06400093, // addi x1, x0, 100
        0x00000113, // addi x2, x0, 0
        0x01400193, // addi x3, x0, 20
        0x0020A023, // loop: sw x2, 0(x1)
        0x0000A203, // lw x4, 0(x1)
        0x00120113, // addi x2, x4, 1
        0xFFF18193, // addi x3, x3, -1
        0xFE0198E3, // bne x3, x0, loop
        0x00100073, // ebreak
    ];

    private static readonly MemoryConfig CacheCfg = new(
        128, 2, 16, 4,
        TlbEntries: 4, TlbPageBytes: 4096, TlbMissLatency: 3
    );

    // Small I-side cache/TLB too, so the ICACHE/ITLB checkpoint sections (otherwise only
    // exercised by the standalone Cache_RoundTrip_/Tlb_RoundTrip_ tests) get an end-to-end pass
    // through the equivalence test as well.
    private static readonly MemoryConfig ICacheCfg = new(
        128, 2, 16, 2,
        TlbEntries: 4, TlbPageBytes: 4096, TlbMissLatency: 2
    );

    private static void Load(FlatMemory mem, uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(0, bytes);
    }

    private static OooTrain MakeTrain(FlatMemory mem, ulong entryPoint = 0, bool withMicroArchTables = true) =>
        new(
            new Rv32Mechanism(), mem, entryPoint,
            robCapacity: 16, iqCapacity: 8,
            iMemConfig: withMicroArchTables ? MicroCheckpointTests.ICacheCfg : null,
            dMemConfig: withMicroArchTables ? MicroCheckpointTests.CacheCfg : null,
            predictor: withMicroArchTables ? new NBitBp(2, 64) : null
        );

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    private static BinaryWriter Writer(Stream s) => new(s, Encoding.UTF8, true);

    // ── Per-table round trips ────────────────────────────────────────────────

    [Fact]
    public void Cache_RoundTrip_TagsDataAndPolicyMatch() {
        var cacheA = new SetAssociativeCache(new FlatMemory(4096), 256, 2, 32, 5);
        cacheA.Read(0, 4);
        cacheA.Read(64, 4);
        cacheA.Write(96, 0xABCD, 4);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { cacheA.WriteState(w); }

        var cacheB = new SetAssociativeCache(new FlatMemory(4096), 256, 2, 32, 5);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { cacheB.ReadState(r); }

        CacheLine[] snapA = cacheA.GetSnapshot();
        CacheLine[] snapB = cacheB.GetSnapshot();
        Assert.Equal(snapA.Length, snapB.Length);
        for (var i = 0; i < snapA.Length; i++) {
            Assert.Equal(snapA[i].Valid, snapB[i].Valid);
            Assert.Equal(snapA[i].Tag, snapB[i].Tag);
            Assert.Equal(snapA[i].Block, snapB[i].Block);
            Assert.Equal(snapA[i].LruAge, snapB[i].LruAge);
        }
    }

    [Fact]
    public void Cache_ReadState_GeometryMismatch_Throws() {
        var cacheA = new SetAssociativeCache(new FlatMemory(4096), 256, 2, 32, 5);
        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { cacheA.WriteState(w); }

        var cacheB = new SetAssociativeCache(new FlatMemory(4096), 512, 2, 32, 5); // different set count
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        Assert.Throws<CheckpointException>(() => cacheB.ReadState(r));
    }

    [Fact]
    public void Tlb_RoundTrip_EntriesMatch() {
        var tlbA = new Tlb(new FlatMemory(1 << 16), 8, 4096, 5);
        tlbA.Read(0x1000, 4);
        tlbA.Read(0x5000, 4);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { tlbA.WriteState(w); }

        var tlbB = new Tlb(new FlatMemory(1 << 16), 8, 4096, 5);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { tlbB.ReadState(r); }

        long missesBefore = tlbB.Misses;
        tlbB.Read(0x1000, 4); // must hit — a miss would mean the mapping wasn't restored
        Assert.Equal(missesBefore, tlbB.Misses);
    }

    [Fact]
    public void NBitBp_RoundTrip_TableMatches() {
        var bpA = new NBitBp(2, 64);
        bpA.Update(0x100, true, 0x200);
        bpA.Update(0x100, true, 0x200);
        bpA.Update(0x300, false, 0x304);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { bpA.WriteState(w); }

        var bpB = new NBitBp(2, 64);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { bpB.ReadState(r); }

        Assert.Equal(bpA.Predict(0x100), bpB.Predict(0x100));
        Assert.Equal(bpA.Predict(0x300), bpB.Predict(0x300));
    }

    /// <summary>
    ///     Trains with a varying (not-constant) outcome pattern biased toward not-taken — a fresh
    ///     <see cref="HashedPerceptronBp" /> has all-zero weights, so <c>Sum() &gt;= 0</c> always
    ///     predicts <em>taken</em> by default; training against a net not-taken bias is what makes
    ///     the trained prediction actually distinguishable from a cold one, which the equality
    ///     assertion alone can't prove (a no-op <c>ReadState</c> would leave <c>bpB</c> cold, and
    ///     cold would trivially equal a warm instance that also happens to predict taken).
    /// </summary>
    [Fact]
    public void HashedPerceptronBp_RoundTrip_HistoryDependentPredictionMatches() {
        var bpA = new HashedPerceptronBp(64, [0, 2, 4,]);
        const ulong pc = 0x100;
        for (var i = 0; i < 16; i++) bpA.Update(pc, i % 3 == 0, pc + 4); // taken 6/16, not-taken 10/16

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { bpA.WriteState(w); }

        var bpB = new HashedPerceptronBp(64, [0, 2, 4,]);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { bpB.ReadState(r); }

        Assert.Equal(bpA.Predict(pc), bpB.Predict(pc));

        // The real proof: the trained prediction must differ from a cold instance's default —
        // otherwise the equality above would pass even with a no-op ReadState.
        var cold = new HashedPerceptronBp(64, [0, 2, 4,]);
        Assert.NotEqual(bpA.Predict(pc), cold.Predict(pc));
    }

    [Fact]
    public void TournamentBp_RoundTrip_LocalAndGlobalHistoryMatch() {
        var bpA = new TournamentBp(6, 64, 8);
        const ulong pcA = 0x100;
        const ulong pcB = 0x200;
        for (var i = 0; i < 20; i++) {
            bpA.Update(pcA, true, pcA + 4); // consistently taken: flips local + global from their
            //                                 not-taken defaults, so trained != cold is provable
            bpA.Update(pcB, i % 5 != 0, pcB + 4); // mostly-taken with occasional misses: global/chooser
        }

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { bpA.WriteState(w); }

        var bpB = new TournamentBp(6, 64, 8);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { bpB.ReadState(r); }

        Assert.Equal(bpA.Predict(pcA), bpB.Predict(pcA));

        // Same theater check as HashedPerceptronBp: prove the trained prediction is actually
        // distinguishable from a cold instance's default (both PHTs start weakly not-taken).
        var cold = new TournamentBp(6, 64, 8);
        Assert.NotEqual(bpA.Predict(pcA), cold.Predict(pcA));
        Assert.Equal(bpA.Predict(pcB), bpB.Predict(pcB));
    }

    // ── BP zoo: TAGE-lineage subclasses (exhaustive, per user follow-up ask) ────
    //
    // Every predictor below extends LTageBp or TageScLBp, so LTageBp's own pipeline
    // equivalence test already proves history-through-pipeline for the inherited substrate;
    // these round trips only need to prove each subclass's own additional layered tables
    // serialize correctly, with the same cold-baseline anti-theater check used above.

    /// <summary>
    ///     Generic round-trip check for an <see cref="IBranchPredictor" />: trains with a
    ///     varying, biased pattern (exercises history-folded tables and biases away
    ///     from whatever a cold instance predicts by default — see the anti-theater note on
    ///     <see cref="HashedPerceptronBp_RoundTrip_HistoryDependentPredictionMatches" />), then
    ///     asserts restored == trained AND trained is distinguishable from a fresh cold instance.
    ///     Defaults to a not-taken-biased pattern (right for predictors that default to
    ///     "taken" when cold, e.g. all-zero-weight perceptrons); pass <paramref name="takenAt" />
    ///     to flip the bias for predictors whose saturating counters default to "not-taken".
    /// </summary>
    private static void AssertBpRoundTripNotTheater<T>(
        Func<T> make,
        ulong pc,
        Func<int, bool>? takenAt = null,
        int iterations = 24
    ) where T : IBranchPredictor {
        takenAt ??= i => i % 3 == 0;
        T bpA = make();
        for (var i = 0; i < iterations; i++) bpA.Update(pc, takenAt(i), pc + 4);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { bpA.WriteState(w); }

        T bpB = make();
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { bpB.ReadState(r); }

        Assert.Equal(bpA.Predict(pc), bpB.Predict(pc));

        T cold = make();
        Assert.NotEqual(bpA.Predict(pc), cold.Predict(pc));
    }

    [Fact]
    public void TageScLBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new TageScLBp(), 0x100);

    [Fact]
    public void BatageBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new BatageBp(), 0x100);

    [Fact]
    public void BullseyeBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new BullseyeBp(), 0x100);

    [Fact]
    public void MultiperspectivePerceptronBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new MultiperspectivePerceptronBp(), 0x100);

    [Fact]
    public void LlbpBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new LlbpBp(), 0x100);

    [Fact]
    public void LlbpXBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new LlbpXBp(), 0x100);

    [Fact]
    public void TeaBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new TeaBp(), 0x100);

    [Fact]
    public void LvcpBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new LvcpBp(), 0x100);

    [Fact]
    public void RunltsBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new RunltsBp(), 0x100);

    [Fact]
    public void VlaTageBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new VlaTageBp(), 0x100);

    // ── BP zoo: standalone (non-TAGE) predictors ─────────────────────────────

    [Fact]
    public void PerceptronBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new PerceptronBp(), 0x100, _ => false);

    [Fact]
    public void CorrelatedBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new CorrelatedBp(), 0x100);

    [Fact]
    public void GselectPredictor_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new GselectPredictor(), 0x100);

    [Fact]
    public void GshareBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new GshareBp(), 0x100);

    [Fact]
    public void IttagePredictor_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new IttagePredictor(), 0x100, i => i % 3 != 0);

    [Fact]
    public void ImliPredictor_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new ImliPredictor(), 0x100, i => i % 3 != 0);

    // ── BP zoo: composed-baseline predictors (not TAGE subclasses — hold a TageScLBp
    // field rather than extending it, so their own WriteState must delegate explicitly) ──

    [Fact]
    public void BranchNetBp_RoundTrip_NotTheater() =>
        AssertBpRoundTripNotTheater(() => new BranchNetBp(), 0x100);

    [Fact]
    public void HypreBp_RoundTrip_NotTheater() =>
        // HYPRE's 1024-bit HD vectors need many more repetitions than the other predictors here
        // to push a Hamming match count across its high threshold (~560) — a handful of updates
        // spreads across too many distinct history-folded query vectors to reinforce any one of
        // them enough. A strongly not-taken-biased pattern over many iterations lets the bounded
        // (16-state) local-history fallback slot repeat enough times to actually diverge from a
        // cold (all-zero, tie-breaks to "taken") instance.
        AssertBpRoundTripNotTheater(() => new HypreBp(), 0x100, _ => false, 400);

    [Fact]
    public void LruPolicy_RoundTrip_AgesMatch() {
        var polA = new LruPolicy(4, 2);
        polA.RecordHit(0, 1);
        polA.RecordInstall(2, 0);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { polA.WriteState(w); }

        var polB = new LruPolicy(4, 2);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { polB.ReadState(r); }

        for (var s = 0; s < 4; s++)
        for (var w2 = 0; w2 < 2; w2++)
            Assert.Equal(polA.GetMetadata(s, w2), polB.GetMetadata(s, w2));
    }

    [Fact]
    public void FifoPolicy_RoundTrip_PointerMatches() {
        var polA = new FifoPolicy(4, 2);
        polA.RecordInstall(0, 0);
        polA.RecordInstall(0, 1);
        polA.RecordInstall(2, 0);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { polA.WriteState(w); }

        var polB = new FifoPolicy(4, 2);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { polB.ReadState(r); }

        for (var s = 0; s < 4; s++)
        for (var w2 = 0; w2 < 2; w2++)
            Assert.Equal(polA.GetMetadata(s, w2), polB.GetMetadata(s, w2));
        Assert.Equal(polA.ChooseVictim(0), polB.ChooseVictim(0));
    }

    [Fact]
    public void MruPolicy_RoundTrip_AgesMatch() {
        var polA = new MruPolicy(4, 2);
        polA.RecordHit(0, 1);
        polA.RecordInstall(2, 0);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { polA.WriteState(w); }

        var polB = new MruPolicy(4, 2);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { polB.ReadState(r); }

        for (var s = 0; s < 4; s++)
        for (var w2 = 0; w2 < 2; w2++)
            Assert.Equal(polA.GetMetadata(s, w2), polB.GetMetadata(s, w2));
    }

    [Fact]
    public void ClockPolicy_RoundTrip_RefBitsAndHandMatch() {
        var polA = new ClockPolicy(4, 2);
        polA.RecordInstall(0, 0);
        polA.RecordHit(0, 1);
        polA.RecordInstall(2, 0);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { polA.WriteState(w); }

        var polB = new ClockPolicy(4, 2);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { polB.ReadState(r); }

        for (var s = 0; s < 4; s++)
        for (var w2 = 0; w2 < 2; w2++)
            Assert.Equal(polA.GetMetadata(s, w2), polB.GetMetadata(s, w2));
        Assert.Equal(polA.ChooseVictim(0), polB.ChooseVictim(0));
    }

    [Fact]
    public void PlruPolicy_RoundTrip_BitsMatch() {
        var polA = new PlruPolicy(4, 4);
        polA.RecordInstall(0, 0);
        polA.RecordHit(0, 2);
        polA.RecordInstall(1, 1);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { polA.WriteState(w); }

        var polB = new PlruPolicy(4, 4);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { polB.ReadState(r); }

        for (var s = 0; s < 4; s++)
        for (var w2 = 0; w2 < 4; w2++)
            Assert.Equal(polA.GetMetadata(s, w2), polB.GetMetadata(s, w2));
        Assert.Equal(polA.ChooseVictim(0), polB.ChooseVictim(0));
    }

    [Fact]
    public void SrripPolicy_RoundTrip_RrpvMatches() {
        var polA = new SrripPolicy(4, 4);
        polA.RecordInstall(0, 0);
        polA.RecordHit(0, 0);
        polA.RecordInstall(1, 2);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { polA.WriteState(w); }

        var polB = new SrripPolicy(4, 4);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { polB.ReadState(r); }

        for (var s = 0; s < 4; s++)
        for (var w2 = 0; w2 < 4; w2++)
            Assert.Equal(polA.GetMetadata(s, w2), polB.GetMetadata(s, w2));
    }

    [Fact]
    public void BrripPolicy_RoundTrip_RrpvAndCounterMatch() {
        var polA = new BrripPolicy(4, 4, bimodalDenominator: 4);
        for (var i = 0; i < 3; i++) polA.RecordInstall(0, i % 4); // advance the bimodal counter partway

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { polA.WriteState(w); }

        var polB = new BrripPolicy(4, 4, bimodalDenominator: 4);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { polB.ReadState(r); }

        for (var s = 0; s < 4; s++)
        for (var w2 = 0; w2 < 4; w2++)
            Assert.Equal(polA.GetMetadata(s, w2), polB.GetMetadata(s, w2));

        // The 4th install exercises the restored bimodal counter identically in both instances —
        // if the counter hadn't round-tripped, this install would diverge (distant vs long RRPV).
        polA.RecordInstall(1, 0);
        polB.RecordInstall(1, 0);
        Assert.Equal(polA.GetMetadata(1, 0), polB.GetMetadata(1, 0));
    }

    [Fact]
    public void DrripPolicy_RoundTrip_PselAndRrpvMatch() {
        var polA = new DrripPolicy(64, 4, sdmSets: 4);
        for (var i = 0; i < 5; i++) polA.RecordInstall(0, i % 4); // SDM-SRRIP set: nudges PSEL

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { polA.WriteState(w); }

        var polB = new DrripPolicy(64, 4, sdmSets: 4);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { polB.ReadState(r); }

        Assert.Equal(polA.Psel, polB.Psel);
        for (var s = 0; s < 64; s++)
        for (var w2 = 0; w2 < 4; w2++)
            Assert.Equal(polA.GetMetadata(s, w2), polB.GetMetadata(s, w2));
    }

    [Fact]
    public void ShipPolicy_RoundTrip_ShctAndSignatureMatch() {
        var polA = new ShipPolicy(4, 4);
        polA.SetPendingSignature(0x100);
        polA.RecordInstall(0, 0);
        polA.RecordHit(0, 0); // trains SHCT[0x100] upward
        polA.SetPendingSignature(0x200);
        polA.RecordInstall(0, 1);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { polA.WriteState(w); }

        var polB = new ShipPolicy(4, 4);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { polB.ReadState(r); }

        Assert.Equal(polA.GetShctCounter(0x100), polB.GetShctCounter(0x100));
        Assert.Equal(polA.GetShctCounter(0x200), polB.GetShctCounter(0x200));
        for (var s = 0; s < 4; s++)
        for (var w2 = 0; w2 < 4; w2++)
            Assert.Equal(polA.GetMetadata(s, w2), polB.GetMetadata(s, w2));

        // A fresh install with the trained signature must reproduce the same RRPV insertion
        // decision in both instances — the real proof the SHCT round-tripped, not just its bytes.
        polA.SetPendingSignature(0x100);
        polA.RecordInstall(0, 2);
        polB.SetPendingSignature(0x100);
        polB.RecordInstall(0, 2);
        Assert.Equal(polA.GetMetadata(0, 2), polB.GetMetadata(0, 2));
    }

    [Fact]
    public void HawkeyePolicy_RoundTrip_PredictorAndOptgenMatch() {
        var polA = new HawkeyePolicy(4, 4);
        for (ulong i = 0; i < 20; i++) {
            polA.SetPendingAddress(i, 0x100);
            polA.RecordInstall(0, (int)(i % 4));
            polA.RecordHitPc(0, (int)(i % 4), i, 0x100);
        }

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { polA.WriteState(w); }

        var polB = new HawkeyePolicy(4, 4);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { polB.ReadState(r); }

        for (var s = 0; s < 4; s++)
        for (var w2 = 0; w2 < 4; w2++)
            Assert.Equal(polA.GetMetadata(s, w2), polB.GetMetadata(s, w2));

        // Exercise OPTgen/the predictor further post-restore — divergence here would indicate
        // _absTime/_absLineTime (the self-referential counters) didn't round-trip together.
        polA.SetPendingAddress(99, 0x300);
        polA.RecordInstall(1, 0);
        polB.SetPendingAddress(99, 0x300);
        polB.RecordInstall(1, 0);
        Assert.Equal(polA.GetMetadata(1, 0), polB.GetMetadata(1, 0));
    }

    [Fact]
    public void Ras_RoundTrip_EntriesMatch() {
        var rasA = new ReturnAddressStack(4);
        rasA.Push(0x1000);
        rasA.Push(0x2000);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { rasA.WriteState(w); }

        var rasB = new ReturnAddressStack(4);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { rasB.ReadState(r); }

        Assert.True(rasB.TryPop(out ulong addr));
        Assert.Equal(0x2000ul, addr);
        Assert.True(rasB.TryPop(out addr));
        Assert.Equal(0x1000ul, addr);
    }

    [Fact]
    public void MicroCheckpoint_Load_InvalidMagic_Throws() {
        using var ms = new MemoryStream([0x01, 0x02, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00,]);
        Assert.Throws<CheckpointException>(() => MicroarchitecturalCheckpoint.Load(ms));
    }

    [Fact]
    public void RestoreWithMismatchedPredictorTypeTag_DoesNotThrow_SkipsForeignBytes() {
        // BPRED sections are tagged with the predictor's concrete type (see
        // OoOPipelineCore.BuildCheckpointSections/RestoreCheckpointSections) precisely so that a
        // checkpoint saved with one predictor type restored into a train using a *different*
        // predictor type is skipped rather than feeding foreign bytes into an unrelated
        // ReadState. Hand-craft a mismatched tag directly (today's codebase only ships one
        // stateful IBranchPredictor, so this is the only way to exercise the mismatch path).
        var mem = new FlatMemory(4096);
        Load(mem, MicroCheckpointTests.LoopProgram);
        var archState = new Rv32ArchState();

        using var ms = new MemoryStream();
        MicroarchitecturalCheckpoint.Save(
            ms, archState, mem, 0UL,
            [
                (
                    "BPRED", w => {
                        w.Write("Some.Bogus.PredictorType");
                        w.Write(0xDEADBEEFu); // would corrupt NBitBp's tables if blindly applied
                    }
                ),
            ]
        );

        OooTrain trainB = MakeTrain(mem);
        ms.Position = 0;
        trainB.RestoreMicroCheckpoint(ms, mem); // must not throw

        RevolutionResult result = trainB.Run(1000);
        Assert.True(result.TotalTicks > 0);
    }

    [Fact]
    public void MicroCheckpoint_MissingTag_TryRestoreSectionReturnsFalse() {
        var mem = new FlatMemory(4096);
        Load(mem, MicroCheckpointTests.LoopProgram);
        using var ms = new MemoryStream();
        MicroarchitecturalCheckpoint.Save(ms, new Rv32ArchState(), mem, 0UL, []);

        ms.Position = 0;
        MicroarchitecturalCheckpoint chk = MicroarchitecturalCheckpoint.Load(ms);
        var invoked = false;
        bool found = chk.TryRestoreSection("ICACHE", _ => invoked = true);
        Assert.False(found);
        Assert.False(invoked);
    }

    // ── Drain ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Drain_ReachesEmptyPipeline() {
        var mem = new FlatMemory(4096);
        Load(mem, MicroCheckpointTests.LoopProgram);
        OooTrain train = MakeTrain(mem);

        train.BeginStepping();
        for (var i = 0; i < 10; i++) train.StepCycle();
        Assert.False(train.IsDrained); // still mid-flight

        train.Drain();
        Assert.True(train.IsDrained);
    }

    [Fact]
    public void SaveMicroCheckpoint_NotDrained_Throws() {
        var mem = new FlatMemory(4096);
        Load(mem, MicroCheckpointTests.LoopProgram);
        OooTrain train = MakeTrain(mem);

        train.BeginStepping();
        train.StepCycle();

        using var ms = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => train.SaveMicroCheckpoint(ms, mem));
    }

    // ── The acceptance gate: bit-identical resume ────────────────────────────

    /// <summary>
    ///     Drain a running train mid-loop, save a microcheckpoint, and continue it — this is the
    ///     faithful reference, since Drain never discards in-flight work, only delays new fetches
    ///     by a few cycles. Separately, reload the checkpoint into a fresh train and run it to
    ///     completion (entryPoint must be the checkpoint's PC — RestoreInto only writes
    ///     ArchState.Pc, not the pipeline's internal fetch-PC latch, exactly like the existing
    ///     Option A convention documented in
    ///     <see cref="CheckpointTests.OutOfOrder_RestoreThenRun_UsesRestoredRegisterAsStoreAddress" />).
    ///     Both windows cover the identical remaining portion of the same deterministic program, so
    ///     final architectural state, ticks spent, retired-instruction count, cache miss count (zero
    ///     new misses in both — the proof the cache didn't cold-start), and branch-misprediction
    ///     count must match exactly; dcache_hits is allowed a ±1 tolerance for wrong-path
    ///     speculative accesses right at a branch-resolution boundary (see the assertion below).
    /// </summary>
    [Fact]
    public void Equivalence_DrainSaveRestoreReload_MatchesDrainedContinuation() {
        var memA = new FlatMemory(4096);
        Load(memA, MicroCheckpointTests.LoopProgram);
        OooTrain trainA = MakeTrain(memA);

        trainA.BeginStepping();
        for (var i = 0; i < 80; i++) trainA.StepCycle(); // partway through the loop
        trainA.Drain();

        ulong checkpointPc = trainA.ArchState.Pc;
        IReadOnlyList<DialBoardSnapshot> baseline = trainA.SnapshotDials();

        using var ms = new MemoryStream();
        trainA.SaveMicroCheckpoint(ms, memA);

        while (trainA.StepCycle()) { }

        RevolutionResult refResult = trainA.FinishStepping(baseline);

        var memB = new FlatMemory(4096);
        Load(memB, MicroCheckpointTests.LoopProgram);
        OooTrain trainB = MakeTrain(memB, checkpointPc);
        ms.Position = 0;
        trainB.RestoreMicroCheckpoint(ms, memB);

        trainB.BeginStepping();
        while (trainB.StepCycle()) { }

        RevolutionResult reloadResult = trainB.FinishStepping();

        for (var r = 0; r < 32; r++)
            Assert.Equal(trainA.ArchState.IntegerRegisters.Read(r), trainB.ArchState.IntegerRegisters.Read(r));
        Assert.Equal(trainA.ArchState.Pc, trainB.ArchState.Pc);

        Assert.Equal(refResult.TotalTicks, reloadResult.TotalTicks);
        Assert.Equal(Counter(refResult, "retired"), Counter(reloadResult, "retired"));
        Assert.Equal(
            Counter(refResult, "dcache_misses"),
            Counter(reloadResult, "dcache_misses")
        );
        Assert.Equal(
            Counter(refResult, "branch_misses"),
            Counter(reloadResult, "branch_misses")
        );

        // dcache_hits is allowed to differ by at most one: a hit/miss right at a branch
        // resolution boundary can include one extra or fewer wrong-path (later-squashed)
        // memory access, depending on cycle-exact pipeline occupancy that isn't part of what a
        // drained-boundary checkpoint claims to preserve (only committed architectural state and
        // steady-state tables are). Verified by direct GetSnapshot() comparison that the
        // restored cache's tags/data/ages are bit-identical to the live one at the checkpoint
        // instant — the discrepancy is confined to wrong-path speculative counting during the
        // remaining run, never to the restored table itself.
        long hitsDelta = Counter(refResult, "dcache_hits")
                       - Counter(reloadResult, "dcache_hits");
        Assert.InRange(hitsDelta, -1, 1);

        // Sanity: the workload actually exercised the tables under test, so a broken
        // WriteState/ReadState would have had something to diverge on.
        Assert.True(Counter(refResult, "dcache_hits") > 0);
    }

    [Fact]
    public void RestoreIntoTrainWithoutMicroArchTables_DoesNotThrow_ColdStartsInstead() {
        var memA = new FlatMemory(4096);
        Load(memA, MicroCheckpointTests.LoopProgram);
        OooTrain trainA = MakeTrain(memA);

        trainA.BeginStepping();
        for (var i = 0; i < 40; i++) trainA.StepCycle();
        trainA.Drain();
        ulong checkpointPc = trainA.ArchState.Pc;

        using var ms = new MemoryStream();
        trainA.SaveMicroCheckpoint(ms, memA);

        var memB = new FlatMemory(4096);
        Load(memB, MicroCheckpointTests.LoopProgram);
        // No cache/TLB/custom predictor configured — restore has nothing matching to feed those
        // sections into, so they must be skipped rather than throwing.
        OooTrain trainB = MakeTrain(memB, checkpointPc, false);
        ms.Position = 0;
        trainB.RestoreMicroCheckpoint(ms, memB); // must not throw despite the missing components

        RevolutionResult result = trainB.Run();
        Assert.True(result.TotalTicks > 0);
    }

    // ── BΔI-compressed L2 (BdiCache) ─────────────────────────────────────────
    //
    // addi x1,x0,0; addi x3,x0,20; loop: lw x4,0(x1); lw x5,64(x1); addi x3,x3,-1;
    // bne x3,x0,loop; ebreak
    // Alternates reads between two lines 64 bytes apart. Paired with a direct-mapped
    // (1 way, 1 line) D-L1, every iteration evicts the other address from L1 — every access is
    // an L1 miss, so L2 (with room for both lines) is touched every iteration too, giving the
    // L2Bdi section real hit/miss/segment state to checkpoint (not just a single one-off miss).
    private static readonly uint[] BdiL2LoopProgram = [
        0x00000093, // addi x1, x0, 0
        0x01400193, // addi x3, x0, 20
        0x0000A203, // loop: lw x4, 0(x1)
        0x400A283, //       lw x5, 64(x1)
        0xFFF18193, //      addi x3, x3, -1
        MicroCheckpointTests.EncodeBne(3, 0, -12),
        0x00100073, // ebreak
    ];

    private static readonly MemoryConfig BdiL2DCacheCfg = new(
        32, 1, 32, 4,
        L2CapacityBytes: 128, L2Ways: 2, L2BlockBytes: 32, L2MissLatency: 8,
        L2Compression: CompressionKind.Bdi,
        TlbEntries: 4, TlbPageBytes: 4096, TlbMissLatency: 3
    );

    private static OooTrain MakeBdiL2Train(FlatMemory mem, ulong entryPoint = 0, bool withL2Bdi = true) =>
        new(
            new Rv32Mechanism(), mem, entryPoint,
            robCapacity: 16, iqCapacity: 8,
            iMemConfig: MicroCheckpointTests.ICacheCfg,
            dMemConfig: withL2Bdi ? MicroCheckpointTests.BdiL2DCacheCfg : MicroCheckpointTests.CacheCfg
        );

    /// <summary>
    ///     Same drain/save/continue-vs-reload structure as
    ///     <see cref="Equivalence_DrainSaveRestoreReload_MatchesDrainedContinuation" />, but for a
    ///     BΔI-compressed L2 (<see cref="BdiCache" />) instead of a plain <see cref="SetAssociativeCache" />
    ///     L2 — proves <see cref="BdiCache.WriteState" />/<see cref="BdiCache.ReadState" /> round-trip
    ///     through a full pipeline checkpoint, not just the standalone unit-level test in
    ///     BdiCacheTests.
    /// </summary>
    [Fact]
    public void L2Bdi_Equivalence_DrainSaveRestoreReload_MatchesDrainedContinuation() {
        var memA = new FlatMemory(4096);
        Load(memA, MicroCheckpointTests.BdiL2LoopProgram);
        OooTrain trainA = MakeBdiL2Train(memA);

        trainA.BeginStepping();
        for (var i = 0; i < 30; i++) trainA.StepCycle(); // partway through the loop
        trainA.Drain();

        ulong checkpointPc = trainA.ArchState.Pc;
        IReadOnlyList<DialBoardSnapshot> baseline = trainA.SnapshotDials();

        using var ms = new MemoryStream();
        trainA.SaveMicroCheckpoint(ms, memA);

        while (trainA.StepCycle()) { }

        RevolutionResult refResult = trainA.FinishStepping(baseline);

        var memB = new FlatMemory(4096);
        Load(memB, MicroCheckpointTests.BdiL2LoopProgram);
        OooTrain trainB = MakeBdiL2Train(memB, checkpointPc);
        ms.Position = 0;
        trainB.RestoreMicroCheckpoint(ms, memB);

        trainB.BeginStepping();
        while (trainB.StepCycle()) { }

        RevolutionResult reloadResult = trainB.FinishStepping();

        Assert.Equal(refResult.TotalTicks, reloadResult.TotalTicks);
        Assert.Equal(Counter(refResult, "retired"), Counter(reloadResult, "retired"));
        Assert.Equal(Counter(refResult, "l2_dcache_misses"), Counter(reloadResult, "l2_dcache_misses"));
        Assert.Equal(Counter(refResult, "l2_dcache_hits"), Counter(reloadResult, "l2_dcache_hits"));

        // Sanity: the workload actually exercised L2Bdi, so a broken WriteState/ReadState would
        // have had something to diverge on.
        Assert.True(Counter(refResult, "l2_dcache_hits") > 0);
    }

    /// <summary>
    ///     The other half of the round-trip theater guard (see feedback memory
    ///     "Checkpoint round-trip theater"): the equivalence test above alone could pass even if
    ///     <see cref="BdiCache.ReadState" /> were a silent no-op, if a cold L2Bdi happened to reach
    ///     the same steady-state miss count by the time the run ends. This test proves the restore
    ///     is load-bearing by diverging a third leg: a train that reaches the identical checkpoint
    ///     PC but is never restored (cold L2Bdi) must incur strictly more L2 misses than the
    ///     properly restored train over the identical remaining window, since it has to refill both
    ///     lines from scratch instead of finding them already resident.
    /// </summary>
    [Fact]
    public void L2Bdi_RestoredTrain_HasFewerL2MissesThanColdStartOverSameRemainingWindow() {
        var memA = new FlatMemory(4096);
        Load(memA, MicroCheckpointTests.BdiL2LoopProgram);
        OooTrain trainA = MakeBdiL2Train(memA);

        trainA.BeginStepping();
        for (var i = 0; i < 30; i++) trainA.StepCycle();
        trainA.Drain();
        ulong checkpointPc = trainA.ArchState.Pc;

        using var ms = new MemoryStream();
        trainA.SaveMicroCheckpoint(ms, memA);

        var memRestored = new FlatMemory(4096);
        Load(memRestored, MicroCheckpointTests.BdiL2LoopProgram);
        OooTrain trainRestored = MakeBdiL2Train(memRestored, checkpointPc);
        ms.Position = 0;
        trainRestored.RestoreMicroCheckpoint(ms, memRestored);
        RevolutionResult restoredResult = trainRestored.Run();

        var memCold = new FlatMemory(4096);
        Load(memCold, MicroCheckpointTests.BdiL2LoopProgram);
        // Same checkpoint PC, same remaining program — but never restored, so L2Bdi starts empty.
        OooTrain trainCold = MakeBdiL2Train(memCold, checkpointPc);
        RevolutionResult coldResult = trainCold.Run();

        Assert.True(
            Counter(coldResult, "l2_dcache_misses") > Counter(restoredResult, "l2_dcache_misses"),
            $"cold-start L2 misses ({Counter(coldResult, "l2_dcache_misses")}) should exceed " +
            $"restored L2 misses ({Counter(restoredResult, "l2_dcache_misses")}) — otherwise " +
            "ReadState isn't actually restoring resident state."
        );
    }

    // ── Optional OoO predictor/prefetcher tables ─────────────────────────────
    //
    // StoreSetPredictor and SmbPredictor are `internal`, so a direct unit-level round trip isn't
    // reachable from this test project — full pipeline equivalence tests are the only available
    // (and, per the design note on each WriteState, the only sufficient) proof for them.
    // TokenPassingCriticalityPredictor and LvpVp are `public`, so they also get a cheap direct
    // round trip. RdipPrefetcher's equivalence test is the most expensive to set up (needs a
    // two-region call/return program plus an I-cache) but reuses RdipPrefetcherTests' program
    // directly.

    private static (RevolutionResult RefResult, RevolutionResult ReloadResult, OooTrain TrainA, OooTrain TrainB)
        RunDrainSaveRestoreEquivalence(
            Func<FlatMemory, ulong, OooTrain> makeTrain,
            Action<FlatMemory> loadProgram,
            Func<OooTrain, bool> triggerReached,
            int marginCycles = 5,
            long tickTolerance = 0
        ) {
        var memA = new FlatMemory(8192);
        loadProgram(memA);
        OooTrain trainA = makeTrain(memA, 0);

        trainA.BeginStepping();
        while (!triggerReached(trainA))
            if (!trainA.StepCycle())
                break;
        for (var i = 0; i < marginCycles; i++)
            if (!trainA.StepCycle())
                break;
        trainA.Drain();

        ulong checkpointPc = trainA.ArchState.Pc;
        IReadOnlyList<DialBoardSnapshot> baseline = trainA.SnapshotDials();

        using var ms = new MemoryStream();
        trainA.SaveMicroCheckpoint(ms, memA);
        while (trainA.StepCycle()) { }

        RevolutionResult refResult = trainA.FinishStepping(baseline);

        var memB = new FlatMemory(8192);
        loadProgram(memB);
        OooTrain trainB = makeTrain(memB, checkpointPc);
        ms.Position = 0;
        trainB.RestoreMicroCheckpoint(ms, memB);
        trainB.BeginStepping();
        while (trainB.StepCycle()) { }

        RevolutionResult reloadResult = trainB.FinishStepping();

        for (var r = 0; r < 32; r++)
            Assert.Equal(trainA.ArchState.IntegerRegisters.Read(r), trainB.ArchState.IntegerRegisters.Read(r));
        Assert.Equal(trainA.ArchState.Pc, trainB.ArchState.Pc);
        Assert.InRange(reloadResult.TotalTicks - refResult.TotalTicks, -tickTolerance, tickTolerance);
        Assert.Equal(
            Counter(refResult, "retired"), Counter(reloadResult, "retired")
        );

        return (refResult, reloadResult, trainA, trainB);
    }

    private static uint EncodeBne(int rs1, int rs2, int byteOffset) {
        var imm = (uint)byteOffset;
        uint imm12 = (imm >> 12) & 1;
        uint imm11 = (imm >> 11) & 1;
        uint imm10To5 = (imm >> 5) & 0x3F;
        uint imm4To1 = (imm >> 1) & 0xF;
        return (imm12 << 31) | (imm10To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (0b001u << 12)
             | (imm4To1 << 8) | (imm11 << 7) | 0x63;
    }

    private static uint EncodeBeq(int rs1, int rs2, int byteOffset) {
        var imm = (uint)byteOffset;
        uint imm12 = (imm >> 12) & 1;
        uint imm11 = (imm >> 11) & 1;
        uint imm10To5 = (imm >> 5) & 0x3F;
        uint imm4To1 = (imm >> 1) & 0xF;
        return (imm12 << 31) | (imm10To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (0b000u << 12)
             | (imm4To1 << 8) | (imm11 << 7) | 0x63;
    }

    /// <summary>
    ///     A store whose address is delayed behind a long dependency chain (x3), and an
    ///     independent load to the same address whose own address (x5) is ready immediately —
    ///     the classic setup for a genuine memory-order violation: the younger load races ahead
    ///     and reads before the older, still-in-flight store resolves. Looped
    ///     <paramref name="iterations" /> times at fixed PCs so StoreSetPredictor's SSIT (keyed
    ///     by store/load PC, threshold 2) trains after the first two violations and then
    ///     correctly prevents the rest — verified directly against a store-sets-disabled control
    ///     in a throwaway scratch run before this test was written.
    /// </summary>
    private static uint[] BuildViolationLoopProgram(int iterations, int chainLength) {
        var body = new List<uint> {
            0x0C800293, // addi x5, x0, 200      -- load address, ready immediately
            0x0C800193, // addi x3, x0, 200      -- store-address chain seed
        };
        for (var i = 0; i < chainLength; i++) body.Add(0x00018193); // addi x3, x3, 0 (delay chain)
        body.Add(0x00018093); // addi x1, x3, 0  -- store address, ready only after the chain
        body.Add(0x0020A023); // sw x2, 0(x1)     -- older, address resolves late
        body.Add(0x0002A203); // lw x4, 0(x5)     -- younger, address ready immediately
        body.Add(0x00020313); // addi x6, x4, 0   -- use x4 so it isn't dead code
        body.Add(0xFFF38393); // addi x7, x7, -1  -- loop counter decrement

        uint bne = EncodeBne(7, 0, -(body.Count * 4));

        var program = new List<uint> { ((uint)iterations << 20) | (7u << 7) | 0x13, }; // addi x7, x0, iterations
        program.AddRange(body);
        program.Add(bne);
        program.Add(0x00100073); // ebreak
        return program.ToArray();
    }

    /// <summary>
    ///     Deliberately adversarial timing: the store's address is delayed behind a long chain
    ///     while the load's is immediate, so the outcome sits exactly on a one-cycle race —
    ///     maximally sensitive to *any* cycle-level scheduling difference between "continuing a
    ///     live pipeline through Drain()" and "resuming a freshly Wind()-ed one", independent of
    ///     the checkpoint mechanism (a similar ±1 timing artifact was also observed, and
    ///     tolerated, for wrong-path dcache_hits counting near a branch-resolution boundary in
    ///     <see cref="Equivalence_DrainSaveRestoreReload_MatchesDrainedContinuation" />). The
    ///     property this test actually exists to guard — the one the design note on
    ///     <c>StoreSetPredictor.WriteState</c> calls out as the real risk — is that the checkpoint
    ///     must never carry over a stale <c>SeqNo</c>-keyed LFST entry that could stall a load's
    ///     dependence prediction forever: confirmed by <c>retired</c> matching exactly (both runs
    ///     converge to the identical final instruction count and architectural state) and by the
    ///     tick divergence staying small and bounded rather than scaling with the number of
    ///     remaining loop iterations (checked manually against 8 vs. 20 total iterations while
    ///     developing this test — a permanent stall would instead grow unboundedly). The claim on
    ///     <c>StoreSetPredictor.WriteState</c> that LFST/<c>_seen1Pc</c> are genuinely all-zero at
    ///     this test's drain point (not just "assumed empty") was verified directly: a temporary
    ///     instrumented build logging any nonzero LFST/<c>_seen1Pc</c> entry at
    ///     <c>WriteState</c> time produced no output across this test, so the residual
    ///     <c>violationsDelta</c> of at most 1 is confirmed to come from un-checkpointed pipeline
    ///     scalar timing (the same category as the wrong-path dcache_hits tolerance above), not
    ///     from a skipped-but-load-bearing LFST entry.
    /// </summary>
    [Fact]
    public void StoreSetPredictor_Equivalence_DrainSaveRestoreReload_MatchesDrainedContinuation() {
        uint[] program = BuildViolationLoopProgram(8, 20);

        (RevolutionResult refResult, RevolutionResult reloadResult, _, _) =
            RunDrainSaveRestoreEquivalence(
                MakeStoreSetTrain,
                mem => Load(mem, program),
                // Wait past the 2nd violation (SSID assigned, threshold=2) before checkpointing,
                // so the checkpoint actually carries a trained SSIT.
                train => train.SnapshotPipeline().Counters.GetValueOrDefault("mem_order_violations") >= 2,
                tickTolerance: 50
            );

        // At most one extra violation from the race-condition sensitivity described above — not
        // the unbounded-stall failure mode a stale-SeqNo bug would cause.
        long violationsDelta = Counter(reloadResult, "mem_order_violations")
                             - Counter(refResult, "mem_order_violations");
        Assert.InRange(violationsDelta, 0, 1);
        return;

        OooTrain MakeStoreSetTrain(FlatMemory mem, ulong entryPoint) =>
            new(new Rv32Mechanism(), mem, entryPoint, robCapacity: 64, iqCapacity: 32, enableStoreSets: true);
    }

    [Fact]
    public void SmbPredictor_Equivalence_DrainSaveRestoreReload_MatchesDrainedContinuation() {
        (RevolutionResult refResult, RevolutionResult reloadResult, _, _) =
            RunDrainSaveRestoreEquivalence(
                MakeSmbTrain,
                mem => Load(mem, MicroCheckpointTests.LoopProgram),
                train => train.SnapshotPipeline().Counters.GetValueOrDefault("smb_bypasses") > 0
            );

        Assert.Equal(
            Counter(refResult, "smb_mispredicts"),
            Counter(reloadResult, "smb_mispredicts")
        );
        Assert.True(Counter(refResult, "smb_bypasses") > 0);
        return;

        OooTrain MakeSmbTrain(FlatMemory mem, ulong entryPoint) =>
            new(new Rv32Mechanism(), mem, entryPoint, robCapacity: 16, iqCapacity: 8, enableSmbBypass: true);
    }

    [Fact]
    public void TokenPassingCriticalityPredictor_RoundTrip_CpTableMatches() {
        var predA = new TokenPassingCriticalityPredictor(16, 256);
        for (ulong i = 0; i < 20; i++)
            predA.OnCommit(
                new CriticalityCommitInfo {
                    InstrId = i, Pc = 0x100, DSourceNode = CpNode.D, DSourceInstrId = i,
                    ESourceNode = CpNode.D, ESourceInstrId = i, CSourceNode = CpNode.E, CSourceInstrId = i,
                }
            );

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { predA.WriteState(w); }

        var predB = new TokenPassingCriticalityPredictor(16, 256);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { predB.ReadState(r); }

        for (ulong pc = 0; pc < 4096; pc += 4) Assert.Equal(predA.PredictCritical(pc), predB.PredictCritical(pc));
    }

    [Fact]
    public void LvpVp_RoundTrip_TableMatches() {
        var vpA = new LvpVp(64);
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 42);
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 42);
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 42);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { vpA.WriteState(w); }

        var vpB = new LvpVp(64);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { vpB.ReadState(r); }

        Assert.Equal(
            vpA.TryPredict(0x100, default(ValueHistoryCheckpoint), out ulong valA),
            vpB.TryPredict(0x100, default(ValueHistoryCheckpoint), out ulong valB)
        );
        Assert.Equal(valA, valB);
    }

    [Fact]
    public void StrideVp_RoundTrip_TableMatches() {
        var vpA = new StrideVp(64);
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 10); // Init, stride seeded to 0
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 20); // stride=10 vs previous 0: no match, stays Init
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 30); // stride=10 vs previous 10: match, Init -> Transient
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 40); // stride=10 vs previous 10: match, Transient -> Steady

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { vpA.WriteState(w); }

        var vpB = new StrideVp(64);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { vpB.ReadState(r); }

        Assert.Equal(
            vpA.TryPredict(0x100, default(ValueHistoryCheckpoint), out ulong valA),
            vpB.TryPredict(0x100, default(ValueHistoryCheckpoint), out ulong valB)
        );
        Assert.Equal(valA, valB);
        Assert.True(valA > 0); // sanity: the trained Steady state actually produced a prediction
    }

    [Fact]
    public void VtageVp_RoundTrip_TaggedComponentMatches() {
        var vpA = new VtageVp(64, 32);
        // First Update always allocates a tagged component (TryFindProvider fails on an empty
        // table), deterministically since both instances share the default seed.
        vpA.Update(0x100, new ValueHistoryCheckpoint(0xABCD), 42);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { vpA.WriteState(w); }

        var vpB = new VtageVp(64, 32);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { vpB.ReadState(r); }

        var history = new ValueHistoryCheckpoint(0xABCD);
        Assert.Equal(
            vpA.TryPredict(0x100, history, out ulong valA), vpB.TryPredict(0x100, history, out ulong valB)
        );
        Assert.Equal(valA, valB);
    }

    [Fact]
    public void DynamicClassificationVp_RoundTrip_ClassificationAndComponentsMatch() {
        DynamicClassificationVp vpA = Make();
        // Three Updates classify the PC (equal deltas -> Computational/StrideVp), then a fourth
        // trains StrideVp itself to Steady.
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 10);
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 20);
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 30); // classifies here (delta1==delta2==10)
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 40);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { vpA.WriteState(w); }

        DynamicClassificationVp vpB = Make();
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { vpB.ReadState(r); }

        Assert.Equal(
            vpA.TryPredict(0x100, default(ValueHistoryCheckpoint), out ulong valA),
            vpB.TryPredict(0x100, default(ValueHistoryCheckpoint), out ulong valB)
        );
        Assert.Equal(valA, valB);
        return;

        DynamicClassificationVp Make() => new(new VtageVp(64, 32), new StrideVp(64));
    }

    [Fact]
    public void HybridVp_RoundTrip_BothComponentsMatch() {
        HybridVp vpA = Make();
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 10);
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 20);
        vpA.Update(0x100, default(ValueHistoryCheckpoint), 30);

        using var ms = new MemoryStream();
        using (BinaryWriter w = Writer(ms)) { vpA.WriteState(w); }

        HybridVp vpB = Make();
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) { vpB.ReadState(r); }

        Assert.Equal(
            vpA.TryPredict(0x100, default(ValueHistoryCheckpoint), out ulong valA),
            vpB.TryPredict(0x100, default(ValueHistoryCheckpoint), out ulong valB)
        );
        Assert.Equal(valA, valB);
        return;

        HybridVp Make() => new(new VtageVp(64, 32), new StrideVp(64));
    }

    [Fact]
    public void ValuePredictor_Equivalence_DrainSaveRestoreReload_MatchesDrainedContinuation() {
        // Register-copy chain that always converges to the same value every iteration — the
        // classic value-prediction win case. Cribbed from ValuePredictionTests.
        uint[] program = [
            0x1F400093, // addi x1, x0, 500
            0x06300113, // addi x2, x0, 99
            0x000101B3, // loop: add x3, x2, x0
            0x00018133, // add x2, x3, x0
            0xFFF08093, // addi x1, x1, -1
            0xFE009AE3, // bne x1, x0, loop
            0x00100073, // ebreak
        ];

        (RevolutionResult refResult, RevolutionResult reloadResult, _, _) =
            RunDrainSaveRestoreEquivalence(
                MakeVpTrain,
                mem => Load(mem, program),
                train => train.SnapshotPipeline().Counters.GetValueOrDefault("vp_predictions") > 0
            );

        Assert.Equal(
            Counter(refResult, "vp_mispredicts"),
            Counter(reloadResult, "vp_mispredicts")
        );
        Assert.True(Counter(refResult, "vp_predictions") > 0);
        return;

        OooTrain MakeVpTrain(FlatMemory mem, ulong entryPoint) =>
            new(new Rv32Mechanism(), mem, entryPoint, robCapacity: 32, iqCapacity: 16, valuePredictor: new LvpVp());
    }

    /// <summary>
    ///     Monotonic-counter loop — the pattern <see cref="StrideVp" />'s own doc comment builds
    ///     and measures against, and the one case <see cref="LvpVp" /> (value-repetition) can never
    ///     predict, so a passing equivalence run here is real proof <c>StrideVp.WriteState</c>
    ///     round-trips the trained (value, stride, FSM) table correctly, not just that some
    ///     component happened to agree. The claim in <c>StrideVp.WriteState</c>'s doc comment that
    ///     <c>_inFlight</c> is genuinely all-zero at a drained boundary (not just assumed) was
    ///     verified the same way as the analogous <c>StoreSetPredictor</c> LFST claim: a temporary
    ///     instrumented build logging any nonzero <c>_inFlight</c> entry inside <c>WriteState</c>
    ///     produced no output across this test.
    /// </summary>
    [Fact]
    public void StrideVpEquivalence_DrainSaveRestoreReload_MatchesDrainedContinuation() {
        uint[] program = [
            0x00000093,          // addi x1, x0, 0        -- counter
            0x03200113,          // addi x2, x0, 50        -- loop count
            0x00408093,          // loop: addi x1, x1, 4   -- monotonic stride-4 value producer
            0xFFF10113,          // addi x2, x2, -1
            EncodeBne(2, 0, -8), // bne x2, x0, loop
            0x00100073,          // ebreak
        ];

        (RevolutionResult refResult, RevolutionResult reloadResult, _, _) =
            RunDrainSaveRestoreEquivalence(
                MakeVpTrain,
                mem => Load(mem, program),
                train => train.SnapshotPipeline().Counters.GetValueOrDefault("vp_predictions") > 0
            );

        Assert.Equal(
            Counter(refResult, "vp_mispredicts"),
            Counter(reloadResult, "vp_mispredicts")
        );
        Assert.True(Counter(refResult, "vp_predictions") > 0);
        return;

        OooTrain MakeVpTrain(FlatMemory mem, ulong entryPoint) =>
            new(new Rv32Mechanism(), mem, entryPoint, robCapacity: 32, iqCapacity: 16, valuePredictor: new StrideVp());
    }

    [Fact]
    public void RdipPrefetcher_Equivalence_DrainSaveRestoreReload_MatchesDrainedContinuation() {
        // Caller (block 0) repeatedly calls a 5-cache-block callee (0x1000+); cribbed from
        // RdipPrefetcherTests, whose comment block explains the exact block layout.
        const uint nop = 0x00000013;
        var caller = new List<uint> {
            0x00A00293, // addi x5, x0, 10       -- loop count (more iterations than the original
            //                                      test, so there's meaningful work either side
            //                                      of the checkpoint)
            0x7FD000EF, // jal x1, 4092 (call 0x1000)
            0xFFF28293, // addi x5, x5, -1
            0xFE029CE3, // bne x5, x0, -8
            0x00100073, // ebreak
        };
        for (var i = 0; i < 11; i++) caller.Add(nop);

        var callee = new List<uint>();
        for (var i = 0; i < 79; i++) callee.Add(nop);
        callee.Add(0x00008067); // jalr x0, x1, 0 -- return

        var iCacheCfg = new MemoryConfig(256, 4, 64);

        (RevolutionResult refResult, RevolutionResult reloadResult, OooTrain trainA, OooTrain trainB) =
            RunDrainSaveRestoreEquivalence(
                MakeRdipTrain, LoadProgram, train => train.ICache!.Prefetches > 0
            );

        Assert.Equal(refResult.TotalTicks, reloadResult.TotalTicks); // already asserted by the helper; kept for clarity
        Assert.True(trainA.ICache!.Prefetches > 0);
        Assert.True(trainB.ICache!.Prefetches >= 0); // reload may or may not need further prefetches; must not throw
        return;

        void LoadProgram(FlatMemory mem) {
            LoadAt(mem, 0, caller.ToArray());
            LoadAt(mem, 0x1000, callee.ToArray());
        }

        OooTrain MakeRdipTrain(FlatMemory mem, ulong entryPoint) =>
            new(
                new Rv32Mechanism(), mem, entryPoint, robCapacity: 16, iqCapacity: 8,
                iMemConfig: iCacheCfg, rdip: true
            );
    }

    private static void LoadAt(FlatMemory mem, ulong address, uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(address, bytes);
    }

    // ── BP zoo: representative-per-family predictor checkpointing ───────────
    //
    // NBitBp/AlwaysNotTaken are the only predictors any existing equivalence test above uses,
    // and neither carries global-history state, so history serialization through a live pipeline
    // (as opposed to a standalone unit-level round trip) has never actually been exercised. This
    // is the one pipeline equivalence test that does: LTageBp, representative of the whole
    // TAGE-lineage family (also the base of TageScLBp/BullseyeBp/MultiperspectiveBp/BatageBp via
    // inheritance — those subclasses' own additional tables still cold-start, only the inherited
    // TAGE/loop/history state round-trips). HashedPerceptronBp/TournamentBp above get a
    // history-populated unit round trip only, per the deliberately capped scope here (BP zoo
    // coverage is representative-per-family, not exhaustive).

    /// <summary>
    ///     addi x1,x0,0; addi x2,x0,<paramref name="iterations" />; loop: andi x3,x1,1;
    ///     beq x3,x0,+8 (skip the next instruction on even iterations); addi x4,x4,1 (odd-only
    ///     body); addi x1,x1,1; bne x1,x2,loop; ebreak. The branch at "beq" strictly alternates
    ///     taken/not-taken every iteration (parity of <c>x1</c>) — a pattern a plain bimodal
    ///     counter (2-bit saturating) can never learn (it always lags one behind), but a
    ///     history-indexed predictor like L-TAGE can, once it allocates a tagged entry keyed off
    ///     recent history. Forces real use of the history-dependent tables under test, not just
    ///     the PC-only bimodal base.
    /// </summary>
    private static uint[] BuildAlternatingParityBranchProgram(int iterations) => [
        0x00000093,                                  // addi x1, x0, 0
        ((uint)iterations << 20) | (2u << 7) | 0x13, // addi x2, x0, iterations
        0x0010F193,                                  // loop: andi x3, x1, 1
        EncodeBeq(3, 0, 8),                          // beq x3, x0, +8 (skip next on even)
        0x00120213,                                  // addi x4, x4, 1
        0x00108093,                                  // addi x1, x1, 1
        EncodeBne(1, 2, -16),                        // bne x1, x2, loop
        0x00100073,                                  // ebreak
    ];

    [Fact]
    public void LTageBp_Equivalence_DrainSaveRestoreReload_MatchesDrainedContinuation() {
        uint[] program = BuildAlternatingParityBranchProgram(40);

        (RevolutionResult refResult, RevolutionResult reloadResult, _, _) =
            RunDrainSaveRestoreEquivalence(
                MakeLTageTrain,
                mem => Load(mem, program),
                // Wait for real mispredicts so the tagged (history-indexed) tables — not just the
                // bimodal base — actually have trained entries by the time we checkpoint.
                train => train.SnapshotPipeline().Counters.GetValueOrDefault("branch_misses") >= 4
            );

        Assert.Equal(
            Counter(refResult, "branch_misses"),
            Counter(reloadResult, "branch_misses")
        );
        return;

        OooTrain MakeLTageTrain(FlatMemory mem, ulong entryPoint) =>
            new(
                new Rv32Mechanism(), mem, entryPoint, robCapacity: 32, iqCapacity: 16,
                predictor: new LTageBp()
            );
    }

    /// <summary>
    ///     <see cref="ImliPredictor" /> is the one standalone predictor with genuine
    ///     speculative-vs-committed counter semantics (<c>_imli</c>/<c>_committedImli</c>) not
    ///     covered by <see cref="LTageBp_Equivalence_DrainSaveRestoreReload_MatchesDrainedContinuation" />'s
    ///     history-register case — so unlike the other standalone predictors (round-trip only),
    ///     it gets its own pipeline equivalence test. Reuses the same alternating-parity program:
    ///     the outer <c>bne</c> loop is itself a genuine backward taken branch (increments
    ///     <c>_imli</c> every iteration), and the inner <c>beq</c> parity branch's PHT index is
    ///     <c>(pc &gt;&gt; 2) ^ _imli</c> — so a broken IMLI-counter restore would show up as
    ///     the inner branch indexing the wrong PHT slot after reload.
    /// </summary>
    [Fact]
    public void ImliPredictor_Equivalence_DrainSaveRestoreReload_MatchesDrainedContinuation() {
        uint[] program = BuildAlternatingParityBranchProgram(40);

        (RevolutionResult refResult, RevolutionResult reloadResult, _, _) =
            RunDrainSaveRestoreEquivalence(
                MakeImliTrain,
                mem => Load(mem, program),
                train => train.SnapshotPipeline().Counters.GetValueOrDefault("branch_misses") >= 4
            );

        Assert.Equal(
            Counter(refResult, "branch_misses"),
            Counter(reloadResult, "branch_misses")
        );
        return;

        OooTrain MakeImliTrain(FlatMemory mem, ulong entryPoint) =>
            new(
                new Rv32Mechanism(), mem, entryPoint, robCapacity: 32, iqCapacity: 16,
                predictor: new ImliPredictor()
            );
    }
}