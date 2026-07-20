#region

using Orrery.Devices;

#endregion

namespace Tests.Orrery;

public class UartDeviceTests {
    private static UartDevice Make(StringWriter sw) => new(sw);
    private static UartDevice Make() => new(TextWriter.Null);

    [Fact]
    public void TxData_Write_OutputsChar() {
        var sw = new StringWriter();
        Make(sw).Write(UartDevice.DefaultBase + 0x00, 'H', 4);
        Assert.Equal("H", sw.ToString());
    }

    [Fact]
    public void TxData_WriteMultiple_OutputsAll() {
        var sw = new StringWriter();
        UartDevice uart = Make(sw);
        uart.Write(UartDevice.DefaultBase, 'H', 4);
        uart.Write(UartDevice.DefaultBase, 'i', 4);
        uart.Write(UartDevice.DefaultBase, '!', 4);
        Assert.Equal("Hi!", sw.ToString());
    }

    [Fact]
    public void TxData_Read_NeverFull() {
        ulong txdata = Make().Read(UartDevice.DefaultBase + 0x00, 4);
        Assert.Equal(0UL, txdata & 0x80000000UL);
    }

    [Fact]
    public void RxData_EmptyQueue_SetsBit31() {
        ulong rxdata = Make().Read(UartDevice.DefaultBase + 0x04, 4);
        Assert.Equal(0x80000000UL, rxdata);
    }

    [Fact]
    public void RxData_Dequeues_EnqueuedByte() {
        UartDevice uart = Make();
        uart.Enqueue(0x42);
        ulong rxdata = uart.Read(UartDevice.DefaultBase + 0x04, 4);
        Assert.Equal(0x42UL, rxdata);
    }

    [Fact]
    public void RxData_AfterDequeue_ReportsEmpty() {
        UartDevice uart = Make();
        uart.Enqueue(0x42);
        uart.Read(UartDevice.DefaultBase + 0x04, 4); // consume
        ulong rxdata = uart.Read(UartDevice.DefaultBase + 0x04, 4);
        Assert.Equal(0x80000000UL, rxdata); // empty again
    }

    [Fact]
    public void TxCtrl_Read_IsEnabled() { Assert.Equal(1UL, Make().Read(UartDevice.DefaultBase + 0x08, 4)); }

    [Fact]
    public void RxCtrl_EmptyQueue_IsZero() { Assert.Equal(0UL, Make().Read(UartDevice.DefaultBase + 0x0C, 4)); }

    [Fact]
    public void RxCtrl_NonEmptyQueue_IsOne() {
        UartDevice uart = Make();
        uart.Enqueue(1);
        Assert.Equal(1UL, uart.Read(UartDevice.DefaultBase + 0x0C, 4));
    }

    [Fact]
    public void UnknownRegister_Read_ReturnsZero() { Assert.Equal(0UL, Make().Read(UartDevice.DefaultBase + 0x1C, 4)); }

    [Fact]
    public void Write_OnlyByteBits_OutputsSingleChar() {
        var sw = new StringWriter();
        // writing 0x141 should output only the low 8 bits: 'A' (0x41)
        Make(sw).Write(UartDevice.DefaultBase, 0x141, 4);
        Assert.Equal("A", sw.ToString());
    }
}