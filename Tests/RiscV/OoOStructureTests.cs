using RiscV.Trains.Ooo;

namespace Tests.RiscV;

// ═══════════════════════════════════════════════════════════════════════════════
// PhysicalRegisterFile
// ═══════════════════════════════════════════════════════════════════════════════

public class PhysicalRegisterFileTests {
    [Fact]
    public void InitialState_AllReadyAllZero() {
        var prf = new PhysicalRegisterFile(32);
        for (var i = 0; i < 32; i++) {
            Assert.True(prf.IsReady(i));
            Assert.Equal(0UL, prf.Read(i));
        }
    }

    [Fact]
    public void Write_ValueReadable_StillReady() {
        var prf = new PhysicalRegisterFile(16);
        prf.Write(5, 42);
        Assert.Equal(42UL, prf.Read(5));
        Assert.True(prf.IsReady(5));
    }

    [Fact]
    public void MarkPending_ClearsReady() {
        var prf = new PhysicalRegisterFile(16);
        prf.MarkPending(3);
        Assert.False(prf.IsReady(3));
        // other registers unaffected
        Assert.True(prf.IsReady(0));
        Assert.True(prf.IsReady(4));
    }

    [Fact]
    public void Write_AfterPending_RestoresReady() {
        var prf = new PhysicalRegisterFile(16);
        prf.MarkPending(7);
        prf.Write(7, 999);
        Assert.True(prf.IsReady(7));
        Assert.Equal(999UL, prf.Read(7));
    }

    [Fact]
    public void Reset_ZeroesValuesAndRestoresAllReady() {
        var prf = new PhysicalRegisterFile(8);
        prf.Write(1, 100);
        prf.MarkPending(2);
        prf.Reset();
        for (var i = 0; i < 8; i++) {
            Assert.True(prf.IsReady(i));
            Assert.Equal(0UL, prf.Read(i));
        }
    }

