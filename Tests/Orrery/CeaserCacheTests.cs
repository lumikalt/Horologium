#region

using System.Text;
using Mechanism;
using Orrery.Cache;
using RiscV32.Memory;

#endregion

namespace Tests.Orrery;

/// <summary>Unit tests for CeaserCache — CEASER (Qureshi, MICRO 2018) / CEASER-S (Qureshi, ISCA 2019).</summary>
public sealed class CeaserCacheTests {
    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPow2CapacityBytes_Throws() {
        Assert.Throws<ArgumentException>(() => new CeaserCache(new FlatMemory(4096), 1000, 4, 32, 10));
    }

    [Fact]
    public void Constructor_NonPow2Ways_Throws() {
        Assert.Throws<ArgumentException>(() => new CeaserCache(new FlatMemory(4096), 1024, 3, 32, 10));
    }

    [Fact]
    public void Constructor_WaysNotMultipleOfPartitions_Throws() {
        Assert.Throws<ArgumentException>(() => new CeaserCache(new FlatMemory(4096), 1024, 4, 32, 10, partitions: 3));
    }

    // ── Basic correctness ─────────────────────────────────────────────────────

    [Fact]
    public void WriteThenRead_ReturnsWrittenValue() {
        // WriteThrough + no-write-allocate default (matching SetAssociativeCache/BdiCache): a write
        // to a never-touched line always writes to backing but never installs it, so the write
        // itself is a miss and the following read is a fresh (separate) miss-and-fill.
        var backing = new FlatMemory(4096);
        var cache = new CeaserCache(backing, 1024, 4, 32, 10);
        cache.Write(0x100, 0xDEADBEEF, 4);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0xDEADBEEFUL, cache.Read(0x100, 4));
        Assert.Equal(2, cache.Misses);
        Assert.Equal(0, cache.Hits);

