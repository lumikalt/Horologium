using Orrery.Cache;
using RiscV32.Memory;

namespace Tests.Orrery;

/// <summary>
/// Tests for <see cref="DirectoryBus"/> correctness.
/// <para>
/// Two cache configurations:
///   "small" — capacityBytes=64, ways=1, block=64 → 1-set 1-way (direct-mapped).
///             Any two distinct line addresses alias to set 0, so reading a second
///             line always evicts the first.
///   "std"   — capacityBytes=256, ways=2, block=64 → 2-set 2-way.
/// </para>
/// </summary>
public class DirectoryBusTests {
    private const int Block = 64;

    private static (DirectoryBus bus, FlatMemory backing) MakeDirBus(int backingSize = 0x1000) {
        var backing = new FlatMemory(backingSize);
        return (new DirectoryBus(backing), backing);
    }

    private static MoesiCache SmallCache(IBus bus) => new(bus, DirectoryBusTests.Block, 1, DirectoryBusTests.Block);
    private static MoesiCache StdCache(IBus bus) => new(bus, 256, 2, DirectoryBusTests.Block);

    // ── Read coherence ────────────────────────────────────────────────────────

    [Fact]
    public void ColdRead_InstallsExclusive() {
        (DirectoryBus bus, _) = MakeDirBus();
        MoesiCache c0 = StdCache(bus);

        _ = c0.Read(0x00, 4);

        Assert.Equal(MoesiState.Exclusive, c0.StateOf(0x00));
        Assert.Equal(1, c0.Misses);
    }

    [Fact]
    public void SecondRead_DowngradesOwnerToShared() {
        (DirectoryBus bus, _) = MakeDirBus();
        MoesiCache c0 = StdCache(bus);
        MoesiCache c1 = StdCache(bus);

        _ = c0.Read(0x00, 4); // c0: E; dir: Exclusive(c0)
        _ = c1.Read(0x00, 4); // snoops c0: E→S; c1: S; dir: Shared

        Assert.Equal(MoesiState.Shared, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c1.StateOf(0x00));
    }

    [Fact]
    public void ThirdRead_JoinsSharedSet_NoSnoop() {
        (DirectoryBus bus, _) = MakeDirBus();
        MoesiCache c0 = StdCache(bus);
        MoesiCache c1 = StdCache(bus);
        MoesiCache c2 = StdCache(bus);

        _ = c0.Read(0x00, 4); // c0: E
        _ = c1.Read(0x00, 4); // c0:S, c1:S
        _ = c2.Read(0x00, 4); // c2 joins; dir knows c2 without probing anyone

        Assert.Equal(MoesiState.Shared, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c1.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c2.StateOf(0x00));
    }

    // ── Write coherence ───────────────────────────────────────────────────────

    [Fact]
    public void Write_ExclusiveOwner_InvalidatesSharers() {
        (DirectoryBus bus, _) = MakeDirBus();
        MoesiCache c0 = StdCache(bus);
        MoesiCache c1 = StdCache(bus);
        MoesiCache c2 = StdCache(bus);

        _ = c0.Read(0x00, 4);          // c0: E
        _ = c1.Read(0x00, 4);          // c0:S, c1:S
        _ = c2.Read(0x00, 4);          // c0:S, c1:S, c2:S
        c0.Write(0x00, 0xDEADBEEF, 4); // BRI snoops c1 and c2 only

        Assert.Equal(MoesiState.Modified, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Invalid, c1.StateOf(0x00));
        Assert.Equal(MoesiState.Invalid, c2.StateOf(0x00));
        Assert.Equal(0xDEADBEEFUL, c0.Read(0x00, 4));
    }

    [Fact]
    public void Write_ModifiedOwner_ReadByPeer_CoherentData() {
        (DirectoryBus bus, FlatMemory backing) = MakeDirBus();
        MoesiCache c0 = StdCache(bus);
        MoesiCache c1 = StdCache(bus);

        c0.Write(0x00, 0x12345678, 4); // c0: M; dir: Exclusive(c0)
        ulong v = c1.Read(0x00, 4);    // SnoopRead(c0): M→O, supplies cache-to-cache

        Assert.Equal(0x12345678UL, v);
        Assert.Equal(MoesiState.Owned, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c1.StateOf(0x00));
        Assert.Equal(1, c1.PeerSupplies);
        Assert.Equal(0UL, backing.Read(0x00, 4)); // no writeback — backing stale
    }

