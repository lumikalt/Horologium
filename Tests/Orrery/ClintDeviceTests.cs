using Orrery.Devices;

namespace Tests.Orrery;

public class ClintDeviceTests {
    private static ClintDevice Make() => new();

    // ── mtime MMIO ────────────────────────────────────────────────────────────

    [Fact]
    public void Mtime_InitiallyZero() {
        ClintDevice clint = Make();
        ulong lo = clint.Read(ClintDevice.DefaultBase + 0xBFF8, 4);
        ulong hi = clint.Read(ClintDevice.DefaultBase + 0xBFFC, 4);
        Assert.Equal(0UL, lo);
        Assert.Equal(0UL, hi);
    }

    [Fact]
    public void Mtime_Write_UpdatesLoAndHi() {
        ClintDevice clint = Make();
        clint.Write(ClintDevice.DefaultBase + 0xBFF8, 0xDEADBEEFu, 4);
        clint.Write(ClintDevice.DefaultBase + 0xBFFC, 0x0000CAFEu, 4);
        Assert.Equal(0xDEADBEEFu, clint.Read(ClintDevice.DefaultBase + 0xBFF8, 4));
        Assert.Equal(0x0000CAFEu, clint.Read(ClintDevice.DefaultBase + 0xBFFC, 4));
    }

    [Fact]
    public void Mtime_WriteHiPreservesLo() {
        ClintDevice clint = Make();
        clint.Write(ClintDevice.DefaultBase + 0xBFF8, 0x12345678u, 4);
        clint.Write(ClintDevice.DefaultBase + 0xBFFC, 0xABCDEF01u, 4);
        Assert.Equal(0x12345678u, clint.Read(ClintDevice.DefaultBase + 0xBFF8, 4));
    }

    [Fact]
    public void Advance_IncreasesMtime() {
        ClintDevice clint = Make();
        clint.Advance();
        clint.Advance();
        Assert.Equal(2UL, clint.Read(ClintDevice.DefaultBase + 0xBFF8, 4));
    }

    // ── mtimecmp MMIO ─────────────────────────────────────────────────────────

    [Fact]
    public void Mtimecmp0_InitiallyMaxValue() {
        ClintDevice clint = Make();
        ulong lo = clint.Read(ClintDevice.DefaultBase + 0x4000, 4);
        ulong hi = clint.Read(ClintDevice.DefaultBase + 0x4004, 4);
        Assert.Equal((ulong)uint.MaxValue, lo);
        Assert.Equal((ulong)uint.MaxValue, hi);
    }

    [Fact]
    public void Mtimecmp0_Write_RoundTrips() {
        ClintDevice clint = Make();
        clint.Write(ClintDevice.DefaultBase + 0x4000, 0x00001000u, 4);
        clint.Write(ClintDevice.DefaultBase + 0x4004, 0x00000000u, 4);
        Assert.Equal(0x00001000u, clint.Read(ClintDevice.DefaultBase + 0x4000, 4));
        Assert.Equal(0x00000000u, clint.Read(ClintDevice.DefaultBase + 0x4004, 4));
    }

    [Fact]
    public void Mtimecmp0_WriteLoPreservesHi() {
        ClintDevice clint = Make();
        clint.Write(ClintDevice.DefaultBase + 0x4004, 0xABCDu, 4);
        clint.Write(ClintDevice.DefaultBase + 0x4000, 0x1234u, 4);
        Assert.Equal(0xABCDu, clint.Read(ClintDevice.DefaultBase + 0x4004, 4));
    }

    // ── msip MMIO ─────────────────────────────────────────────────────────────

    [Fact]
    public void Msip0_InitiallyZero() { Assert.Equal(0UL, Make().Read(ClintDevice.DefaultBase + 0x0000, 4)); }

    [Fact]
    public void Msip0_Write_RoundTrips() {
        ClintDevice clint = Make();
        clint.Write(ClintDevice.DefaultBase + 0x0000, 1, 4);
        Assert.Equal(1UL, clint.Read(ClintDevice.DefaultBase + 0x0000, 4));
    }

    [Fact]
    public void Msip0_Write_MasksToLsb() {
        ClintDevice clint = Make();
        clint.Write(ClintDevice.DefaultBase + 0x0000, 0xFF, 4);
        Assert.Equal(1UL, clint.Read(ClintDevice.DefaultBase + 0x0000, 4));
    }

    // ── TimerPending / SoftwarePending ────────────────────────────────────────

    [Fact]
    public void TimerPending_MtimeUnderMtimecmp_IsFalse() {
        ClintDevice clint = Make();
        for (var i = 0; i < 10; i++) clint.Advance();
        Assert.False(clint.TimerPending());
    }

    [Fact]
    public void TimerPending_MtimeReachesMtimecmp_IsTrue() {
        ClintDevice clint = Make();
        clint.Write(ClintDevice.DefaultBase + 0x4000, 5, 4);
        clint.Write(ClintDevice.DefaultBase + 0x4004, 0, 4);
        for (var i = 0; i < 5; i++) clint.Advance();
        Assert.True(clint.TimerPending());
    }

    [Fact]
    public void TimerPending_AfterMtimecmpRaised_IsFalse() {
        ClintDevice clint = Make();
        // Set mtimecmp = 3, tick 3 times → fires
        clint.Write(ClintDevice.DefaultBase + 0x4000, 3, 4);
        clint.Write(ClintDevice.DefaultBase + 0x4004, 0, 4);
        for (var i = 0; i < 3; i++) clint.Advance();
        Assert.True(clint.TimerPending());

        // Reprogram mtimecmp to far future — should deassert
        clint.Write(ClintDevice.DefaultBase + 0x4004, uint.MaxValue, 4);
        Assert.False(clint.TimerPending());
    }

    [Fact]
    public void SoftwarePending_Msip0Set_IsTrue() {
        ClintDevice clint = Make();
        clint.Write(ClintDevice.DefaultBase + 0x0000, 1, 4);
        Assert.True(clint.SoftwarePending());
    }

    [Fact]
    public void SoftwarePending_Msip0Cleared_IsFalse() {
        ClintDevice clint = Make();
        clint.Write(ClintDevice.DefaultBase + 0x0000, 1, 4);
        Assert.True(clint.SoftwarePending());
        clint.Write(ClintDevice.DefaultBase + 0x0000, 0, 4);
        Assert.False(clint.SoftwarePending());
    }

    // ── TicksPerInstruction ───────────────────────────────────────────────────

    [Fact]
    public void TicksPerInstruction_ControlsAdvanceRate() {
        var clint = new ClintDevice { TicksPerInstruction = 10, };
        // mtimecmp = 50; fires after 5 advances × 10 = 50
        clint.Write(ClintDevice.DefaultBase + 0x4000, 50, 4);
        clint.Write(ClintDevice.DefaultBase + 0x4004, 0, 4);
        for (var i = 0; i < 4; i++) clint.Advance();
        Assert.False(clint.TimerPending()); // 40 < 50
        clint.Advance();
        Assert.True(clint.TimerPending()); // 50 >= 50
    }
}