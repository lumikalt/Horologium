#region

using Orrery.Cache;
using Orrery.Observation;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Store address/data decomposition on <see cref="OooTrain" /> (µops
///     micro-fusion item, store half): a store's address operand (rs1) getting independent
///     effect from its data operand (rs2) on address-only consumers — chiefly
///     <c>HasUnresolvedPrecedingStore</c> (<see cref="FuLatencyConfig.ConservativeLoads" />)
///     and the Store Sets predictor — instead of both only ever becoming visible together, as
///     the store's single atomic Issue/Execute event requires today.
///     <para>
///         Gated by <c>enableEarlyStoreAddress</c>. The payoff only exists when something
///         actually consults <c>SqEntry.AddressKnown</c> to gate a younger load — by default
///         (neither <c>ConservativeLoads</c> nor Store Sets enabled) a load issues
///         speculatively regardless, so these tests turn on <c>ConservativeLoads</c>
///         specifically to exercise the gap this feature closes.
///     </para>
///     <para>
///         Committed values are always verified through a subsequent load instruction (register
///         read-back), never a raw backing-memory read — a real cache config sits between
///         <c>DLayers.Accessor</c> (what the store actually writes through) and the raw
///         <see cref="FlatMemory" /> instance, so reading the latter directly can observe a
///         write before it's flushed back.
///     </para>
///     <para>
///         <see cref="SameAddressLoad_UnderStoreSets_DoesNotRaceStoreData" /> covers the same bug
///         class fixed on <c>CprTrain</c> (see <see cref="CprStoreAddressSplitTests" />):
///         <c>StoreSetStallLoad</c> used to gate on <c>AddressKnown</c>, which — once address and
///         data could resolve independently — let a predicted-dependent load race a store's real
///         write and guaranteed a memory-order violation on every prediction instead of the clean
///         stall Store Sets exists to provide. Fixed to gate on <c>DataKnown</c> instead.
///     </para>
/// </summary>
public class OooStoreAddressSplitTests {
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

    private static OooTrain Run(
        uint[] program,
        bool enableEarlyStoreAddress,
        MemoryConfig? dMemConfig = null,
        FuLatencyConfig? fuConfig = null
    ) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        mem.Write(1600, 42, 4);
        var train = new OooTrain(
            new Rv32Mechanism(), mem, issueWidth: 4, dMemConfig: dMemConfig, fuLatency: fuConfig,
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
        OooStoreAddressSplitTests.Ebreak,
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoreCommitsCorrectData_RegardlessOfEarlyAddressResolution(bool enableEarly) {
        OooTrain train = Run(AddressFastDataSlowProgram(), enableEarly, OooStoreAddressSplitTests.SlowMissConfig);
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
            OooStoreAddressSplitTests.Ebreak,
        ];
        OooTrain train = Run(program, enableEarly, OooStoreAddressSplitTests.SlowMissConfig);
        Assert.Equal(45UL, train.ArchState.IntegerRegisters.Read(5)); // 42 + 1 + 1 + 1
    }

    [Fact]
    public void EarlyAddressResolution_ReleasesConservativeLoad_WhenDataOperandIsSlow() {
        uint[] program = [
            Addi(1, 0, 400), // x1 = 400, store address base — trivially ready
            Lw(2, 0, 1600),  // x2 = mem[1600] — SLOW cold miss, store's data operand
            Sw(1, 2, 0),     // mem[400] = x2 — address ready immediately, data ready late
            Lw(4, 0, 800),   // younger, non-aliasing load; conservative loads must wait on the
            // store above having a KNOWN address (not a known value) before issuing
            Lw(5, 0, 400), // read the store back
            OooStoreAddressSplitTests.Ebreak,
        ];
        var fuConfig = new FuLatencyConfig(ConservativeLoads: true);

        var withMem = new FlatMemory(65536);
        Load(withMem, program);
        withMem.Write(1600, 42, 4);
        withMem.Write(800, 7, 4);
        var without = new OooTrain(
            new Rv32Mechanism(), withMem, issueWidth: 4, dMemConfig: OooStoreAddressSplitTests.SlowMissConfig,
            fuLatency: fuConfig, enableEarlyStoreAddress: false
        );
        without.Run();

        var withEarlyMem = new FlatMemory(65536);
        Load(withEarlyMem, program);
        withEarlyMem.Write(1600, 42, 4);
        withEarlyMem.Write(800, 7, 4);
        var with = new OooTrain(
            new Rv32Mechanism(), withEarlyMem, issueWidth: 4, dMemConfig: OooStoreAddressSplitTests.SlowMissConfig,
            fuLatency: fuConfig, enableEarlyStoreAddress: true
        );
        with.Run();

        Assert.Equal(7UL, without.ArchState.IntegerRegisters.Read(4));
        Assert.Equal(7UL, with.ArchState.IntegerRegisters.Read(4));
        Assert.Equal(42UL, without.ArchState.IntegerRegisters.Read(5));
        Assert.Equal(42UL, with.ArchState.IntegerRegisters.Read(5));

        DialBoardSnapshot withoutSnap = without.SnapshotPipeline();
        DialBoardSnapshot withSnap = with.SnapshotPipeline();
        Assert.True(
            withSnap.Counters["cycles"] < withoutSnap.Counters["cycles"],
            $"expected early address resolution to reduce cycles: without={withoutSnap.Counters["cycles"]}, " +
            $"with={withSnap.Counters["cycles"]}"
        );
    }

    // Store address ready fast (x0-based), data ready slow (deterministic multiply chain, so
    // it's equally slow every loop iteration — not a cache-state-dependent miss), younger
    // same-address load, run around a real backward branch so the Store Sets predictor
    // (enableStoreSets) gets a real chance to train "this load depends on this store" and
    // release it. Regression guard for the AddressKnown-vs-DataKnown bug: releasing on address
    // alone would let the load race the store's real write and violate on every iteration
    // instead of cleanly stalling, since TryForwardFromStore also requires DataKnown and so
    // can't forward in time either. Asserts early resolution causes no MORE violations than
    // early resolution disabled — the fixed behavior — rather than a cycle-count reduction,
    // since Store Sets' payoff here is "avoid the violation", not a scheduling latency win.
    [Fact]
    public void SameAddressLoad_UnderStoreSets_DoesNotRaceStoreData() {
        uint[] program = [
            Addi(9, 0, 4),                    // pc=0: loop trip count = 4
            Addi(6, 0, 10),                   // pc=4 [LOOP]
            Mul(6, 6, 6),                     // pc=8: x6 = 100 — deterministically slow store data
            Mul(6, 6, 6),                     // pc=12: x6 = 10000
            Sw(0, 6, 400),                    // pc=16: mem[400] = x6, address = x0+400 (trivially ready)
            Lw(4, 0, 400),                    // pc=20: younger, same-address load
            Addi(9, 9, -1),                   // pc=24
            Bne(9, 0, 4 - 28),                // pc=28: branch back to pc=4 if x9 != 0
            OooStoreAddressSplitTests.Ebreak, // pc=32
        ];

        var withoutMem = new FlatMemory(65536);
        Load(withoutMem, program);
        var without = new OooTrain(
            new Rv32Mechanism(), withoutMem, issueWidth: 4,
            enableStoreSets: true, enableEarlyStoreAddress: false
        );
        without.Run();

        var withMem = new FlatMemory(65536);
        Load(withMem, program);
        var with = new OooTrain(
            new Rv32Mechanism(), withMem, issueWidth: 4,
            enableStoreSets: true, enableEarlyStoreAddress: true
        );
        with.Run();

        Assert.Equal(10000UL, without.ArchState.IntegerRegisters.Read(4));
        Assert.Equal(10000UL, with.ArchState.IntegerRegisters.Read(4));

        long withoutViolations = without.SnapshotPipeline().Counters["mem_order_violations"];
        long withViolations = with.SnapshotPipeline().Counters["mem_order_violations"];
        Assert.True(
            withViolations <= withoutViolations,
            $"expected early address resolution not to increase violations under Store Sets: " +
            $"without={withoutViolations}, with={withViolations}"
        );
    }
}