    [Fact]
    public void OwnedLine_ThirdReader_SuppliedByOwner_DirectoryTracksSharers() {
        (DirectoryBus bus, _) = MakeDirBus();
        MoesiCache c0 = StdCache(bus);
        MoesiCache c1 = StdCache(bus);
        MoesiCache c2 = StdCache(bus);

        c0.Write(0x00, 0xABCD, 4);  // c0: M; dir: Exclusive(c0)
        _ = c1.Read(0x00, 4);       // c0: O; dir: Owned(c0, {c1})
        ulong v = c2.Read(0x00, 4); // owner supplies again; dir: Owned(c0, {c1,c2})

        Assert.Equal(0xABCDUL, v);
        Assert.Equal(1, c2.PeerSupplies);
        Assert.Equal(MoesiState.Owned, c0.StateOf(0x00));

        // A write by a sharer must reach both the owner and the other sharer.
        c1.Write(0x00, 0x9999, 4);
        Assert.Equal(MoesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Invalid, c2.StateOf(0x00));
        Assert.Equal(MoesiState.Modified, c1.StateOf(0x00));
        Assert.Equal(1, c0.Writebacks); // dirty owner wrote back on invalidate
    }

    [Fact]
    public void OwnedOwner_Evicts_SharersRemain_NextReadFillsFromBacking() {
        // Owner eviction writes back, so backing is clean; the directory collapses the
        // entry to its S sharers and a later reader joins them without any probe.
        (DirectoryBus bus, FlatMemory backing) = MakeDirBus();
        MoesiCache c0 = SmallCache(bus);
        MoesiCache c1 = SmallCache(bus);
        MoesiCache c2 = SmallCache(bus);

        c0.Write(0x00, 0x5150, 4); // c0: M
        _ = c1.Read(0x00, 4);      // c0: O; dir: Owned(c0, {c1})
        _ = c0.Read(0x40, 4);      // c0 evicts 0x00 → writeback; dir: Shared({c1})

        Assert.Equal(0x5150UL, backing.Read(0x00, 4));

        ulong v = c2.Read(0x00, 4); // joins sharers, clean fill from backing
        Assert.Equal(0x5150UL, v);
        Assert.Equal(0, c2.PeerSupplies);
        Assert.Equal(MoesiState.Shared, c1.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c2.StateOf(0x00));
    }

    [Fact]
    public void OwnedLine_LastSharerEvicts_OwnerStillServicesReads() {
        (DirectoryBus bus, _) = MakeDirBus();
        MoesiCache c0 = StdCache(bus);
        MoesiCache c1 = SmallCache(bus);
        MoesiCache c2 = StdCache(bus);

        c0.Write(0x00, 0x7777, 4); // c0: M
        _ = c1.Read(0x00, 4);      // c0: O; dir: Owned(c0, {c1})
        _ = c1.Read(0x40, 4);      // c1 (1-way) evicts 0x00; dir collapses to owner-only

        ulong v = c2.Read(0x00, 4); // owner must still be found and supply
        Assert.Equal(0x7777UL, v);
        Assert.Equal(1, c2.PeerSupplies);
        Assert.Equal(MoesiState.Owned, c0.StateOf(0x00));
    }

    // ── Precise eviction tracking ─────────────────────────────────────────────

    [Fact]
    public void Eviction_ExclusiveOwner_NextReadGetsExclusive() {
        // With 1-set 1-way caches, reading 0x40 evicts 0x00.
        (DirectoryBus bus, _) = MakeDirBus();
        MoesiCache c0 = SmallCache(bus);
        MoesiCache c1 = SmallCache(bus);

        _ = c0.Read(0x00, 4); // c0: E; dir: Exclusive(c0)
        _ = c0.Read(0x40, 4); // c0 evicts 0x00 → Evicted(c0,0x00) → dir: Uncached

        _ = c1.Read(0x00, 4); // dir: Uncached → c1 gets E (no SnoopRead)

        Assert.Equal(MoesiState.Exclusive, c1.StateOf(0x00));
    }

