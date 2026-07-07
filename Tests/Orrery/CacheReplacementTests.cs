using Mechanism;
using Orrery.Cache;
using RiscV32.Memory;

namespace Tests.Orrery;

/// <summary>
/// Unit tests for SRRIP, BRRIP, and DRRIP replacement policies.
/// Policy internals are exercised through both the policy classes directly and
/// through SetAssociativeCache to confirm end-to-end integration.
///
/// All RRIP tests use M=2 (2-bit RRPV; values 0–3) as the paper recommends.
///   0 = near-immediate  (hit-promoted)
///   2 = long            (SRRIP insertion, bimodal "lucky" insert)
///   3 = distant         (victim target; BRRIP default insertion)
/// </summary>
public class CacheReplacementTests {
    // ── Helpers ───────────────────────────────────────────────────────────────

    // 4-way, 1-set SRRIP cache (64 B capacity / 4 ways / 16 B line = 1 set).
    private static SetAssociativeCache MakeSrrip1Set(IMemory backing) =>
        new(backing, 64, 4, 16, 10, 0, ReplacementPolicyKind.Srrip);

    // Addresses that all map to set 0 (stride = 2*sets*blockSize = 32 for 1-set cache).
    private const ulong A1 = 0x00, A2 = 0x10, A3 = 0x20, A4 = 0x30;
    private const ulong B1 = 0x40, B2 = 0x50, B3 = 0x60;

    // ── SRRIP: insertion RRPV ────────────────────────────────────────────────

    [Fact]
    public void Srrip_Insert_PlacesAtLongRrpv() {
        // SRRIP inserts at RRPV 2 (long), not 0 (near-immediate) or 3 (distant).
        var policy = new SrripPolicy(1, 4);
        policy.ChooseVictim(0); // call ChooseVictim first (as the cache would)
        policy.RecordInstall(0, 0);
        Assert.Equal(2, policy.GetMetadata(0, 0));
    }

    [Fact]
    public void Srrip_HitPromotion_SetsRrpvToZero() {
        var policy = new SrripPolicy(1, 4);
        policy.RecordInstall(0, 0); // RRPV = 2
        policy.RecordHit(0, 0);     // HP: RRPV → 0
        Assert.Equal(0, policy.GetMetadata(0, 0));
    }

    [Fact]
    public void Srrip_VictimSearch_IncrementsAllWhenNoDistantEntry() {
        // Fill all 4 ways; each gets RRPV=2. On the next miss, no way has RRPV=3,
        // so all get incremented to 3, then way 0 is returned as the victim.
        var policy = new SrripPolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            policy.ChooseVictim(0);
            policy.RecordInstall(0, w);
        }

        // All ways now at RRPV=2. Verify.
        for (var w = 0; w < 4; w++) Assert.Equal(2, policy.GetMetadata(0, w));

