#region

using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;

// ReSharper disable ShiftExpressionZeroLeftOperand

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Store address/data decomposition on <see cref="CprTrain" /> — the same
///     <c>enableEarlyStoreAddress</c> mechanism verified on <see cref="OooTrain" />
///     (<see cref="OooStoreAddressSplitTests" />), ported onto <see cref="HierarchicalStoreQueue" />
///     and <c>_entryByInstrId</c>/<c>CheckpointEntry.SqIdx</c> lookups instead of
///     <c>RobEntry</c>/<c>RobIndex</c>.
///     <para>
///         Unlike <see cref="OooTrain" />'s <c>ConservativeLoads</c> flag, CPR has no blanket
///         "stall on any unresolved store" mode — its only address-only gate is the Store Sets
///         memory-dependence predictor (<c>enableStoreSets</c>, on by default), which only stalls
///         a load once it has a *trained* prediction for that load's PC. These tests cover
///         correctness (including the real bug this port caught — see below) rather than a
///         CprTrain-specific timing measurement; the timing payoff itself is verified once, on
///         the identical shared code path, by <see cref="OooStoreAddressSplitTests" />.
///     </para>
///     <para>
///         Porting this feature to CprTrain surfaced a real bug, not just a mechanical port: the
///         Store Sets predicted-dependence release check (in <c>TryIssueSlot</c>) used to read
///         <c>sq.AddressKnown</c> as a proxy for "the store has fully resolved, safe to let the
///         predicted-dependent load go" — correct before this feature existed, since address and
///         data became known atomically together. Early address resolution breaks that
///         assumption (address can now be known well before data), so the release check now
///         reads <c>sq.DataKnown</c> instead. Without that fix, a predicted-dependent load could
///         be released as soon as the address alone resolved, race the store's real write, and
///         read stale memory — reproduced directly by
///         <see cref="StoreSetsPath_StillProducesCorrectResult_AcrossLoopIterations" /> before the
///         fix (an unbounded violation/recovery loop, never terminating).
///     </para>
/// </summary>
public class CprStoreAddressSplitTests {
    private const uint Ebreak = 0x00100073;

    private static readonly MemoryConfig SlowMissConfig =
        new(4096, 4, 32, 60);

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

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Lw(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0000011);

    private static uint Sw(int rs1, int rs2, int imm) =>
        (uint)(((imm & 0xFE0) << 20) | (rs2 << 20) | (rs1 << 15) | (0b010 << 12) | ((imm & 0x1F) << 7) | 0b0100011);

    private static uint Mul(int rd, int rs1, int rs2) =>
        (uint)((0b0000001 << 25) | (rs2 << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0110011);

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10To5 = (imm >> 5) & 0x3F;
        uint bits4To1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4To1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private static CprTrain Run(uint[] program, bool enableEarlyStoreAddress, MemoryConfig? dMemConfig = null) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        var train = new CprTrain(
            new Rv32Mechanism(), mem, issueWidth: 4, dMemConfig: dMemConfig,
            enableEarlyStoreAddress: enableEarlyStoreAddress
        );
        train.Run();
        return train;
    }

