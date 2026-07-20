#region

using Mechanism;
using Orrery.Cache;
using RiscV32.Memory;

#endregion

namespace Tests.Orrery;

/// <summary>
///     Unit tests for Tlb (direct-mapped, identity-mapping TLB wrapping IMemory).
///     Uses a 4KB page size and 4-entry TLB unless otherwise specified.
/// </summary>
public class TlbTests {
    private const int PageSize = 4096;
    private const int TlbEntries = 4;

    private static Tlb MakeTlb(IMemory backing, int missLatency = 8) =>
        new(backing, TlbTests.TlbEntries, TlbTests.PageSize, missLatency);

    // ── Cold miss / hit ───────────────────────────────────────────────────────

    [Fact]
    public void ColdMiss_RecordsMissAndStalls() {
        var mem = new FlatMemory(TlbTests.PageSize * 2);
        mem.Load(0, [42,]);

        Tlb tlb = MakeTlb(mem);
        ulong val = tlb.Read(0, 1);

        Assert.Equal(42UL, val);
        Assert.Equal(1, tlb.Misses);
        Assert.Equal(0, tlb.Hits);
        Assert.Equal(8, tlb.ConsumePendingStalls());
    }

    [Fact]
    public void SecondAccessSamePage_IsHit() {
        var mem = new FlatMemory(TlbTests.PageSize * 2);
        mem.Load(0, [10,]);
        mem.Load(4, [20,]);

        Tlb tlb = MakeTlb(mem);
        tlb.Read(0, 1); // cold miss for page 0
        tlb.ConsumePendingStalls();

        tlb.Read(4, 1); // same page — hit

        Assert.Equal(1, tlb.Hits);
        Assert.Equal(1, tlb.Misses);
        Assert.Equal(0, tlb.ConsumePendingStalls());
    }

    [Fact]
    public void DifferentPages_EachPaysMissOnce() {
        var mem = new FlatMemory(TlbTests.PageSize * 3);

        Tlb tlb = MakeTlb(mem);
        tlb.Read(0, 1);                     // page 0 — miss
        tlb.Read(TlbTests.PageSize, 1);     // page 1 — miss
        tlb.Read(TlbTests.PageSize * 2, 1); // page 2 — miss

        Assert.Equal(3, tlb.Misses);
        Assert.Equal(0, tlb.Hits);
        Assert.Equal(24, tlb.ConsumePendingStalls()); // 3 × 8
    }

    // ── Identity mapping ──────────────────────────────────────────────────────

    [Fact]
    public void IdentityMapping_ReturnsPhysicalEqualsVirtual() {
        var mem = new FlatMemory(TlbTests.PageSize * 2);
        mem.Load(0x100, [0xDE,]);
        mem.Load(0x101, [0xAD,]);

        Tlb tlb = MakeTlb(mem);
        Assert.Equal(0xDEUL, tlb.Read(0x100, 1));
        Assert.Equal(0xADUL, tlb.Read(0x101, 1));
    }

    // ── Direct-mapped conflict eviction ──────────────────────────────────────

    [Fact]
    public void ConflictEviction_DirectMapped_EvictsOldEntry() {
        // TLB has 4 entries. Pages that alias to the same index are PageSize * 4 apart.
        var mem = new FlatMemory(TlbTests.PageSize * 9);
        mem.Load(0, [0xAA,]);
        mem.Load(TlbTests.PageSize * 4, [0xBB,]); // maps to the same TLB index as page 0

        Tlb tlb = MakeTlb(mem);
        tlb.Read(0, 1);                     // install page 0 in slot 0
        tlb.Read(TlbTests.PageSize * 4, 1); // conflict — evicts page 0, installs page 4

        long stallsAfterTwoMisses = tlb.ConsumePendingStalls();

        // Re-accessing page 0 should be a miss again (it was evicted).
        tlb.Read(0, 1);
        Assert.Equal(3, tlb.Misses);
        Assert.True(stallsAfterTwoMisses > 0);
    }

    // ── ConsumePendingStalls clears budget ────────────────────────────────────

    [Fact]
    public void ConsumePendingStalls_ClearsAfterRead() {
        Tlb tlb = MakeTlb(new FlatMemory(TlbTests.PageSize * 2), 20);
        tlb.Read(0, 1);                 // page 0 miss
        tlb.Read(TlbTests.PageSize, 1); // page 1 miss

        long stalls = tlb.ConsumePendingStalls();
        Assert.Equal(40, stalls); // 2 × 20

        Assert.Equal(0, tlb.ConsumePendingStalls()); // cleared
    }

    // ── Writes delegate to backing ─────────────────────────────────────────────

    [Fact]
    public void Write_DelegatesToBacking() {
        var mem = new FlatMemory(TlbTests.PageSize);
        Tlb tlb = MakeTlb(mem);

        tlb.Write(0x200, 0xBEEF, 2);

        Assert.Equal(0xBEEFUL, mem.Read(0x200, 2));
    }

    // ── Load invalidation ─────────────────────────────────────────────────────

    [Fact]
    public void Load_InvalidatesAffectedPages() {
        var mem = new FlatMemory(TlbTests.PageSize * 2);
        mem.Load(0, [0x11,]);

        Tlb tlb = MakeTlb(mem);
        tlb.Read(0, 1); // install page 0
        tlb.ConsumePendingStalls();

        tlb.Load(0, [0x22,]); // invalidates page 0's TLB entry

        // Next read should miss again.
        tlb.Read(0, 1);
        Assert.Equal(2, tlb.Misses);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NonPowerOfTwoEntries_Throws() {
        var mem = new FlatMemory(TlbTests.PageSize);
        Assert.Throws<ArgumentException>(() => new Tlb(mem, 3, TlbTests.PageSize, 10));
    }
}