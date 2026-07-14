using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level tests for <see cref="DaeTrain" /> — the runtime-slicing Decoupled
///     Access-Execute train (Smith, ISCA 1982). Exercises the Access/Execute lane
///     classification heuristic, same-lane vs. cross-lane register dependency handling (the
///     <see cref="HandoffSlot" /> mechanism), and barrier-synchronized control flow.
/// </summary>
public class DaeTrainTests {
    private const uint Ebreak = 0x00100073;

    private static (DaeTrain train, FlatMemory mem) Make(int laneQueueDepth = 8, int memSize = 4096) {
        var mem = new FlatMemory(memSize);
        var train = new DaeTrain(new Rv32Mechanism(), mem, laneQueueDepth: laneQueueDepth);
        return (train, mem);
    }

    private static void Load(FlatMemory mem, ulong address, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        mem.Load(address, bytes);
    }

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("dae.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    /// <summary>
    ///     A program with no loads or stores: every instruction is address-untainted, so all of
    ///     it lands on the Execute lane and the Access lane never issues anything. Straight-line
    ///     correctness sanity check with zero cross-lane traffic. Assembled from:
    ///     <c>addi x1,x0,5; addi x2,x1,3; add x3,x1,x2; mul x4,x2,x3; ebreak</c>.
    /// </summary>
    [Fact]
    public void StraightLineAlu_AllExecuteLane_ProducesCorrectResult() {
        uint[] program = [
            0x00500093, // addi x1, x0, 5
            0x00308113, // addi x2, x1, 3
            0x002081B3, // add  x3, x1, x2
            0x02310233, // mul  x4, x2, x3
            DaeTrainTests.Ebreak,
        ];

        (DaeTrain dae, FlatMemory mem) = Make();
        Load(mem, 0, program);

        RevolutionResult result = dae.Run(10_000);

        Assert.Equal(5UL, dae.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(8UL, dae.ArchState.IntegerRegisters.Read(2));
        Assert.Equal(13UL, dae.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(104UL, dae.ArchState.IntegerRegisters.Read(4));
        Assert.Equal(0L, Counter(result, "access_issued"));
        Assert.Equal(4L, Counter(result, "execute_issued"));
    }

    /// <summary>
    ///     The canonical DAE motivating pattern: an address is computed, a load uses it, the
    ///     address is incremented for the next element (pulled back onto the Access lane via
    ///     taint propagation from the first load), a second load uses the incremented address
    ///     (a same-lane dependency — no handoff slot needed), and finally the two *loaded
    ///     values* are combined (Execute lane, since a load's result is never address-tainted).
    ///     Assembled from:
    ///     <c>
    ///         addi x2,x0,0x100; lw x3,0(x2); addi x2,x2,4; lw x4,0(x2); add x5,x3,x4;
    ///         ebreak
    ///     </c>
    ///     . Memory holds 10 at 0x100 and 20 at 0x104, so x5 must end up 30.
    /// </summary>
    [Fact]
    public void PointerWalk_AddressChainOnAccessLane_ValuesOnExecuteLane() {
        uint[] program = [
            0x10000113, // addi x2, x0, 0x100
            0x00012183, // lw   x3, 0(x2)
            0x00410113, // addi x2, x2, 4
            0x00012203, // lw   x4, 0(x2)
            0x004182B3, // add  x5, x3, x4
            DaeTrainTests.Ebreak,
        ];

        (DaeTrain dae, FlatMemory mem) = Make();
        Load(mem, 0, program);
        Load(mem, 0x100, 10, 20);

        RevolutionResult result = dae.Run(10_000);

        Assert.Equal(0x100UL, dae.ArchState.IntegerRegisters.Read(2) - 4);
        Assert.Equal(10UL, dae.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(20UL, dae.ArchState.IntegerRegisters.Read(4));
        Assert.Equal(30UL, dae.ArchState.IntegerRegisters.Read(5));

        // Deterministic lane placement: addi x2,x0,0x100 (untainted) + add x5,x3,x4 (both
        // sources are load results, never tainted) => Execute; both lw's + the address-chain
        // addi x2,x2,4 (source x2 tainted by the first lw) => Access.
        Assert.Equal(3L, Counter(result, "access_issued"));
        Assert.Equal(2L, Counter(result, "execute_issued"));
        Assert.True(Counter(result, "retired") >= program.Length);
    }

    /// <summary>
    ///     A store followed by a load of the same address: both are unconditionally
    ///     Access-class, so they land in the same lane and execute in strict program order —
    ///     the load must observe the just-stored value with no separate memory-disambiguation
    ///     mechanism needed. Assembled from:
    ///     <c>addi x1,x0,0xCAFE; addi x2,x0,0x200; sw x1,0(x2); lw x3,0(x2); ebreak</c>.
    /// </summary>
    [Fact]
    public void StoreThenLoad_SameAddress_SameLane_ObservesStoredValue() {
        // x1 = 0xCAFE needs LUI+ADDI since 0xCAFE doesn't fit a 12-bit signed immediate.
        uint[] program = [
            0x0000D0B7, // lui  x1, 0xD      -> x1 = 0x0000D000
            0xAFE08093, // addi x1, x1, -0x502 -> x1 = 0x0000CAFE
            0x20000113, // addi x2, x0, 0x200
            0x00112023, // sw   x1, 0(x2)
            0x00012183, // lw   x3, 0(x2)
            DaeTrainTests.Ebreak,
        ];

        (DaeTrain dae, FlatMemory mem) = Make();
        Load(mem, 0, program);

        RevolutionResult result = dae.Run(10_000);

        Assert.Equal(0xCAFEUL, dae.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(0xCAFEUL, dae.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(2L, Counter(result, "access_issued")); // sw, lw
    }

    [Fact]
    public void SingleEbreak_HaltsImmediately() {
        (DaeTrain dae, FlatMemory mem) = Make();
        Load(mem, 0, DaeTrainTests.Ebreak);

        RevolutionResult result = dae.Run(1_000);

        Assert.True(dae.IsIdle);
        Assert.Equal(1L, Counter(result, "retired"));
    }

    /// <summary>
    ///     A full Access lane queue (many independent loads ahead of it) must not block the
    ///     Execute lane from making progress on unrelated arithmetic dispatched after it — the
    ///     defining property of a decoupled front end. Ten independent loads saturate a
    ///     <c>laneQueueDepth=4</c> Access queue while a handful of untainted adds are
    ///     interleaved; both lanes must still retire everything and produce correct values.
    /// </summary>
    [Fact]
    public void SaturatedAccessLane_DoesNotBlockExecuteLaneProgress() {
        // lw x10, 0(x2)   = imm(0)<<20 | rs1(2)<<15 | funct3(2)<<12 | rd(10)<<7 | opcode(3)
        const uint lwX10 = (2u << 15) | (2u << 12) | (10u << 7) | 0x03;
        // addi x11, x11, 1 = imm(1)<<20 | rs1(11)<<15 | funct3(0)<<12 | rd(11)<<7 | opcode(0x13)
        const uint addiX11Inc = (1u << 20) | (11u << 15) | (11u << 7) | 0x13;

        var words = new List<uint> {
            0x20000113, // addi x2, x0, 0x200  (base address, Execute-class: x0 untainted)
        };
        for (var i = 0; i < 10; i++) {
            words.Add(lwX10);      // Access lane: independent load from a fixed address
            words.Add(addiX11Inc); // Execute lane: independent of x2's chain entirely
        }

        words.Add(DaeTrainTests.Ebreak);

        var mem = new FlatMemory(4096);
        var dae = new DaeTrain(new Rv32Mechanism(), mem, laneQueueDepth: 4);
        Load(mem, 0, words.ToArray());
        Load(mem, 0x200, 0xABCD);

        RevolutionResult result = dae.Run(20_000);

        Assert.Equal(0xABCDUL, dae.ArchState.IntegerRegisters.Read(10));
        Assert.Equal(10UL, dae.ArchState.IntegerRegisters.Read(11));
        Assert.Equal(10L, Counter(result, "access_issued"));  // the 10 loads
        Assert.Equal(11L, Counter(result, "execute_issued")); // base addi + 10 independent increments
    }
}