    // x1 = 400 (store address base, ready immediately); x2 = mem[1600] (store data — a cold
    // D-cache miss, ready late); sw x2,0(x1) — address ready fast, data ready slow. x5 reads
    // the store back to verify the committed value (through the same memory hierarchy the
    // store wrote through, not a raw backing-memory read).
    private static uint[] AddressFastDataSlowProgram() => [
        Addi(1, 0, 400),
        Lw(2, 0, 1600),
        Sw(1, 2, 0),
        Lw(5, 0, 400),
        CprStoreAddressSplitTests.Ebreak,
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoreCommitsCorrectData_RegardlessOfEarlyAddressResolution(bool enableEarly) {
        var mem = new FlatMemory(65536);
        Load(mem, AddressFastDataSlowProgram());
        mem.Write(1600, 42, 4);
        var train = new CprTrain(
            new Rv32Mechanism(), mem, issueWidth: 4, dMemConfig: CprStoreAddressSplitTests.SlowMissConfig,
            enableEarlyStoreAddress: enableEarly
        );
        train.Run();
        Assert.Equal(42UL, train.ArchState.IntegerRegisters.Read(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoreCommitsCorrectData_AddressResolvesManyCyclesBeforeData(bool enableEarly) {
        // Adversarial ordering: the address operand is trivially ready (x0-based, immediate),
        // while the data operand is a deliberately slow chain — several dependent ALU ops on
        // top of a cold miss — widening the address-known-to-data-known gap. Regression guard
        // for the "commit before data ready" bug class: even with early resolution on, the
        // store must never write anything until its real Execute (needing both operands) fires.
        uint[] program = [
            Lw(2, 0, 1600), // x2 = mem[1600], cold miss
            Addi(2, 2, 1),  // a few dependent ALU hops widen the delay before x2 is finally ready
            Addi(2, 2, 1),
            Addi(2, 2, 1),
            Sw(0, 2, 400), // mem[400] = x2, address = x0+400 (trivially ready)
            Lw(5, 0, 400), // read the store back
            CprStoreAddressSplitTests.Ebreak,
        ];
        var mem = new FlatMemory(65536);
        Load(mem, program);
        mem.Write(1600, 42, 4);
        var train = new CprTrain(
            new Rv32Mechanism(), mem, issueWidth: 4, dMemConfig: CprStoreAddressSplitTests.SlowMissConfig,
            enableEarlyStoreAddress: enableEarly
        );
        train.Run();
        Assert.Equal(45UL, train.ArchState.IntegerRegisters.Read(5)); // 42 + 1 + 1 + 1
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpeculativeLoad_ViolatingStore_StillRecoversToCheckpoint_WithEarlyAddressResolution(bool enableEarly) {
        // Regression guard: early address resolution must not interfere with memory-order
        // violation detection (CheckLoadViolations, unchanged — still called only from the
        // store's real full-Execute completion). Same program shape as
        // CprTrainTests.SpeculativeLoad_ViolatingStore_RecoversToCheckpoint.
        uint[] program = [
            Addi(6, 0, 10),
            Mul(7, 6, 6), // x7 = 100
            Mul(7, 7, 6), // x7 = 1000
            Addi(2, 0, 42),
            Sw(7, 2, 0), // mem[1000] = 42, address hangs on the multiply chain
            Addi(5, 0, 1000),
            Lw(4, 5, 0), // younger, same-address load — speculates past the still-unresolved store
            CprStoreAddressSplitTests.Ebreak,
        ];
        var mem = new FlatMemory(4096);
        Load(mem, program);
        var train = new CprTrain(
            new Rv32Mechanism(), mem, issueWidth: 2, enableEarlyStoreAddress: enableEarly
        );
        RevolutionResult result = train.Run(100_000);

        Assert.Equal(42UL, train.ArchState.IntegerRegisters.Read(4));
        Assert.True(Counter(result, "mem_order_violations") >= 1, "the early load must be caught and rolled back");
        Assert.True(Counter(result, "recoveries") >= 1);
    }

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("cpr.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    // Address-fast/data-slow store (deterministic multiply chain, not a cache miss, so it's
    // equally slow every loop iteration) with a younger same-address load, run around a real
    // backward branch twice — exercises the exact call path
    // EarlyAddressResolution_ReleasesConservativeLoad_WhenDataOperandIsSlow does on OooTrain,
    // but through a real violation+recovery+re-fetch cycle instead of a straight-line program.
    private static uint[] BuildTrainThenStallProgram() => [
        Addi(9, 0, 2), // pc=0: loop trip count = 2
        Addi(1, 0, 400), // pc=4 [LOOP]: store address base — trivially ready every iteration
        Addi(6, 0, 10), // pc=8
        Mul(6, 6, 6), // pc=12: x6 = 100 — deterministically slow every iteration (not cache-state-dependent)
        Mul(6, 6, 6), // pc=16: x6 = 10000 — store data
        Sw(1, 6, 0), // pc=20: mem[400] = x6 — address ready fast, data ready slow
        Lw(4, 1, 0), // pc=24: younger, same-address load
        Addi(9, 9, -1), // pc=28
        Bne(9, 0, 4 - 32), // pc=32: branch back to pc=4 if x9 != 0
        CprStoreAddressSplitTests.Ebreak, // pc=36
    ];

    // NOTE: this does NOT assert a cycle-count reduction the way the OooTrain ConservativeLoads
    // test does. Empirically, CprTrain's Store Sets predictor didn't reach a confidently-trained
    // state within this program's 2 iterations — both iterations independently hit a real
    // violation+recovery rather than iteration 2 landing a predicted stall for early resolution
    // to shorten — so there was no trained-stall window to measure here. The mechanism itself is
    // exercised and verified correct (this test, run with the feature both off and on, still
    // produces the right final value and doesn't regress violation detection); the timing payoff
    // for CprTrain specifically rests on the same code path already timing-verified on OooTrain
    // (StepEarlyStoreAddressResolution, TryComputeStoreAddress, and the DataKnown split are
    // identical between the two trains — only the entry-lookup mechanism differs), not on a
    // CprTrain-specific measurement.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoreSetsPath_StillProducesCorrectResult_AcrossLoopIterations(bool enableEarly) {
        uint[] program = BuildTrainThenStallProgram();
        CprTrain train = Run(program, enableEarly);
        Assert.Equal(10000UL, train.ArchState.IntegerRegisters.Read(4));
    }
}