    [Fact]
    public void OutOfRange_Throws() {
        var prf = new PhysicalRegisterFile(8);
        Assert.Throws<ArgumentOutOfRangeException>(() => prf.Read(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => prf.Write(8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => prf.IsReady(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => prf.MarkPending(-1));
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// RenameMap
// ═══════════════════════════════════════════════════════════════════════════════

public class RenameMapTests {
    [Fact]
    public void InitialMapping_IdentityForArchRegisters() {
        var rm = new RenameMap(4, 8);
        for (var i = 0; i < 4; i++) Assert.Equal(i, rm.Lookup(i));
    }

    [Fact]
    public void InitialFreeCount_EqualsPhysMinusArch() {
        var rm = new RenameMap(4, 10);
        Assert.Equal(6, rm.FreeCount);
        Assert.True(rm.HasFree);
    }

    [Fact]
    public void Rename_AllocatesNewPhysAndReturnsOld() {
        var rm = new RenameMap(4, 6);
        // arch reg 2 is initially mapped to phys 2
        (int newPhys, int oldPhys) = rm.Rename(2);
        Assert.Equal(2, oldPhys);            // old mapping
        Assert.True(newPhys >= 4);           // new phys from free list
        Assert.Equal(newPhys, rm.Lookup(2)); // RAT updated
    }

    [Fact]
    public void Rename_ExhaustsFreeList_HasFreeFalse() {
        var rm = new RenameMap(2, 3);
        // 1 free register
        Assert.True(rm.HasFree);
        rm.Rename(0);
        Assert.False(rm.HasFree);
    }

    [Fact]
    public void Rename_WhenNoFree_Throws() {
        var rm = new RenameMap(2, 3);
        rm.Rename(0); // uses the only free register
        Assert.Throws<InvalidOperationException>(() => rm.Rename(1));
    }

    [Fact]
    public void FreePhysical_ReturnsRegisterToFreeList() {
        var rm = new RenameMap(2, 3);
        (int newPhys, int oldPhys) = rm.Rename(0);
        int freeAfterRename = rm.FreeCount;
        rm.FreePhysical(oldPhys);
        Assert.Equal(freeAfterRename + 1, rm.FreeCount);
    }

    [Fact]
    public void RestoreMapping_SetsRatDirectly() {
        var rm = new RenameMap(4, 8);
        (int newPhys, _) = rm.Rename(1);
        // Simulate flush: walk-back restores old mapping
        rm.RestoreMapping(1, 1); // original arch1 → phys1
        Assert.Equal(1, rm.Lookup(1));
    }

    [Fact]
    public void Reset_RestoresInitialState() {
        var rm = new RenameMap(4, 8);
        rm.Rename(0);
        rm.Rename(1);
        rm.Reset();
        for (var i = 0; i < 4; i++) Assert.Equal(i, rm.Lookup(i));
        Assert.Equal(4, rm.FreeCount); // 8 - 4 = 4 free again
    }

    [Fact]
    public void LookupOutOfRange_Throws() {
        var rm = new RenameMap(4, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => rm.Lookup(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => rm.Lookup(4));
    }

    // ── Superscalar dispatch invariants ───────────────────────────────────────

    [Fact]
    public void Rename_SameCycleChain_SecondLookupSeesFirstRename() {
        // In superscalar dispatch, inst2's source lookup must see inst1's new
        // mapping if inst1 renamed that register earlier the same cycle.
        var rm = new RenameMap(4, 8);
        (int p2New, _) = rm.Rename(2);     // inst1 writes x2
        Assert.Equal(p2New, rm.Lookup(2)); // inst2's Lookup(x2) sees updated RAT
    }

    [Fact]
    public void Rename_SameCycleSameArch_Twice_ChainsOldPhys() {
        // Two instructions in the same dispatch group both writing x2:
        // the second's PrevPhysDestination must be the first's NewPhys so that
        // walk-back recovery on flush restores the RAT in the right order.
        var rm = new RenameMap(4, 8);
        (int newA, _) = rm.Rename(2);        // first writer to x2
        (int newB, int oldB) = rm.Rename(2); // second writer to x2, same cycle
        Assert.Equal(newA, oldB);            // second's "prev" is first's "new"
        Assert.NotEqual(newA, newB);         // each gets a distinct physical register
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ReorderBuffer
// ═══════════════════════════════════════════════════════════════════════════════

public class ReorderBufferTests {
    [Fact]
    public void InitialState_EmptyNotFull() {
        var rob = new ReorderBuffer(8);
        Assert.True(rob.IsEmpty);
        Assert.False(rob.IsFull);
        Assert.Equal(0, rob.Count);
    }

    [Fact]
    public void Allocate_ReturnsIndex_CountIncreases() {
        var rob = new ReorderBuffer(4);
        int idx = rob.Allocate();
        Assert.Equal(0, idx);
        Assert.Equal(1, rob.Count);
        Assert.False(rob.IsEmpty);
    }

    [Fact]
    public void AllocateToCapacity_IsFull() {
        var rob = new ReorderBuffer(4);
        for (var i = 0; i < 4; i++) rob.Allocate();
        Assert.True(rob.IsFull);
    }

    [Fact]
    public void Allocate_WhenFull_Throws() {
        var rob = new ReorderBuffer(2);
        rob.Allocate();
        rob.Allocate();
        Assert.Throws<InvalidOperationException>(() => rob.Allocate());
    }

    [Fact]
    public void Retire_HeadComplete_AdvancesHead() {
        var rob = new ReorderBuffer(4);
        int idx = rob.Allocate();
        rob.At(idx).IsComplete = true;
        rob.Retire();
        Assert.Equal(0, rob.Count);
        Assert.True(rob.IsEmpty);
    }

    [Fact]
    public void Retire_WhenEmpty_Throws() {
        var rob = new ReorderBuffer(4);
        Assert.Throws<InvalidOperationException>(rob.Retire);
    }

    [Fact]
    public void InOrder_Returns_OldestFirst() {
        var rob = new ReorderBuffer(4);
        int a = rob.Allocate();
        rob.At(a).Pc = 0x100;
        int b = rob.Allocate();
        rob.At(b).Pc = 0x104;
        int c = rob.Allocate();
        rob.At(c).Pc = 0x108;

        List<ulong> pcs = rob.InOrder().Select(e => e.Entry.Pc).ToList();
        Assert.Equal([0x100UL, 0x104UL, 0x108UL,], pcs);
    }

    [Fact]
    public void Retire_ThenAllocate_CircularWrap() {
        var rob = new ReorderBuffer(3);
        // fill to capacity
        for (var i = 0; i < 3; i++) {
            int idx = rob.Allocate();
            rob.At(idx).IsComplete = true;
        }

        // retire one to make room
        rob.Retire();
        Assert.False(rob.IsFull);
        // allocate wraps around to slot 0
        int wrapped = rob.Allocate();
        Assert.Equal(0, wrapped);
    }

    [Fact]
    public void At_WrapsAroundCapacity() {
        var rob = new ReorderBuffer(4);
        int idx = rob.Allocate();
        // At() should handle modular indexing
        RobEntry e = rob.At(idx);
        Assert.True(e.Valid);
    }

    [Fact]
    public void Flush_ResetsToEmpty() {
        var rob = new ReorderBuffer(4);
        rob.Allocate();
        rob.Allocate();
        rob.Flush();
        Assert.True(rob.IsEmpty);
        Assert.Equal(0, rob.Count);
        // can allocate fresh
        int idx = rob.Allocate();
        Assert.Equal(0, idx);
    }

    [Fact]
    public void Entry_FieldsStoredAndRetrieved() {
        var rob = new ReorderBuffer(4);
        int idx = rob.Allocate();
        RobEntry e = rob.At(idx);
        e.Pc = 0xDEAD;
        e.ArchDestination = 5;
        e.PhysDestination = 37;
        e.PrevPhysDestination = 5;
        e.PredictedNextPc = 0xBEEF;

        RobEntry read = rob.At(idx);
        Assert.Equal(0xDEADUL, read.Pc);
        Assert.Equal(5, read.ArchDestination);
        Assert.Equal(37, read.PhysDestination);
        Assert.Equal(0xBEEFUL, read.PredictedNextPc);
    }

    [Fact]
    public void MultipleCycles_AllocateRetireAllocate_Correct() {
        // Simulates the ROB across several Dispatch/Commit cycles.
        var rob = new ReorderBuffer(4);

        // Cycle 1: dispatch 2 instructions
        int i0 = rob.Allocate();
        rob.At(i0).Pc = 0;
        int i1 = rob.Allocate();
        rob.At(i1).Pc = 4;

        // Cycle 2: both complete out-of-order (i1 finishes first)
        rob.At(i1).IsComplete = true;
        rob.At(i0).IsComplete = true;

        // Commit: retire in order
        Assert.Equal(0UL, rob.Head.Pc);
        rob.Retire();
        Assert.Equal(4UL, rob.Head.Pc);
        rob.Retire();
        Assert.True(rob.IsEmpty);

        // Cycle 3: ROB reusable after drain
        int i2 = rob.Allocate();
        rob.At(i2).Pc = 8;
        Assert.Equal(8UL, rob.Head.Pc);
    }

    [Fact]
    public void RobEntryHalt_FieldsRoundtrip() {
        var rob = new ReorderBuffer(4);
        int idx = rob.Allocate();
        rob.At(idx).IsHalt = true;
        rob.At(idx).IsStore = true;
        rob.At(idx).StoreAddress = 0xFF00;
        rob.At(idx).StoreValue = 0xDEAD;
        rob.At(idx).StoreWidth = 4;

        Assert.True(rob.At(idx).IsHalt);
        Assert.True(rob.At(idx).IsStore);
        Assert.Equal(0xFF00UL, rob.At(idx).StoreAddress);
        Assert.Equal(0xDEADUL, rob.At(idx).StoreValue);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// IssueQueue
// ═══════════════════════════════════════════════════════════════════════════════

public class IssueQueueTests {
    [Fact]
    public void InitialState_EmptyNotFull() {
        var iq = new IssueQueue(8);
        Assert.True(iq.IsEmpty);
        Assert.False(iq.IsFull);
        Assert.Equal(0, iq.Count);
    }

    [Fact]
    public void Allocate_ReturnsSlot_CountIncreases() {
        var iq = new IssueQueue(4);
        int slot = iq.Allocate();
        Assert.True(slot >= 0);
        Assert.Equal(1, iq.Count);
        Assert.True(iq.At(slot).Busy);
    }

    [Fact]
    public void Allocate_WhenFull_ReturnsNegOne() {
        var iq = new IssueQueue(2);
        iq.Allocate();
        iq.Allocate();
        Assert.True(iq.IsFull);
        Assert.Equal(-1, iq.Allocate());
    }

    [Fact]
    public void IsReady_NoSources_ImmediatelyReady() {
        var iq = new IssueQueue(4);
        int slot = iq.Allocate();
        // Src1Tag, Src2Tag, Src3Tag all -1 by default → IsReady = true
        Assert.True(iq.At(slot).IsReady);
    }

    [Fact]
    public void IsReady_WaitingForSource_NotReady() {
        var iq = new IssueQueue(4);
        int slot = iq.Allocate();
        iq.At(slot).Src1Tag = 10; // waiting for phys reg 10
        Assert.False(iq.At(slot).IsReady);
    }

    [Fact]
    public void Broadcast_WakesMatchingEntry() {
        var iq = new IssueQueue(4);
        int slot = iq.Allocate();
        iq.At(slot).Src1Tag = 10;
        iq.At(slot).Src2Tag = 11;

        iq.Broadcast(10, 42);
        Assert.True(iq.At(slot).Src1Ready);
        Assert.Equal(42UL, iq.At(slot).Src1Value);
        Assert.False(iq.At(slot).Src2Ready); // not yet — still waiting for phys 11
    }

    [Fact]
    public void Broadcast_DoesNotOverwriteAlreadyReady() {
        var iq = new IssueQueue(4);
        int slot = iq.Allocate();
        iq.At(slot).Src1Tag = 5;
        iq.At(slot).Src1Ready = true;
        iq.At(slot).Src1Value = 99;

        iq.Broadcast(5, 0); // already ready → should not overwrite
        Assert.Equal(99UL, iq.At(slot).Src1Value);
    }

    [Fact]
    public void Broadcast_AllSourcesReady_EntryBecomesReady() {
        var iq = new IssueQueue(4);
        int slot = iq.Allocate();
        iq.At(slot).Src1Tag = 7;
        iq.At(slot).Src2Tag = 8;

        iq.Broadcast(7, 1);
        Assert.False(iq.At(slot).IsReady); // src2 still pending
        iq.Broadcast(8, 2);
        Assert.True(iq.At(slot).IsReady); // both ready now
    }

    [Fact]
    public void FindReady_ReturnsReadyEntry() {
        var iq = new IssueQueue(4);
        int blocked = iq.Allocate();
        iq.At(blocked).Src1Tag = 5; // waiting

        int ready = iq.Allocate();
        // no sources → immediately ready

        Assert.Equal(ready, iq.FindReady());
    }

    [Fact]
    public void FindReady_WithFilter_RespectsFilter() {
        var iq = new IssueQueue(4);
        int slot = iq.Allocate();
        iq.At(slot).RobIndex = 42;

        // filter that only accepts rob index 99
        Assert.Equal(-1, iq.FindReady(e => e.RobIndex == 99));
        Assert.Equal(slot, iq.FindReady(e => e.RobIndex == 42));
    }

    [Fact]
    public void FindReady_NoneReady_ReturnsNegOne() {
        var iq = new IssueQueue(4);
        int slot = iq.Allocate();
        iq.At(slot).Src1Tag = 3; // blocked

        Assert.Equal(-1, iq.FindReady());
    }

    [Fact]
    public void FindReadyBatch_ReturnsManyReadyEntries() {
        var iq = new IssueQueue(8);
        // 3 ready, 1 blocked
        for (var i = 0; i < 3; i++) iq.Allocate();
        int blocked = iq.Allocate();
        iq.At(blocked).Src1Tag = 77;

        Span<int> results = stackalloc int[4];
        int found = iq.FindReadyBatch(results, 4);
        Assert.Equal(3, found);
    }

    [Fact]
    public void FindReadyBatch_RespectsMaxCount() {
        var iq = new IssueQueue(8);
        for (var i = 0; i < 6; i++) iq.Allocate();

        Span<int> results = stackalloc int[6];
        int found = iq.FindReadyBatch(results, 2);
        Assert.Equal(2, found);
    }

    [Fact]
    public void Free_ReleasesSlot() {
        var iq = new IssueQueue(2);
        int slot = iq.Allocate();
        Assert.Equal(1, iq.Count);
        iq.Free(slot);
        Assert.Equal(0, iq.Count);
        Assert.False(iq.At(slot).Busy);
    }

    [Fact]
    public void Flush_ClearsAllEntries() {
        var iq = new IssueQueue(4);
        iq.Allocate();
        iq.Allocate();
        iq.Flush();
        Assert.Equal(0, iq.Count);
        Assert.True(iq.IsEmpty);
        for (var i = 0; i < 4; i++) Assert.False(iq.At(i).Busy);
    }

    [Fact]
    public void Broadcast_Src3Tag_WakesThirdOperand() {
        var iq = new IssueQueue(4);
        int slot = iq.Allocate();
        iq.At(slot).Src3Tag = 20;

        iq.Broadcast(20, 777);
        Assert.True(iq.At(slot).Src3Ready);
        Assert.Equal(777UL, iq.At(slot).Src3Value);
    }
}