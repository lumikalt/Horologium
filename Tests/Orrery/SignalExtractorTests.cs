using Orrery.Observation;
using Orrery.Train;

namespace Tests.Orrery;

public class SignalExtractorTests {
    // Builds a two-gear result: a pipeline gear with retired/cycles/branch_misses
    // counters and an ipc dial, and a cache gear with dcache_hits/dcache_misses.
    // Snapshots every 100 ticks, cumulative values as Train.Run would record them.
    private static RevolutionResult MakeResult() {
        TimeSeriesPoint Point(
            long tick,
            long retired,
            long cycles,
            long misses,
            long hits,
            long cacheMisses
        ) => new(
            tick, [
                new DialBoardSnapshot(
                    "train.pipeline",
                    new Dictionary<string, long> {
                        ["retired"] = retired, ["cycles"] = cycles, ["branch_misses"] = misses,
                    },
                    new Dictionary<string, double> { ["ipc"] = cycles == 0 ? 0 : retired / (double)cycles, },
                    new Dictionary<string, IReadOnlyDictionary<string, long>>()
                ),
                new DialBoardSnapshot(
                    "train.l1d",
                    new Dictionary<string, long> {
                        ["dcache_hits"] = hits, ["dcache_misses"] = cacheMisses,
                    },
                    new Dictionary<string, double>(),
                    new Dictionary<string, IReadOnlyDictionary<string, long>>()
                ),
            ]
        );

        List<TimeSeriesPoint> ts = [
            Point(100, 80, 100, 4, 30, 10),
            Point(200, 130, 200, 10, 60, 20),
            Point(300, 130, 200, 10, 60, 20), // halted — no progress in window
        ];
        return new RevolutionResult(300, 0, ts[^1].Snapshots, ts);
    }

    [Fact]
    public void ListSignals_NoTimeSeries_ReturnsEmpty() {
        var result = new RevolutionResult(100, 0, []);
        Assert.Empty(SignalExtractor.ListSignals(result));
    }

    [Fact]
    public void ListSignals_IncludesCountersDerivedAndDials() {
        IReadOnlyList<string> names = SignalExtractor.ListSignals(MakeResult());

        Assert.Contains("pipeline.retired", names);
        Assert.Contains("pipeline.branch_misses", names);
        Assert.Contains("pipeline.ipc (windowed)", names);
        Assert.Contains("pipeline.ipc (cumulative)", names);
        Assert.Contains("l1d.dcache_hit_rate (windowed)", names);
        Assert.Contains("l1d.dcache_hits", names);
    }

    [Fact]
    public void ListSignals_NoHitRateWithoutMatchingMissCounter() {
        var result = new RevolutionResult(
            100, 0, [], [
                new TimeSeriesPoint(
                    100, [
                        new DialBoardSnapshot(
                            "train.g",
                            new Dictionary<string, long> { ["dcache_hits"] = 5, },
                            new Dictionary<string, double>(),
                            new Dictionary<string, IReadOnlyDictionary<string, long>>()
                        ),
                    ]
                ),
            ]
        );
        Assert.DoesNotContain("g.dcache_hit_rate (windowed)", SignalExtractor.ListSignals(result));
    }

    [Fact]
    public void Extract_Counter_DefaultsToPerWindowDeltas() {
        Signal? sig = SignalExtractor.Extract(MakeResult(), "pipeline.retired");

        Assert.NotNull(sig);
        Assert.Equal([100, 200, 300,], sig.Ticks);
        Assert.Equal([80, 50, 0,], sig.Values);
    }

    [Fact]
    public void Extract_Counter_CumulativeKeepsRawValues() {
        Signal? sig = SignalExtractor.Extract(MakeResult(), "pipeline.retired", true);

        Assert.NotNull(sig);
        Assert.Equal([80, 130, 130,], sig.Values);
    }

    [Fact]
    public void Extract_WindowedIpc_UsesPerWindowRetiredOverCycles() {
        Signal? sig = SignalExtractor.Extract(MakeResult(), "pipeline.ipc (windowed)");

        Assert.NotNull(sig);
        Assert.Equal(0.80, sig.Values[0], 10); // 80/100
        Assert.Equal(0.50, sig.Values[1], 10); // 50/100
    }

    [Fact]
    public void Extract_WindowedRatio_CarriesForwardWhenWindowIsIdle() {
        Signal? sig = SignalExtractor.Extract(MakeResult(), "pipeline.ipc (windowed)");

        Assert.NotNull(sig);
        Assert.Equal(sig.Values[1], sig.Values[2]); // halted window repeats the last rate
    }

    [Fact]
    public void Extract_WindowedHitRate_UsesHitsOverAccesses() {
        Signal? sig = SignalExtractor.Extract(MakeResult(), "l1d.dcache_hit_rate (windowed)");

        Assert.NotNull(sig);
        Assert.Equal(0.75, sig.Values[0], 10); // 30/(30+10)
        Assert.Equal(0.75, sig.Values[1], 10); // 30/(30+10)
    }

    [Fact]
    public void Extract_CumulativeDial_ReadsSnapshotValues() {
        Signal? sig = SignalExtractor.Extract(MakeResult(), "pipeline.ipc (cumulative)");

        Assert.NotNull(sig);
        Assert.Equal(0.80, sig.Values[0], 10); // 80/100
        Assert.Equal(0.65, sig.Values[1], 10); // 130/200
    }

    [Fact]
    public void Extract_UnknownSignal_ReturnsNull() {
        Assert.Null(SignalExtractor.Extract(MakeResult(), "pipeline.nonexistent"));
        Assert.Null(SignalExtractor.Extract(MakeResult(), "nope.retired"));
        Assert.Null(SignalExtractor.Extract(MakeResult(), "pipeline.nonexistent (windowed)"));
    }

    [Fact]
    public void Extract_NoTimeSeries_ReturnsNull() {
        var result = new RevolutionResult(100, 0, []);
        Assert.Null(SignalExtractor.Extract(result, "pipeline.retired"));
    }
}