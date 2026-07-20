#region

using Orrery.Devices;

#endregion

namespace Tests.Orrery;

public class PlicDeviceTests {
    private static PlicDevice Make(int contexts = 2) => new(contexts);

    private static ulong PriorityAddr(int src) => PlicDevice.DefaultBase + (ulong)(src * 4);
    private static ulong PendingAddr(int word) => PlicDevice.DefaultBase + 0x001000 + (ulong)(word * 4);

    private static ulong EnableAddr(int ctx, int word) =>
        PlicDevice.DefaultBase + 0x002000 + (ulong)(ctx * 0x80) + (ulong)(word * 4);

    private static ulong ThresholdAddr(int ctx) => PlicDevice.DefaultBase + 0x200000 + (ulong)(ctx * 0x1000);
    private static ulong ClaimAddr(int ctx) => PlicDevice.DefaultBase + 0x200004 + (ulong)(ctx * 0x1000);

    // ── Source priority ───────────────────────────────────────────────────────

    [Fact]
    public void Priority_Source0_AlwaysZero() {
        PlicDevice p = Make();
        p.Write(PriorityAddr(0), 7, 4);
        Assert.Equal(0UL, p.Read(PriorityAddr(0), 4));
    }

    [Fact]
    public void Priority_Write_RoundTrips() {
        PlicDevice p = Make();
        p.Write(PriorityAddr(1), 3, 4);
        Assert.Equal(3UL, p.Read(PriorityAddr(1), 4));
    }

    [Fact]
    public void Priority_InitiallyZero() {
        PlicDevice p = Make();
        Assert.Equal(0UL, p.Read(PriorityAddr(10), 4));
    }

    // ── Pending bits (read-only from MMIO) ────────────────────────────────────

    [Fact]
    public void Pending_InitiallyZero() {
        PlicDevice p = Make();
        Assert.Equal(0UL, p.Read(PendingAddr(0), 4));
    }

    [Fact]
    public void Assert_SetsPendingBit() {
        PlicDevice p = Make();
        p.Assert(3); // source 3 → word 0, bit 3
        Assert.Equal(1UL << 3, p.Read(PendingAddr(0), 4));
    }

