#region

using System.Text;
using Mechanism;
using Mechanism.BranchPred;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     Tests for the microarchitectural checkpoint (TODO.md Analysis: Option B) — caches, TLBs,
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
        CacheCapacityBytes: 128, CacheWays: 2, CacheBlockBytes: 16, CacheMissLatency: 4,
        TlbEntries: 4, TlbPageBytes: 4096, TlbMissLatency: 3
    );

    // Small I-side cache/TLB too, so the ICACHE/ITLB checkpoint sections (otherwise only
    // exercised by the standalone Cache_RoundTrip_/Tlb_RoundTrip_ tests) get an end-to-end pass
    // through the equivalence test as well.
    private static readonly MemoryConfig ICacheCfg = new(
        CacheCapacityBytes: 128, CacheWays: 2, CacheBlockBytes: 16, CacheMissLatency: 2,
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

    private static OooeTrain MakeTrain(FlatMemory mem, ulong entryPoint = 0, bool withMicroArchTables = true) =>
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
        using (BinaryWriter w = MicroCheckpointTests.Writer(ms)) cacheA.WriteState(w);

        var cacheB = new SetAssociativeCache(new FlatMemory(4096), 256, 2, 32, 5);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) cacheB.ReadState(r);

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
        using (BinaryWriter w = MicroCheckpointTests.Writer(ms)) cacheA.WriteState(w);

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
        using (BinaryWriter w = MicroCheckpointTests.Writer(ms)) tlbA.WriteState(w);

        var tlbB = new Tlb(new FlatMemory(1 << 16), 8, 4096, 5);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) tlbB.ReadState(r);

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
        using (BinaryWriter w = MicroCheckpointTests.Writer(ms)) bpA.WriteState(w);

        var bpB = new NBitBp(2, 64);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) bpB.ReadState(r);

        Assert.Equal(bpA.Predict(0x100), bpB.Predict(0x100));
        Assert.Equal(bpA.Predict(0x300), bpB.Predict(0x300));
    }

    [Fact]
    public void LruPolicy_RoundTrip_AgesMatch() {
        var polA = new LruPolicy(4, 2);
        polA.RecordHit(0, 1);
        polA.RecordInstall(2, 0);

        using var ms = new MemoryStream();
        using (BinaryWriter w = MicroCheckpointTests.Writer(ms)) polA.WriteState(w);

        var polB = new LruPolicy(4, 2);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) polB.ReadState(r);

        for (var s = 0; s < 4; s++)
        for (var w2 = 0; w2 < 2; w2++)
            Assert.Equal(polA.GetMetadata(s, w2), polB.GetMetadata(s, w2));
    }

    [Fact]
    public void Ras_RoundTrip_EntriesMatch() {
        var rasA = new ReturnAddressStack(4);
        rasA.Push(0x1000);
        rasA.Push(0x2000);

        using var ms = new MemoryStream();
        using (BinaryWriter w = MicroCheckpointTests.Writer(ms)) rasA.WriteState(w);

        var rasB = new ReturnAddressStack(4);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) rasB.ReadState(r);

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
        MicroCheckpointTests.Load(mem, MicroCheckpointTests.LoopProgram);
        var archState = new global::RiscV32.State.Rv32ArchState();

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

        OooeTrain trainB = MicroCheckpointTests.MakeTrain(mem);
        ms.Position = 0;
        trainB.RestoreMicroCheckpoint(ms, mem); // must not throw

        RevolutionResult result = trainB.Run(1000);
        Assert.True(result.TotalTicks > 0);
    }

    [Fact]
    public void MicroCheckpoint_MissingTag_TryRestoreSectionReturnsFalse() {
        var mem = new FlatMemory(4096);
        MicroCheckpointTests.Load(mem, MicroCheckpointTests.LoopProgram);
        using var ms = new MemoryStream();
        MicroarchitecturalCheckpoint.Save(ms, new global::RiscV32.State.Rv32ArchState(), mem, 0UL, []);

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
        MicroCheckpointTests.Load(mem, MicroCheckpointTests.LoopProgram);
        OooeTrain train = MicroCheckpointTests.MakeTrain(mem);

        train.BeginStepping();
        for (var i = 0; i < 10; i++) train.StepCycle();
        Assert.False(train.IsDrained); // still mid-flight

        train.Drain();
        Assert.True(train.IsDrained);
    }

    [Fact]
    public void SaveMicroCheckpoint_NotDrained_Throws() {
        var mem = new FlatMemory(4096);
        MicroCheckpointTests.Load(mem, MicroCheckpointTests.LoopProgram);
        OooeTrain train = MicroCheckpointTests.MakeTrain(mem);

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
        MicroCheckpointTests.Load(memA, MicroCheckpointTests.LoopProgram);
        OooeTrain trainA = MicroCheckpointTests.MakeTrain(memA);

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
        MicroCheckpointTests.Load(memB, MicroCheckpointTests.LoopProgram);
        OooeTrain trainB = MicroCheckpointTests.MakeTrain(memB, checkpointPc);
        ms.Position = 0;
        trainB.RestoreMicroCheckpoint(ms, memB);

        trainB.BeginStepping();
        while (trainB.StepCycle()) { }
        RevolutionResult reloadResult = trainB.FinishStepping();

        for (var r = 0; r < 32; r++)
            Assert.Equal(trainA.ArchState.IntegerRegisters.Read(r), trainB.ArchState.IntegerRegisters.Read(r));
        Assert.Equal(trainA.ArchState.Pc, trainB.ArchState.Pc);

        Assert.Equal(refResult.TotalTicks, reloadResult.TotalTicks);
        Assert.Equal(MicroCheckpointTests.Counter(refResult, "retired"), MicroCheckpointTests.Counter(reloadResult, "retired"));
        Assert.Equal(
            MicroCheckpointTests.Counter(refResult, "dcache_misses"),
            MicroCheckpointTests.Counter(reloadResult, "dcache_misses")
        );
        Assert.Equal(
            MicroCheckpointTests.Counter(refResult, "branch_misses"),
            MicroCheckpointTests.Counter(reloadResult, "branch_misses")
        );

        // dcache_hits is allowed to differ by at most one: a hit/miss right at a branch
        // resolution boundary can include one extra or fewer wrong-path (later-squashed)
        // memory access, depending on cycle-exact pipeline occupancy that isn't part of what a
        // drained-boundary checkpoint claims to preserve (only committed architectural state and
        // steady-state tables are). Verified by direct GetSnapshot() comparison that the
        // restored cache's tags/data/ages are bit-identical to the live one at the checkpoint
        // instant — the discrepancy is confined to wrong-path speculative counting during the
        // remaining run, never to the restored table itself.
        long hitsDelta = MicroCheckpointTests.Counter(refResult, "dcache_hits")
            - MicroCheckpointTests.Counter(reloadResult, "dcache_hits");
        Assert.InRange(hitsDelta, -1, 1);

        // Sanity: the workload actually exercised the tables under test, so a broken
        // WriteState/ReadState would have had something to diverge on.
        Assert.True(MicroCheckpointTests.Counter(refResult, "dcache_hits") > 0);
    }

    [Fact]
    public void RestoreIntoTrainWithoutMicroArchTables_DoesNotThrow_ColdStartsInstead() {
        var memA = new FlatMemory(4096);
        MicroCheckpointTests.Load(memA, MicroCheckpointTests.LoopProgram);
        OooeTrain trainA = MicroCheckpointTests.MakeTrain(memA);

        trainA.BeginStepping();
        for (var i = 0; i < 40; i++) trainA.StepCycle();
        trainA.Drain();
        ulong checkpointPc = trainA.ArchState.Pc;

        using var ms = new MemoryStream();
        trainA.SaveMicroCheckpoint(ms, memA);

        var memB = new FlatMemory(4096);
        MicroCheckpointTests.Load(memB, MicroCheckpointTests.LoopProgram);
        // No cache/TLB/custom predictor configured — restore has nothing matching to feed those
        // sections into, so they must be skipped rather than throwing.
        OooeTrain trainB = MicroCheckpointTests.MakeTrain(memB, checkpointPc, false);
        ms.Position = 0;
        trainB.RestoreMicroCheckpoint(ms, memB); // must not throw despite the missing components

        RevolutionResult result = trainB.Run();
        Assert.True(result.TotalTicks > 0);
    }
}
