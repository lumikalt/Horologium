using Orrery.Scheduling;

namespace Tests.Orrery;

public class EscapementTests {
    // ── Basic scheduling ──────────────────────────────────────────────────────

    [Fact]
    public void Schedule_SingleEvent_Fires() {
        var esc = new Escapement();
        var fired = false;

        esc.Schedule(() => fired = true, 1, Phase.Execute);
        esc.Run();

        Assert.True(fired);
    }

    [Fact]
    public void Schedule_EventsFireInTickOrder() {
        var esc = new Escapement();
        var order = new List<int>();

        esc.Schedule(() => order.Add(3), 3, Phase.Execute);
        esc.Schedule(() => order.Add(1), 1, Phase.Execute);
        esc.Schedule(() => order.Add(2), 2, Phase.Execute);

        esc.Run();

        Assert.Equal([1, 2, 3,], order);
    }

    [Fact]
    public void Schedule_EventsAtSameTick_FireInPhaseOrder() {
        var esc = new Escapement();
        var order = new List<Phase>();

        // Scheduled in reverse order deliberately
        esc.Schedule(() => order.Add(Phase.Flush), 1, Phase.Flush);
        esc.Schedule(() => order.Add(Phase.Fetch), 1, Phase.Fetch);
        esc.Schedule(() => order.Add(Phase.Commit), 1, Phase.Commit);
        esc.Schedule(() => order.Add(Phase.ArborUpdate), 1, Phase.ArborUpdate);
        esc.Schedule(() => order.Add(Phase.Execute), 1, Phase.Execute);
        esc.Schedule(() => order.Add(Phase.Writeback), 1, Phase.Writeback);
        esc.Schedule(() => order.Add(Phase.Collection), 1, Phase.Collection);

        esc.Run();

        Assert.Equal(
            [
                Phase.Fetch,
                Phase.Execute,
                Phase.ArborUpdate,
                Phase.Writeback,
                Phase.Commit,
                Phase.Flush,
                Phase.Collection,
            ],
            order
        );
    }

    // ── ScheduleAfter ─────────────────────────────────────────────────────────

    [Fact]
    public void ScheduleAfter_FiresAtCorrectTick() {
        var esc = new Escapement();
        long firedAt = -1;

        esc.Schedule(
            () => { esc.ScheduleAfter(() => firedAt = esc.CurrentTick, 3, Phase.Execute); }, 2,
            Phase.Execute
        );

        esc.Run();

        Assert.Equal(5, firedAt);
    }

    [Fact]
    public void ScheduleAfter_ZeroDelay_LaterPhase_Works() {
        var esc = new Escapement();
        var order = new List<string>();

        esc.Schedule(
            () => {
                order.Add("execute");
                esc.ScheduleAfter(() => order.Add("writeback"), 0, Phase.Writeback);
            }, 1, Phase.Execute
        );

        esc.Run();

        Assert.Equal(["execute", "writeback",], order);
    }

    [Fact]
    public void ScheduleAfter_ZeroDelay_SameOrEarlierPhase_Throws() {
        var esc = new Escapement();

        esc.Schedule(
            () => {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                                                               esc.ScheduleAfter(() => { }, 0, Phase.Execute)
                ); // same phase

                Assert.Throws<ArgumentOutOfRangeException>(() =>
                                                               esc.ScheduleAfter(() => { }, 0, Phase.Fetch)
                ); // earlier phase
            }, 1, Phase.Execute
        );

