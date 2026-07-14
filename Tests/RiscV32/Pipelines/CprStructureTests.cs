using Pipeline.Ooo;

namespace Tests.RiscV32.Pipelines;

/// CheckpointList / Checkpoint (CPR, Akkary et al., MICRO 2003)
public class CheckpointListTests {
    [Fact]
    public void OpenAppendComplete_TracksCountersAndOrder() {
        var list = new CheckpointList(4);
        Assert.True(list.IsEmpty);

        Checkpoint cp = list.Open([0, 1, 2,], 0x100, 1);
        Assert.Equal(1, list.Count);
        Assert.Same(cp, list.Head);
        Assert.Same(cp, list.Tail);
        Assert.Equal(0x100UL, cp.RestartPc);
        Assert.Equal(1UL, cp.Seq);

        CheckpointEntry e1 = cp.Append();
        e1.InstrId = 10;
        CheckpointEntry e2 = cp.Append();
        e2.InstrId = 11;
        Assert.False(cp.AllComplete);
        Assert.Equal(10UL, cp.FirstInstrId);
        Assert.Equal(11UL, cp.LastInstrId);

        cp.MarkEntryComplete();
        Assert.False(cp.AllComplete);
        cp.MarkEntryComplete();
        Assert.True(cp.AllComplete);
    }

    [Fact]
    public void RetireHead_AdvancesFifoOrder() {
        var list = new CheckpointList(2);
        list.Open([], 0x0, 1);
        list.Open([], 0x40, 2);
        Assert.True(list.IsFull);
        Assert.Equal(1UL, list.Head.Seq);

        list.RetireHead();
        Assert.Equal(2UL, list.Head.Seq);
        Assert.Equal(1, list.Count);

        Checkpoint reopened = list.Open([], 0x80, 3);
        Assert.Equal(3UL, list.Tail.Seq);
        Assert.Same(reopened, list.Tail);
    }

    [Fact]
    public void DiscardTail_ReturnsYoungestWithoutClearing() {
        var list = new CheckpointList(3);
        list.Open([5,], 0x0, 1);
        list.Open([6,], 0x40, 2);

        Checkpoint discarded = list.DiscardTail();
        Assert.Equal(2UL, discarded.Seq);
        Assert.Equal([6,], discarded.RatSnapshot); // still readable for use-count balancing
        Assert.Equal(1, list.Count);
        Assert.Equal(1UL, list.Tail.Seq);
    }

    [Fact]
    public void ReopenForRecovery_KeepsSnapshotDropsEntries() {
        var list = new CheckpointList(2);
        Checkpoint cp = list.Open([7, 8,], 0x200, 5);
        cp.Append().InstrId = 42;
        cp.MarkEntryComplete();
        cp.CommittedCount = 1;

        cp.ReopenForRecovery();
        Assert.Empty(cp.Entries);
        Assert.Equal(0, cp.CompletedCount);
        Assert.Equal(0, cp.CommittedCount);
        Assert.Equal([7, 8,], cp.RatSnapshot);
        Assert.Equal(0x200UL, cp.RestartPc);
        Assert.Equal(5UL, cp.Seq);
    }

    [Fact]
    public void InOrder_EnumeratesOldestToYoungest() {
        var list = new CheckpointList(3);
        list.Open([], 0, 1);
        list.Open([], 0, 2);
        list.RetireHead();
        list.Open([], 0, 3);
        list.Open([], 0, 4);
        Assert.Equal([2UL, 3UL, 4UL,], list.InOrder().Select(c => c.Seq).ToArray());
    }
}

/// HierarchicalStoreQueue + MTB (CPR §4.2)
public class HierarchicalStoreQueueTests {
    [Fact]
    public void L1Membership_IsTheYoungestEntries() {
        var hsq = new HierarchicalStoreQueue(2, 4, 16);
        int a = hsq.Allocate();
        int b = hsq.Allocate();
        // Only two entries: both within the L1 window.
        Assert.True(hsq.IsL1(a));
        Assert.True(hsq.IsL1(b));

        int c = hsq.Allocate();
        int d = hsq.Allocate();
        // Four entries, l1Capacity=2: the two oldest have aged out into the L2 tier.
        Assert.False(hsq.IsL1(a));
        Assert.False(hsq.IsL1(b));
        Assert.True(hsq.IsL1(c));
        Assert.True(hsq.IsL1(d));
    }

    [Fact]
    public void Mtb_FastNegative_TracksLiveStores() {
        var hsq = new HierarchicalStoreQueue(2, 4);
        Assert.False(hsq.MayHaveMatchingStore(0x1000));

        int idx = hsq.Allocate();
        Assert.False(hsq.MayHaveMatchingStore(0x1000)); // address not yet resolved
        hsq.RecordAddressKnown(idx, 0x1000);
        Assert.True(hsq.MayHaveMatchingStore(0x1000));
        Assert.True(hsq.MayHaveMatchingStore(0x1020));  // same 64-byte block
        Assert.False(hsq.MayHaveMatchingStore(0x2000)); // different, non-aliasing slot

        hsq.Retire();
        Assert.False(hsq.MayHaveMatchingStore(0x1000)); // count decremented, not sticky
    }