        int victim = policy.ChooseVictim(0);
        // After incrementing, all ways reach RRPV=3; way 0 is the first.
        Assert.Equal(0, victim);
        for (var w = 0; w < 4; w++) Assert.Equal(3, policy.GetMetadata(0, w));
    }

    [Fact]
    public void Srrip_VictimSearch_SelectsFirstDistantWay() {
        // Way 1 starts at distant (3), others at near-immediate (0).
        var policy = new SrripPolicy(1, 4);
        for (var w = 0; w < 4; w++) policy.RecordHit(0, w); // all → 0
        // Manually corrupt way 1 by filling it without a subsequent hit.
        policy.RecordInstall(0, 1); // RRPV=2
        // Increment all twice to reach 3 → but we want only way 1 at 3.
        // Instead, fill way 1 and then do two increment steps.
        // Easier: install way 1, install way 1 again after incrementing.
        // Just use the existing way 1 at RRPV=2 and bump it manually via a victim search.
        // Actually: fill way 2 and way 3 so they also have RRPV=2; way 0 and way 1 get hit.
        // Skip: just verify selecting way 1 directly.
        policy.RecordInstall(0, 2); // RRPV=2
        policy.RecordInstall(0, 3); // RRPV=2
        // ways: 0=0, 1=2, 2=2, 3=2. Next victim: no RRPV=3 → increment → 0=1, 1=3, 2=3, 3=3.
        // First way with RRPV=3 is way 1.
        int victim = policy.ChooseVictim(0);
        Assert.Equal(1, victim);
    }

    // ── SRRIP: scan resistance ────────────────────────────────────────────────

    [Fact]
    public void Srrip_ScanResistance_PreservesActiveWorkingSet() {
        // Scenario from the paper (§4.2, Figure 3c): 4-way cache, working set {a1,a2,a3},
        // scan of length 3 {b1,b2,b3}. SRRIP preserves the working set after the scan.
        // Scan resistance holds when Slen ≤ (2^M−1)×(A−w) = 3×(4−3) = 3 (Eq. 1).
        var mem = new FlatMemory(1024);
        foreach (ulong a in new[] {
                     CacheReplacementTests.A1, CacheReplacementTests.A2, CacheReplacementTests.A3,
                     CacheReplacementTests.B1, CacheReplacementTests.B2, CacheReplacementTests.B3,
                 })
            mem.Load(a, [0xAA,]);

        SetAssociativeCache cache = MakeSrrip1Set(mem);

        // Fill working set (cold misses).
        cache.Read(CacheReplacementTests.A1, 1);
        cache.Read(CacheReplacementTests.A2, 1);
        cache.Read(CacheReplacementTests.A3, 1);
        cache.ConsumePendingStalls();

        // Re-access working set → RRPV = 0.
        cache.Read(CacheReplacementTests.A1, 1);
        cache.Read(CacheReplacementTests.A2, 1);
        cache.Read(CacheReplacementTests.A3, 1);

        // Scan (3 blocks, no reuse).
        cache.Read(CacheReplacementTests.B1, 1);
        cache.Read(CacheReplacementTests.B2, 1);
        cache.Read(CacheReplacementTests.B3, 1);
        cache.ConsumePendingStalls();

        long hitsBefore = cache.Hits;

        // Working set re-access: all must be hits.
        cache.Read(CacheReplacementTests.A1, 1);
        cache.Read(CacheReplacementTests.A2, 1);
        cache.Read(CacheReplacementTests.A3, 1);

        Assert.Equal(hitsBefore + 3, cache.Hits); // all three are cache hits
        // No additional misses after working-set re-access.
        Assert.Equal(0, cache.ConsumePendingStalls());
    }

    [Fact]
    public void Srrip_LruWouldFail_ScanResistance() {
        // The same scan scenario fails under LRU: the working-set blocks are evicted.
        var mem = new FlatMemory(1024);
        foreach (ulong a in new[] {
                     CacheReplacementTests.A1, CacheReplacementTests.A2, CacheReplacementTests.A3,
                     CacheReplacementTests.B1, CacheReplacementTests.B2, CacheReplacementTests.B3,
                 })
            mem.Load(a, [0xAA,]);

        var cache = new SetAssociativeCache(mem, 64, 4, 16, 10); // LRU by default

        cache.Read(CacheReplacementTests.A1, 1);
        cache.Read(CacheReplacementTests.A2, 1);
        cache.Read(CacheReplacementTests.A3, 1);
        cache.ConsumePendingStalls();
        cache.Read(CacheReplacementTests.A1, 1);
        cache.Read(CacheReplacementTests.A2, 1);
        cache.Read(CacheReplacementTests.A3, 1); // hits, all promoted to MRU
        cache.Read(CacheReplacementTests.B1, 1);
        cache.Read(CacheReplacementTests.B2, 1);
        cache.Read(CacheReplacementTests.B3, 1); // scan evicts A3, A2, A1
        cache.ConsumePendingStalls();

        long missesBefore = cache.Misses;
        // At least some working-set accesses should be misses under LRU after the scan.
        cache.Read(CacheReplacementTests.A1, 1);
        cache.Read(CacheReplacementTests.A2, 1);
        cache.Read(CacheReplacementTests.A3, 1);
        Assert.True(cache.Misses > missesBefore);
    }

    // ── BRRIP: insertion behaviour ────────────────────────────────────────────

    [Fact]
    public void Brrip_Insert_MostlyAtDistant_OccasionallyAtLong() {
        // ε=1/32: first 31 installs are distant (RRPV=3), 32nd is long (RRPV=2), 33rd is distant again.
        // Test directly on the policy (33 installs into distinct ways in a 64-way set) so that
        // distant-RRPV blocks are not immediately re-evicted by the cache's victim selection.
        var policy = new BrripPolicy(1, 64, bimodalDenominator: 32);

        int longCount = 0, distantCount = 0;
        for (var w = 0; w < 33; w++) {
            // ChooseVictim is called first in the cache; call it here too to match the sequence.
            policy.ChooseVictim(0);
            policy.RecordInstall(0, w);
            int rrpv = policy.GetMetadata(0, w);
            switch (rrpv) {
                case 2: longCount++; break;
                case 3: distantCount++; break;
            }
        }

        Assert.Equal(1, longCount);
        Assert.Equal(32, distantCount);
    }

    // ── DRRIP: PSEL counter ───────────────────────────────────────────────────

    [Fact]
    public void Drrip_SrripSdmMiss_IncrementsPsel() {
        // Misses in SDM_SRRIP sets should increment PSEL.
        const int sets = 16;
        var policy = new DrripPolicy(sets, 4, 2, 2);
        int initial = policy.Psel;

        // Set 0 is SDM_SRRIP. Force a victim then record installs.
        for (var i = 0; i < 5; i++) {
            policy.ChooseVictim(0);
            policy.RecordInstall(0, 0); // 5 SRRIP SDM misses → PSEL should increase by 5
        }

        Assert.Equal(initial + 5, policy.Psel);
    }

    [Fact]
    public void Drrip_BrripSdmMiss_DecrementsPsel() {
        // Misses in SDM_BRRIP sets should decrement PSEL.
        const int sets = 16;
        var policy = new DrripPolicy(sets, 4, 2, 2);
        int initial = policy.Psel;

        // Set 2 is SDM_BRRIP (sdmSets=2, so SDM_BRRIP = sets [2, 4)).
        for (var i = 0; i < 5; i++) {
            policy.ChooseVictim(2);
            policy.RecordInstall(2, 0); // 5 BRRIP SDM misses → PSEL decreases by 5
        }

        Assert.Equal(initial - 5, policy.Psel);
    }

    [Fact]
    public void Drrip_FollowerUsesBrripWhenPselHigh() {
        // When PSEL ≥ threshold (512 for 10-bit), follower sets use BRRIP insertion
        // (mostly distant = RRPV 3, occasional long = RRPV 2).
        // Drive PSEL above threshold via SDM_SRRIP misses, then check follower inserts.
        const int sets = 16;
        var policy = new DrripPolicy(sets, 4, 2, 2);

        // Need 512 - initial + 1 SRRIP SDM misses to exceed the threshold.
        // initial = 511 (pselThreshold - 1 = 511).
        // threshold = 512. So we need 1 SRRIP miss to bring it to 512 → BRRIP wins.
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0); // PSEL → 512 (BRRIP wins)
        Assert.True(policy.Psel >= 512);

        // Now follower set (set 4) should use BRRIP insertion.
        // Do 33 installs into follower way 0 (the bimodal period).
        // First 32: distant (RRPV 3); 33rd: long (RRPV 2).
        int longCount = 0, distantCount = 0;
        for (var i = 0; i < 33; i++) {
            policy.ChooseVictim(4);
            policy.RecordInstall(4, 0);
            int rrpv = policy.GetMetadata(4, 0);
            switch (rrpv) {
                case 2: longCount++; break;
                case 3: distantCount++; break;
            }
        }

        Assert.Equal(1, longCount);
        Assert.Equal(32, distantCount);
    }

    [Fact]
    public void Drrip_FollowerUsesSrripWhenPselLow() {
        // When PSEL < threshold, follower sets use SRRIP insertion (always RRPV 2).
        const int sets = 16;
        var policy = new DrripPolicy(sets, 4, 2, 2);
        // Initial PSEL = 511 < 512 → SRRIP wins.
        Assert.True(policy.Psel < 512);

        // Follower set (set 4): all installs should produce RRPV=2 (SRRIP).
        for (var i = 0; i < 10; i++) {
            policy.ChooseVictim(4);
            policy.RecordInstall(4, 0);
            Assert.Equal(2, policy.GetMetadata(4, 0));
        }
    }

    // ── DRRIP: thrash resistance (end-to-end through cache) ───────────────────

    [Fact]
    public void Drrip_ThrashingPattern_FollowerSwitchesToBrrip() {
        // After enough SRRIP SDM misses PSEL exceeds the threshold, switching followers to BRRIP.
        // Verify through the policy directly: thrash the SDM_SRRIP set, then confirm that a
        // follower set uses BRRIP insertion (bimodal pattern: mostly distant, 1/32 long).
        const int sets = 16;
        var policy = new DrripPolicy(sets, 4, 2, 2);

        // Need 1 miss on SDM_SRRIP set to push PSEL from 511 → 512.
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);
        Assert.True(policy.Psel >= 512, $"PSEL={policy.Psel} expected >= 512 after SRRIP SDM miss");

        // Follower set (set 4): 33 installs should exhibit bimodal BRRIP pattern.
        // The policy has only 4 ways, so ChooseVictim picks the way and RecordInstall sets it.
        // We record the RRPV assigned by RecordInstall for each of the 33 calls.
        int longCount = 0, distantCount = 0;
        for (var i = 0; i < 33; i++) {
            int victim = policy.ChooseVictim(4);
            policy.RecordInstall(4, victim);
            switch (policy.GetMetadata(4, victim)) {
                case 2: longCount++; break;
                case 3: distantCount++; break;
            }
        }

        Assert.Equal(1, longCount);
        Assert.Equal(32, distantCount);
    }

    // ── PSEL saturation ───────────────────────────────────────────────────────

    [Fact]
    public void Drrip_Psel_SaturatesAtBothEnds() {
        var policy = new DrripPolicy(16, 4, 2, 2);
        // Drive PSEL to max via SDM_SRRIP misses.
        for (var i = 0; i < 2000; i++) {
            policy.ChooseVictim(0);
            policy.RecordInstall(0, 0);
        }

        Assert.Equal(1023, policy.Psel);

        // Drive PSEL to min via SDM_BRRIP misses.
        for (var i = 0; i < 2000; i++) {
            policy.ChooseVictim(2);
            policy.RecordInstall(2, 0);
        }

        Assert.Equal(0, policy.Psel);
    }

    // ── SHiP: SHCT and insertion RRPV ────────────────────────────────────────

    [Fact]
    public void Ship_ColdInstall_InsertsAtDistant() {
        // SHCT starts at zero for every signature; first install → RRPV = 3 (distant).
        var policy = new ShipPolicy(1, 4);
        policy.SetPendingSignature(0x42);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);
        Assert.Equal(3, policy.GetMetadata(0, 0));
    }

    [Fact]
    public void Ship_HitIncrementsShct_AndSetsOutcome() {
        // After a hit, SHCT[sig] increases from 0 → 1, making future installs insert at long.
        var policy = new ShipPolicy(1, 4);
        policy.SetPendingSignature(0x10);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);
        Assert.Equal(0, policy.GetShctCounter(0x10)); // SHCT still 0 before any hit

        policy.RecordHit(0, 0);
        Assert.Equal(1, policy.GetShctCounter(0x10));

        // Install another line with the same signature → RRPV = 2 (long), not 3 (distant).
        policy.SetPendingSignature(0x10);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 1);
        Assert.Equal(2, policy.GetMetadata(0, 1));
    }

    [Fact]
    public void Ship_EvictionWithoutReuse_DecrementsShct() {
        // A line that is evicted without ever being hit causes SHCT[sig] to decrement.
        var policy = new ShipPolicy(1, 4);
        policy.SetPendingSignature(0x20);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);

        // Warm SHCT[0x20] up to 2 via another install path.
        policy.SetPendingSignature(0x20);
        policy.RecordHit(0, 0); // SHCT → 1
        policy.RecordHit(0, 0); // SHCT → 2

        // Now install a new line into way 0 (evicts the existing one without a fresh hit since counter was reset).
        // Re-install: outcome is still true from the hits; that blocks decrement.
        // To test the decrement path we need a line installed, never hit, then evicted.
        // Reset: install into way 1 with fresh sig 0x21, never hit it, then evict it.
        policy.SetPendingSignature(0x21);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 1); // SHCT[0x21]==0 → distant; outcome=false
        // Warm SHCT[0x21] to 2 via hits on a different way with same sig.
        policy.RecordHit(0, 1); // SHCT[0x21] → 1
        policy.RecordHit(0, 1); // SHCT[0x21] → 2
        // outcome[way1] is now true. Re-install way1 with a new sig to reset outcome.
        policy.SetPendingSignature(0x21);
        policy.RecordInstall(0, 1); // evicts (outcome=true → no decrement); new outcome=false, SHCT[0x21]=2→ long
        // Now way 1 has sig=0x21, outcome=false, RRPV=2.
        // Evict way 1 without a hit → decrement SHCT[0x21].
        int before = policy.GetShctCounter(0x21);
        policy.SetPendingSignature(0x99);
        policy.RecordInstall(0, 1); // evicts way 1 (outcome=false) → SHCT[0x21]--
        Assert.Equal(before - 1, policy.GetShctCounter(0x21));
    }

    [Fact]
    public void Ship_EvictionWithReuse_DoesNotDecrementShct() {
        // A line that is hit before eviction keeps its SHCT counter intact.
        var policy = new ShipPolicy(1, 4);
        policy.SetPendingSignature(0x30);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0); // outcome=false
        policy.RecordHit(0, 0);     // outcome → true, SHCT[0x30] → 1

        int before = policy.GetShctCounter(0x30);
        policy.SetPendingSignature(0x40);
        policy.RecordInstall(0, 0); // evicts (outcome=true → no decrement)
        Assert.Equal(before, policy.GetShctCounter(0x30));
    }

    [Fact]
    public void Ship_ShctSaturates() {
        // SHCT counter saturates at 7 (3-bit max); RecordHit beyond 7 does not overflow.
        var policy = new ShipPolicy(1, 4);
        policy.SetPendingSignature(0x50);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);
        for (var i = 0; i < 20; i++) policy.RecordHit(0, 0);
        Assert.Equal(7, policy.GetShctCounter(0x50));
    }

    [Fact]
    public void Ship_ColdSlot_DoesNotDecrementShct() {
        // A cold (never-installed) way evicted by RecordInstall must not touch SHCT[0].
        var policy = new ShipPolicy(1, 4);
        int before = policy.GetShctCounter(0);
        policy.SetPendingSignature(0x60);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);                     // way 0 was cold
        Assert.Equal(before, policy.GetShctCounter(0)); // SHCT[0] unchanged
    }

    [Fact]
    public void Ship_MultipleHits_IncrementShctEachTime() {
        // Every hit on the same line increments SHCT, not just the first.
        var policy = new ShipPolicy(1, 4);
        policy.SetPendingSignature(0x70);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);
        for (var i = 1; i <= 5; i++) {
            policy.RecordHit(0, 0);
            Assert.Equal(i, policy.GetShctCounter(0x70));
        }
    }

    // ── SHiP: end-to-end through SetAssociativeCache ─────────────────────────

    [Fact]
    public void Ship_EndToEnd_SetPendingSignatureRoutedThroughCache() {
        // Integration test: confirms that SetPendingSignature dispatches to ShipPolicy
        // (not to the DIM no-op) and that reuse history is updated on demand hits.
        // 4-way 1-set cache; address A1 maps to set 0 with signature = (A1 >> offsetBits).
        var mem = new FlatMemory(1024);
        foreach (ulong a in new[] {
                     CacheReplacementTests.A1, CacheReplacementTests.A2, CacheReplacementTests.A3,
                     CacheReplacementTests.A4,
                 })
            mem.Load(a, [0xBB,]);

        var cache = new SetAssociativeCache(mem, 64, 4, 16, 10, 0, ReplacementPolicyKind.Ship);

        // Cold miss on A1 → installed at RRPV=3 (distant, SHCT[sig]==0).
        cache.Read(CacheReplacementTests.A1, 1);
        cache.ConsumePendingStalls();

        // Demand hit on A1 → RRIP-HP sets RRPV=0; SHiP increments SHCT[sig] → 1.
        long hitsBefore = cache.Hits;
        cache.Read(CacheReplacementTests.A1, 1);
        Assert.Equal(hitsBefore + 1, cache.Hits); // it was a hit

        // Fill remaining ways: A2, A3, A4 (cold misses; all get RRPV=3 for their sig).
        cache.Read(CacheReplacementTests.A2, 1);
        cache.Read(CacheReplacementTests.A3, 1);
        cache.Read(CacheReplacementTests.A4, 1);
        cache.ConsumePendingStalls();

        // Now re-access A1: it was hit-promoted to RRPV=0, so it must survive the fills.
        long hitsAfter = cache.Hits;
        cache.Read(CacheReplacementTests.A1, 1);
        Assert.Equal(hitsAfter + 1, cache.Hits); // A1 is still in cache
    }

    [Fact]
    public void Ship_EndToEnd_WarmSignatureInsertedAtLongRrpv() {
        // Verifies via GetSnapshot that a block reinstalled after eviction gets RRPV=2 (long)
        // when SHCT[sig] > 0, not RRPV=3 (distant) as it would for a cold signature.
        //
        // Phase 1: fill all 4 ways with miss-then-hit pairs so every block is at RRPV=0
        //          and SHCT[sig_x]=1 for each signature.
        // Phase 2: one new miss forces ChooseVictim to increment all RRPV 0→1→2→3; A is
        //          evicted and replaced by E (cold, RRPV=3 for its signature).
        // Phase 3: reinstall A — SHCT[sig(A)]=1 → insert at RRPV=2, not 3.
        //          GetSnapshot confirms LruAge=2 for A's way.
        var mem = new FlatMemory(4096);
        // 4-way 1-set cache (256 B / 4 ways / 64 B line = 1 set; offsetBits=6).
        const ulong a = 0x000, b = 0x040, c = 0x080, d = 0x0C0, e = 0x100;
        foreach (ulong x in new[] { a, b, c, d, e, }) mem.Load(x, [0xEE,]);

        var cache = new SetAssociativeCache(mem, 256, 4, 64, 10, 0, ReplacementPolicyKind.Ship);

        // Phase 1: miss + hit each block to warm SHCT and leave all at RRPV=0.
        cache.Read(a, 1);
        cache.Read(a, 1); // cold miss → way 0 RRPV=3; hit → RRPV=0, SHCT[0]→1
        cache.Read(b, 1);
        cache.Read(b, 1); // → way 1 RRPV=0, SHCT[1]→1
        cache.Read(c, 1);
        cache.Read(c, 1); // → way 2 RRPV=0, SHCT[2]→1
        cache.Read(d, 1);
        cache.Read(d, 1); // → way 3 RRPV=0, SHCT[3]→1
        cache.ConsumePendingStalls();

        // Phase 2: miss E — all RRPV=0, so ChooseVictim increments 0→1→2→3;
        // evicts A (way 0, the first to reach 3). E inserted cold (RRPV=3).
        cache.Read(e, 1);
        cache.ConsumePendingStalls();

        // Phase 3: miss A — SHCT[sig(A)] = 1 > 0 → insert at RRPV=2 (long), not 3.
        cache.Read(a, 1);
        cache.ConsumePendingStalls();

        // Tag for A = 0x000 >> 6 = 0.
        CacheLine[] snap = cache.GetSnapshot();
        CacheLine? aLine = snap.FirstOrDefault(l => l is { Valid: true, Tag: 0, });

        Assert.NotNull(aLine);
        Assert.Equal(2, aLine.LruAge); // warm SHCT → RRPV = 2 (long)
    }

    [Fact]
    public void ShipPc_CrossAddressWarmBucket_InsertedAtLongRrpv() {
        // SHiP-PC indexes SHCT by load PC, not by address block number.
        // Two loads with the same PC but different addresses share the same SHCT bucket.
        // Warming the bucket through addr_A (miss + hit, PC=pcX) must cause addr_B
        // (different address, hence different SHiP-Mem signature, but same PC=pcX)
        // to install at RRPV=2 on its first miss — not RRPV=3 as SHiP-Mem would give.
        //
        // Geometry: 4-way, 4-set, 32-byte blocks (512 B total).
        //   offsetBits=5, indexBits=2.
        //   addr_A=0x000 → set 0, SHiP-Mem sig=0.
        //   addr_B=0x0A0 → set 1, SHiP-Mem sig=5 (cold bucket under SHiP-Mem).
        //   Both use PC=0x1000 → same SHiP-PC SHCT bucket.
        const ulong addrA = 0x000;
        const ulong addrB = 0x0A0;
        const ulong pc = 0x1000;

        var mem = new FlatMemory(512);
        foreach (ulong a in new[] { addrA, addrB, }) mem.Load(a, [0xAA,]);

        var cache = new SetAssociativeCache(mem, 512, 4, 32, 10, 0, ReplacementPolicyKind.ShipPc);

        // Warm SHCT[pc & 0x3FFF] via miss+hit on addr_A.
        cache.SetRequestPc(pc);
        cache.Read(addrA, 1); // cold miss → RRPV=3, SHCT[pc&mask]=0
        cache.SetRequestPc(pc);
        cache.Read(addrA, 1); // hit → RRPV=0, SHCT[pc&mask]=1
        cache.ConsumePendingStalls();

        // Miss addr_B with the same PC. SHCT[pc&mask]=1 > 0 → install at RRPV=2.
        // Under SHiP-Mem the signature would be addr_B>>5=5 (cold bucket → RRPV=3).
        cache.SetRequestPc(pc);
        cache.Read(addrB, 1);
        cache.ConsumePendingStalls();

        // addr_B: set=(0x0A0>>5)&3=1, tag=0x0A0>>7=1. Verify RRPV=2 (warm PC bucket).
        CacheLine[] snap = cache.GetSnapshot();
        CacheLine? bLine = snap.FirstOrDefault(l => l is { Valid: true, Set: 1, Tag: 1, });

        Assert.NotNull(bLine);
        Assert.Equal(2, bLine.LruAge); // RRPV=2 proves SHCT was indexed by PC, not address
    }

    // ── FIFO ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Fifo_EvictsInInstallOrder() {
        // First four misses each get a fresh way; the fifth must evict way 0 (first installed).
        var policy = new FifoPolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            Assert.Equal(w, policy.ChooseVictim(0)); // gets the next empty way in order
            policy.RecordInstall(0, w);
        }

        Assert.Equal(0, policy.ChooseVictim(0)); // pointer wrapped back to way 0
    }

    [Fact]
    public void Fifo_HitDoesNotChangeEvictionOrder() {
        // FIFO ignores hits. After filling all 4 ways and hitting way 0 many times,
        // way 0 is still the next victim (unlike LRU, which would protect it).
        var policy = new FifoPolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            policy.ChooseVictim(0);
            policy.RecordInstall(0, w);
        }

        for (var i = 0; i < 10; i++) policy.RecordHit(0, 0);
        Assert.Equal(0, policy.ChooseVictim(0)); // way 0 still next, hits are irrelevant
    }

    [Fact]
    public void Fifo_CyclesCorrectlyAfterEvictions() {
        // After filling all 4 ways, the eviction order cycles 0→1→2→3→0→…
        var policy = new FifoPolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            policy.ChooseVictim(0);
            policy.RecordInstall(0, w);
        }

        for (var w = 0; w < 8; w++) {
            Assert.Equal(w % 4, policy.ChooseVictim(0));
            policy.RecordInstall(0, w % 4);
        }
    }

    [Fact]
    public void Fifo_EndToEnd_HitDoesNotPreventEviction() {
        // Integration: fill 4-way 1-set FIFO cache, repeatedly hit A1, then miss A5.
        // FIFO must evict A1 (first installed); LRU would have evicted A2 instead.
        const ulong a1 = 0x00, a2 = 0x10, a3 = 0x20, a4 = 0x30, a5 = 0x40;
        var mem = new FlatMemory(256);
        foreach (ulong a in new[] { a1, a2, a3, a4, a5, }) mem.Load(a, [0xBB,]);

        var cache = new SetAssociativeCache(mem, 64, 4, 16, 10, 0, ReplacementPolicyKind.Fifo);
        cache.Read(a1, 1);
        cache.Read(a2, 1);
        cache.Read(a3, 1);
        cache.Read(a4, 1);
        cache.ConsumePendingStalls();

        for (var i = 0; i < 5; i++) cache.Read(a1, 1); // promote A1 in LRU terms; FIFO ignores

        cache.Read(a5, 1); // evicts A1 (first installed)
        cache.ConsumePendingStalls();

        // A2 must still be present (only A1 was evicted, FIFO didn't touch A2 yet).
        long hitsA2 = cache.Hits;
        cache.Read(a2, 1);
        Assert.Equal(hitsA2 + 1, cache.Hits);

        // A1 must be gone: FIFO evicted it despite the repeated hits.
        long missesBefore = cache.Misses;
        cache.Read(a1, 1);
        Assert.Equal(missesBefore + 1, cache.Misses);
    }

    // ── CLOCK ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Clock_FreshCache_FillsSequentially() {
        // All reference bits start at 0; hand starts at 0.
        // ChooseVictim returns way 0 without scanning, then RecordInstall advances hand to 1.
        var policy = new ClockPolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            Assert.Equal(w, policy.ChooseVictim(0));
            policy.RecordInstall(0, w);
        }
    }

    [Fact]
    public void Clock_SecondChance_ReferencedWaySkipped() {
        // Set up a known state: bits=[1,0,1,0], hand=0.
        // ChooseVictim must skip way 0 (bit=1, clear it), then return way 1 (bit=0).
        var policy = new ClockPolicy(1, 4);
        policy.RecordInstall(0, 0); // bit[0]=1, hand=1
        policy.RecordInstall(0, 2); // bit[2]=1, hand=3
        // bit=[1,0,1,0], hand=3

        // Advance hand to 0 by finding the victim at 3 (bit=0), then installing.
        Assert.Equal(3, policy.ChooseVictim(0)); // hand=3, bit=0 → returns 3
        policy.RecordInstall(0, 3);              // bit[3]=1, hand=0

        // Now bits=[1,0,1,1], hand=0.
        // ChooseVictim: bit[0]=1→clear→hand=1; bit[1]=0 → return 1.
        Assert.Equal(1, policy.ChooseVictim(0));
    }

    [Fact]
    public void Clock_AllReferenced_SweepsAllAndEvictsHandWay() {
        // When every way has bit=1, one full sweep clears them all;
        // the way at the original hand position (0) is returned.
        var policy = new ClockPolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            policy.ChooseVictim(0);
            policy.RecordInstall(0, w);
        }

        // All bits=1, hand=0 (wrap-around after install(3)).
        Assert.Equal(0, policy.ChooseVictim(0));
    }

    [Fact]
    public void Clock_HitSetsReferenceBit() {
        // After filling 4 ways (all bits=1, hand=0), one sweep clears all and evicts way 0.
        // Then hit way 2 → bit[2]=1. Next victim skips way 2 and takes way 1 instead.
        var policy = new ClockPolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            policy.ChooseVictim(0);
            policy.RecordInstall(0, w);
        }

        // First eviction: sweep clears all bits, evicts way 0.
        Assert.Equal(0, policy.ChooseVictim(0));
        policy.RecordInstall(0, 0); // bit[0]=1, hand=1; bits=[1,0,0,0]

        policy.RecordHit(0, 2); // bits=[1,0,1,0], hand=1

        // ChooseVictim from hand=1: bit[1]=0 → return 1 (way 2 was skipped because bit=1).
        Assert.Equal(1, policy.ChooseVictim(0));
    }

    [Fact]
    public void Clock_EndToEnd_RecentlyHitLineSurvivesEviction() {
        // Integration: after filling A1–A4, the first miss (A5) sweeps all bits and evicts A1.
        // Then hitting A3 sets its reference bit. The next miss (A6) must evict A2 (bit=0),
        // not A3 (bit=1, gets a second chance).
        const ulong a1 = 0x00, a2 = 0x10, a3 = 0x20, a4 = 0x30, a5 = 0x40, a6 = 0x50;
        var mem = new FlatMemory(512);
        foreach (ulong a in new[] { a1, a2, a3, a4, a5, a6, }) mem.Load(a, [0xEE,]);

        var cache = new SetAssociativeCache(mem, 64, 4, 16, 10, 0, ReplacementPolicyKind.Clock);
        cache.Read(a1, 1);
        cache.Read(a2, 1);
        cache.Read(a3, 1);
        cache.Read(a4, 1);
        cache.ConsumePendingStalls();

        // Miss A5 — sweeps all bits (second chance), evicts A1 (way 0). Bits: [1,0,0,0], hand=1.
        cache.Read(a5, 1);
        cache.ConsumePendingStalls();

        // Hit A3 — sets A3's reference bit to 1. Bits: [1,0,1,0], hand=1.
        cache.Read(a3, 1);

        // Miss A6 — hand=1, bit[A2]=0 → A2 evicted (A3 survives via second chance).
        cache.Read(a6, 1);
        cache.ConsumePendingStalls();

        // A3 must still be in cache (second chance protected it).
        // Check BEFORE reading A2 to avoid reinstall side-effects.
        long hitsA3 = cache.Hits;
        cache.Read(a3, 1);
        Assert.Equal(hitsA3 + 1, cache.Hits);

        // A2 was evicted (bit was 0 when hand passed it).
        long missesA2 = cache.Misses;
        cache.Read(a2, 1);
        Assert.Equal(missesA2 + 1, cache.Misses);
    }

    // ── MRU ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Mru_FillOrder_Sequential() {
        // Install puts new lines at LRU (oldest) position, so ChooseVictim cycles through
        // ways in sequential order during initial fill — same as LRU cold start.
        var policy = new MruPolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            Assert.Equal(w, policy.ChooseVictim(0));
            policy.RecordInstall(0, w);
        }
    }

    [Fact]
    public void Mru_HitPromotion_MakesWayNextVictim() {
        // After filling all ways, hitting way 2 promotes it to age 0 (MRU = next victim).
        // This is the inverse of LRU, where a hit protects the way.
        var policy = new MruPolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            policy.ChooseVictim(0);
            policy.RecordInstall(0, w);
        }

        policy.RecordHit(0, 2);
        Assert.Equal(2, policy.ChooseVictim(0));
    }

    [Fact]
    public void Mru_InverseOfLru_HittingLruCandidateMakesItVictim() {
        // Under LRU, hitting way 3 (the LRU candidate) protects it; way 2 becomes LRU.
        // Under MRU, hitting way 3 makes it the next victim regardless of prior install order.
        var policy = new MruPolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            policy.ChooseVictim(0);
            policy.RecordInstall(0, w);
        }

        // Before any hit, way 0 is at age 0 (victim from cold-fill order).
        // Hit way 3 → way 3 promoted to age 0 → way 3 is now the victim.
        policy.RecordHit(0, 3);
        Assert.Equal(3, policy.ChooseVictim(0));
    }

    [Fact]
    public void Mru_EndToEnd_HitLineEvictedBeforeUnhitLine() {
        // Integration: 4-way 1-set MRU cache. Fill A1–A4, hit A4, miss A5.
        // MRU must evict A4 (most recently hit); LRU would have protected A4 and evicted A1.
        const ulong a1 = 0x00, a2 = 0x10, a3 = 0x20, a4 = 0x30, a5 = 0x40;
        var mem = new FlatMemory(256);
        foreach (ulong a in new[] { a1, a2, a3, a4, a5, }) mem.Load(a, [0xDD,]);

        var cache = new SetAssociativeCache(mem, 64, 4, 16, 10, 0, ReplacementPolicyKind.Mru);
        cache.Read(a1, 1);
        cache.Read(a2, 1);
        cache.Read(a3, 1);
        cache.Read(a4, 1);
        cache.ConsumePendingStalls();

        // Hit A4 to make it the MRU (next eviction candidate).
        cache.Read(a4, 1);

        cache.Read(a5, 1); // forces an eviction — must evict A4 (most recently hit)
        cache.ConsumePendingStalls();

        // A3 must still be present (it was never hit after fill, so it survived).
        // Check BEFORE reading A4 to avoid reinstall side-effects.
        long hitsA3 = cache.Hits;
        cache.Read(a3, 1);
        Assert.Equal(hitsA3 + 1, cache.Hits);

        // A4 must be gone — MRU evicted the most recently hit line.
        long missesA4 = cache.Misses;
        cache.Read(a4, 1);
        Assert.Equal(missesA4 + 1, cache.Misses);
    }

    // ── Tree-PLRU ─────────────────────────────────────────────────────────────

    [Fact]
    public void Plru_FreshCache_EvictsWay0() {
        // All bits start false → root points left → victim is always way 0 before any installs.
        var policy = new PlruPolicy(1, 4);
        Assert.Equal(0, policy.ChooseVictim(0));
    }

    [Fact]
    public void Plru_FillOrderIsNonSequential() {
        // 4-way Tree-PLRU: the fill order driven by ChooseVictim is 0, 2, 1, 3
        // (not sequential) because each install flips tree bits toward the other subtree.
        var policy = new PlruPolicy(1, 4);
        int[] expected = [0, 2, 1, 3,];
        for (var i = 0; i < 4; i++) {
            Assert.Equal(expected[i], policy.ChooseVictim(0));
            policy.RecordInstall(0, expected[i]);
        }

        // After filling all 4, tree resets to the same initial state → way 0 again.
        Assert.Equal(0, policy.ChooseVictim(0));
    }

    [Fact]
    public void Plru_AfterAccessingWay0_EvictsFromRightSubtree() {
        // Fill all 4 ways, then hit way 0. The bits now point away from way 0's subtree,
        // so the victim must be way 2 (sibling leaf in the right subtree, not way 0).
        var policy = new PlruPolicy(1, 4);
        int[] fillOrder = [0, 2, 1, 3,];
        foreach (int w in fillOrder) {
            policy.ChooseVictim(0);
            policy.RecordInstall(0, w);
        }

        policy.RecordHit(0, 0); // promote way 0
        Assert.Equal(2, policy.ChooseVictim(0));
    }

    [Fact]
    public void Plru_TwoWay_IsExactLru() {
        // 2-way PLRU is exact LRU: after accessing way 0, way 1 is the victim and vice-versa.
        var policy = new PlruPolicy(1, 2);
        policy.RecordInstall(0, 0);
        Assert.Equal(1, policy.ChooseVictim(0)); // way 1 is LRU

        policy.RecordInstall(0, 1);
        Assert.Equal(0, policy.ChooseVictim(0)); // way 0 is now LRU

        policy.RecordHit(0, 0);                  // access way 0 again
        Assert.Equal(1, policy.ChooseVictim(0)); // way 1 back to LRU
    }

    [Fact]
    public void Plru_EndToEnd_MruProtectedOnMiss() {
        // Integration: 4-way 1-set PLRU cache. Fill A1–A4, repeatedly hit A1, then miss A5.
        // Tree-PLRU must protect A1 (recently accessed); A2 or A3 gets evicted instead.
        // Unlike FIFO which evicts A1 despite hits.
        const ulong a1 = 0x00, a2 = 0x10, a3 = 0x20, a4 = 0x30, a5 = 0x40;
        var mem = new FlatMemory(256);
        foreach (ulong a in new[] { a1, a2, a3, a4, a5, }) mem.Load(a, [0xCC,]);

        var cache = new SetAssociativeCache(mem, 64, 4, 16, 10, 0, ReplacementPolicyKind.Plru);
        cache.Read(a1, 1);
        cache.Read(a2, 1);
        cache.Read(a3, 1);
        cache.Read(a4, 1);
        cache.ConsumePendingStalls();

        // Repeatedly hit A1 so PLRU considers it recently used.
        for (var i = 0; i < 5; i++) cache.Read(a1, 1);

        cache.Read(a5, 1); // forces an eviction — must NOT evict A1
        cache.ConsumePendingStalls();

        // A1 must still be present (PLRU protects it due to recent access).
        // Check A1 hit BEFORE reading any other block to avoid reinstall side-effects.
        long hitsA1 = cache.Hits;
        cache.Read(a1, 1);
        Assert.Equal(hitsA1 + 1, cache.Hits);
    }

    // ── Random ────────────────────────────────────────────────────────────────

    [Fact]
    public void Random_ChooseVictim_AlwaysReturnsValidWay() {
        var policy = new RandomPolicy(1, 4);
        for (var i = 0; i < 200; i++) Assert.InRange(policy.ChooseVictim(0), 0, 3);
    }

    [Fact]
    public void Random_WithFixedSeed_CoversAllWays() {
        // With seed 0 and 4 ways, 100 calls should cover all four values.
        var policy = new RandomPolicy(1, 4);
        var seen = new HashSet<int>();
        for (var i = 0; i < 100; i++) seen.Add(policy.ChooseVictim(0));
        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void Random_HitAndInstall_AreNoops() {
        var policy = new RandomPolicy(1, 4);
        policy.RecordHit(0, 2);
        policy.RecordInstall(0, 2);
        Assert.Equal(0, policy.GetMetadata(0, 2));
    }

    // ── Hawkeye ───────────────────────────────────────────────────────────────

    [Fact]
    public void Hawkeye_ColdMiss_InsertsCacheAverse() {
        // First access to any tag: prevAbsTime=-1, OPT-miss, predictor 4→3 (<4),
        // so line is inserted at RRPV=7 (cache-averse).
        var policy = new HawkeyePolicy(1, 4);
        policy.SetPendingAddress(0x10UL, 100);
        policy.RecordInstall(0, 0);
        Assert.Equal(7, policy.GetMetadata(0, 0));
    }

    [Fact]
    public void Hawkeye_Hit_DecrementsRrpv() {
        var policy = new HawkeyePolicy(1, 4);
        // Install way 0 (cold miss → RRPV=7).
        policy.SetPendingAddress(0x10UL, 100);
        policy.RecordInstall(0, 0);
        Assert.Equal(7, policy.GetMetadata(0, 0));

        // A demand hit should decrement RRPV: 7→6.
        policy.RecordHitPc(0, 0, 0x10UL, 100);
        policy.RecordHit(0, 0);
        Assert.Equal(6, policy.GetMetadata(0, 0));
    }

    [Fact]
    public void Hawkeye_TrainedHotPc_InsertsCacheFriendly() {
        // After repeatedly hitting a line (driving the predictor counter above the
        // threshold), a subsequent install for the same PC must get RRPV=0.
        var policy = new HawkeyePolicy(1, 4);

        // First install (cold miss) → counter 4→3, RRPV=7.
        policy.SetPendingAddress(CacheReplacementTests.TagA, 1);
        policy.RecordInstall(0, 0);

        // Simulate 4 consecutive hits on the same line.
        // Each hit: OPTgen sees short interval with low occupancy → OPT-hit → counter++.
        // After 4 hits: counter = 3+4 = 7.
        for (var i = 0; i < 4; i++) {
            policy.RecordHitPc(0, 0, CacheReplacementTests.TagA, 1);
            policy.RecordHit(0, 0);
        }

        // Now simulate eviction + reinstall with the same PC.
        // OPTgen should see the recent liveness → OPT-hit → counter stays ≥ 4 → friendly.
        policy.SetPendingAddress(CacheReplacementTests.TagA, 1);
        policy.RecordInstall(0, 0);
        Assert.Equal(0, policy.GetMetadata(0, 0)); // cache-friendly → RRPV=0
    }

    [Fact]
    public void Hawkeye_ChooseVictim_PrefersMaxRrpv() {
        // Install all 4 ways (cold → RRPV=7 each), then hit way 0 (RRPV→6).
        // ChooseVictim must return one of ways 1–3 (still at 7), not way 0.
        var policy = new HawkeyePolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            policy.SetPendingAddress((ulong)(w + 1) * 0x10, (ulong)(w + 1));
            policy.RecordInstall(0, w);
        }

        // Hit way 0 to reduce its RRPV.
        policy.RecordHit(0, 0); // 7→6

        int victim = policy.ChooseVictim(0);
        Assert.NotEqual(0, victim); // way 0 is protected (RRPV=6 < 7)
        Assert.InRange(victim, 1, 3);
    }

    [Fact]
    public void Hawkeye_ChooseVictim_AgesWhenNoneAtMaxRrpv() {
        // Install all 4 ways, then hit all of them (RRPV 7→6). No way at RRPV=7.
        // ChooseVictim must age all by 1 and then return a valid way.
        var policy = new HawkeyePolicy(1, 4);
        for (var w = 0; w < 4; w++) {
            policy.SetPendingAddress((ulong)(w + 1) * 0x10, (ulong)(w + 1));
            policy.RecordInstall(0, w);
        }

        for (var w = 0; w < 4; w++) policy.RecordHit(0, w); // all RRPV 7→6

        int victim = policy.ChooseVictim(0); // must age 6→7 then return way 0
        Assert.InRange(victim, 0, 3);
        Assert.Equal(7, policy.GetMetadata(0, victim)); // victim is at 7 after aging
    }

    [Fact]
    public void Hawkeye_EndToEnd_BasicHitMiss() {
        // End-to-end: 4-way, 1-set Hawkeye cache. With an untrained predictor every
        // cold miss inserts at RRPV=7, so ChooseVictim always picks way 0 until a
        // line proves itself cache-friendly. Verify: most-recently-installed line hits.
        const ulong a = 0x00, b = 0x10, c = 0x20, d = 0x30;
        var mem = new FlatMemory(256);
        for (ulong i = 0; i < 256; i++) mem.Load(i, [0xCC,]);

        var cache = new SetAssociativeCache(mem, 64, 4, 16, 10, 0, ReplacementPolicyKind.Hawkeye);
        cache.SetRequestPc(0x1000);

        // Four cold misses (way 0 reused each time with untrained predictor).
        cache.Read(a, 1);
        cache.Read(b, 1);
        cache.Read(c, 1);
        cache.Read(d, 1);
        Assert.Equal(4, cache.Misses);

        // The last installed line (D) is still in the cache; re-reading hits.
        long hitsBefore = cache.Hits;
        cache.Read(d, 1);
        Assert.Equal(hitsBefore + 1, cache.Hits);

        // Train PC 0x1000 to be cache-friendly (drive predictor from 3 to ≥ 4).
        // RecordHitPc calls for D will each be OPT-hits (D is still resident).
        for (var i = 0; i < 4; i++) cache.Read(d, 1); // 4 hits train the predictor up

        // After training, read A (evicts D's way). The next miss for PC 0x1000 should
        // produce a cache-friendly install (RRPV=0). The test just checks no throw and
        // miss count increments correctly.
        long missesNow = cache.Misses;
        cache.Read(a, 1);
        Assert.Equal(missesNow + 1, cache.Misses);
    }

    // Tag constants reused by Hawkeye tests.
    private const ulong TagA = 0x10UL;
}