    [Fact]
    public void Eviction_AllSharers_NextReadGetsExclusive() {
        // Both sharers evict; next reader must install as Exclusive, not Shared.
        (DirectoryBus bus, _) = MakeDirBus();
        MoesiCache c0 = SmallCache(bus);
        MoesiCache c1 = SmallCache(bus);
        MoesiCache c2 = SmallCache(bus);

        _ = c0.Read(0x00, 4); // c0: E
        _ = c1.Read(0x00, 4); // c0:S, c1:S; dir: Shared({c0,c1})
        _ = c0.Read(0x40, 4); // c0 evicts 0x00 → Evicted → dir: Shared({c1})
        _ = c1.Read(0x40, 4); // c1 evicts 0x00 → Evicted → dir: empty → removed

        _ = c2.Read(0x00, 4); // dir: Uncached → c2 gets E

        Assert.Equal(MoesiState.Exclusive, c2.StateOf(0x00));
    }

    [Fact]
    public void Eviction_PartialSharer_NewReaderJoinsRemainder() {
        // One sharer evicts; a new reader still gets Shared (joining the remaining sharer).
        (DirectoryBus bus, _) = MakeDirBus();
        MoesiCache c0 = SmallCache(bus);
        MoesiCache c1 = SmallCache(bus);
        MoesiCache c2 = SmallCache(bus);

        _ = c0.Read(0x00, 4); // c0: E
        _ = c1.Read(0x00, 4); // c0:S, c1:S
        _ = c0.Read(0x40, 4); // c0 evicts 0x00 → Evicted → dir: Shared({c1})

        _ = c2.Read(0x00, 4); // dir: Shared({c1}) → c2 joins → Shared({c1,c2})

        Assert.Equal(MoesiState.Invalid, c0.StateOf(0x00)); // evicted
        Assert.Equal(MoesiState.Shared, c1.StateOf(0x00));
        Assert.Equal(MoesiState.Shared, c2.StateOf(0x00));
    }

    // ── BusLoad ───────────────────────────────────────────────────────────────

    [Fact]
    public void BusLoad_InvalidatesAllHolders() {
        (DirectoryBus bus, _) = MakeDirBus();
        MoesiCache c0 = StdCache(bus);
        MoesiCache c1 = StdCache(bus);

        _ = c0.Read(0x00, 4);
        _ = c1.Read(0x00, 4); // both Shared

        // Load() on either MoesiCache calls BusLoad → invalidates all caches for the line.
        c0.Load(0x00, new byte[DirectoryBusTests.Block]);

        Assert.Equal(MoesiState.Invalid, c0.StateOf(0x00));
        Assert.Equal(MoesiState.Invalid, c1.StateOf(0x00));
    }

    // ── LR/SC reservation table integration ──────────────────────────────────

    [Fact]
    public void BusReadInvalidate_CancelsReservation() {
        var table = new ReservationTable();
        var bus = new DirectoryBus(new FlatMemory(0x1000), table);
        _ = new MoesiCache(bus, 256, 2, DirectoryBusTests.Block); // register so _blockSize is set
        table.Set(0, 0x00);                                       // hart 0 reserves 0x00

        bus.BusReadInvalidate(new MoesiCache(bus, 256, 2, DirectoryBusTests.Block), 0x00);

        Assert.False(table.TryConsume(0, 0x00));
    }

    // ── Drop-in parity with MoesiBus ──────────────────────────────────────────

    [Fact]
    public void DropIn_ReadWriteRead_MatchesMoesiBus() {
        // Run the same R/W/R sequence through both buses and assert identical final states.
        static ulong Scenario(IBus bus) {
            MoesiCache c0 = new(bus, 256, 2, DirectoryBusTests.Block);
            MoesiCache c1 = new(bus, 256, 2, DirectoryBusTests.Block);
            c0.Write(0x00, 0xCAFEBABE, 4);
            _ = c1.Read(0x00, 4); // c1 fills; c0: S
            c1.Write(0x00, 0x12345678, 4);
            return c0.Read(0x00, 4); // c0 re-fetches; should get 0x12345678
        }

        var mb = new FlatMemory(0x1000);
        var db = new FlatMemory(0x1000);
        ulong moesiBusResult = Scenario(new MoesiBus(mb));
        ulong dirBusResult = Scenario(new DirectoryBus(db));

        Assert.Equal(moesiBusResult, dirBusResult);
        Assert.Equal(0x12345678UL, dirBusResult);
    }
}