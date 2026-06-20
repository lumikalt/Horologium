using Orrery.Ports;
using Orrery.Scheduling;

namespace Tests.Orrery;

public class ArborTests {
    // ── Basic delivery ────────────────────────────────────────────────────────

    [Fact]
    public void Send_DeliversDataToReceiver() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);
        var @in = new InArbor<int>("in");
        int received = -1;

        @out.Bind(@in);
        @in.OnReceive = v => received = v;

        esc.Schedule(() => @out.Send(42), 1, Phase.Execute);
        esc.Run();

        Assert.Equal(42, received);
    }

    [Fact]
    public void Send_DeliversAtCorrectTick() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);
        var @in = new InArbor<int>("in");
        long deliveredAt = -1;

        @out.Bind(@in, 3);
        @in.OnReceive = _ => deliveredAt = esc.CurrentTick;

        esc.Schedule(() => @out.Send(0), 2, Phase.Execute);
        esc.Run();

        Assert.Equal(5, deliveredAt); // sent at tick 2, latency 3 → delivered at tick 5
    }

    [Fact]
    public void Send_DeliversAtPortUpdatePhase() {
        var esc = new Escapement();
        var @out = new OutArbor<string>("out", esc);
        var @in = new InArbor<string>("in");
        var deliveredAt = (Phase)(-1);

        @out.Bind(@in);
        @in.OnReceive = _ => deliveredAt = esc.CurrentPhase;

        esc.Schedule(() => @out.Send("hello"), 1, Phase.Execute);
        esc.Run();

        Assert.Equal(Phase.PortUpdate, deliveredAt);
    }

    [Fact]
    public void Send_MultipleValues_AllDelivered() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);
        var @in = new InArbor<int>("in");
        var received = new List<int>();

        @out.Bind(@in);
        @in.OnReceive = received.Add;

        esc.Schedule(() => @out.Send(1), 1, Phase.Execute);
        esc.Schedule(() => @out.Send(2), 2, Phase.Execute);
        esc.Schedule(() => @out.Send(3), 3, Phase.Execute);
        esc.Run();

        Assert.Equal([1, 2, 3,], received);
    }

    // ── Latency ───────────────────────────────────────────────────────────────

    [Fact]
    public void Send_LatencyOne_DeliversNextTick() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);
        var @in = new InArbor<int>("in");
        long arrivedAt = -1;

        @out.Bind(@in);
        @in.OnReceive = _ => arrivedAt = esc.CurrentTick;

        esc.Schedule(() => @out.Send(0), 1, Phase.Execute);
        esc.Run();

        Assert.Equal(2, arrivedAt);
    }

    [Fact]
    public void Send_HighLatency_DeliversAtCorrectTick() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);
        var @in = new InArbor<int>("in");
        long arrivedAt = -1;

        @out.Bind(@in, 10);
        @in.OnReceive = _ => arrivedAt = esc.CurrentTick;

        esc.Schedule(() => @out.Send(0), 1, Phase.Execute);
        esc.Run();

        Assert.Equal(11, arrivedAt);
    }

    // ── Chained arbors ────────────────────────────────────────────────────────

    [Fact]
    public void ChainedArbors_DataFlowsCorrectly() {
        // A → B → C, each with latency 1
        var esc = new Escapement();
        var outA = new OutArbor<int>("outA", esc);
        var inB = new InArbor<int>("inB");
        var outB = new OutArbor<int>("outB", esc);
        var inC = new InArbor<int>("inC");

        outA.Bind(inB);
        outB.Bind(inC);

        // B forwards whatever it receives
        inB.OnReceive = v => outB.Send(v * 2);

        int finalValue = -1;
        long finalTick = -1;
        inC.OnReceive = v => {
            finalValue = v;
            finalTick = esc.CurrentTick;
        };

        esc.Schedule(() => outA.Send(7), 1, Phase.Execute);
        esc.Run();

        Assert.Equal(14, finalValue); // 7 * 2
        Assert.Equal(3, finalTick);   // tick 1 → tick 2 (inB) → tick 3 (inC)
    }

    // ── Generic types ─────────────────────────────────────────────────────────

    [Fact]
    public void Arbor_WorksWithReferenceType() {
        var esc = new Escapement();
        var @out = new OutArbor<string>("out", esc);
        var @in = new InArbor<string>("in");
        string? received = null;

        @out.Bind(@in);
        @in.OnReceive = v => received = v;

        esc.Schedule(() => @out.Send("tooth"), 1, Phase.Execute);
        esc.Run();

        Assert.Equal("tooth", received);
    }

    [Fact]
    public void Arbor_WorksWithRecordType() {
        var esc = new Escapement();
        var @out = new OutArbor<TestPacket>("out", esc);
        var @in = new InArbor<TestPacket>("in");
        TestPacket? received = null;

        @out.Bind(@in);
        @in.OnReceive = v => received = v;

        var packet = new TestPacket(99, 3.14);
        esc.Schedule(() => @out.Send(packet), 1, Phase.Execute);
        esc.Run();

        Assert.Equal(packet, received);
    }

    private record TestPacket(int Id, double Value);

    // ── IsBound ───────────────────────────────────────────────────────────────

    [Fact]
    public void IsBound_FalseBeforeBind() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);

        Assert.False(@out.IsBound);
    }

    [Fact]
    public void IsBound_TrueAfterBind() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);
        var @in = new InArbor<int>("in");

        @out.Bind(@in);

        Assert.True(@out.IsBound);
    }

    // ── Error conditions ──────────────────────────────────────────────────────

    [Fact]
    public void Send_WithoutBind_Throws() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);

        Assert.Throws<InvalidOperationException>(() => @out.Send(1));
    }

    [Fact]
    public void Bind_ZeroLatency_Throws() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);
        var @in = new InArbor<int>("in");

        Assert.Throws<ArgumentOutOfRangeException>(() => @out.Bind(@in, 0));
    }

    [Fact]
    public void Bind_NegativeLatency_Throws() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);
        var @in = new InArbor<int>("in");

        Assert.Throws<ArgumentOutOfRangeException>(() => @out.Bind(@in, -1));
    }

    [Fact]
    public void Bind_Twice_Throws() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);
        var in1 = new InArbor<int>("in1");
        var in2 = new InArbor<int>("in2");

        @out.Bind(in1);

        Assert.Throws<InvalidOperationException>(() => @out.Bind(in2));
    }

    [Fact]
    public void Deliver_WithNoHandler_Throws() {
        var esc = new Escapement();
        var @out = new OutArbor<int>("out", esc);
        var @in = new InArbor<int>("in");

        @out.Bind(@in);
        // Deliberately no OnReceive set

        esc.Schedule(() => @out.Send(1), 1, Phase.Execute);

        Assert.Throws<InvalidOperationException>(() => esc.Run());
    }

    [Fact]
    public void OutArbor_NullName_Throws() {
        Assert.Throws<ArgumentNullException>(() => new OutArbor<int>(null!, new Escapement()));
    }

    [Fact]
    public void InArbor_EmptyName_Throws() { Assert.Throws<ArgumentException>(() => new InArbor<int>("")); }
}