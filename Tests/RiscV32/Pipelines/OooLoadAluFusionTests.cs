#region

using Orrery.Cache;
using Orrery.Observation;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Load+ALU micro-fusion on <see cref="OooTrain" /> (TODO.md's µops micro-fusion item):
///     a load immediately followed by an ALU op that reads and overwrites the load's own
///     destination register (e.g. <c>lw t0,0(a0); addi t0,t0,4</c>), fused at Rename into a
///     single ROB+IQ entry — same mechanism and call sites as the existing compare+branch
///     macro-fusion (<see cref="OooMacroFusionTests" />), reusing <c>FusedSecondInstrId</c>
///     unmodified.
///     <para>
///         As with compare+branch, an OoO machine's fetch/rename width already caps a dense
///         independent stream regardless of fusion, so the win only shows up as ROB-occupancy
///         pressure relief — <see cref="ReducesCyclesWhenDenseWorkOutgrowsTheRob" /> mirrors
///         <c>OooMacroFusionTests</c>' own pressure-forcing test shape.
///     </para>
/// </summary>
public class OooLoadAluFusionTests {
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

    private static uint Add(int rd, int rs1, int rs2) =>
        (uint)((0b0000000 << 25) | (rs2 << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0110011);

    private static uint Lw(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0000011);

    private static OooTrain Run(
        uint[] program,
        bool enableMacroFusion,
        int robCapacity = 32,
        int iqCapacity = 32,
        MemoryConfig? dMemConfig = null
    ) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        var train = new OooTrain(
            new Rv32Mechanism(enableMacroFusion: enableMacroFusion), mem,
            issueWidth: 4, robCapacity: robCapacity, iqCapacity: iqCapacity, dMemConfig: dMemConfig
        );
        train.Run();
        return train;
    }

    // x1 = 200 (address base), mem[200] = 10, lw x5,0(x1) then addi x5,x5,4 => x5 = 14.
    private static uint[] LoadUseProgram() => [
        Addi(1, 0, 200),
        Lw(5, 1, 0),
        Addi(5, 5, 4),
        OooLoadAluFusionTests.Ebreak,
    ];

    private static void RunWithSeededData(uint[] program, bool enableMacroFusion, out OooTrain train) {
        var mem = new FlatMemory(65536);
        Load(mem, program);
        mem.Write(200, 10, 4);
        train = new OooTrain(new Rv32Mechanism(enableMacroFusion: enableMacroFusion), mem, issueWidth: 4);
        train.Run();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadThenAdd_ProducesSameResultRegardlessOfFusion(bool fusion) {
        RunWithSeededData(LoadUseProgram(), fusion, out OooTrain train);
        Assert.Equal(14UL, train.ArchState.IntegerRegisters.Read(5));
    }

    [Fact]
    public void LoadAluFusion_ActuallyFiresOnTheLoadUseIdiom() {
        RunWithSeededData(LoadUseProgram(), true, out OooTrain train);
        Assert.True(train.SnapshotPipeline().Counters["micro_fusions"] > 0, "expected the load+addi pair to fuse");
    }

    // Register-register form (lw x5,0(x2); add x5,x5,x6): the fused entry's SourceRegisters
    // must include x6 — an independent register the ALU half reads but the load itself never
    // touches — or the issue queue would let the pair issue as soon as the load's own address
    // is ready, reading whatever stale/uninitialized value happens to sit behind x6's physical
    // register instead of waiting for its real producer. x6 comes from a deliberately slow,
    // separately-missing load so its value genuinely isn't ready when the fused pair's own load
    // resolves; a wrong final result would mean the wakeup wait was skipped.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadThenAdd_RegisterRegisterForm_WaitsForIndependentLongLatencySource(bool fusion) {
        // Only 2 instructions precede the fusible pair (base-register loads read straight off
        // x0, no separate address-setup ADDI needed) — StepRename's TryPeekSecondDecoded only
        // ever considers two instructions it can see together in the decode queue at once, so
        // an extra preceding instruction here can shift fetch-window alignment and make this
        // specific pair miss fusion for a cycle-timing reason unrelated to what's being tested
        // (same fetch-window-dependent characteristic OooMacroFusionTests documents for
        // compare+branch); this shape is empirically verified to fuse reliably.
        uint[] program = [
            Addi(1, 0, 200),
            Lw(6, 1, 0),    // x6 = mem[200] — cold D-cache miss, resolves late
            Lw(5, 0, 2000), // x5 = mem[2000] (base x0) — fused with the add below
            Add(5, 5, 6),   // x5 = x5 + x6 — must wait for x6's real value, not a stale one
            OooLoadAluFusionTests.Ebreak,
        ];
        var mem = new FlatMemory(65536);
        Load(mem, program);
        mem.Write(200, 7, 4);
        mem.Write(2000, 3, 4);
        var dMemConfig = new MemoryConfig(4096, 4, 32, 60);
        var train = new OooTrain(
            new Rv32Mechanism(enableMacroFusion: fusion), mem, issueWidth: 4, dMemConfig: dMemConfig
        );
        train.Run();

        Assert.Equal(10UL, train.ArchState.IntegerRegisters.Read(5)); // 3 + 7, regardless of arrival order
        if (fusion) Assert.True(train.SnapshotPipeline().Counters["micro_fusions"] > 0, "expected the pair to fuse");
    }

    [Fact]
    public void PreservesRetiredInstructionCount() {
        var unfusedMem = new FlatMemory(65536);
        Load(unfusedMem, LoadUseProgram());
        unfusedMem.Write(200, 10, 4);
        var unfused = new OooTrain(new Rv32Mechanism(enableMacroFusion: false), unfusedMem, issueWidth: 4);
        unfused.Run();

        var fusedMem = new FlatMemory(65536);
        Load(fusedMem, LoadUseProgram());
        fusedMem.Write(200, 10, 4);
        var fused = new OooTrain(new Rv32Mechanism(enableMacroFusion: true), fusedMem, issueWidth: 4);
        fused.Run();

        Assert.True(fused.SnapshotPipeline().Counters["micro_fusions"] > 0, "expected the pair to fuse");
        Assert.Equal(
            unfused.SnapshotPipeline().Counters["retired"],
            fused.SnapshotPipeline().Counters["retired"]
        );
    }

    [Fact]
    public void DoesNotFuse_WhenAluDestinationDiffersFromLoadDestination() {
        // lw x5,0(x1); addi x6,x5,4 — x6 != x5, so the load's value could still be
        // observed independently later; must not fuse without a liveness analysis.
        uint[] program = [
            Addi(1, 0, 200),
            Lw(5, 1, 0),
            Addi(6, 5, 4),
            OooLoadAluFusionTests.Ebreak,
        ];
        RunWithSeededData(program, true, out OooTrain train);
        Assert.Equal(0, train.SnapshotPipeline().Counters["micro_fusions"]);
        Assert.Equal(10UL, train.ArchState.IntegerRegisters.Read(5));
        Assert.Equal(14UL, train.ArchState.IntegerRegisters.Read(6));
    }

    [Fact]
    public void DoesNotFuse_WhenAnInterveningInstructionSeparatesThePair() {
        uint[] program = [
            Addi(1, 0, 200),
            Lw(5, 1, 0),
            Addi(2, 0, 1), // intervening, unrelated
            Addi(5, 5, 4),
            OooLoadAluFusionTests.Ebreak,
        ];
        RunWithSeededData(program, true, out OooTrain train);
        Assert.Equal(0, train.SnapshotPipeline().Counters["micro_fusions"]);
        Assert.Equal(14UL, train.ArchState.IntegerRegisters.Read(5));
    }

    // A cold-miss load sits at the ROB head (nothing retires until it resolves), followed by
    // `copies` independent lw+addi pairs (all hitting the same already-cached address, so only
    // the very first pays a miss) that don't touch the head load's destination — free to
    // dispatch/execute out of order, but each still costs a ROB entry until the head retires.
    // robCapacity is sized so the *unfused* instruction count (2*copies) exceeds it (forcing
    // dispatch to stall on ROB-full mid-backlog) while the *fused* count (copies) fits entirely.
    private static uint[] BuildRobPressureProgram(int copies) {
        var program = new List<uint> {
            Lw(10, 0, 0),     // cold D-cache miss: nothing behind this retires until it resolves
            Addi(1, 0, 2000), // address base for the repeated pairs (safely beyond the program's own bytes)
        };
        for (var i = 0; i < copies; i++) {
            program.Add(Lw(5, 1, 0));   // x5 = mem[2000] — hits cache after the first iteration
            program.Add(Addi(5, 5, 1)); // x5 += 1 — fusible read-modify pair
        }

        program.Add(OooLoadAluFusionTests.Ebreak);
        return program.ToArray();
    }

    [Fact]
    public void ReducesCyclesWhenDenseWorkOutgrowsTheRob() {
        const int copies = 12;
        const int robCapacity = 16; // 2*copies=24 > 16 (unfused stalls); copies=12 <= 16 (fused fits)
        uint[] program = BuildRobPressureProgram(copies);
        var dMemConfig = new MemoryConfig(4096, 4, 32, 60);

        DialBoardSnapshot unfused = Run(program, false, robCapacity, robCapacity * 2, dMemConfig).SnapshotPipeline();
        DialBoardSnapshot fused = Run(program, true, robCapacity, robCapacity * 2, dMemConfig).SnapshotPipeline();

        Assert.True(fused.Counters["micro_fusions"] > 0, "expected at least one load+ALU fusion");
        Assert.Equal(0, unfused.Counters["micro_fusions"]);

        Assert.Equal(unfused.Counters["retired"], fused.Counters["retired"]);
        Assert.True(
            fused.Counters["cycles"] < unfused.Counters["cycles"],
            $"expected fusion to reduce cycles: unfused={unfused.Counters["cycles"]}, fused={fused.Counters["cycles"]}"
        );
    }
}