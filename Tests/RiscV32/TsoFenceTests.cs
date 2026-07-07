using Mechanism;
using Orrery.Cache;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32;

/// <summary>
/// TSO fence modeling. Under TSO the only reordering the OoO train performs is
/// store→load: a committed store's write-miss penalty drains asynchronously through
/// the write buffer while younger loads issue freely. A FENCE with W in the
/// predecessor set and R in the successor set (<see cref="ITooth.IsStoreLoadFence"/>)
/// closes that window: the fence issues only at the ROB head once the write buffer
/// has fully drained, and younger loads may not issue while the fence is in the ROB.
/// Fences without W→R ordering are timing no-ops, as TSO already provides their
/// guarantees (loads and stores each retire in program order; stores write through
/// the D-cache at commit).
/// </summary>
public class TsoFenceTests {
    private const uint Ebreak = 0x00100073;
    private const uint Nop = 0x00000013;
    private const uint FenceWr = 0x0120000F;   // fence w,r
    private const uint FenceFull = 0x0FF0000F; // fence iorw,iorw

    private static byte[] ToBytes(params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    // ── Single hart: fence drains the write buffer before post-fence loads ────

    /// <summary>
    /// Runs: sw (write miss absorbed into the write buffer) ; fence-or-nop ; lw
    /// (independent address). Without the fence the load issues while the store's
    /// write-bus penalty is still draining; with it, the fence holds at the ROB head
    /// until the buffer empties and the load waits behind the fence.
    /// </summary>
    private static (long Cycles, ulong X3, ulong StoredWord) RunStoreFenceLoad(uint middle) {
        const uint addiX2 = 0x20000113; // addi x2, x0, 0x200  (store address)
        const uint addiX1 = 0x07700093; // addi x1, x0, 0x77   (store value)
        const uint swX1 = 0x00112023;   // sw   x1, 0(x2)      → write miss (no-write-allocate)
        const uint addiX4 = 0x28000213; // addi x4, x0, 0x280  (load address, different line)
        const uint lwX3 = 0x00022183;   // lw   x3, 0(x4)

        var mem = new FlatMemory(0x1000);
        mem.Load(0, ToBytes(addiX2, addiX1, swX1, middle, addiX4, lwX3, TsoFenceTests.Ebreak));

        var train = new OooeTrain(
            new Rv32Mechanism(), mem,
            dMemConfig: new MemoryConfig(
                16384, 4, 64
            ),
            writeBufferCapacity: 4
        );

        RevolutionResult r = train.Run(10_000);
        long cycles = r.Find("ooo.pipeline")!.Counters["cycles"];
        return (cycles, train.ArchState.IntegerRegisters.Read(3), mem.Read(0x200, 4));
    }

    [Fact]
    public void OoO_StoreLoadFence_DelaysPostFenceLoad_UntilWriteBufferDrains() {
        (long fencedCycles, ulong fencedX3, ulong fencedStore) = RunStoreFenceLoad(TsoFenceTests.FenceWr);
        (long freeCycles, ulong freeX3, ulong freeStore) = RunStoreFenceLoad(TsoFenceTests.Nop);

        // Architectural results are identical — the fence is timing-only.
        Assert.Equal(0x77UL, fencedStore);
        Assert.Equal(0x77UL, freeStore);
        Assert.Equal(freeX3, fencedX3);

        // The store's 10-cycle write-miss penalty sits in the write buffer; the fence
        // must hold the post-fence load for (most of) that drain, so the fenced run
        // is measurably longer. Margin of 5 tolerates pipeline-overlap jitter.
        Assert.True(
            fencedCycles >= freeCycles + 5,
            $"fence did not delay the post-fence load: fenced={fencedCycles}, free={freeCycles}"
        );
    }

    /// <summary>
    /// A fence without W→R ordering (here: fence r,r) must not drain the write buffer
    /// — TSO already orders load→load, so it is a timing no-op and the run matches
    /// the nop version exactly.
    /// </summary>
    [Fact]
    public void OoO_NonStoreLoadFence_IsTimingNoOp() {
        const uint fenceRr = 0x0220000F; // fence r,r
        (long rrCycles, _, _) = RunStoreFenceLoad(fenceRr);
        (long freeCycles, _, _) = RunStoreFenceLoad(TsoFenceTests.Nop);

        Assert.Equal(freeCycles, rrCycles);
    }

    // ── Multi-hart: message-passing litmus (MP) over MOESIF ─────────────────────

    /// <summary>
    /// Classic MP litmus on two OoO harts with per-hart MOESIF caches:
    ///   H0: data = 0xCAFE ; fence ; flag = 1
    ///   H1: spin until flag != 0 ; fence ; read data
    /// TSO guarantees H1 sees data = 0xCAFE once it observes flag = 1. Exercises the
    /// fence end-to-end in the OoO issue path (head-serialization + load gating)
    /// under cross-hart MOESIF invalidation of the spun-on flag line.
    /// </summary>
    [Fact]
    public void OoOHarts_MessagePassingLitmus_FencedDataIsVisibleWhenFlagIs() {
        // H0 at 0x00 (OooeTrain PRF starts zeroed — all values computed in-program):
        const uint luiX1 = 0x0000D0B7;  // lui  x1, 0xD
        const uint addiX1 = 0xAFE08093; // addi x1, x1, -1282  → x1 = 0xCAFE (data value)
        const uint addiX2 = 0x20000113; // addi x2, x0, 0x200  (data address)
        const uint addiX7 = 0x30000393; // addi x7, x0, 0x300  (flag address, different line)
        const uint swData = 0x00112023; // sw   x1, 0(x2)
        const uint addiX8 = 0x00100413; // addi x8, x0, 1      (flag value)
        const uint swFlag = 0x0083A023; // sw   x8, 0(x7)

        // H1 at 0x80:
        const uint addiX6 = 0x30000313;  // addi x6, x0, 0x300  (flag address)
        const uint addiX4 = 0x20000213;  // addi x4, x0, 0x200  (data address)
        const uint lwFlag = 0x00032283;  // loop: lw x5, 0(x6)
        const uint beqSpin = 0xFE028EE3; // beq  x5, x0, loop   (offset -4)
        const uint lwData = 0x00022183;  // lw   x3, 0(x4)

        var flat = new FlatMemory(0x1000);
        flat.Load(
            0x00,
            ToBytes(
                luiX1, addiX1, addiX2, addiX7, swData, TsoFenceTests.FenceFull, addiX8, swFlag, TsoFenceTests.Ebreak
            )
        );
        flat.Load(
            0x80, ToBytes(addiX6, addiX4, lwFlag, beqSpin, TsoFenceTests.FenceFull, lwData, TsoFenceTests.Ebreak)
        );

        var bus = new MoesifBus(flat);
        var cache0 = new MoesifCache(bus, 1024, 2, 64);
        var cache1 = new MoesifCache(bus, 1024, 2, 64);

        var train0 = new OooeTrain(new Rv32Mechanism(), cache0);
        var train1 = new OooeTrain(new Rv32Mechanism(), cache1, 0x80);

        new MultiHartPipeline(train0, train1).Run(10_000);

        // H1 escaped the spin (flag observed as 1) and the fenced data write was visible.
        Assert.Equal(1UL, train1.ArchState.IntegerRegisters.Read(5));
        Assert.Equal(0xCAFEUL, train1.ArchState.IntegerRegisters.Read(3));
    }
}