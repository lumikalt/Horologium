using Mechanism;
using Orrery.Cache;
using Orrery.Devices;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32.System;

/// <summary>
///     Integration tests for UART MMIO: a hand-assembled RV32 program stores characters to the
///     SiFive UART0 txdata register at 0x10013000 via a <see cref="PeripheralBus" />.
/// </summary>
public class UartMmioTests {
    // lui  x5, 0x10013      → x5  = 0x10013000 (UART base)
    // addi x6, x0, 72      → x6  = 'H'
    // sw   x6, 0(x5)       → txdata = 'H'
    // addi x6, x0, 105     → x6  = 'i'
    // sw   x6, 0(x5)       → txdata = 'i'
    // ebreak
    private static byte[] MakeUartProgram() {
        uint[] words = [
            0x100132B7u, // lui  x5,  0x10013
            0x04800313u, // addi x6,  x0, 72   ('H')
            0x0062A023u, // sw   x6,  0(x5)
            0x06900313u, // addi x6,  x0, 105  ('i')
            0x0062A023u, // sw   x6,  0(x5)
            0x00100073u, // ebreak
        ];
        var b = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            b[i * 4 + 0] = (byte)words[i];
            b[i * 4 + 1] = (byte)(words[i] >> 8);
            b[i * 4 + 2] = (byte)(words[i] >> 16);
            b[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        return b;
    }

    [Fact]
    public void SingleCycle_WritesToUart_ViaPeripheralBus() {
        var output = new StringWriter();
        var uart = new UartDevice(output);
        byte[] program = MakeUartProgram();
        var workload = new ByteArrayWorkload(program);

        var flatMem = new FlatMemory(workload.MemorySize);
        workload.Load(flatMem);
        IMemory bus = new PeripheralBus(flatMem, [(uart, UartDevice.DefaultBase, UartDevice.RegionSize),]);

        new SingleCycleTrain(new Rv32Mechanism(), bus, workload.EntryPoint).Run(50);

        Assert.Equal("Hi", output.ToString());
    }

    [Fact]
    public void FiveStage_WithCache_WritesToUart_ViaPeripheralBus() {
        var output = new StringWriter();
        var uart = new UartDevice(output);
        byte[] program = MakeUartProgram();
        var workload = new ByteArrayWorkload(program);

        var flatMem = new FlatMemory(workload.MemorySize);
        workload.Load(flatMem);
        IMemory bus = new PeripheralBus(flatMem, [(uart, UartDevice.DefaultBase, UartDevice.RegionSize),]);

        // Configure an L1 D-cache with the UART region marked uncacheable so stores
        // go straight to the PeripheralBus (and hence to UartDevice) without being
        // absorbed by the cache.
        var dCfg = new MemoryConfig(
            4096,
            UncacheableBase: UartDevice.DefaultBase,
            UncacheableSize: UartDevice.RegionSize
        );

        new FiveStageTrain(
            new Rv32Mechanism(), bus, workload.EntryPoint,
            iMemConfig: MemoryConfig.None,
            dMemConfig: dCfg
        ).Run(200);

        Assert.Equal("Hi", output.ToString());
    }

    [Fact]
    public void PeripheralBus_Load_GoesToBacking() {
        // Load() must always target the backing (FlatMemory), not any device.
        var flatMem = new FlatMemory(256);
        var uart = new UartDevice(TextWriter.Null);
        var bus = new PeripheralBus(flatMem, [(uart, UartDevice.DefaultBase, UartDevice.RegionSize),]);

        ReadOnlySpan<byte> data = [0xAA, 0xBB,];
        bus.Load(0, data); // should write to FlatMemory

        Assert.Equal(0xBBAAUL, flatMem.Read(0, 2));
    }
}