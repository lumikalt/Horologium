using Orrery.Devices;

namespace Tests.Orrery;

public class Ns16550aUartTests {
    private static Ns16550aUart Make(StringWriter sw) => new(sw);
    private static Ns16550aUart Make() => new(TextWriter.Null);

    [Fact]
    public void Thr_Write_OutputsChar() {
        var sw = new StringWriter();
        Make(sw).Write(Ns16550aUart.DefaultBase + 0x00, 'H', 4);
        Assert.Equal("H", sw.ToString());
    }

    [Fact]
    public void Thr_Write_MultipleChars() {
        var sw = new StringWriter();
        Ns16550aUart uart = Make(sw);
        uart.Write(Ns16550aUart.DefaultBase, 'O', 4);
        uart.Write(Ns16550aUart.DefaultBase, 'K', 4);
        Assert.Equal("OK", sw.ToString());
    }

    [Fact]
    public void Thr_Write_MasksToLowByte() {
        var sw = new StringWriter();
        // 0x141 → only low byte 0x41 = 'A'
        Make(sw).Write(Ns16550aUart.DefaultBase, 0x141, 4);
        Assert.Equal("A", sw.ToString());
    }

    [Fact]
    public void Lsr_Read_ThreAndTemtAlwaysSet() {
        ulong lsr = Make().Read(Ns16550aUart.DefaultBase + 0x05, 4);
        Assert.NotEqual(0u, lsr & (1u << 5)); // THRE
        Assert.NotEqual(0u, lsr & (1u << 6)); // TEMT
    }

    [Fact]
    public void Lsr_EmptyRx_DrNotSet() {
        ulong lsr = Make().Read(Ns16550aUart.DefaultBase + 0x05, 4);
        Assert.Equal(0u, lsr & (1u << 0)); // DR=0 when RX empty
    }

    [Fact]
    public void Lsr_NonEmptyRx_DrSet() {
        Ns16550aUart uart = Make();
        uart.Enqueue(0x41);
        ulong lsr = uart.Read(Ns16550aUart.DefaultBase + 0x05, 4);
        Assert.NotEqual(0u, lsr & (1u << 0)); // DR=1
    }

    [Fact]
    public void Rhr_EmptyQueue_ReturnsZero() { Assert.Equal(0UL, Make().Read(Ns16550aUart.DefaultBase + 0x00, 4)); }

    [Fact]
    public void Rhr_DequeuesEnqueuedByte() {
        Ns16550aUart uart = Make();
        uart.Enqueue(0x42);
        Assert.Equal(0x42UL, uart.Read(Ns16550aUart.DefaultBase + 0x00, 4));
    }

    [Fact]
    public void Iir_Read_NoPendingInterrupt() {
        // Bit 0 = 1 means no interrupt pending
        ulong iir = Make().Read(Ns16550aUart.DefaultBase + 0x02, 4);
        Assert.NotEqual(0u, iir & 1u);
    }

    [Fact]
    public void Scr_ReadWrite_RoundTrips() {
        Ns16550aUart uart = Make();
        uart.Write(Ns16550aUart.DefaultBase + 0x07, 0xAB, 4);
        Assert.Equal(0xABUL, uart.Read(Ns16550aUart.DefaultBase + 0x07, 4));
    }

    [Fact]
    public void UnmappedRegister_Read_ReturnsZero() {
        Assert.Equal(0UL, Make().Read(Ns16550aUart.DefaultBase + 0xF0, 4));
    }
}