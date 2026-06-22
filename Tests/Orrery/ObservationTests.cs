using Orrery.Gears;
using Orrery.Observation;
using Orrery.Scheduling;
using Orrery.Tree;

namespace Tests.Orrery;

public class ObservationTests {
    // ── Counter ───────────────────────────────────────────────────────────────

    [Fact]
    public void Counter_StartsAtZero() {
        var c = new Counter("cycles");
        Assert.Equal(0, c.Value);
    }

    [Fact]
    public void Counter_Increment_IncreasesByOne() {
        var c = new Counter("cycles");
        c.Increment();
        c.Increment();
        Assert.Equal(2, c.Value);
    }

    [Fact]
    public void Counter_IncrementBy_IncreasesByAmount() {
        var c = new Counter("bytes");
        c.IncrementBy(100);
        c.IncrementBy(50);
        Assert.Equal(150, c.Value);
    }

    [Fact]
    public void Counter_IncrementBy_NegativeAmount_Throws() {
        var c = new Counter("cycles");
        Assert.Throws<ArgumentOutOfRangeException>(() => c.IncrementBy(-1));
    }

    [Fact]
    public void Counter_Reset_ReturnsToZero() {
        var c = new Counter("cycles");
        c.IncrementBy(999);

        // Reset is internal — test via DialBoard
        var board = new DialBoard("test");
        Counter bc = board.AddCounter("cycles");
        bc.IncrementBy(999);
        board.Reset();

        Assert.Equal(0, bc.Value);
    }

    // ── Dial ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Dial_ReadsFromExpression() {
        var cycles = new Counter("cycles");
        var retired = new Counter("retired");
        var ipc = new Dial("ipc", () => retired.Value / (double)cycles.Value);

        cycles.IncrementBy(100);
        retired.IncrementBy(87);

