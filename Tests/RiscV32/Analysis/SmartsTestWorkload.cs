#region

using Mechanism;
using Orrery.Cache;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

// ReSharper disable InconsistentNaming

#endregion

namespace Tests.RiscV32.Analysis;

/// <summary>
///     Shared hand-assembled RV32I workload for SMARTS driver validation (<see cref="SmartsDriverTests" />
///     and <see cref="SmartsDriverOooTests" />): a loop with a backward-taken branch, a memory
///     read-modify-write accumulator (the handoff-fidelity oracle — see
///     <see cref="SmartsDriverTests" />'s doc comment), and a D-cache-exercising load.
/// </summary>
internal static class SmartsTestWorkload {
    internal const int Iters = 4000;
    internal const ulong AccumulatorAddress = 128;
    private const ulong ScratchLoadAddress = 64;
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

    private static uint Lui(int rd, int imm20) => (uint)(((imm20 & 0xFFFFF) << 12) | (rd << 7) | 0b0110111);

    private static uint Lw(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b010 << 12) | (rd << 7) | 0b0000011);

    private static uint Sw(int rs2, int rs1, int imm) {
        uint immU = (uint)imm & 0xFFF;
        uint imm11_5 = (immU >> 5) & 0x7F;
        uint imm4_0 = immU & 0x1F;
        return (imm11_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (0b010u << 12) | (imm4_0 << 7) | 0b0100011u;
    }

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10_5 = (imm >> 5) & 0x3F;
        uint bits4_1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10_5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4_1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    // x1 = iteration counter, x4 = D-cache-exercising scratch load address, x5 = accumulator
    // address. Loop body is 6 instructions (24 bytes): a memory read-modify-write accumulator
    // (cross-iteration RAW dependency through memory, not just registers) plus an unrelated
    // scratch load, decrement, and backward-taken branch.
    //
    // Iters (4000) exceeds ADDI's ±2047 signed-immediate range, so x1 needs a LUI+ADDI pair
    // (the standard RISC-V large-constant idiom) rather than a single ADDI — encoding 4000
    // directly into ADDI's 12-bit immediate field silently sign-extends it to -96, making the
    // loop counter go deeply negative instead of ever hitting exactly 0 (an actually-infinite
    // loop that no test here ever noticed, since every other test's instruction budget stayed
    // well short of where the bug would matter — until a halt-detection regression test needed
    // the workload to genuinely reach its end).
    internal static FlatMemory BuildProgram() {
        var mem = new FlatMemory(4096);
        Load(
            mem,
            Lui(1, 1),                                              // 0x00: x1 = 0x1000 (4096)
            Addi(1, 1, SmartsTestWorkload.Iters - 4096),            // 0x04: x1 += (Iters - 4096) = Iters
            Addi(4, 0, (int)SmartsTestWorkload.ScratchLoadAddress), // 0x08: x4 = scratch address
            Addi(5, 0, (int)SmartsTestWorkload.AccumulatorAddress), // 0x0C: x5 = accumulator address
            Lw(3, 5, 0),                                            // 0x10: loop: lw x3, 0(x5)
            Addi(3, 3, 1),                                          // 0x14: addi x3, x3, 1
            Sw(3, 5, 0),                                            // 0x18: sw x3, 0(x5)
            Lw(6, 4, 0),                                            // 0x1C: lw x6, 0(x4)
            Addi(1, 1, -1),                                         // 0x20: addi x1, x1, -1
            Bne(1, 0, -20),                                         // 0x24: bne x1, x0, loop
            SmartsTestWorkload.Ebreak                               // 0x28
        );
        return mem;
    }

    internal static MemoryConfig DCache() => new(256, 4, 64);

    // SmartsDriver.Run only samples up to the last requested unit's measured window, not the
    // whole workload — so the accumulator's correct final value is not Iters, it is whatever an
    // independent, un-sampled functional reference computes for the exact same total instruction
    // count. That count is the systematic-sampling schedule's own closed form (last unit's
    // measured window ends at J + (N-1)*K + U), not anything read back from SmartsDriver's
    // internals — this reference must stay independent of the code path it's checking.
    internal static long ExpectedTotalInstructions(SmartsParameters p) => p.J + (p.N - 1) * p.K + p.U;

    internal static int RunFunctionalReferenceAccumulator(long totalInstructions) {
        FlatMemory mem = BuildProgram();
        var mechanism = new Rv32Mechanism();
        var counter = new InstructionCounter();
        var train = new SingleCycleTrain(mechanism, mem, commitObserver: counter);
        train.BeginStepping();
        while (counter.Count < totalInstructions && train.StepCycle()) { }

        train.FinishStepping();
        return (int)mem.Read(SmartsTestWorkload.AccumulatorAddress, 4);
    }

    // The correctness property SmartsDriver needs is "produces the same final state as running
    // the SAME detailed pipeline continuously the whole time" — not "matches a functional
    // single-cycle reference". A continuous, un-sampled FiveStageTrain/OooTrain run of this
    // workload can read a small, fixed amount ahead of SingleCycleTrain when both are stopped
    // mid-stream at the same committed-instruction count — not a pipeline correctness bug (the
    // two match exactly when both run to natural completion instead of being stopped mid-stream;
    // see SmartsPipelineShadowDecisiveTest). The commit-count observer fires in WriteBack, one
    // stage after MemoryStage has already written a trailing store's data, so a store still
    // in-flight past the stop point can already be visible in memory before it's been counted.
    // So the oracle for "did the handoff preserve state" must be a continuous run of the SAME
    // pipeline kind stopped at the SAME instruction count, not the SingleCycleTrain reference
    // above, which has no such in-flight shadow.
    internal static int RunContinuousDetailedAccumulator(
        long totalInstructions,
        Func<IMechanism, IMemory, InstructionCounter, ISteppableTrain> trainFactory
    ) {
        FlatMemory mem = BuildProgram();
        var mechanism = new Rv32Mechanism();
        var counter = new InstructionCounter();
        ISteppableTrain train = trainFactory(mechanism, mem, counter);
        train.BeginStepping();
        while (counter.Count < totalInstructions && train.StepCycle()) { }

        train.FinishStepping();
        return (int)mem.Read(SmartsTestWorkload.AccumulatorAddress, 4);
    }
}