    [Fact]
    public void Mtb_TruncateYoungerThan_DecrementsCounts() {
        var hsq = new HierarchicalStoreQueue(4, 4);
        int a = hsq.Allocate();
        hsq.At(a).InstrId = 1;
        hsq.RecordAddressKnown(a, 0x1000);
        int b = hsq.Allocate();
        hsq.At(b).InstrId = 2;
        hsq.RecordAddressKnown(b, 0x3000);

        hsq.TruncateYoungerThan(1);
        Assert.Equal(1, hsq.Count);
        Assert.True(hsq.MayHaveMatchingStore(0x1000));
        Assert.False(hsq.MayHaveMatchingStore(0x3000));
    }

    [Fact]
    public void InOrderIndexed_PairsEntriesWithTierTestableIndices() {
        var hsq = new HierarchicalStoreQueue(1, 2, 16);
        int a = hsq.Allocate();
        hsq.At(a).SeqNo = 1;
        int b = hsq.Allocate();
        hsq.At(b).SeqNo = 2;

        (int Index, SqEntry Entry)[] items = hsq.InOrderIndexed().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(1UL, items[0].Entry.SeqNo);
        Assert.False(hsq.IsL1(items[0].Index)); // oldest aged out of the 1-entry L1 window
        Assert.True(hsq.IsL1(items[1].Index));
    }
}

/// JRS confidence estimator as used for selective checkpointing (CPR §4.1.1)
public class CheckpointConfidencePredictorTests {
    [Fact]
    public void ColdBranch_IsLowConfidence() {
        var conf = new CheckpointConfidencePredictor(64);
        Assert.True(conf.IsLowConfidence(0x100));
    }

    [Fact]
    public void FifteenCorrectPredictions_ReachHighConfidence() {
        var conf = new CheckpointConfidencePredictor(1); // single counter: immune to history indexing
        for (var i = 0; i < 14; i++) {
            conf.Update(0x100, true, true);
            Assert.True(conf.IsLowConfidence(0x100));
        }

        conf.Update(0x100, true, true);
        Assert.False(conf.IsLowConfidence(0x100)); // saturated at 15
    }

    [Fact]
    public void Misprediction_ResetsCounterToZero() {
        var conf = new CheckpointConfidencePredictor(1);
        for (var i = 0; i < 15; i++) conf.Update(0x100, true, true);
        Assert.False(conf.IsLowConfidence(0x100));

        conf.Update(0x100, false, false);
        Assert.True(conf.IsLowConfidence(0x100));
        // One correct prediction is nowhere near enough to regain confidence.
        conf.Update(0x100, true, true);
        Assert.True(conf.IsLowConfidence(0x100));
    }
}

/// PhysicalRegisterFile aggressive-reclamation state (CPR §4.3)
public class CprRegisterReclamationTests {
    [Fact]
    public void ReclaimRequires_ZeroCount_Unmapped_AndValueSettled() {
        var prf = new PhysicalRegisterFile(8);
        prf.InitializeAllocation(4);

        // Phys 2 backs an arch register: allocated, mapped, ready.
        Assert.False(prf.IsReclaimable(2)); // mapped (unmapped flag clear)

        prf.AddRef(2);                      // a renamed reader
        prf.MarkUnmapped(2);                // its arch register was renamed again
        Assert.False(prf.IsReclaimable(2)); // outstanding reader

        prf.Release(2);
        Assert.True(prf.IsReclaimable(2)); // count 0 + unmapped + ready

        prf.MarkFreed(2);
        Assert.False(prf.IsAllocated(2));
        Assert.False(prf.IsReclaimable(2)); // not allocated: nothing to reclaim
    }

    [Fact]
    public void PendingRegister_NotReclaimable_UntilWrittenOrAbandoned() {
        var prf = new PhysicalRegisterFile(8);
        prf.MarkPending(5); // allocated to an in-flight producer
        prf.MarkUnmapped(5);
        Assert.False(prf.IsReclaimable(5)); // a write may still arrive

        prf.MarkAbandoned(5); // producer drained to the SDB / was squashed
        Assert.True(prf.IsReclaimable(5));
    }

    [Fact]
    public void AllocationGeneration_BumpsPerAllocation() {
        var prf = new PhysicalRegisterFile(8);
        int g0 = prf.AllocationGeneration(3);
        prf.MarkPending(3);
        int g1 = prf.AllocationGeneration(3);
        prf.MarkPending(3);
        int g2 = prf.AllocationGeneration(3);
        Assert.True(g1 > g0);
        Assert.True(g2 > g1);
    }

    [Fact]
    public void Write_ClearsNav_AndMarkMapped_ClearsReclaimLegs() {
        var prf = new PhysicalRegisterFile(8);
        prf.MarkPending(6);
        prf.MarkNav(6);
        Assert.True(prf.IsNav(6));
        prf.Write(6, 99);
        Assert.False(prf.IsNav(6));

        prf.MarkUnmapped(6);
        prf.MarkAbandoned(6);
        prf.MarkMapped(6); // recovery restored it into the RAT
        Assert.False(prf.IsReclaimable(6));
    }
}