        esc.Run();
    }

    [Fact]
    public void ScheduleAfter_NegativeDelay_Throws() {
        var esc = new Escapement();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
                                                       esc.ScheduleAfter(() => { }, -1, Phase.Execute)
        );
    }

    // ── ScheduleNextTick ──────────────────────────────────────────────────────

    [Fact]
    public void ScheduleNextTick_FiresOnFollowingTick() {
        var esc = new Escapement();
        long firedAt = -1;

        esc.Schedule(
            () => { esc.ScheduleNextTick(() => firedAt = esc.CurrentTick, Phase.Execute); }, 4, Phase.Execute
        );

        esc.Run();

        Assert.Equal(5, firedAt);
    }

    // ── Past scheduling guards ────────────────────────────────────────────────

    [Fact]
    public void Schedule_InThePast_Throws() {
        var esc = new Escapement();

        // Advance the clock to tick 5
        esc.Schedule(() => { }, 5, Phase.Execute);
        esc.Run();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
                                                       esc.Schedule(() => { }, 3, Phase.Execute)
        );
    }

    [Fact]
    public void Schedule_AlreadyPassedPhase_AtCurrentTick_Throws() {
        var esc = new Escapement();

        esc.Schedule(
            () => {
                // We are currently in Execute — scheduling Fetch at the same tick is illegal
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                                                               esc.Schedule(
                                                                   () => { }, esc.CurrentTick, Phase.Fetch
                                                               )
                );
            }, 1, Phase.Execute
        );

        esc.Run();
    }

    // ── Run with tick limit ───────────────────────────────────────────────────

    [Fact]
    public void Run_StopsAtTickLimit() {
        var esc = new Escapement();
        var fired = new List<long>();

        for (long t = 1; t <= 5; t++) {
            long tick = t;
            esc.Schedule(() => fired.Add(tick), tick, Phase.Execute);
        }

        esc.Run(3);

        Assert.Equal([1L, 2L, 3L,], fired);
    }

    [Fact]
    public void Run_ReturnsEventCount() {
        var esc = new Escapement();

        esc.Schedule(() => { }, 1, Phase.Fetch);
        esc.Schedule(() => { }, 1, Phase.Execute);
        esc.Schedule(() => { }, 2, Phase.Execute);

        long count = esc.Run();

        Assert.Equal(3, count);
    }

    // ── Step ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Step_ProcessesExactlyOneTick() {
        var esc = new Escapement();
        var fired = new List<long>();

        esc.Schedule(() => fired.Add(1), 1, Phase.Execute);
        esc.Schedule(() => fired.Add(2), 2, Phase.Execute);
        esc.Schedule(() => fired.Add(3), 3, Phase.Execute);

        esc.Step(); // tick 1
        Assert.Equal([1L,], fired);

        esc.Step(); // tick 2
        Assert.Equal([1L, 2L,], fired);
    }

    [Fact]
    public void Step_ProcessesAllPhasesWithinTick() {
        var esc = new Escapement();
        var order = new List<Phase>();

        esc.Schedule(() => order.Add(Phase.Fetch), 1, Phase.Fetch);
        esc.Schedule(() => order.Add(Phase.Execute), 1, Phase.Execute);
        esc.Schedule(() => order.Add(Phase.Commit), 1, Phase.Commit);

        esc.Step();

        Assert.Equal([Phase.Fetch, Phase.Execute, Phase.Commit,], order);
    }

    // ── CurrentTick and CurrentPhase ─────────────────────────────────────────

    [Fact]
    public void CurrentTick_ReflectsTickBeingProcessed() {
        var esc = new Escapement();
        long observedTick = -1;

        esc.Schedule(() => observedTick = esc.CurrentTick, 7, Phase.Execute);
        esc.Run();

        Assert.Equal(7, observedTick);
    }

    [Fact]
    public void CurrentPhase_ReflectsPhaseBeingProcessed() {
        var esc = new Escapement();
        var observedPhase = (Phase)(-1);

        esc.Schedule(() => observedPhase = esc.CurrentPhase, 1, Phase.Writeback);
        esc.Run();

        Assert.Equal(Phase.Writeback, observedPhase);
    }

    // ── IsIdle ────────────────────────────────────────────────────────────────

    [Fact]
    public void IsIdle_TrueWhenQueueEmpty() {
        var esc = new Escapement();
        Assert.True(esc.IsIdle);
    }

    [Fact]
    public void IsIdle_FalseWhenEventsPending() {
        var esc = new Escapement();
        esc.Schedule(() => { }, 1, Phase.Execute);
        Assert.False(esc.IsIdle);
    }

    [Fact]
    public void IsIdle_TrueAfterRunCompletes() {
        var esc = new Escapement();
        esc.Schedule(() => { }, 1, Phase.Execute);
        esc.Run();
        Assert.True(esc.IsIdle);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ClearsQueueAndResetsTime() {
        var esc = new Escapement();
        var fired = false;

        esc.Schedule(() => fired = true, 5, Phase.Execute);
        esc.Reset();

        Assert.Equal(0, esc.CurrentTick);
        Assert.Equal(Phase.Fetch, esc.CurrentPhase);
        Assert.True(esc.IsIdle);

        esc.Run(); // nothing left to fire
        Assert.False(fired);
    }

    // ── Events scheduled from within callbacks ────────────────────────────────

    [Fact]
    public void EventScheduledFromCallback_FiresCorrectly() {
        var esc = new Escapement();
        var order = new List<string>();

        esc.Schedule(
            () => {
                order.Add("first");
                esc.ScheduleNextTick(() => order.Add("second"), Phase.Execute);
            }, 1, Phase.Execute
        );

        esc.Run();

        Assert.Equal(["first", "second",], order);
    }

    [Fact]
    public void ChainedEvents_FireInOrder() {
        var esc = new Escapement();
        var order = new List<int>();

        // Each event schedules the next one
        void Chain(int n) {
            order.Add(n);
            if (n < 5) esc.ScheduleNextTick(() => Chain(n + 1), Phase.Execute);
        }

        esc.Schedule(() => Chain(1), 1, Phase.Execute);
        esc.Run();

        Assert.Equal([1, 2, 3, 4, 5,], order);
    }
}