        Assert.Equal(0xDEADBEEFUL, cache.Read(0x100, 4));
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void ReadMiss_FetchesFromBacking() {
        var backing = new FlatMemory(4096);
        backing.Write(0x200, 0x12345678, 4);
        var cache = new CeaserCache(backing, 1024, 4, 32, 10);
        Assert.Equal(0x12345678UL, cache.Read(0x200, 4));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0x12345678UL, cache.Read(0x200, 4));
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void CrossBoundaryAccess_BypassesCache() {
        var backing = new FlatMemory(4096);
        var cache = new CeaserCache(backing, 1024, 4, 32, 10);
        ulong lastByteOfLine = 32 - 4;
        cache.Write(lastByteOfLine + 2, 0xAABBCCDD, 4);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0, cache.Misses);
        Assert.Equal(0xAABBCCDDUL, backing.Read(lastByteOfLine + 2, 4));
    }

    // ── Data integrity across remap/rekey ─────────────────────────────────────

    // capacityBytes = ways * sets * blockBytes: 4 ways * 8 sets * 32 bytes = 1024.
    // aplr=2 -> a set is remapped every 2*4=8 accesses; a full epoch (8 sets) is 64 accesses.
    private static CeaserCache SmallWriteBackCache(IMemory backing, int aplr = 2, int seed = 1) =>
        new(backing, 1024, 4, 32, 10, aplr: aplr, seed: seed, writePolicy: WritePolicyKind.WriteBack);

    [Fact]
    public void DirtyLine_SurvivesFullRemapEpoch() {
        var backing = new FlatMemory(65536);
        CeaserCache cache = SmallWriteBackCache(backing);

        cache.Write(0x40, 0xCAFEF00DUL, 4); // dirty, resident, not yet in backing
        Assert.Equal(0UL, backing.Read(0x40, 4)); // confirms it's genuinely only in the cache

        // Drive well past one full epoch (64 accesses) with unrelated addresses spread across
        // many lines/sets so remap sweeps actually have resident lines to relocate.
        for (var i = 0; i < 200; i++) cache.Read((ulong)(0x1000 + i * 32), 4);

        Assert.True(cache.RemapSteps >= 8); // at least one full epoch completed
        Assert.Equal(0xCAFEF00DUL, cache.Read(0x40, 4)); // correct regardless of hit or evict-then-refetch
    }

    [Fact]
    public void DirtyLine_SurvivesExactEpochBoundary() {
        var backing = new FlatMemory(65536);
        CeaserCache cache = SmallWriteBackCache(backing);

        cache.Write(0x80, 0x11223344UL, 4);

        // Exactly one epoch's worth of accesses: sets(8) * aplr(2) * ways(4) = 64.
        for (var i = 0; i < 64; i++) cache.Read((ulong)(0x2000 + i * 32), 4);

        Assert.Equal(8, cache.RemapSteps);
        Assert.Equal(0x11223344UL, cache.Read(0x80, 4));
    }

    // ── Key genuinely enters the mix ──────────────────────────────────────────

    [Fact]
    public void DifferentKeys_MostlyRedistributeSetAssignment() {
        var cache1 = new CeaserCache(new FlatMemory(4096), 1024, 4, 32, 10, seed: 1);
        var cache2 = new CeaserCache(new FlatMemory(4096), 1024, 4, 32, 10, seed: 2);

        const int n = 200;
        var sameSet = 0;
        for (var i = 0; i < n; i++) {
            ulong addr = (ulong)i * 32;
            if (cache1.SetOf(0, addr) == cache2.SetOf(0, addr)) sameSet++;
        }

        // 8 sets -> ~1/8 chance of agreement if the keys are genuinely independent. A bug that
        // ignores the key entirely (or folds it in only weakly) would make every address agree.
        Assert.True(sameSet < n / 2, $"{sameSet}/{n} addresses landed in the same set under different keys.");
    }

    // ── CEASER-S partition independence ───────────────────────────────────────

    [Fact]
    public void Partitions_AreIndependentlyKeyed() {
        // 8 ways / 2 partitions = 4 ways per partition; 4 sets.
        var cache = new CeaserCache(new FlatMemory(4096), 1024, 8, 32, 10, partitions: 2);

        const int n = 200;
        var sameSet = 0;
        for (var i = 0; i < n; i++) {
            ulong addr = (ulong)i * 32;
            if (cache.SetOf(0, addr) == cache.SetOf(1, addr)) sameSet++;
        }

        Assert.True(sameSet < n / 2, $"{sameSet}/{n} addresses landed in the same set across partitions.");
    }

    [Fact]
    public void Partitions_PropertyReflectsConstructorArgument() {
        var cache = new CeaserCache(new FlatMemory(4096), 1024, 8, 32, 10, partitions: 2);
        Assert.Equal(2, cache.Partitions);

        var plain = new CeaserCache(new FlatMemory(4096), 1024, 4, 32, 10);
        Assert.Equal(1, plain.Partitions);
    }

    // ── Remap cadence ──────────────────────────────────────────────────────────

    [Fact]
    public void RemapSteps_FireAtExpectedCadence() {
        // ways=4, aplr=5 -> threshold = 20 accesses per remap step (single partition).
        var cache = new CeaserCache(new FlatMemory(4096), 1024, 4, 32, 10, aplr: 5);

        for (var i = 0; i < 45; i++) cache.Read(0x100, 4); // repeated access to one line
        Assert.Equal(2, cache.RemapSteps); // floor(45 / 20)

        for (var i = 0; i < 20; i++) cache.Read(0x100, 4); // 65 total -> floor(65/20)=3
        Assert.Equal(3, cache.RemapSteps);
    }

    [Fact]
    public void PeekRead_DoesNotAdvanceRemapState() {
        var cache = new CeaserCache(new FlatMemory(4096), 1024, 4, 32, 10, aplr: 1);
        for (var i = 0; i < 100; i++) cache.PeekRead(0x100, 4);
        Assert.Equal(0, cache.RemapSteps);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0, cache.Misses);
    }

    // ── Remapping has a measurable cost ───────────────────────────────────────

    [Fact]
    public void AggressiveRemap_CausesMoreMissesThanNoRemap() {
        // 4 sets * 4 ways = 16 resident slots; a 12-address working set fits comfortably, so any
        // extra misses beyond the first fill-through are attributable to remap-driven relocation
        // evicting a still-useful line, not ordinary conflict/capacity pressure.
        const int addresses = 12;
        const int rounds = 6;

        long MissesFor(int aplr) {
            var backing = new FlatMemory(4096);
            var cache = new CeaserCache(backing, 512, 4, 32, 10, aplr: aplr);
            for (var r = 0; r < rounds; r++)
            for (var i = 0; i < addresses; i++)
                cache.Read((ulong)(i * 32), 4);
            return cache.Misses;
        }

        long aggressive = MissesFor(1); // threshold = 1*4 = 4 accesses/step: remaps constantly
        long none = MissesFor(1_000_000); // threshold effectively never reached in this test

        Assert.True(aggressive > none, $"aggressive={aggressive} none={none}");
    }

    // ── Checkpoint round-trip ─────────────────────────────────────────────────

    [Fact]
    public void Checkpoint_RestoresAcrossMidEpochRemapProgress() {
        // ways=4, aplr=2 -> threshold = 8 accesses/step (single partition).
        var original = new CeaserCache(
            new FlatMemory(65536), 1024, 4, 32, 10, aplr: 2, seed: 7, writePolicy: WritePolicyKind.WriteBack
        );

        original.Write(0x40, 0xABCDEF01UL, 4); // 1 access, dirty line resident
        for (var i = 0; i < 4; i++) original.Read((ulong)(0x1000 + i * 32), 4); // 4 more -> ACtr=5, no remap yet
        Assert.Equal(0, original.RemapSteps);

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) original.WriteState(w);

        // A cold instance with the same geometry/config and a different (empty) backing, so the
        // integrity check below can only pass if the checkpoint actually carried the dirty bytes.
        var restored = new CeaserCache(
            new FlatMemory(65536), 1024, 4, 32, 10, aplr: 2, seed: 7, writePolicy: WritePolicyKind.WriteBack
        );
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) restored.ReadState(r);

        Assert.Equal(0xABCDEF01UL, restored.PeekRead(0x40, 4)); // non-mutating: doesn't touch ACtr

        // ACtr survived the round-trip: 3 more accesses (5 restored + 3 = 8 = threshold) must
        // trigger exactly one remap step. If ReadState silently failed to restore ACtr (left at
        // 0), this would need 8 more, not 3 — the "checkpoint theater" failure mode, where
        // warm==restored can pass even though restore is broken.
        for (var i = 0; i < 3; i++) restored.Read((ulong)(0x2000 + i * 32), 4);
        Assert.Equal(1, restored.RemapSteps);
    }

    [Fact]
    public void Checkpoint_PreservesRngStateAcrossPostRestoreEpochWrap() {
        // ways=4, sets=4 (capacity=512), aplr=1 -> threshold=4 accesses/step, epoch=16 accesses.
        // Checkpointing only AFTER at least one full epoch has already elapsed matters: a fresh
        // instance's RNG starts in the same place a never-advanced original's would, so a bug that
        // drops _rngState from the checkpoint is invisible if the checkpoint is taken before the
        // first epoch wrap (restored's fresh RNG accidentally coincides with original's still-fresh
        // RNG). Priming past a wrap first means original's RNG has already drawn a regenerated key
        // beyond construction, so a restored instance with an unrestored (fresh) RNG would diverge
        // from that point on.
        const int capacityBytes = 512, ways = 4, blockBytes = 32, aplr = 1, seed = 99;

        var original = new CeaserCache(
            new FlatMemory(65536), capacityBytes, ways, blockBytes, 10, aplr: aplr, seed: seed
        );

        // Prime past at least one full epoch (4 remap steps = 16 accesses) before checkpointing.
        for (var i = 0; i < 20; i++) original.Read((ulong)(0x1000 + i * 32), 4);
        Assert.True(original.RemapSteps >= 4, "priming should have crossed at least one epoch wrap");

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) original.WriteState(w);
        // RemapSteps/Misses are session-local stat counters, not part of restorable state (same as
        // BdiCache's Hits/Misses) — a freshly constructed-then-restored instance starts them at 0.
        // Compare deltas over the identical post-checkpoint window, not raw cumulative values.
        long remapStepsAtCheckpoint = original.RemapSteps;
        long missesAtCheckpoint = original.Misses;

        // A small working set revisited over many rounds (not 80 distinct one-shot addresses): a
        // miss count only carries any signal about placement/eviction (and therefore about which
        // key was in effect) if addresses are actually re-accessed after being evicted or relocated.
        const int workingSetSize = 8, rounds = 10;
        var postCheckpointAddresses = new ulong[workingSetSize * rounds];
        for (var r = 0; r < rounds; r++)
        for (var i = 0; i < workingSetSize; i++)
            postCheckpointAddresses[r * workingSetSize + i] = (ulong)(0x2000 + i * 32);

        // Reference continuation: the SAME instance, driven through several more epoch wraps.
        foreach (ulong a in postCheckpointAddresses) original.Read(a, 4);
        long originalRemapDelta = original.RemapSteps - remapStepsAtCheckpoint;
        Assert.Equal(20, originalRemapDelta); // 80/4, deterministic regardless of RNG state

        // A cold instance restored from the checkpoint, then driven through the identical sequence.
        var restored = new CeaserCache(new FlatMemory(65536), capacityBytes, ways, blockBytes, 10, aplr: aplr, seed: seed);
        ms.Position = 0;
        using (var r = new BinaryReader(ms)) restored.ReadState(r);
        foreach (ulong a in postCheckpointAddresses) restored.Read(a, 4);
        Assert.Equal(originalRemapDelta, restored.RemapSteps);

        // Decisive check: compare the LIVE key's effect directly via SetOf, not aggregate hit/miss
        // counts (a thrash-heavy workload's miss count can coincidentally match across different
        // keys even when the keys themselves differ — verified empirically). With a correct restore,
        // `original` and `restored` share byte-for-byte identical CurrKey/NextKey/SPtr after driving
        // the identical post-restore access sequence, so SetOf must agree for every address. If
        // _rngState weren't restored, `restored`'s post-restore epoch wrap regenerates a key from a
        // reset RNG position instead of `original`'s true (already-advanced) one — with only 4 sets,
        // independently-drawn keys agree on any given address ~1/4 of the time by chance, so this
        // drops sharply below 100% (verified empirically: ~25% with the bug reintroduced).
        const int probeCount = 200;
        var sameSet = 0;
        for (var i = 0; i < probeCount; i++) {
            ulong addr = (ulong)(0x5000 + i * 32);
            if (original.SetOf(0, addr) == restored.SetOf(0, addr)) sameSet++;
        }

        Assert.Equal(probeCount, sameSet);
    }

    [Fact]
    public void Checkpoint_GeometryMismatch_Throws() {
        var original = new CeaserCache(new FlatMemory(4096), 1024, 4, 32, 10);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true)) original.WriteState(w);

        var differentGeometry = new CeaserCache(new FlatMemory(4096), 1024, 4, 32, 10, partitions: 2);
        ms.Position = 0;
        using var r = new BinaryReader(ms);
        Assert.Throws<CheckpointException>(() => differentGeometry.ReadState(r));
    }

    // ── Snapshot introspection ────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_ReflectsResidentLine() {
        var cache = new CeaserCache(new FlatMemory(4096), 1024, 4, 32, 10);
        cache.Read(0x300, 4);

        CeaserCacheLine[] snapshot = cache.GetSnapshot();
        CeaserCacheLine? resident = snapshot.FirstOrDefault(l => l.Valid && l.Address == 0x300);
        Assert.NotNull(resident);
    }
}
