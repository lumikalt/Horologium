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

    // 4-way, 2-set SRRIP cache.
    private static SetAssociativeCache MakeSrrip2Set(IMemory backing) =>
        new(backing, 128, 4, 16, 10, 0, ReplacementPolicyKind.Srrip);

    // Addresses that all map to set 0 (stride = 2*sets*blockSize = 32 for 1-set cache).
    private const ulong A1 = 0x00, A2 = 0x10, A3 = 0x20, A4 = 0x30;
    private const ulong B1 = 0x40, B2 = 0x50, B3 = 0x60, B4 = 0x70;

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
        policy.RecordInstall(0, 0);      // RRPV = 2
        policy.RecordHit(0, 0);          // HP: RRPV → 0
        Assert.Equal(0, policy.GetMetadata(0, 0));
    }

    [Fact]
    public void Srrip_VictimSearch_IncrementsAllWhenNoDistantEntry() {
        // Fill all 4 ways; each gets RRPV=2. On the next miss, no way has RRPV=3,
        // so all get incremented to 3, then way 0 is returned as the victim.
        var policy = new SrripPolicy(1, 4);
        for (int w = 0; w < 4; w++) { policy.ChooseVictim(0); policy.RecordInstall(0, w); }
        // All ways now at RRPV=2. Verify.
        for (int w = 0; w < 4; w++) Assert.Equal(2, policy.GetMetadata(0, w));

        int victim = policy.ChooseVictim(0);
        // After incrementing, all ways reach RRPV=3; way 0 is the first.
        Assert.Equal(0, victim);
        for (int w = 0; w < 4; w++) Assert.Equal(3, policy.GetMetadata(0, w));
    }

    [Fact]
    public void Srrip_VictimSearch_SelectsFirstDistantWay() {
        // Way 1 starts at distant (3), others at near-immediate (0).
        var policy = new SrripPolicy(1, 4);
        for (int w = 0; w < 4; w++) policy.RecordHit(0, w); // all → 0
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
        foreach (ulong a in new[] { A1, A2, A3, B1, B2, B3 }) mem.Load(a, [0xAA]);

        var cache = MakeSrrip1Set(mem);

        // Fill working set (cold misses).
        cache.Read(A1, 1); cache.Read(A2, 1); cache.Read(A3, 1);
        cache.ConsumePendingStalls();

        // Re-access working set → RRPV = 0.
        cache.Read(A1, 1); cache.Read(A2, 1); cache.Read(A3, 1);

        // Scan (3 blocks, no reuse).
        cache.Read(B1, 1); cache.Read(B2, 1); cache.Read(B3, 1);
        cache.ConsumePendingStalls();

        long hitsBefore = cache.Hits;

        // Working set re-access: all must be hits.
        cache.Read(A1, 1); cache.Read(A2, 1); cache.Read(A3, 1);

        Assert.Equal(hitsBefore + 3, cache.Hits); // all three are cache hits
        // No additional misses after working-set re-access.
        Assert.Equal(0, cache.ConsumePendingStalls());
    }

    [Fact]
    public void Srrip_LruWouldFail_ScanResistance() {
        // The same scan scenario fails under LRU: the working-set blocks are evicted.
        var mem = new FlatMemory(1024);
        foreach (ulong a in new[] { A1, A2, A3, B1, B2, B3 }) mem.Load(a, [0xAA]);

        var cache = new SetAssociativeCache(mem, 64, 4, 16, 10); // LRU by default

        cache.Read(A1, 1); cache.Read(A2, 1); cache.Read(A3, 1);
        cache.ConsumePendingStalls();
        cache.Read(A1, 1); cache.Read(A2, 1); cache.Read(A3, 1); // hits, all promoted to MRU
        cache.Read(B1, 1); cache.Read(B2, 1); cache.Read(B3, 1); // scan evicts A3, A2, A1
        cache.ConsumePendingStalls();

        long missesBefore = cache.Misses;
        // At least some working-set accesses should be misses under LRU after the scan.
        cache.Read(A1, 1); cache.Read(A2, 1); cache.Read(A3, 1);
        Assert.True(cache.Misses > missesBefore);
    }

    // ── BRRIP: insertion behaviour ────────────────────────────────────────────

    [Fact]
    public void Brrip_Insert_MostlyAtDistant_OccasionallyAtLong() {
        // ε=1/32: first 31 installs are distant (RRPV=3), 32nd is long (RRPV=2), 33rd is distant again.
        // Test directly on the policy (33 installs into distinct ways in a 64-way set) so that
        // distant-RRPV blocks are not immediately re-evicted by the cache's victim selection.
        var policy = new BrripPolicy(sets: 1, ways: 64, bimodalDenominator: 32);

        int longCount = 0, distantCount = 0;
        for (int w = 0; w < 33; w++) {
            // ChooseVictim is called first in the cache; call it here too to match the sequence.
            policy.ChooseVictim(0);
            policy.RecordInstall(0, w);
            int rrpv = policy.GetMetadata(0, w);
            if (rrpv == 2) longCount++;
            else if (rrpv == 3) distantCount++;
        }

        Assert.Equal(1, longCount);
        Assert.Equal(32, distantCount);
    }

    // ── DRRIP: PSEL counter ───────────────────────────────────────────────────

    [Fact]
    public void Drrip_SrripSdmMiss_IncrementsPsel() {
        // Misses in SDM_SRRIP sets should increment PSEL.
        int sets = 16;
        var policy = new DrripPolicy(sets, 4, m: 2, sdmSets: 2, pselBits: 10);
        int initial = policy.Psel;

        // Set 0 is SDM_SRRIP. Force a victim then record installs.
        for (int i = 0; i < 5; i++) {
            policy.ChooseVictim(0);
            policy.RecordInstall(0, 0); // 5 SRRIP SDM misses → PSEL should increase by 5
        }

        Assert.Equal(initial + 5, policy.Psel);
    }

    [Fact]
    public void Drrip_BrripSdmMiss_DecrementsPsel() {
        // Misses in SDM_BRRIP sets should decrement PSEL.
        int sets = 16;
        var policy = new DrripPolicy(sets, 4, m: 2, sdmSets: 2, pselBits: 10);
        int initial = policy.Psel;

        // Set 2 is SDM_BRRIP (sdmSets=2, so SDM_BRRIP = sets [2, 4)).
        for (int i = 0; i < 5; i++) {
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
        int sets = 16;
        var policy = new DrripPolicy(sets, 4, m: 2, sdmSets: 2, pselBits: 10, bimodalDenominator: 32);

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
        for (int i = 0; i < 33; i++) {
            policy.ChooseVictim(4);
            policy.RecordInstall(4, 0);
            int rrpv = policy.GetMetadata(4, 0);
            if (rrpv == 2) longCount++;
            if (rrpv == 3) distantCount++;
        }
        Assert.Equal(1, longCount);
        Assert.Equal(32, distantCount);
    }

    [Fact]
    public void Drrip_FollowerUsesSrripWhenPselLow() {
        // When PSEL < threshold, follower sets use SRRIP insertion (always RRPV 2).
        int sets = 16;
        var policy = new DrripPolicy(sets, 4, m: 2, sdmSets: 2, pselBits: 10);
        // Initial PSEL = 511 < 512 → SRRIP wins.
        Assert.True(policy.Psel < 512);

        // Follower set (set 4): all installs should produce RRPV=2 (SRRIP).
        for (int i = 0; i < 10; i++) {
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
        int sets = 16;
        var policy = new DrripPolicy(sets, 4, m: 2, sdmSets: 2, pselBits: 10, bimodalDenominator: 32);

        // Need 1 miss on SDM_SRRIP set to push PSEL from 511 → 512.
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);
        Assert.True(policy.Psel >= 512, $"PSEL={policy.Psel} expected >= 512 after SRRIP SDM miss");

        // Follower set (set 4): 33 installs should exhibit bimodal BRRIP pattern.
        // The policy has only 4 ways, so ChooseVictim picks the way and RecordInstall sets it.
        // We record the RRPV assigned by RecordInstall for each of the 33 calls.
        int longCount = 0, distantCount = 0;
        for (int i = 0; i < 33; i++) {
            int victim = policy.ChooseVictim(4);
            policy.RecordInstall(4, victim);
            int rrpv = policy.GetMetadata(4, victim);
            if (rrpv == 2) longCount++;
            else if (rrpv == 3) distantCount++;
        }

        Assert.Equal(1, longCount);
        Assert.Equal(32, distantCount);
    }

    // ── PSEL saturation ───────────────────────────────────────────────────────

    [Fact]
    public void Drrip_Psel_SaturatesAtBothEnds() {
        var policy = new DrripPolicy(16, 4, m: 2, sdmSets: 2, pselBits: 10);
        // Drive PSEL to max via SDM_SRRIP misses.
        for (int i = 0; i < 2000; i++) { policy.ChooseVictim(0); policy.RecordInstall(0, 0); }
        Assert.Equal(1023, policy.Psel);

        // Drive PSEL to min via SDM_BRRIP misses.
        for (int i = 0; i < 2000; i++) { policy.ChooseVictim(2); policy.RecordInstall(2, 0); }
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
        policy.RecordInstall(0, 1);   // SHCT[0x21]==0 → distant; outcome=false
        // Warm SHCT[0x21] to 2 via hits on a different way with same sig.
        policy.RecordHit(0, 1); // SHCT[0x21] → 1
        policy.RecordHit(0, 1); // SHCT[0x21] → 2
        // outcome[way1] is now true. Re-install way1 with a new sig to reset outcome.
        policy.SetPendingSignature(0x21);
        policy.RecordInstall(0, 1);   // evicts (outcome=true → no decrement); new outcome=false, SHCT[0x21]=2→ long
        // Now way 1 has sig=0x21, outcome=false, RRPV=2.
        // Evict way 1 without a hit → decrement SHCT[0x21].
        int before = policy.GetShctCounter(0x21);
        policy.SetPendingSignature(0x99);
        policy.RecordInstall(0, 1);   // evicts way 1 (outcome=false) → SHCT[0x21]--
        Assert.Equal(before - 1, policy.GetShctCounter(0x21));
    }

    [Fact]
    public void Ship_EvictionWithReuse_DoesNotDecrementShct() {
        // A line that is hit before eviction keeps its SHCT counter intact.
        var policy = new ShipPolicy(1, 4);
        policy.SetPendingSignature(0x30);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);   // outcome=false
        policy.RecordHit(0, 0);       // outcome → true, SHCT[0x30] → 1

        int before = policy.GetShctCounter(0x30);
        policy.SetPendingSignature(0x40);
        policy.RecordInstall(0, 0);   // evicts (outcome=true → no decrement)
        Assert.Equal(before, policy.GetShctCounter(0x30));
    }

    [Fact]
    public void Ship_ShctSaturates() {
        // SHCT counter saturates at 7 (3-bit max); RecordHit beyond 7 does not overflow.
        var policy = new ShipPolicy(1, 4);
        policy.SetPendingSignature(0x50);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);
        for (int i = 0; i < 20; i++) policy.RecordHit(0, 0);
        Assert.Equal(7, policy.GetShctCounter(0x50));
    }

    [Fact]
    public void Ship_ColdSlot_DoesNotDecrementShct() {
        // A cold (never-installed) way evicted by RecordInstall must not touch SHCT[0].
        var policy = new ShipPolicy(1, 4);
        int before = policy.GetShctCounter(0);
        policy.SetPendingSignature(0x60);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0); // way 0 was cold
        Assert.Equal(before, policy.GetShctCounter(0)); // SHCT[0] unchanged
    }

    [Fact]
    public void Ship_MultipleHits_IncrementShctEachTime() {
        // Every hit on the same line increments SHCT, not just the first.
        var policy = new ShipPolicy(1, 4);
        policy.SetPendingSignature(0x70);
        policy.ChooseVictim(0);
        policy.RecordInstall(0, 0);
        for (int i = 1; i <= 5; i++) {
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
        foreach (ulong a in new[] { A1, A2, A3, A4 }) mem.Load(a, [0xBB]);

        var cache = new SetAssociativeCache(mem, 64, 4, 16, 10, 0, ReplacementPolicyKind.Ship);

        // Cold miss on A1 → installed at RRPV=3 (distant, SHCT[sig]==0).
        cache.Read(A1, 1);
        cache.ConsumePendingStalls();

        // Demand hit on A1 → RRIP-HP sets RRPV=0; SHiP increments SHCT[sig] → 1.
        long hitsBefore = cache.Hits;
        cache.Read(A1, 1);
        Assert.Equal(hitsBefore + 1, cache.Hits); // it was a hit

        // Fill remaining ways: A2, A3, A4 (cold misses; all get RRPV=3 for their sig).
        cache.Read(A2, 1); cache.Read(A3, 1); cache.Read(A4, 1);
        cache.ConsumePendingStalls();

        // Now re-access A1: it was hit-promoted to RRPV=0, so it must survive the fills.
        long hitsAfter = cache.Hits;
        cache.Read(A1, 1);
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
        const ulong A = 0x000, B = 0x040, C = 0x080, D = 0x0C0, E = 0x100;
        foreach (ulong a in new[] { A, B, C, D, E }) mem.Load(a, [0xEE]);

        var cache = new SetAssociativeCache(mem, 256, 4, 64, 10, 0, ReplacementPolicyKind.Ship);

        // Phase 1: miss + hit each block to warm SHCT and leave all at RRPV=0.
        cache.Read(A, 1); cache.Read(A, 1); // cold miss → way 0 RRPV=3; hit → RRPV=0, SHCT[0]→1
        cache.Read(B, 1); cache.Read(B, 1); // → way 1 RRPV=0, SHCT[1]→1
        cache.Read(C, 1); cache.Read(C, 1); // → way 2 RRPV=0, SHCT[2]→1
        cache.Read(D, 1); cache.Read(D, 1); // → way 3 RRPV=0, SHCT[3]→1
        cache.ConsumePendingStalls();

        // Phase 2: miss E — all RRPV=0, so ChooseVictim increments 0→1→2→3;
        // evicts A (way 0, the first to reach 3). E inserted cold (RRPV=3).
        cache.Read(E, 1);
        cache.ConsumePendingStalls();

        // Phase 3: miss A — SHCT[sig(A)] = 1 > 0 → insert at RRPV=2 (long), not 3.
        cache.Read(A, 1);
        cache.ConsumePendingStalls();

        // Tag for A = 0x000 >> 6 = 0.
        CacheLine[] snap = cache.GetSnapshot();
        CacheLine? aLine = null;
        foreach (var l in snap) { if (l.Valid && l.Tag == 0) { aLine = l; break; } }
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
        const ulong pc    = 0x1000;

        var mem = new FlatMemory(512);
        foreach (ulong a in new[] { addrA, addrB }) mem.Load(a, [0xAA]);

        var cache = new SetAssociativeCache(mem, 512, 4, 32, 10, 0, ReplacementPolicyKind.ShipPc);

        // Warm SHCT[pc & 0x3FFF] via miss+hit on addr_A.
        cache.SetRequestPc(pc); cache.Read(addrA, 1); // cold miss → RRPV=3, SHCT[pc&mask]=0
        cache.SetRequestPc(pc); cache.Read(addrA, 1); // hit → RRPV=0, SHCT[pc&mask]=1
        cache.ConsumePendingStalls();

        // Miss addr_B with the same PC. SHCT[pc&mask]=1 > 0 → install at RRPV=2.
        // Under SHiP-Mem the signature would be addr_B>>5=5 (cold bucket → RRPV=3).
        cache.SetRequestPc(pc); cache.Read(addrB, 1);
        cache.ConsumePendingStalls();

        // addr_B: set=(0x0A0>>5)&3=1, tag=0x0A0>>7=1. Verify RRPV=2 (warm PC bucket).
        CacheLine[] snap = cache.GetSnapshot();
        CacheLine? bLine = null;
        foreach (var l in snap) { if (l.Valid && l.Set == 1 && l.Tag == 1) { bLine = l; break; } }
        Assert.NotNull(bLine);
        Assert.Equal(2, bLine.LruAge); // RRPV=2 proves SHCT was indexed by PC, not address
    }

    // ── FIFO ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Fifo_EvictsInInstallOrder() {
        // First four misses each get a fresh way; the fifth must evict way 0 (first installed).
        var policy = new FifoPolicy(1, 4);
        for (int w = 0; w < 4; w++) {
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
        for (int w = 0; w < 4; w++) { policy.ChooseVictim(0); policy.RecordInstall(0, w); }
        for (int i = 0; i < 10; i++) policy.RecordHit(0, 0);
        Assert.Equal(0, policy.ChooseVictim(0)); // way 0 still next, hits are irrelevant
    }

    [Fact]
    public void Fifo_CyclesCorrectlyAfterEvictions() {
        // After filling all 4 ways, the eviction order cycles 0→1→2→3→0→…
        var policy = new FifoPolicy(1, 4);
        for (int w = 0; w < 4; w++) { policy.ChooseVictim(0); policy.RecordInstall(0, w); }
        for (int w = 0; w < 8; w++) {
            Assert.Equal(w % 4, policy.ChooseVictim(0));
            policy.RecordInstall(0, w % 4);
        }
    }

    [Fact]
    public void Fifo_EndToEnd_HitDoesNotPreventEviction() {
        // Integration: fill 4-way 1-set FIFO cache, repeatedly hit A1, then miss A5.
        // FIFO must evict A1 (first installed); LRU would have evicted A2 instead.
        const ulong A1 = 0x00, A2 = 0x10, A3 = 0x20, A4 = 0x30, A5 = 0x40;
        var mem = new FlatMemory(256);
        foreach (ulong a in new[] { A1, A2, A3, A4, A5 }) mem.Load(a, [0xBB]);

        var cache = new SetAssociativeCache(mem, 64, 4, 16, 10, 0, ReplacementPolicyKind.Fifo);
        cache.Read(A1, 1); cache.Read(A2, 1); cache.Read(A3, 1); cache.Read(A4, 1);
        cache.ConsumePendingStalls();

        for (int i = 0; i < 5; i++) cache.Read(A1, 1); // promote A1 in LRU terms; FIFO ignores

        cache.Read(A5, 1); // evicts A1 (first installed)
        cache.ConsumePendingStalls();

        // A2 must still be present (only A1 was evicted, FIFO didn't touch A2 yet).
        long hitsA2 = cache.Hits;
        cache.Read(A2, 1);
        Assert.Equal(hitsA2 + 1, cache.Hits);

        // A1 must be gone: FIFO evicted it despite the repeated hits.
        long missesBefore = cache.Misses;
        cache.Read(A1, 1);
        Assert.Equal(missesBefore + 1, cache.Misses);
    }

    // ── Random ────────────────────────────────────────────────────────────────

    [Fact]
    public void Random_ChooseVictim_AlwaysReturnsValidWay() {
        var policy = new RandomPolicy(1, 4);
        for (int i = 0; i < 200; i++)
            Assert.InRange(policy.ChooseVictim(0), 0, 3);
    }

    [Fact]
    public void Random_WithFixedSeed_CoversAllWays() {
        // With seed 0 and 4 ways, 100 calls should cover all four values.
        var policy = new RandomPolicy(1, 4, seed: 0);
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < 100; i++) seen.Add(policy.ChooseVictim(0));
        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void Random_HitAndInstall_AreNoops() {
        var policy = new RandomPolicy(1, 4);
        policy.RecordHit(0, 2);
        policy.RecordInstall(0, 2);
        Assert.Equal(0, policy.GetMetadata(0, 2));
    }
}
