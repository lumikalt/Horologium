#region

using Orrery.Gears;
using Orrery.Ports;
using Orrery.Scheduling;
using Orrery.Tree;

#endregion

namespace Tests.Orrery;

public class GearTests {
    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void Gear_RegistersItselfInTree() {
        var root = new SimNode("top");
        var esc = new Escapement();
        var gear = new TestGear("fetch", root, esc);

        Assert.Equal("top.fetch", gear.Path);
        Assert.Equal("fetch", gear.Name);
        Assert.Same(gear.Node, root.Find("fetch"));
    }

    [Fact]
    public void Gear_NestedUnderParent() {
        var root = new SimNode("top");
        var cpu = new SimNode("cpu", root);
        var esc = new Escapement();
        var gear = new TestGear("decode", cpu, esc);

        Assert.Equal("top.cpu.decode", gear.Path);
        Assert.Same(gear.Node, root.Find("cpu.decode"));
    }

    // ── Arbor declaration ─────────────────────────────────────────────────────

    [Fact]
    public void Initialize_ArborsAreCreated() {
        var root = new SimNode("top");
        var esc = new Escapement();
        var gear = new ProducerGear("producer", root, esc);

        gear.Initialize();

        Assert.NotNull(gear.OutInstructions);
    }

    [Fact]
    public void AddOutArbor_AfterFinalizing_Throws() {
        var root = new SimNode("top");
        var esc = new Escapement();
        var gear = new LateArborGear("bad", root, esc);

        gear.Initialize();
        root.BeginFinalizing();

        Assert.Throws<InvalidOperationException>(gear.TryAddArborLate);
    }

    // ── Lifecycle hooks ───────────────────────────────────────────────────────

    [Fact]
    public void Initialize_IsCalled_DuringBuilding() {
        var root = new SimNode("top");
        var esc = new Escapement();
        var gear = new TrackingGear("g", root, esc);

        gear.Initialize();

        Assert.True(gear.InitializeCalled);
        Assert.False(gear.SealCalled);
        Assert.False(gear.ResetCalled);
        Assert.False(gear.WindCalled);
    }

    [Fact]
    public void Seal_IsCalled_DuringFinalizing() {
        var root = new SimNode("top");
        var esc = new Escapement();
        var gear = new TrackingGear("g", root, esc);

        gear.Initialize();
        root.BeginFinalizing();
        gear.Seal();

        Assert.True(gear.SealCalled);
    }

    [Fact]
    public void Reset_ClearsState() {
        var root = new SimNode("top");
        var esc = new Escapement();
        var gear = new TrackingGear("g", root, esc);

        gear.Initialize();
        root.BeginFinalizing();
        gear.Seal();
        root.BeginRunning();
        gear.Reset();

        Assert.True(gear.ResetCalled);
    }

    [Fact]
    public void Wind_SchedulesWork() {
        var root = new SimNode("top");
        var esc = new Escapement();
        var gear = new TrackingGear("g", root, esc);

        gear.Initialize();
        root.BeginFinalizing();
        gear.Seal();
        root.BeginRunning();
        gear.Wind();

        Assert.False(esc.IsIdle); // Wind() should have scheduled something
    }

    // ── End-to-end: two gears communicating ──────────────────────────────────

    [Fact]
    public void TwoGears_ProducerConsumer_DataFlowsCorrectly() {
        var root = new SimNode("top");
        var esc = new Escapement();

        var producer = new ProducerGear("producer", root, esc);
        var consumer = new ConsumerGear("consumer", root, esc);

        // Building phase
        producer.Initialize();
        consumer.Initialize();

        // Finalizing phase
        root.BeginFinalizing();
        producer.OutInstructions!.Bind(consumer.InInstructions!);
        consumer.Seal();

        // Running phase
        root.BeginRunning();
        producer.Wind();

        esc.Run();

        Assert.Equal([10, 20, 30,], consumer.Received);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class TestGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc);

    private sealed class TrackingGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc) {
        public bool InitializeCalled { get; private set; }
        public bool SealCalled { get; private set; }
        public bool ResetCalled { get; private set; }
        public bool WindCalled { get; private set; }

        public override void Initialize() => InitializeCalled = true;
        public override void Seal() => SealCalled = true;
        public override void Reset() => ResetCalled = true;

        public override void Wind() {
            WindCalled = true;
            Escapement.Schedule(() => { }, 1, Phase.Execute);
        }
    }

    private sealed class LateArborGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc) {
        public void TryAddArborLate() => AddOutArbor<int>("too_late");
    }

    private sealed class ProducerGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc) {
        public OutArbor<int>? OutInstructions { get; private set; }

        public override void Initialize() { OutInstructions = AddOutArbor<int>("out_instructions"); }

        public override void Wind() {
            // Send three values on ticks 1, 2, 3
            Escapement.Schedule(() => OutInstructions!.Send(10), 1, Phase.Execute);
            Escapement.Schedule(() => OutInstructions!.Send(20), 2, Phase.Execute);
            Escapement.Schedule(() => OutInstructions!.Send(30), 3, Phase.Execute);
        }
    }

    private sealed class ConsumerGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc) {
        public InArbor<int>? InInstructions { get; private set; }
        public List<int> Received { get; } = [];

        public override void Initialize() { InInstructions = AddInArbor<int>("in_instructions"); }

        public override void Seal() { InInstructions!.OnReceive = v => Received.Add(v); }
    }
}