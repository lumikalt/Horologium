#region

using Mechanism;
using Orrery.Cache;
using Orrery.Observation;
using Pipeline;
using Pipeline.Ooo;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Store address/data decomposition on <see cref="OooTrain" /> (TODO.md's µops
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
/// </summary>
public class OooStoreAddressSplitTests {
    private const uint Ebreak = 0x00100073;

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

    private static readonly MemoryConfig SlowMissConfig =
        new(CacheCapacityBytes: 4096, CacheWays: 4, CacheBlockBytes: 32, CacheMissLatency: 60);

    private static OooTrain Run(
        uint[] program, bool enableEarlyStoreAddress, MemoryConfig? dMemConfig = null, FuLatencyConfig? fuConfig = null
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
            Addi(2, 2, 1), // a few dependent ALU hops widen the delay before x2 is finally ready
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
            Lw(2, 0, 1600), // x2 = mem[1600] — SLOW cold miss, store's data operand
            Sw(1, 2, 0), // mem[400] = x2 — address ready immediately, data ready late
            Lw(4, 0, 800), // younger, non-aliasing load; conservative loads must wait on the
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
}
