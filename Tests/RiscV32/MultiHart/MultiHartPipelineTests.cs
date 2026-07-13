using Orrery.Cache;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32.MultiHart;

/// <summary>
///     Integration tests for <see cref="MultiHartPipeline" />: two independent trains
///     stepped round-robin, and coherent read via shared MoesifBus.
///     <para>
///         Encoded instructions:
///         addi x1, x0, 42  = 0x02A00093
///         addi x1, x0, 99  = 0x06300093
///         sw   x1, 0(x2)   = 0x00112023
///         lw   x3, 0(x4)   = 0x00022183
///         ebreak            = 0x00100073
///     </para>
///     <para>
///         MOESIF timing guarantee: MultiHartPipeline steps H0 before H1 in every outer tick.
///         For single-Gear trains (SingleCycle, Superscalar) the write and read both happen
///         in the same outer tick — H0 first. For the five-stage pipeline both trains reach
///         EX at the same outer tick (tick 3) and H0's EX fires before H1's, so H0's store
///         writes to cache0 (→ M) before H1's load issues a BusRead.
///     </para>
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

    // ── Per-hart MOESIF cache coherence ─────────────────────────────────────────

    [Fact]
    public void PerHartMoesifCaches_WriteByHart0_ReadByHart1_SeesCoherentValue() {
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

        var bus = new MoesifBus(flat);
        var cache0 = new MoesifCache(bus, 256, 2, 64);
        var cache1 = new MoesifCache(bus, 256, 2, 64);

        var train0 = new SingleCycleTrain(new Rv32Mechanism(), cache0);
        var train1 = new SingleCycleTrain(new Rv32Mechanism(), cache1, 0x40);

        train0.ArchState.IntegerRegisters.Write(1, 0xCAFE); // value to store
        train0.ArchState.IntegerRegisters.Write(2, 0x200);  // store address
        train1.ArchState.IntegerRegisters.Write(4, 0x200);  // load address

        new MultiHartPipeline(train0, train1).Run(1_000);

        Assert.Equal(0xCAFEUL, train1.ArchState.IntegerRegisters.Read(3));

        // Writer supplies cache-to-cache and keeps the line as Owned; reader installs Shared
        Assert.Equal(MoesifState.Owned, cache0.StateOf(0x200)); // writer keeps dirty ownership (MOESIF)
        Assert.Equal(MoesifState.Shared, cache1.StateOf(0x200));
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
    public void FiveStageHarts_MoesifCoherence_WriteByHart0_ReadByHart1_SeesCoherentValue() {
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

        var bus = new MoesifBus(flat);
        var cache0 = new MoesifCache(bus, 256, 2, 64);
        var cache1 = new MoesifCache(bus, 256, 2, 64);

        var train0 = new FiveStageTrain(new Rv32Mechanism(), cache0);
        var train1 = new FiveStageTrain(new Rv32Mechanism(), cache1, 0x40);

        train0.ArchState.IntegerRegisters.Write(1, 0xCAFE);
        train0.ArchState.IntegerRegisters.Write(2, 0x200);
        train1.ArchState.IntegerRegisters.Write(4, 0x200);

        new MultiHartPipeline(train0, train1).Run(1_000);

        Assert.Equal(0xCAFEUL, train1.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(MoesifState.Owned, cache0.StateOf(0x200)); // writer keeps dirty ownership (MOESIF)
        Assert.Equal(MoesifState.Shared, cache1.StateOf(0x200));
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

    [Fact]
    public void OoOHarts_MoesifCoherence_StoreCommitsThenLoadSeesCoherentValue() {
        // OooeTrain's PRF starts zeroed — ArchState.IntegerRegisters.Write does not seed the
        // PRF, so all values must be computed within the programs (like addi x1,x0,42 above).
        //
        // H0 at 0x00: lui+addi build x1=0xCAFE; addi builds x2=0x200; sw x1,0(x2); ebreak
        //   → sw commits 0xCAFE to 0x200 via cache0 (→ M) at OoO cycle ~9; halts cycle ~9.
        //
        // H1 at 0x40: 15 nops fill the decode queue before addi x4,x0,0x200 and lw arrive,
        //   delaying lw to OoO cycle ~13, safely past H0's ~cycle-9 commit.
        //   lw misses cold cache1 → BusRead snoop: cache0 M→writeback+S, cache1 installs S.
        //
        // Nop count chosen so lw executes ~4 cycles after H0's commit; revisit if the OoO
        // pipeline timing changes (e.g. wider issue or different IQ partitioning).

        const uint luiX1 = 0x0000D0B7;  // lui  x1, 0xD          → x1 = 0x0000D000
        const uint addiX1 = 0xAFE08093; // addi x1, x1, -1282    → x1 = 0x0000CAFE
        const uint addiX2 = 0x20000113; // addi x2, x0, 0x200    → x2 = 0x200
        const uint swX1 = 0x00112023;   // sw   x1, 0(x2)
        const uint nop = 0x00000013;    // addi x0, x0, 0         (timing pad — delays lw dispatch)
        const uint addiX4 = 0x20000213; // addi x4, x0, 0x200    → x4 = 0x200
        const uint lwX3 = 0x00022183;   // lw   x3, 0(x4)

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(luiX1, addiX1, addiX2, swX1, MultiHartPipelineTests.Ebreak));
        flat.Load(
            0x40, ToBytes(
                nop, nop, nop, nop, nop, nop, nop, nop, nop, nop, nop, nop, nop, nop, nop,
                addiX4, lwX3, MultiHartPipelineTests.Ebreak
            )
        );

        var bus = new MoesifBus(flat);
        var cache0 = new MoesifCache(bus, 256, 2, 64);
        var cache1 = new MoesifCache(bus, 256, 2, 64);

        var train0 = new OooeTrain(new Rv32Mechanism(), cache0);
        var train1 = new OooeTrain(new Rv32Mechanism(), cache1, 0x40);

        new MultiHartPipeline(train0, train1).Run(1_000);

        Assert.Equal(0xCAFEUL, train1.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(MoesifState.Owned, cache0.StateOf(0x200)); // writer keeps dirty ownership (MOESIF)
        Assert.Equal(MoesifState.Shared, cache1.StateOf(0x200));
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
        var bus = new MoesifBus(flat, table);
        var cache0 = new MoesifCache(bus, 256, 2, 64);
        var cache1 = new MoesifCache(bus, 256, 2, 64);

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

    // ── OoO LR/SC atomics across pipeline trains ──────────────────────────────

    [Fact]
    public void OooPipelinedHart_LrScAtomic_ScFailsWhenRemoteStoreIntervenes() {
        // H0 (OooeTrain) at 0x00:
        //   li x2, 0x200         — address
        //   lr.w x1, (x2)        — reservation set at outer tick 6 (H0 StepExecute); x1 = 0
        //   sc.w x4, x1, (x2)   — rs2=x1 creates data-dep on lr.w; head-gated; executes at tick 8
        //   ebreak
        // H1 (SingleCycleTrain) at 0x80:
        //   li x1, 0x200 / li x5, 0x99 / nop×4 / sw x5,0(x1) / ebreak
        //
        // Timeline (outer ticks, H0 runs before H1 each tick):
        //   T=6  H0: lr.w executes → reservation[H0]=0x200; LoadStoreLatency=1 so result
        //            is in _cdbBuffer immediately (countdown 1-1=0).
        //   T=7  H0: Complete broadcasts lr.w result (x1=0), commits lr.w, issues sc.w.
        //        H1: sw x5,0(x1) → BusReadInvalidate → reservation CANCELLED.
        //   T=8  H0: StepExecute: sc.w → TryConsume(0, 0x200) → false → x4=1 (fail). ✓
        const uint liX2 = 0x20000113;    // addi x2, x0, 0x200
        const uint lrW = 0x100120AF;     // lr.w x1, (x2)
        const uint scW4Dep = 0x1811222F; // sc.w x4, x1, (x2)  — rs2=x1 (data dep on lr.w)
        const uint liX1H1 = 0x20000093;  // addi x1, x0, 0x200
        const uint liX5 = 0x09900293;    // addi x5, x0, 0x99
        const uint swX5X1 = 0x0050A023;  // sw x5, 0(x1)
        const uint nop = 0x00000013;

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(liX2, lrW, scW4Dep, MultiHartPipelineTests.Ebreak));
        flat.Load(0x80, ToBytes(liX1H1, liX5, nop, nop, nop, nop, swX5X1, MultiHartPipelineTests.Ebreak));

        var table = new ReservationTable();
        var bus = new MoesifBus(flat, table);
        var cache0 = new MoesifCache(bus, 256, 2, 64);
        var cache1 = new MoesifCache(bus, 256, 2, 64);

        var train0 = new OooeTrain(new Rv32Mechanism(reservationTable: table, hartId: 0), cache0);
        var train1 = new SingleCycleTrain(new Rv32Mechanism(reservationTable: table, hartId: 1), cache1, 0x80);

        new MultiHartPipeline(train0, train1).Run(5_000);

        Assert.Equal(0UL, train0.ArchState.IntegerRegisters.Read(1)); // lr.w loaded 0
        Assert.Equal(1UL, train0.ArchState.IntegerRegisters.Read(4)); // sc.w failed
    }

    [Fact]
    public void OooPipelinedHart_LrScAtomic_ScSucceedsWithNoRemoteStore() {
        // Same H0 OooeTrain LR/SC pair; H1 halts immediately without storing.
        // Reservation is never cancelled → SC.W succeeds (x4 = 0).
        const uint liX2 = 0x20000113;    // addi x2, x0, 0x200
        const uint lrW = 0x100120AF;     // lr.w x1, (x2)
        const uint scW4Dep = 0x1811222F; // sc.w x4, x1, (x2)

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(liX2, lrW, scW4Dep, MultiHartPipelineTests.Ebreak));
        flat.Load(0x80, ToBytes(MultiHartPipelineTests.Ebreak));

        var table = new ReservationTable();
        var bus = new MoesifBus(flat, table);
        var cache0 = new MoesifCache(bus, 256, 2, 64);
        var cache1 = new MoesifCache(bus, 256, 2, 64);

        var train0 = new OooeTrain(new Rv32Mechanism(reservationTable: table, hartId: 0), cache0);
        var train1 = new SingleCycleTrain(new Rv32Mechanism(reservationTable: table, hartId: 1), cache1, 0x80);

        new MultiHartPipeline(train0, train1).Run(5_000);

        Assert.Equal(0UL, train0.ArchState.IntegerRegisters.Read(1)); // lr.w loaded 0
        Assert.Equal(0UL, train0.ArchState.IntegerRegisters.Read(4)); // sc.w succeeded
    }

    // ── RunConcurrent (two-phase parallel) ───────────────────────────────────

    [Fact]
    public void RunConcurrent_TwoIndependentHarts_BitIdenticalToSequentialRun() {
        // Builds the same two-hart independent program twice: once driven by Run()
        // (MoesifBus directly) and once by RunConcurrent (DeferredBus).  Asserts that
        // both modes produce identical final register values — the bit-identical claim.
        const uint addi42 = 0x02A00093;
        const uint addi99 = 0x06300093;

        static (SingleCycleTrain t0, SingleCycleTrain t1) BuildSeq() {
            var flat = new FlatMemory(0x100);
            flat.Load(0x00, ToBytes(addi42, MultiHartPipelineTests.Ebreak));
            flat.Load(0x40, ToBytes(addi99, MultiHartPipelineTests.Ebreak));
            var bus = new MoesifBus(flat);
            var t0 = new SingleCycleTrain(new Rv32Mechanism(), new MoesifCache(bus, 256, 2, 64));
            var t1 = new SingleCycleTrain(new Rv32Mechanism(), new MoesifCache(bus, 256, 2, 64), 0x40);
            new MultiHartPipeline(t0, t1).Run(1_000);
            return (t0, t1);
        }

        (SingleCycleTrain seq0, SingleCycleTrain seq1) = BuildSeq();

        var flat2 = new FlatMemory(0x100);
        flat2.Load(0x00, ToBytes(addi42, MultiHartPipelineTests.Ebreak));
        flat2.Load(0x40, ToBytes(addi99, MultiHartPipelineTests.Ebreak));
        var realBus = new MoesifBus(flat2);
        var def0 = new DeferredBus(realBus);
        var def1 = new DeferredBus(realBus);
        var con0 = new SingleCycleTrain(new Rv32Mechanism(), new MoesifCache(def0, 256, 2, 64));
        var con1 = new SingleCycleTrain(new Rv32Mechanism(), new MoesifCache(def1, 256, 2, 64), 0x40);
        new MultiHartPipeline(con0, con1).RunConcurrent([def0, def1,], 1_000);

        Assert.Equal(seq0.ArchState.IntegerRegisters.Read(1), con0.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(seq1.ArchState.IntegerRegisters.Read(1), con1.ArchState.IntegerRegisters.Read(1));
    }

    [Fact]
    public void RunConcurrent_WellSeparatedOoO_ProducerConsumerSeesCoherentValue() {
        // Same program structure as OoOHarts_MoesifCoherence_StoreCommitsThenLoadSeesCoherentValue
        // but run via RunConcurrent with DeferredBus.
        // H0's store commits at outer tick ~9; H1's load fires at outer tick ~13 —
        // well-separated (4+ ticks apart) so no same-tick cross-hart conflict occurs.
        const uint luiX1 = 0x0000D0B7;  // lui  x1, 0xD
        const uint addiX1 = 0xAFE08093; // addi x1, x1, -1282  → x1 = 0xCAFE
        const uint addiX2 = 0x20000113; // addi x2, x0, 0x200
        const uint swX1 = 0x00112023;   // sw   x1, 0(x2)
        const uint nop = 0x00000013;
        const uint addiX4 = 0x20000213; // addi x4, x0, 0x200
        const uint lwX3 = 0x00022183;   // lw   x3, 0(x4)

        var flat = new FlatMemory(0x1000);
        flat.Load(0x00, ToBytes(luiX1, addiX1, addiX2, swX1, MultiHartPipelineTests.Ebreak));
        flat.Load(
            0x40, ToBytes(
                nop, nop, nop, nop, nop, nop, nop, nop, nop, nop, nop, nop, nop, nop, nop,
                addiX4, lwX3, MultiHartPipelineTests.Ebreak
            )
        );

        var realBus = new MoesifBus(flat);
        var def0 = new DeferredBus(realBus);
        var def1 = new DeferredBus(realBus);
        var cache0 = new MoesifCache(def0, 256, 2, 64);
        var cache1 = new MoesifCache(def1, 256, 2, 64);

        var train0 = new OooeTrain(new Rv32Mechanism(), cache0);
        var train1 = new OooeTrain(new Rv32Mechanism(), cache1, 0x40);

        new MultiHartPipeline(train0, train1).RunConcurrent([def0, def1,], 1_000);

        Assert.Equal(0xCAFEUL, train1.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(MoesifState.Owned, cache0.StateOf(0x200)); // writer keeps dirty ownership (MOESIF)
        Assert.Equal(MoesifState.Shared, cache1.StateOf(0x200));
    }
}