        Assert.Equal(0.87, ipc.Read(), 5);
    }

    [Fact]
    public void Dial_AlwaysReflectsCurrentState() {
        var cycles = new Counter("cycles");
        var dial = new Dial("x", () => cycles.Value * 2.0);

        cycles.IncrementBy(5);
        Assert.Equal(10.0, dial.Read());

        cycles.IncrementBy(5);
        Assert.Equal(20.0, dial.Read()); // updates live
    }

    // ── Setting ───────────────────────────────────────────────────────────────

    [Fact]
    public void Setting_HasDefaultValue() {
        var s = new Setting<int>("width", 4);
        Assert.Equal(4, s.Value);
    }

    [Fact]
    public void Setting_CanBeChanged_BeforeLock() {
        var s = new Setting<int>("width", 4);
        s.Value = 8;
        Assert.Equal(8, s.Value);
    }

    [Fact]
    public void Setting_Throws_AfterLock() {
        var s = new Setting<int>("width", 4);
        ((ILockable)s).Lock();

        Assert.Throws<InvalidOperationException>(() => s.Value = 8);
    }

    [Fact]
    public void Setting_IsLocked_ReflectsState() {
        var s = new Setting<string>("name", "default");
        Assert.False(s.IsLocked);

        ((ILockable)s).Lock();
        Assert.True(s.IsLocked);
    }

    // ── DialBoard ─────────────────────────────────────────────────────────────

    [Fact]
    public void DialBoard_AddCounter_ReturnsUsableCounter() {
        var board = new DialBoard("top.fetch");
        Counter c = board.AddCounter("cycles", "Total cycles elapsed");

        c.IncrementBy(42);
        Assert.Equal(42, board.GetCounter("cycles").Value);
    }

    [Fact]
    public void DialBoard_AddDial_ReturnsUsableDial() {
        var board = new DialBoard("top.fetch");
        Counter cycles = board.AddCounter("cycles");
        Counter retired = board.AddCounter("retired");
        _ = board.AddDial("ipc", () => retired.Value / (double)cycles.Value);

        cycles.IncrementBy(10);
        retired.IncrementBy(8);

        Assert.Equal(0.8, board.GetDial("ipc").Read(), 5);
    }

    [Fact]
    public void DialBoard_DuplicateCounterName_Throws() {
        var board = new DialBoard("top.fetch");
        board.AddCounter("cycles");

        Assert.Throws<InvalidOperationException>(() => board.AddCounter("cycles"));
    }

    [Fact]
    public void DialBoard_DuplicateDialName_Throws() {
        var board = new DialBoard("top.fetch");
        board.AddDial("ipc", () => 1.0);

        Assert.Throws<InvalidOperationException>(() => board.AddDial("ipc", () => 2.0));
    }

    [Fact]
    public void DialBoard_GetCounter_MissingName_Throws() {
        var board = new DialBoard("top.fetch");
        Assert.Throws<KeyNotFoundException>(() => board.GetCounter("missing"));
    }

    [Fact]
    public void DialBoard_GetDial_MissingName_Throws() {
        var board = new DialBoard("top.fetch");
        Assert.Throws<KeyNotFoundException>(() => board.GetDial("missing"));
    }

    [Fact]
    public void DialBoard_Reset_ZeroesAllCounters() {
        var board = new DialBoard("top.fetch");
        Counter c1 = board.AddCounter("a");
        Counter c2 = board.AddCounter("b");

        c1.IncrementBy(10);
        c2.IncrementBy(20);
        board.Reset();

        Assert.Equal(0, c1.Value);
        Assert.Equal(0, c2.Value);
    }

    // ── Snapshot ──────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_CapturesCurrentValues() {
        var board = new DialBoard("top.fetch");
        Counter cycles = board.AddCounter("cycles");
        Counter retired = board.AddCounter("retired");
        _ = board.AddDial("ipc", () => retired.Value / (double)cycles.Value);

        cycles.IncrementBy(100);
        retired.IncrementBy(75);

        DialBoardSnapshot snap = board.Snapshot();

        Assert.Equal("top.fetch", snap.OwnerPath);
        Assert.Equal(100, snap.Counters["cycles"]);
        Assert.Equal(75, snap.Counters["retired"]);
        Assert.Equal(0.75, snap.Dials["ipc"], 5);
    }

    [Fact]
    public void Snapshot_IsImmutable_AfterFurtherIncrements() {
        var board = new DialBoard("top.fetch");
        Counter cycles = board.AddCounter("cycles");

        cycles.IncrementBy(10);
        DialBoardSnapshot snap = board.Snapshot();

        cycles.IncrementBy(90); // mutate after snapshot

        Assert.Equal(10, snap.Counters["cycles"]); // snapshot unchanged
        Assert.Equal(100, cycles.Value);           // live counter updated
    }

    // ── Gear integration ──────────────────────────────────────────────────────

    [Fact]
    public void Gear_DialBoard_IsAccessible() {
        var root = new SimNode("top");
        var esc = new Escapement();
        var gear = new MetricGear("fetch", root, esc);

        gear.Initialize();

        gear.Dials.GetCounter("cycles").IncrementBy(5);
        Assert.Equal(5, gear.Dials.GetCounter("cycles").Value);
    }

    [Fact]
    public void Gear_Reset_ZeroesDialBoard() {
        var root = new SimNode("top");
        var esc = new Escapement();
        var gear = new MetricGear("fetch", root, esc);

        gear.Initialize();
        gear.Dials.GetCounter("cycles").IncrementBy(50);

        root.BeginFinalizing();
        root.BeginRunning();
        gear.Reset();

        Assert.Equal(0, gear.Dials.GetCounter("cycles").Value);
    }

    [Fact]
    public void Gear_Setting_LockedAfterLockSettings() {
        var root = new SimNode("top");
        var esc = new Escapement();
        var gear = new ConfigurableGear("fetch", root, esc);

        gear.Initialize();
        gear.IssueWidth.Value = 4;

        gear.LockSettings();

        Assert.Throws<InvalidOperationException>(() => gear.IssueWidth.Value = 8);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class MetricGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc) {
        public override void Initialize() {
            Dials.AddCounter("cycles", "Total cycles");
            Dials.AddDial("double_cycles", () => Dials.GetCounter("cycles").Value * 2.0);
        }
    }

    private sealed class ConfigurableGear(string name, SimNode parent, Escapement esc)
        : Gear(name, parent, esc) {
        public Setting<int> IssueWidth { get; private set; } = null!;

        public override void Initialize() {
            IssueWidth = AddSetting("issue_width", 1, "Number of instructions issued per cycle");
        }
    }
}