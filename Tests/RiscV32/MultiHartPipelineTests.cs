using Orrery.Cache;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// Integration tests for <see cref="MultiHartPipeline"/>: two independent
/// SingleCycleTrains stepped round-robin, and coherent read via shared MesiBus.
///
/// Encoded instructions:
///   addi x1, x0, 42  = 0x02A00093
///   addi x1, x0, 99  = 0x06300093
///   sw   x1, 0(x2)   = 0x00112023
///   lw   x3, 0(x4)   = 0x00022183
///   ebreak            = 0x00100073
/// </summary>
public class MultiHartPipelineTests {
    private const uint Ebreak = 0x00100073;

    private static byte[] ToBytes(params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    // ── Independent execution ─────────────────────────────────────────────────

    [Fact]
    public void TwoHarts_RunIndependently_BothProduceCorrectResult() {
        // Hart 0: addi x1, x0, 42; ebreak  →  x1 = 42
        // Hart 1: addi x1, x0, 99; ebreak  →  x1 = 99
        const uint Addi42 = 0x02A00093;
        const uint Addi99 = 0x06300093;

        var mem0 = new FlatMemory(0x100);
        var mem1 = new FlatMemory(0x100);
        mem0.Load(0x00, ToBytes(Addi42, MultiHartPipelineTests.Ebreak));
        mem1.Load(0x00, ToBytes(Addi99, MultiHartPipelineTests.Ebreak));

        var train0 = new SingleCycleTrain(new Rv32Mechanism(), mem0);
        var train1 = new SingleCycleTrain(new Rv32Mechanism(), mem1);

        var pipeline = new MultiHartPipeline(train0, train1);
        pipeline.Run(1_000);

        Assert.Equal(42UL, train0.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(99UL, train1.ArchState.IntegerRegisters.Read(1));
    }

    [Fact]
    public void HartCount_IsCorrect() {
        var mem = new FlatMemory(0x100);
        mem.Load(0x00, ToBytes(MultiHartPipelineTests.Ebreak));

        var t0 = new SingleCycleTrain(new Rv32Mechanism(), mem);
        var t1 = new SingleCycleTrain(new Rv32Mechanism(), mem);
        var t2 = new SingleCycleTrain(new Rv32Mechanism(), mem);

        Assert.Equal(3, new MultiHartPipeline(t0, t1, t2).HartCount);
    }

    // ── Per-hart MESI cache coherence ─────────────────────────────────────────

    [Fact]
    public void PerHartMesiCaches_WriteByHart0_ReadByHart1_SeesCoherentValue() {
        // Hart 0 at 0x00: sw x1, 0(x2)  →  writes 0xCAFE to 0x200 via cache0 (→ M)
        //                 ebreak
        // Hart 1 at 0x40: lw x3, 0(x4)  →  reads from 0x200 via cache1
        //                 ebreak
        //
        // Round-robin tick 1: H0 sw → cache0 line 0x200 is Modified.
        //                     H1 lw → BusRead: cache0 M→writeback+S, cache1 installs S, reads 0xCAFE.
        // After run: train1.ArchState.IntegerRegisters.Read(3) == 0xCAFE.

        const uint SwX1 = 0x00112023; // sw x1, 0(x2)
        const uint LwX3 = 0x00022183; // lw x3, 0(x4)

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(SwX1, MultiHartPipelineTests.Ebreak));
        flat.Load(0x40, ToBytes(LwX3, MultiHartPipelineTests.Ebreak));

        var bus = new MesiBus(flat);
        var cache0 = new MesiCache(bus, 256, 2, 64);
        var cache1 = new MesiCache(bus, 256, 2, 64);

        var train0 = new SingleCycleTrain(new Rv32Mechanism(), cache0, 0x00);
        var train1 = new SingleCycleTrain(new Rv32Mechanism(), cache1, 0x40);

        train0.ArchState.IntegerRegisters.Write(1, 0xCAFE); // value to store
        train0.ArchState.IntegerRegisters.Write(2, 0x200);  // store address
        train1.ArchState.IntegerRegisters.Write(4, 0x200);  // load address

        new MultiHartPipeline(train0, train1).Run(1_000);

        Assert.Equal(0xCAFEUL, train1.ArchState.IntegerRegisters.Read(3));

        // Both caches should be in Shared state after the BusRead snoop
        Assert.Equal(MesiState.Shared, cache0.StateOf(0x200));
        Assert.Equal(MesiState.Shared, cache1.StateOf(0x200));
    }
}