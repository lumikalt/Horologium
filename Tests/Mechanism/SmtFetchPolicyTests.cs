using Mechanism.SmtFetchPolicies;

namespace Tests.Mechanism;

public class SmtFetchPolicyTests {
    [Fact]
    public void RoundRobin_FillsSlotsInOrder_WhenAllAvailable() {
        var p = new RoundRobinFetchPolicy();
        p.BeginCycle(3);
        bool[] available = [true, true, true,];

        Assert.Equal(0, p.SelectHart(available));
        Assert.Equal(1, p.SelectHart(available));
        Assert.Equal(2, p.SelectHart(available));
    }

    [Fact]
    public void RoundRobin_SkipsUnavailableHarts() {
        var p = new RoundRobinFetchPolicy();
        p.BeginCycle(3);
        bool[] available = [true, false, true,];

        Assert.Equal(0, p.SelectHart(available));
        Assert.Equal(2, p.SelectHart(available));
    }

    [Fact]
    public void RoundRobin_ReturnsMinusOne_WhenNoneAvailable() {
        var p = new RoundRobinFetchPolicy();
        p.BeginCycle(2);
        bool[] available = [false, false,];

        Assert.Equal(-1, p.SelectHart(available));
    }

    [Fact]
    public void RoundRobin_RotatesStartHart_AcrossCycles() {
        // hartCount=2, one slot per cycle: cycle 0 starts at hart 0, cycle 1 at hart 1, ...
        var p = new RoundRobinFetchPolicy();
        bool[] available = [true, true,];

        p.BeginCycle(2);
        Assert.Equal(0, p.SelectHart(available));

        p.BeginCycle(2);
        Assert.Equal(1, p.SelectHart(available));

        p.BeginCycle(2);
        Assert.Equal(0, p.SelectHart(available));
    }

    [Fact]
    public void Icount_ColdStart_BehavesLikeRoundRobin_WhenNoStallsYet() {
        var p = new IcountFetchPolicy();
        p.BeginCycle(2);
        bool[] available = [true, true,];

        // All scores tied at 0: tie-break falls back to in-order scan.
        Assert.Equal(0, p.SelectHart(available));
    }

    [Fact]
    public void Icount_PrioritizesHart_WithFewerRecentStalls() {
        var p = new IcountFetchPolicy();

        // Hart 0 stalls heavily (cache misses), hart 1 stays clean.
        p.BeginCycle(2);
        p.OnIssued(0, 100);
        p.OnIssued(1, 0);

        p.BeginCycle(2);
        bool[] available = [true, true,];

        // Hart 1 has the lower EMA score, so it should be picked first.
        Assert.Equal(1, p.SelectHart(available));
    }

    [Fact]
    public void Icount_ScoreDecays_SoAStaleStallEventuallyStopsDominating() {
        var p = new IcountFetchPolicy();

        p.BeginCycle(2);
        p.OnIssued(0, 100);
        p.OnIssued(1, 0);

        // Hart 1 then stalls repeatedly while hart 0's old penalty decays away.
        for (var i = 0; i < 10; i++) {
            p.BeginCycle(2);
            p.OnIssued(0, 0);
            p.OnIssued(1, 20);
        }

        p.BeginCycle(2);
        bool[] available = [true, true,];

        // Hart 0's decayed history should now be cheaper than hart 1's sustained stalling.
        Assert.Equal(0, p.SelectHart(available));
    }

    [Fact]
    public void Icount_SkipsUnavailableHarts() {
        var p = new IcountFetchPolicy();
        p.BeginCycle(3);
        p.OnIssued(0, 0);
        p.OnIssued(1, 0);
        p.OnIssued(2, 50);

        p.BeginCycle(3);
        bool[] available = [false, true, true,];

        // Hart 0 is unavailable even though it has the lowest score.
        Assert.Equal(1, p.SelectHart(available));
    }
}