using Orrery.Gears;
using Orrery.Observation;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Train;
using Orrery.Tree;

namespace Tests.Orrery;

public class TrainTests {
    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void Train_CreatesRootNode() {
        var esc = new Escapement();
        var train = new Train("top", esc);

        Assert.Equal("top", train.Name);
        Assert.Equal("top", train.Root.Path);
    }

    // ── AddGear ───────────────────────────────────────────────────────────────

    [Fact]
    public void AddGear_RegistersGearInTree() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        var gear = train.AddGear<CountingGear>("counter");

        Assert.Equal("top.counter", gear.Path);
        Assert.Same(gear.Node, train.Root.Find("counter"));
    }

    [Fact]
    public void AddGear_AfterBuild_Throws() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.Build();

        Assert.Throws<InvalidOperationException>(() => train.AddGear<CountingGear>("late"));
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_CallsInitializeOnAllGears() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        var g1 = train.AddGear<TrackingGear>("g1");
        var g2 = train.AddGear<TrackingGear>("g2");

        train.Build();

        Assert.True(g1.InitializeCalled);
        Assert.True(g2.InitializeCalled);
    }

    [Fact]
    public void Build_CallsFinalizeOnAllGears() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        var g1 = train.AddGear<TrackingGear>("g1");
        var g2 = train.AddGear<TrackingGear>("g2");

        train.Build();

        Assert.True(g1.FinalizeCalled);
        Assert.True(g2.FinalizeCalled);
    }

    [Fact]
    public void Build_LocksAllSettings() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        var gear = train.AddGear<ConfigurableGear>("cfg");

        train.Build();

        Assert.Throws<InvalidOperationException>(() => gear.Width.Value = 8);
    }

    [Fact]
    public void Build_Twice_Throws() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.Build();

        Assert.Throws<InvalidOperationException>(train.Build);
    }

    [Fact]
    public void Build_TreeIsInFinalizingAfterBuild() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.AddGear<CountingGear>("g");
        train.Build();

        Assert.Equal(SimLifecycle.Finalizing, train.Root.Lifecycle);
    }

    // ── Run ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Run_WithoutBuild_Throws() {
        var esc = new Escapement();
        var train = new Train("top", esc);

        Assert.Throws<InvalidOperationException>(() => train.Run());
    }

    [Fact]
    public void Run_CallsTickOnAllGears() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        var g1 = train.AddGear<TrackingGear>("g1");
        var g2 = train.AddGear<TrackingGear>("g2");

        train.Build();
        train.Run();

        Assert.True(g1.TickCalled);
        Assert.True(g2.TickCalled);
    }

    [Fact]
    public void Run_TreeIsFinishedAfterRun() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.AddGear<CountingGear>("g");
        train.Build();
        train.Run();

        Assert.Equal(SimLifecycle.Finished, train.Root.Lifecycle);
    }

    [Fact]
    public void Run_Twice_Throws() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.AddGear<CountingGear>("g");
        train.Build();
        train.Run();

        Assert.Throws<InvalidOperationException>(() => train.Run());
    }

    // ── RevolutionResult ──────────────────────────────────────────────────────

    [Fact]
    public void Run_ReturnsCorrectEventCount() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.AddGear<CountingGear>("g"); // schedules 5 events across 5 ticks
        train.Build();

        RevolutionResult result = train.Run();

        Assert.Equal(5, result.TotalEvents);
    }

    [Fact]
    public void Run_ReturnsCorrectTick() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.AddGear<CountingGear>("g");
        train.Build();

        RevolutionResult result = train.Run();

        Assert.Equal(5, result.TotalTicks);
    }

    [Fact]
    public void Run_SnapshotsAllDialBoards() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.AddGear<CountingGear>("g");
        train.Build();

        RevolutionResult result = train.Run();

        DialBoardSnapshot? snap = result.Find("top.g");
        Assert.NotNull(snap);
        Assert.Equal(5, snap.Counters["ticks"]);
    }

    [Fact]
    public void Run_MaxTicks_StopsEarly() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.AddGear<CountingGear>("g"); // would run 5 ticks
        train.Build();

        RevolutionResult result = train.Run(3);

        DialBoardSnapshot? snap = result.Find("top.g");
        Assert.NotNull(snap);
        Assert.Equal(3, snap.Counters["ticks"]);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_AllowsSecondRun() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.AddGear<CountingGear>("g");
        train.Build();

        RevolutionResult r1 = train.Run();
        train.Reset();
        RevolutionResult r2 = train.Run();

        Assert.Equal(5, r1.Find("top.g")!.Counters["ticks"]);
        Assert.Equal(5, r2.Find("top.g")!.Counters["ticks"]);
    }

    [Fact]
    public void Reset_ZeroesDialBoards() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.AddGear<CountingGear>("g");
        train.Build();

        train.Run();
        train.Reset();

        // After reset, counter should be back to zero
        SimNode? gear = train.Root.Find("g");
        Assert.NotNull(gear);
    }

    [Fact]
    public void Reset_CallsResetOnAllGears() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        var g = train.AddGear<TrackingGear>("g");

        train.Build();
        train.Run();
        train.Reset();

        Assert.True(g.ResetCalled);
    }

    [Fact]
    public void Reset_WithoutBuild_Throws() {
        var esc = new Escapement();
        var train = new Train("top", esc);

        Assert.Throws<InvalidOperationException>(() => train.Reset());
    }

    // ── DumpTopology ──────────────────────────────────────────────────────────

    [Fact]
    public void DumpTopology_ContainsGearNames() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        train.AddGear<CountingGear>("fetch");
        train.AddGear<CountingGear>("decode");

        string dump = train.DumpTopology();

        Assert.Contains("fetch", dump);
        Assert.Contains("decode", dump);
    }

    // ── End-to-end: producer → consumer through Train ─────────────────────────

    [Fact]
    public void EndToEnd_ProducerConsumer_DataFlowsCorrectly() {
        var esc = new Escapement();
        var train = new Train("top", esc);
        var producer = train.AddGear<ProducerGear>("producer");
        var consumer = train.AddGear<ConsumerGear>("consumer");

        // Wire them up during Finalize
        producer.OnFinalize = () =>
            producer.Out!.Bind(consumer.In!, 1);

        train.Build();
        RevolutionResult result = train.Run();

        Assert.Equal([10, 20, 30,], consumer.Received);

        DialBoardSnapshot? snap = result.Find("top.consumer");
        Assert.NotNull(snap);
        Assert.Equal(3, snap.Counters["received"]);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class TrackingGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc) {
        public bool InitializeCalled { get; private set; }
        public bool FinalizeCalled { get; private set; }
        public bool ResetCalled { get; private set; }
        public bool TickCalled { get; private set; }

        public override void Initialize() => InitializeCalled = true;
        public override void Finalize() => FinalizeCalled = true;

        public override void Reset() {
            base.Reset();
            ResetCalled = true;
        }

        public override void Tick() => TickCalled = true;
    }

    private sealed class CountingGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc) {
        private Counter _ticks = null!;

        public override void Initialize() { _ticks = Dials.AddCounter("ticks", "Ticks elapsed"); }

        public override void Tick() {
            // Schedule one event per tick for 5 ticks
            for (var t = 1; t <= 5; t++) {
                int tick = t;
                Escapement.Schedule(() => { _ticks.Increment(); }, tick, Phase.Execute);
            }
        }
    }

    private sealed class ConfigurableGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc) {
        public Setting<int> Width { get; private set; } = null!;

        public override void Initialize() { Width = AddSetting("width", 1); }
    }

    private sealed class ProducerGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc) {
        public OutArbor<int>? Out { get; private set; }
        public Action? OnFinalize { get; set; }

        public override void Initialize() { Out = AddOutArbor<int>("out"); }

        public override void Finalize() => OnFinalize?.Invoke();

        public override void Tick() {
            Escapement.Schedule(() => Out!.Send(10), 1, Phase.Execute);
            Escapement.Schedule(() => Out!.Send(20), 2, Phase.Execute);
            Escapement.Schedule(() => Out!.Send(30), 3, Phase.Execute);
        }
    }

    private sealed class ConsumerGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc) {
        public InArbor<int>? In { get; private set; }
        public List<int> Received { get; } = new();
        private Counter _received = null!;

        public override void Initialize() {
            In = AddInArbor<int>("in");
            _received = Dials.AddCounter("received");
        }

        public override void Finalize() {
            In!.OnReceive = v => {
                Received.Add(v);
                _received.Increment();
            };
        }
    }
}