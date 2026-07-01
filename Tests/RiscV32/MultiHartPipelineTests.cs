using Orrery.Cache;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// Integration tests for <see cref="MultiHartPipeline"/>: two independent trains
/// stepped round-robin, and coherent read via shared MesiBus.
/// <para>
/// Encoded instructions:
///   addi x1, x0, 42  = 0x02A00093
///   addi x1, x0, 99  = 0x06300093
///   sw   x1, 0(x2)   = 0x00112023
///   lw   x3, 0(x4)   = 0x00022183
///   ebreak            = 0x00100073
/// </para>
/// <para>
/// MESI timing guarantee: MultiHartPipeline steps H0 before H1 in every outer tick.
/// For single-Gear trains (SingleCycle, Superscalar) the write and read both happen
/// in the same outer tick — H0 first. For the five-stage pipeline both trains reach
/// EX at the same outer tick (tick 3) and H0's EX fires before H1's, so H0's store
/// writes to cache0 (→ M) before H1's load issues a BusRead.
/// </para>
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
        const uint addi42 = 0x02A00093;
        const uint addi99 = 0x06300093;

        var mem0 = new FlatMemory(0x100);
        var mem1 = new FlatMemory(0x100);
        mem0.Load(0x00, ToBytes(addi42, MultiHartPipelineTests.Ebreak));
        mem1.Load(0x00, ToBytes(addi99, MultiHartPipelineTests.Ebreak));

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

        const uint swX1 = 0x00112023; // sw x1, 0(x2)
        const uint lwX3 = 0x00022183; // lw x3, 0(x4)

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(swX1, MultiHartPipelineTests.Ebreak));
        flat.Load(0x40, ToBytes(lwX3, MultiHartPipelineTests.Ebreak));

        var bus = new MesiBus(flat);
        var cache0 = new MesiCache(bus, 256, 2, 64);
        var cache1 = new MesiCache(bus, 256, 2, 64);

        var train0 = new SingleCycleTrain(new Rv32Mechanism(), cache0);
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

    // ── FiveStageTrain ────────────────────────────────────────────────────────

    [Fact]
    public void TwoFiveStageHarts_RunIndependently_BothProduceCorrectResult() {
        const uint addi42 = 0x02A00093;
        const uint addi99 = 0x06300093;

        var mem0 = new FlatMemory(0x100);
        var mem1 = new FlatMemory(0x100);
        mem0.Load(0x00, ToBytes(addi42, MultiHartPipelineTests.Ebreak));
        mem1.Load(0x00, ToBytes(addi99, MultiHartPipelineTests.Ebreak));

        var train0 = new FiveStageTrain(new Rv32Mechanism(), mem0);
        var train1 = new FiveStageTrain(new Rv32Mechanism(), mem1);

        new MultiHartPipeline(train0, train1).Run(1_000);

        Assert.Equal(42UL, train0.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(99UL, train1.ArchState.IntegerRegisters.Read(1));
    }

    [Fact]
    public void FiveStageHarts_MesiCoherence_WriteByHart0_ReadByHart1_SeesCoherentValue() {
        // H0 (0x00): sw x1, 0(x2) then ebreak  — writes 0xCAFE to 0x200 via cache0
        // H1 (0x40): lw x3, 0(x4) then ebreak  — reads  0x200 via cache1
        //
        // Both pipelines reach EX at outer tick 3. H0's EX fires first (round-robin
        // order), writing cache0 line → M before H1's EX issues a BusRead.

        const uint swX1 = 0x00112023;
        const uint lwX3 = 0x00022183;

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(swX1, MultiHartPipelineTests.Ebreak));
        flat.Load(0x40, ToBytes(lwX3, MultiHartPipelineTests.Ebreak));

        var bus = new MesiBus(flat);
        var cache0 = new MesiCache(bus, 256, 2, 64);
        var cache1 = new MesiCache(bus, 256, 2, 64);

        var train0 = new FiveStageTrain(new Rv32Mechanism(), cache0);
        var train1 = new FiveStageTrain(new Rv32Mechanism(), cache1, 0x40);

        train0.ArchState.IntegerRegisters.Write(1, 0xCAFE);
        train0.ArchState.IntegerRegisters.Write(2, 0x200);
        train1.ArchState.IntegerRegisters.Write(4, 0x200);

        new MultiHartPipeline(train0, train1).Run(1_000);

        Assert.Equal(0xCAFEUL, train1.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(MesiState.Shared, cache0.StateOf(0x200));
        Assert.Equal(MesiState.Shared, cache1.StateOf(0x200));
    }

    // ── SuperscalarTrain ──────────────────────────────────────────────────────

    [Fact]
    public void TwoSuperscalarHarts_RunIndependently_BothProduceCorrectResult() {
        const uint addi42 = 0x02A00093;
        const uint addi99 = 0x06300093;

        var mem0 = new FlatMemory(0x100);
        var mem1 = new FlatMemory(0x100);
        mem0.Load(0x00, ToBytes(addi42, MultiHartPipelineTests.Ebreak));
        mem1.Load(0x00, ToBytes(addi99, MultiHartPipelineTests.Ebreak));

        var train0 = new SuperscalarTrain(new Rv32Mechanism(), mem0);
        var train1 = new SuperscalarTrain(new Rv32Mechanism(), mem1);

        new MultiHartPipeline(train0, train1).Run(1_000);

        Assert.Equal(42UL, train0.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(99UL, train1.ArchState.IntegerRegisters.Read(1));
    }

    // ── OooeTrain ─────────────────────────────────────────────────────────────

    [Fact]
    public void TwoOoOHarts_RunIndependently_BothProduceCorrectResult() {
        const uint addi42 = 0x02A00093;
        const uint addi99 = 0x06300093;

        var mem0 = new FlatMemory(0x100);
        var mem1 = new FlatMemory(0x100);
        mem0.Load(0x00, ToBytes(addi42, MultiHartPipelineTests.Ebreak));
        mem1.Load(0x00, ToBytes(addi99, MultiHartPipelineTests.Ebreak));

        var train0 = new OooeTrain(new Rv32Mechanism(), mem0);
        var train1 = new OooeTrain(new Rv32Mechanism(), mem1);

        new MultiHartPipeline(train0, train1).Run(1_000);

        Assert.Equal(42UL, train0.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(99UL, train1.ArchState.IntegerRegisters.Read(1));
    }

    // ── LR/SC atomics across pipeline trains ──────────────────────────────────

    [Fact]
    public void PipelinedHarts_LrScAtomic_ScFailsWhenRemoteStoreIntervenes() {
        // H0 at 0x00: lr.w x1, (x2)      — loads 0 from 0x200, sets reservation
        //             sc.w x4, x3, (x2)  — H1 stored between LR and SC → x4 = 1 (fail)
        //             ebreak
        // H1 at 0x40: sw x5, 0(x6)       — stores 0xDEAD to 0x200 via cache1;
        //                                   BusReadInvalidate cancels H0's reservation
        //             ebreak
        //
        // Round-robin ordering:
        //   outer tick 1: H0 = lr.w  (reservation set)
        //                 H1 = sw    (BusReadInvalidate → reservation cancelled)
        //   outer tick 2: H0 = sc.w  (no reservation → x4 = 1)

        const uint lrW = 0x100120AF;  // lr.w  x1,    (x2)
        const uint scW4 = 0x1831222F; // sc.w  x4, x3, (x2)
        const uint swH1 = 0x00532023; // sw    x5, 0(x6)

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(lrW, scW4, MultiHartPipelineTests.Ebreak));
        flat.Load(0x40, ToBytes(swH1, MultiHartPipelineTests.Ebreak));

        var table = new ReservationTable();
        var bus = new MesiBus(flat, table);
        var cache0 = new MesiCache(bus, 256, 2, 64);
        var cache1 = new MesiCache(bus, 256, 2, 64);

        var train0 = new SingleCycleTrain(new Rv32Mechanism(reservationTable: table, hartId: 0), cache0);
        var train1 = new SingleCycleTrain(new Rv32Mechanism(reservationTable: table, hartId: 1), cache1, 0x40);

        train0.ArchState.IntegerRegisters.Write(2, 0x200);  // lr.w / sc.w address
        train0.ArchState.IntegerRegisters.Write(3, 0xBEEF); // would-be SC store value (never written)
        train1.ArchState.IntegerRegisters.Write(5, 0xDEAD); // sw store value
        train1.ArchState.IntegerRegisters.Write(6, 0x200);  // sw address

        new MultiHartPipeline(train0, train1).Run(1_000);

        // LR read the initial zero; SC failed because H1 stored in between
        Assert.Equal(0UL, train0.ArchState.IntegerRegisters.Read(1)); // lr.w result
        Assert.Equal(1UL, train0.ArchState.IntegerRegisters.Read(4)); // sc.w failure flag
    }
}