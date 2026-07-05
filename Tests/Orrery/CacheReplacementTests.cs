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
}
