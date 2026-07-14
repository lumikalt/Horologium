using Mechanism.BranchPredictModels;
using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level invariant tests for runahead execution (Mutlu et al., HPCA 2003; Naithani
///     et al., HPCA 2020 "Precise Runahead Execution" / ISCA 2021 "Vector Runahead") as wired
///     into <see cref="OooeTrain" /> via <c>enableRunahead</c>/<c>runaheadBudget</c>.
///     <para>
///         The shadow lane never commits anything to a real architectural state — every real
///         instruction is still fetched, dispatched, and executed for real once ROB room frees
///         up, exactly as it would be with the feature off. Every test here therefore runs the
///         same program twice (feature off vs on) and asserts identical final architectural
///         register state, regardless of what the shadow lane predicted, tainted, or discarded
///         along the way.
///     </para>
/// </summary>
public class RunaheadTests {
    private static (OooeTrain train, FlatMemory mem) Make(
        bool enableRunahead,
        int runaheadBudget = 200,
        int issueWidth = 2,
        int robCapacity = 8,
        int iqCapacity = 32,
        int memSize = 4096
    ) {
        var mem = new FlatMemory(memSize);
        var train = new OooeTrain(
            new Rv32Mechanism(), mem,
            issueWidth: issueWidth,
            robCapacity: robCapacity,
            iqCapacity: iqCapacity,
            dMemConfig: new MemoryConfig(1024),
            enableRunahead: enableRunahead,
            runaheadBudget: runaheadBudget
        );
        return (train, mem);
    }

    private static void Load(FlatMemory mem, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(0, bytes);
    }

    private static void AssertIdenticalArchState(OooeTrain off, OooeTrain on) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
    }

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("ooo.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    /// <summary>
    ///     A cache-cold load (address 400, never touched before) stalls the ROB — with a
    ///     robCapacity of 8, seven independent <c>addi</c>s dispatch behind it for real and fill
    ///     the ROB solid while the load is still in flight (default L1 miss latency of 10
    ///     cycles gives comfortable margin over the ~4 cycles seven instructions take to
    ///     dispatch at issueWidth 2), tripping <c>NeedsRunahead</c>. The final <c>addi</c> reads
    ///     the load's own destination register (x2), exercising the taint seed at episode entry
    ///     even in the simplest case. Assembled from:
    ///     <c>
    ///         addi x1,x0,400; lw x2,0(x1); addi
    ///         x3,x0,1; addi x4,x0,2; addi x5,x0,3; addi x6,x0,4; addi x7,x0,5; addi x8,x0,6; addi
    ///         x9,x0,7; addi x10,x0,8; addi x11,x0,9; addi x12,x0,10; addi x13,x0,11; addi
    ///         x14,x0,12; addi x15,x2,1; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void FullRobStallBehindMissingLoad_ArchStateIdenticalToWithout_AndEpisodeOccurs() {
        uint[] program = [
            0x19000093, // addi x1, x0, 400
            0x0000A103, // lw   x2, 0(x1)
            0x00100193, // addi x3, x0, 1
            0x00200213, // addi x4, x0, 2
            0x00300293, // addi x5, x0, 3
            0x00400313, // addi x6, x0, 4
            0x00500393, // addi x7, x0, 5
            0x00600413, // addi x8, x0, 6
            0x00700493, // addi x9, x0, 7
            0x00800513, // addi x10, x0, 8
            0x00900593, // addi x11, x0, 9
            0x00A00613, // addi x12, x0, 10
            0x00B00693, // addi x13, x0, 11
            0x00C00713, // addi x14, x0, 12
            0x00110793, // addi x15, x2, 1
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(false);
        (OooeTrain on, FlatMemory memOn) = Make(true);
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);

        Assert.Equal(0L, Counter(offResult, "runahead_episodes"));
        Assert.True(Counter(onResult, "runahead_episodes") > 0, "no runahead episode was ever entered");
        Assert.True(Counter(onResult, "runahead_instructions") > 0, "the shadow lane never ran any instruction");
    }

    /// <summary>
    ///     Same ROB-filling setup as above, but the shadow-lane-only tail is a load whose
    ///     address register is exactly the still-in-flight blocking load's destination (x2) —
    ///     <c>lw x20, 0(x2)</c>. Taint seeded at episode entry (x2's live mapping isn't ready)
    ///     must gate this load's real memory access entirely: the shadow lane must not
    ///     dereference whatever garbage value x2 happens to hold, and must not crash. Assembled
    ///     from:
    ///     <c>
    ///         addi x1,x0,400; lw x2,0(x1); addi x3,x0,1; ...; addi x12,x0,10; lw
    ///         x20,0(x2); ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void TaintedAddressLoad_NeverDereferenced_ArchStateIdenticalToWithout() {
        uint[] program = [
            0x19000093, // addi x1, x0, 400
            0x0000A103, // lw   x2, 0(x1)
            0x00100193, // addi x3, x0, 1
            0x00200213, // addi x4, x0, 2
            0x00300293, // addi x5, x0, 3
            0x00400313, // addi x6, x0, 4
            0x00500393, // addi x7, x0, 5
            0x00600413, // addi x8, x0, 6
            0x00700493, // addi x9, x0, 7
            0x00800513, // addi x10, x0, 8
            0x00900593, // addi x11, x0, 9
            0x00A00613, // addi x12, x0, 10
            0x00012A03, // lw   x20, 0(x2)   -- tainted address, executed only in the shadow lane
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(false);
        (OooeTrain on, FlatMemory memOn) = Make(true);
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.True(Counter(onResult, "runahead_episodes") > 0, "no runahead episode was ever entered");
    }

    /// <summary>
    ///     Same ROB-filling setup; the shadow-lane-only tail is a store immediately followed by
    ///     a load from the same address (
    ///     <c>
    ///         addi x10,x0,800; addi x11,x0,999; sw x11,0(x10); lw
    ///         x12,0(x10)
    ///     </c>
    ///     ) — the shadow store-to-load forwarding path within a single episode.
    ///     <see cref="OooeTrain" />'s shadow store buffer is scratch-only and is never applied to
    ///     real memory; the real store still executes for real once the ROB drains (there is no
    ///     way to externally observe an intra-episode-only value, since nothing shadow-computed
    ///     is ever kept — this is a deliberate part of the design, not a test gap). What this
    ///     test actually verifies is that going through the shadow lane first — store, forwarded
    ///     reload, discard, then real re-execution — produces the same correct final value as
    ///     never having run the shadow lane at all, i.e., no corruption or leakage from the
    ///     discarded scratch buffer into the real re-execution.
    /// </summary>
    [Fact]
    public void StoreThenLoadSameAddress_ShadowForwardingDoesNotCorruptRealReexecution() {
        uint[] program = [
            0x19000093, // addi x1, x0, 400
            0x0000A103, // lw   x2, 0(x1)
            0x00100193, // addi x3, x0, 1
            0x00200213, // addi x4, x0, 2
            0x00300293, // addi x5, x0, 3
            0x00400313, // addi x6, x0, 4
            0x00500393, // addi x7, x0, 5
            0x00600413, // addi x8, x0, 6
            0x00700493, // addi x9, x0, 7
            0x32000513, // addi x10, x0, 800
            0x3E700593, // addi x11, x0, 999
            0x00B52023, // sw   x11, 0(x10)   -- shadow-lane store
            0x00052603, // lw   x12, 0(x10)   -- shadow-lane load, same address
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(false);
        (OooeTrain on, FlatMemory memOn) = Make(true);
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.Equal(999UL, on.ArchState.IntegerRegisters.Read(12));
        Assert.True(Counter(onResult, "runahead_episodes") > 0, "no runahead episode was ever entered");
    }

    /// <summary>
    ///     The branch's compare operand is produced by a <c>mul</c> (multi-cycle FU), so its
    ///     resolution lags several cycles behind its dispatch — long enough for four more real
    ///     <c>addi</c>s (the wrong, not-taken-predicted path) to dispatch behind it and fill the
    ///     ROB (load, addi, mul, beq, x5..x8 = 8 entries) before the branch actually completes.
    ///     A runahead episode is therefore already active when the branch resolves taken
    ///     (mispredicting <see cref="AlwaysNotTakenPredictor" />) and squashes
    ///     the wrong-path entries. <see cref="OooeTrain" />'s flush path must restore the RAT
    ///     from the episode's snapshot before the squash's own walk-back runs. Assembled from:
    ///     <c>
    ///         addi x1,x0,400; lw x2,0(x1); addi x3,x0,5; mul x4,x3,x3; beq x4,x4,taken; addi
    ///         x5,x0,111; addi x6,x0,222; addi x7,x0,333; addi x8,x0,444; addi x9,x0,555; taken: addi
    ///         x10,x0,777; addi x11,x0,888; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void FlushDuringActiveEpisode_RecoversCleanly_ArchStateIdenticalToWithout() {
        uint[] program = [
            0x19000093, // addi x1, x0, 400
            0x0000A103, // lw   x2, 0(x1)
            0x00500193, // addi x3, x0, 5
            0x02318233, // mul  x4, x3, x3
            0x00420C63, // beq  x4, x4, taken   -- always taken; delayed by the mul dependency
            0x06F00293, // addi x5, x0, 111     -- wrong-path (predicted not-taken)
            0x0DE00313, // addi x6, x0, 222     -- wrong-path
            0x14D00393, // addi x7, x0, 333     -- wrong-path
            0x1BC00413, // addi x8, x0, 444     -- wrong-path, fills the ROB to capacity
            0x22B00493, // addi x9, x0, 555     -- queued behind the full ROB
            0x30900513, // taken: addi x10, x0, 777
            0x37800593, // addi x11, x0, 888
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(false);
        (OooeTrain on, FlatMemory memOn) = Make(true);
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        on.Run();

        AssertIdenticalArchState(off, on);
        // Both paths converge on the same fall-through address, so the branch outcome is
        // externally invisible in final register state; what matters is that recovery after a
        // flush mid-episode leaves the RAT correct for every instruction that follows.
        Assert.Equal(777UL, on.ArchState.IntegerRegisters.Read(10));
        Assert.Equal(888UL, on.ArchState.IntegerRegisters.Read(11));
    }

    /// <summary>
    ///     Same ROB-filling setup as the first test, but with <c>runaheadBudget: 4</c> and a
    ///     long tail of 22 independent shadow-lane-eligible <c>addi</c>s (x10..x31) — far more
    ///     than one 4-instruction episode can cover. Because the blocking load stays in flight
    ///     for the L1's full miss latency, the shadow lane must exit at the budget and
    ///     immediately re-enter (still stalled, still incomplete head) rather than running
    ///     unbounded or wedging after the first exit — expect more than one episode.
    /// </summary>
    [Fact]
    public void SmallBudget_TerminatesAndReentersRatherThanRunningUnbounded() {
        uint[] program = [
            0x19000093, // addi x1, x0, 400
            0x0000A103, // lw   x2, 0(x1)
            0x00100193, // addi x3, x0, 1
            0x00200213, // addi x4, x0, 2
            0x00300293, // addi x5, x0, 3
            0x00400313, // addi x6, x0, 4
            0x00500393, // addi x7, x0, 5
            0x00600413, // addi x8, x0, 6
            0x00700493, // addi x9, x0, 7
            0x00A00513, // addi x10, x0, 10
            0x00B00593, // addi x11, x0, 11
            0x00C00613, // addi x12, x0, 12
            0x00D00693, // addi x13, x0, 13
            0x00E00713, // addi x14, x0, 14
            0x00F00793, // addi x15, x0, 15
            0x01000813, // addi x16, x0, 16
            0x01100893, // addi x17, x0, 17
            0x01200913, // addi x18, x0, 18
            0x01300993, // addi x19, x0, 19
            0x01400A13, // addi x20, x0, 20
            0x01500A93, // addi x21, x0, 21
            0x01600B13, // addi x22, x0, 22
            0x01700B93, // addi x23, x0, 23
            0x01800C13, // addi x24, x0, 24
            0x01900C93, // addi x25, x0, 25
            0x01A00D13, // addi x26, x0, 26
            0x01B00D93, // addi x27, x0, 27
            0x01C00E13, // addi x28, x0, 28
            0x01D00E93, // addi x29, x0, 29
            0x01E00F13, // addi x30, x0, 30
            0x01F00F93, // addi x31, x0, 31
            0x00100073, // ebreak
        ];

        (OooeTrain off, FlatMemory memOff) = Make(false);
        (OooeTrain on, FlatMemory memOn) = Make(true, 4);
        Load(memOff, program);
        Load(memOn, program);

        off.Run();
        RevolutionResult onResult = on.Run();

        AssertIdenticalArchState(off, on);
        Assert.True(
            Counter(onResult, "runahead_episodes") >= 2,
            "a 4-instruction budget against a long shadow-eligible tail should force multiple episodes"
        );
    }
}