    [Fact]
    public void Assert_MultipleSources() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Assert(5);
        Assert.Equal((1UL << 1) | (1UL << 5), p.Read(PendingAddr(0), 4));
    }

    [Fact]
    public void Pending_WriteIgnored() {
        PlicDevice p = Make();
        p.Assert(2);
        p.Write(PendingAddr(0), 0, 4); // should have no effect
        Assert.Equal(1UL << 2, p.Read(PendingAddr(0), 4));
    }

    [Fact]
    public void Deassert_DoesNotClearPending_BeforeClaim() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Deassert(1);
        // Pending stays set until claimed (level-triggered: deassert removes asserted latch)
        Assert.Equal(1UL << 1, p.Read(PendingAddr(0), 4));
    }

    // ── Enable bits ───────────────────────────────────────────────────────────

    [Fact]
    public void Enable_InitiallyZero() {
        PlicDevice p = Make();
        Assert.Equal(0UL, p.Read(EnableAddr(0, 0), 4));
        Assert.Equal(0UL, p.Read(EnableAddr(1, 0), 4));
    }

    [Fact]
    public void Enable_Write_RoundTrips() {
        PlicDevice p = Make();
        p.Write(EnableAddr(0, 0), 0b110, 4);
        Assert.Equal(0b110UL, p.Read(EnableAddr(0, 0), 4));
    }

    [Fact]
    public void Enable_Context0_And_Context1_Independent() {
        PlicDevice p = Make();
        p.Write(EnableAddr(0, 0), 0xFF, 4);
        Assert.Equal(0UL, p.Read(EnableAddr(1, 0), 4));
    }

    // ── Threshold ────────────────────────────────────────────────────────────

    [Fact]
    public void Threshold_InitiallyZero() {
        PlicDevice p = Make();
        Assert.Equal(0UL, p.Read(ThresholdAddr(0), 4));
        Assert.Equal(0UL, p.Read(ThresholdAddr(1), 4));
    }

    [Fact]
    public void Threshold_Write_RoundTrips() {
        PlicDevice p = Make();
        p.Write(ThresholdAddr(1), 5, 4);
        Assert.Equal(5UL, p.Read(ThresholdAddr(1), 4));
    }

    // ── ExternalPending ───────────────────────────────────────────────────────

    [Fact]
    public void ExternalPending_False_WhenNothingAsserted() { Assert.False(Make().ExternalPending(0)); }

    [Fact]
    public void ExternalPending_False_WhenAssertedButNotEnabled() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Write(PriorityAddr(1), 1, 4);
        // enable bit for source 1 in context 0 NOT set
        Assert.False(p.ExternalPending(0));
    }

    [Fact]
    public void ExternalPending_False_WhenPriorityZero() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Write(EnableAddr(0, 0), 1u << 1, 4); // enable source 1 ctx 0
        // priority left at 0 → never fires
        Assert.False(p.ExternalPending(0));
    }

    [Fact]
    public void ExternalPending_False_WhenPriorityAtOrBelowThreshold() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Write(PriorityAddr(1), 3, 4);
        p.Write(EnableAddr(0, 0), 1u << 1, 4);
        p.Write(ThresholdAddr(0), 3, 4); // threshold == priority → not strictly greater
        Assert.False(p.ExternalPending(0));
    }

    [Fact]
    public void ExternalPending_True_WhenPriorityAboveThreshold() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Write(PriorityAddr(1), 4, 4);
        p.Write(EnableAddr(0, 0), 1u << 1, 4);
        p.Write(ThresholdAddr(0), 3, 4);
        Assert.True(p.ExternalPending(0));
    }

    [Fact]
    public void ExternalPending_CorrectContext_Selected() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Write(PriorityAddr(1), 1, 4);
        p.Write(EnableAddr(1, 0), 1u << 1, 4); // enabled on context 1 only
        Assert.False(p.ExternalPending(0));
        Assert.True(p.ExternalPending(1));
    }

    // ── Claim ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Claim_ReturnsZero_WhenNoPending() {
        PlicDevice p = Make();
        Assert.Equal(0UL, p.Read(ClaimAddr(0), 4));
    }

    [Fact]
    public void Claim_ReturnsHighestPrioritySource() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Assert(2);
        p.Write(PriorityAddr(1), 3, 4);
        p.Write(PriorityAddr(2), 7, 4);
        p.Write(EnableAddr(0, 0), (1u << 1) | (1u << 2), 4);
        // source 2 wins (higher priority)
        Assert.Equal(2UL, p.Read(ClaimAddr(0), 4));
    }

    [Fact]
    public void Claim_ClearsPendingBit() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Write(PriorityAddr(1), 1, 4);
        p.Write(EnableAddr(0, 0), 1u << 1, 4);
        p.Read(ClaimAddr(0), 4); // claim
        Assert.Equal(0UL, p.Read(PendingAddr(0), 4));
    }

    [Fact]
    public void ExternalPending_False_AfterClaim() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Write(PriorityAddr(1), 1, 4);
        p.Write(EnableAddr(0, 0), 1u << 1, 4);
        Assert.True(p.ExternalPending(0));
        p.Read(ClaimAddr(0), 4); // claim
        Assert.False(p.ExternalPending(0));
    }

    // ── Complete ──────────────────────────────────────────────────────────────

    [Fact]
    public void Complete_DoesNotReassert_WhenSourceDeasserted() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Write(PriorityAddr(1), 1, 4);
        p.Write(EnableAddr(0, 0), 1u << 1, 4);
        p.Read(ClaimAddr(0), 4);     // claim
        p.Deassert(1);               // source went low before complete
        p.Write(ClaimAddr(0), 1, 4); // complete
        Assert.Equal(0UL, p.Read(PendingAddr(0), 4));
    }

    [Fact]
    public void Complete_ReassertsPending_WhenSourceStillAsserted() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Write(PriorityAddr(1), 1, 4);
        p.Write(EnableAddr(0, 0), 1u << 1, 4);
        p.Read(ClaimAddr(0), 4);     // claim (clears pending)
        p.Write(ClaimAddr(0), 1, 4); // complete while source still high
        Assert.Equal(1UL << 1, p.Read(PendingAddr(0), 4));
    }

    [Fact]
    public void ExternalPending_True_AfterComplete_WhenSourceStillAsserted() {
        PlicDevice p = Make();
        p.Assert(1);
        p.Write(PriorityAddr(1), 1, 4);
        p.Write(EnableAddr(0, 0), 1u << 1, 4);
        p.Read(ClaimAddr(0), 4);
        p.Write(ClaimAddr(0), 1, 4); // complete — should re-pend
        Assert.True(p.ExternalPending(0));
    }
}