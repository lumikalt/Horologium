#region

using Mechanism.BranchPred;

#endregion

namespace Tests.Mechanism;

public class ReturnAddressStackTests {
    [Fact]
    public void Pop_EmptyStack_ReturnsFalse() {
        var ras = new ReturnAddressStack(4);
        Assert.False(ras.TryPop(out _));
    }

    [Fact]
    public void Push_ThenPop_ReturnsAddress() {
        var ras = new ReturnAddressStack(4);
        ras.Push(0x1000);
        Assert.True(ras.TryPop(out ulong addr));
        Assert.Equal(0x1000UL, addr);
    }

    [Fact]
    public void MultiPush_ThenPop_LifoOrder() {
        var ras = new ReturnAddressStack(4);
        ras.Push(0x100);
        ras.Push(0x200);
        ras.Push(0x300);
        Assert.True(ras.TryPop(out ulong a));
        Assert.Equal(0x300UL, a);
        Assert.True(ras.TryPop(out ulong b));
        Assert.Equal(0x200UL, b);
        Assert.True(ras.TryPop(out ulong c));
        Assert.Equal(0x100UL, c);
        Assert.False(ras.TryPop(out _));
    }

    [Fact]
    public void Overflow_OldestEntryEvicted() {
        var ras = new ReturnAddressStack(2);
        ras.Push(0x100);
        ras.Push(0x200);
        ras.Push(0x300); // evicts 0x100
        Assert.True(ras.TryPop(out ulong a));
        Assert.Equal(0x300UL, a);
        Assert.True(ras.TryPop(out ulong b));
        Assert.Equal(0x200UL, b);
        Assert.False(ras.TryPop(out _));
    }
}