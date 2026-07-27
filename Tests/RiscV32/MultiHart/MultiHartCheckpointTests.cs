#region

using Mechanism;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.MultiCore;
using RiscV32.State;
using RiscV32.Syscalls;

// ReSharper disable ShiftExpressionZeroLeftOperand

#endregion

namespace Tests.RiscV32.MultiHart;

/// <summary>
///     <see cref="MultiHartCheckpoint" />/<see cref="MultiHartWarmupMeasureDriver" /> round-tripped
///     across two genuinely different engines — captured mid-run from the functional
///     <see cref="MultiHartKernel" /> (LoopPoint's own profiling-pass driver), restored into fresh
///     <see cref="FiveStageTrain" />s driven by <see cref="MultiHartPipeline" /> — the same
///     functional-fast-forward-then-detailed-timing handoff <c>ArchitecturalCheckpoint</c> already
///     proves single-hart.
///     <para>
///         Per <c>feedback_checkpoint_roundtrip_theater</c>: warm-equals-restored alone would pass
///         even with a no-op <c>RestoreInto</c>, since a freshly-constructed train's ArchState starts
///         zeroed and could coincidentally end up matching a broken restore for a lucky program. The
///         decisive comparison here is against a <em>cold</em> reference: two <see cref="FiveStageTrain" />s
///         run the identical program start-to-finish with no checkpoint involved at all. Each hart
///         runs a different iteration count to a different target address specifically so a restore
///         bug that mixed up per-hart state (wrong hart's registers, wrong hart's slice of the ISA
///         blob) would produce a visibly wrong final value rather than accidentally matching.
///     </para>
/// </summary>
public class MultiHartCheckpointTests {
    private const uint Ebreak = 0x0010_0073;

    // loop: addi x1, x1, 1 ; sw x1, 0(x3) ; addi x2, x2, -1 ; bne x2, x0, loop ; ebreak
    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Sw(int rs2, int rs1, int imm) {
        var immU = (uint)imm;
        uint imm11To5 = (immU >> 5) & 0x7F;
        uint imm4To0 = immU & 0x1F;
        return (imm11To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (0b010u << 12) | (imm4To0 << 7) | 0b0100011u;
    }

    private static uint Bne(int rs1, int rs2, int immOffset) {
        var imm = (uint)immOffset;
        uint bit12 = (imm >> 12) & 0x1;
        uint bit11 = (imm >> 11) & 0x1;
        uint bits10To5 = (imm >> 5) & 0x3F;
        uint bits4To1 = (imm >> 1) & 0xF;
        return (bit12 << 31) | (bits10To5 << 25) | ((uint)rs2 << 20) | ((uint)rs1 << 15)
             | (0b001u << 12) | (bits4To1 << 8) | (bit11 << 7) | 0b1100011u;
    }

    private static void LoadProgram(FlatMemory mem, ulong baseAddr) {
        uint[] words = [Addi(1, 1, 1), Sw(1, 3, 0), Addi(2, 2, -1), Bne(2, 0, -12), MultiHartCheckpointTests.Ebreak,];
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        mem.Load(baseAddr, bytes);
    }

    [Fact]
    public void CaptureOnFunctionalKernel_RestoreOnFiveStageTrains_MatchesAColdFiveStageOnlyRun() {
        const int hart0Iterations = 50;
        const int hart1Iterations = 73;
        const ulong hart0Addr = 0x1000;
        const ulong hart1Addr = 0x1004;

        // ── Cold reference: both harts run start-to-finish on FiveStageTrain, no checkpoint at all.
        var coldMem = new FlatMemory(0x2000);
        LoadProgram(coldMem, 0x00);
        LoadProgram(coldMem, 0x40);
        var coldTrain0 = new FiveStageTrain(new Rv32Mechanism(), coldMem);
        var coldTrain1 = new FiveStageTrain(new Rv32Mechanism(), coldMem, 0x40);
        coldTrain0.ArchState.IntegerRegisters.Write(2, hart0Iterations);
        coldTrain0.ArchState.IntegerRegisters.Write(3, hart0Addr);
        coldTrain1.ArchState.IntegerRegisters.Write(2, hart1Iterations);
        coldTrain1.ArchState.IntegerRegisters.Write(3, hart1Addr);
        new MultiHartPipeline(coldTrain0, coldTrain1).Run(10_000);

        ulong expectedX1Hart0 = coldTrain0.ArchState.IntegerRegisters.Read(1);
        ulong expectedX1Hart1 = coldTrain1.ArchState.IntegerRegisters.Read(1);
        ulong expectedMemHart0 = coldMem.Read(hart0Addr, 4);
        ulong expectedMemHart1 = coldMem.Read(hart1Addr, 4);
        Assert.Equal((ulong)hart0Iterations, expectedX1Hart0);
        Assert.Equal((ulong)hart1Iterations, expectedX1Hart1);

        // ── Warm functional run on MultiHartKernel, checkpointed mid-run (well before either hart's
        // own halt: hart0 needs 50*4=200 instructions, hart1 needs 73*4=292 — 100 ticks is short of both).
        var warmMem = new FlatMemory(0x2000);
        LoadProgram(warmMem, 0x00);
        LoadProgram(warmMem, 0x40);
        var mech0 = new Rv32Mechanism();
        var mech1 = new Rv32Mechanism();
        var kernel = new MultiHartKernel(warmMem, mech0, mech1);
        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);
        kernel.StateOf(0).IntegerRegisters.Write(2, hart0Iterations);
        kernel.StateOf(0).IntegerRegisters.Write(3, hart0Addr);
        kernel.StateOf(1).IntegerRegisters.Write(2, hart1Iterations);
        kernel.StateOf(1).IntegerRegisters.Write(3, hart1Addr);

        for (var i = 0; i < 100; i++) kernel.Step();

        using var ms = new MemoryStream();
        MultiHartCheckpoint.Save(ms, [kernel.StateOf(0), kernel.StateOf(1),], warmMem, null, (ulong)kernel.Ticks);
        ms.Position = 0;
        MultiHartCheckpoint checkpoint = MultiHartCheckpoint.Load(ms);

        Assert.Equal(2, checkpoint.HartCount);

        // ── Restore into fresh FiveStageTrains and run the rest of the way.
        var restoreMem = new FlatMemory(0x2000);
        var restoreTrain0 = new FiveStageTrain(new Rv32Mechanism(), restoreMem);
        var restoreTrain1 = new FiveStageTrain(new Rv32Mechanism(), restoreMem);
        checkpoint.RestoreInto([restoreTrain0.ArchState, restoreTrain1.ArchState,], restoreMem, null);

        new MultiHartPipeline(restoreTrain0, restoreTrain1).Run(10_000);

        Assert.Equal(expectedX1Hart0, restoreTrain0.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(expectedX1Hart1, restoreTrain1.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(expectedMemHart0, restoreMem.Read(hart0Addr, 4));
        Assert.Equal(expectedMemHart1, restoreMem.Read(hart1Addr, 4));
    }

    [Fact]
    public void WarmupMeasureDriver_OnRestoredCheckpoint_ProducesSaneBaselineSubtractedDials() {
        const int hart0Iterations = 60;
        const int hart1Iterations = 60;
        const ulong hart0Addr = 0x1000;
        const ulong hart1Addr = 0x1004;

        var warmMem = new FlatMemory(0x2000);
        LoadProgram(warmMem, 0x00);
        LoadProgram(warmMem, 0x40);
        var mech0 = new Rv32Mechanism();
        var mech1 = new Rv32Mechanism();
        var kernel = new MultiHartKernel(warmMem, mech0, mech1);
        kernel.SetEntryPoint(0, 0x00);
        kernel.SetEntryPoint(1, 0x40);
        kernel.StateOf(0).IntegerRegisters.Write(2, hart0Iterations);
        kernel.StateOf(0).IntegerRegisters.Write(3, hart0Addr);
        kernel.StateOf(1).IntegerRegisters.Write(2, hart1Iterations);
        kernel.StateOf(1).IntegerRegisters.Write(3, hart1Addr);
        for (var i = 0; i < 40; i++) kernel.Step(); // well short of either hart's 240-instruction total

        using var ms = new MemoryStream();
        MultiHartCheckpoint.Save(ms, [kernel.StateOf(0), kernel.StateOf(1),], warmMem, null, (ulong)kernel.Ticks);
        ms.Position = 0;
        MultiHartCheckpoint checkpoint = MultiHartCheckpoint.Load(ms);

        var restoreMem = new FlatMemory(0x2000);
        var counter0 = new InstructionCounter();
        var counter1 = new InstructionCounter();
        var train0 = new FiveStageTrain(new Rv32Mechanism(), restoreMem, commitObserver: counter0);
        var train1 = new FiveStageTrain(new Rv32Mechanism(), restoreMem, commitObserver: counter1);
        checkpoint.RestoreInto([train0.ArchState, train1.ArchState,], restoreMem, null);

        (RevolutionResult[] results, _, _) = MultiHartWarmupMeasureDriver.RunWarmupThenMeasure(
            [train0, train1,], [counter0, counter1,], 20, 100
        );

        Assert.Equal(2, results.Length);
        // Global targets are warmup=20, measure=100 (2 harts, so ~10/~50 retired per hart on each
        // side respectively) — the discriminating check is the *summed* measure-window retired count
        // landing near 100, not just "some value <= 100" (which an unsubtracted baseline would also
        // satisfy: warmup+measure retired per hart is itself well under 100). A broken baseline
        // (measure phase's dials not actually offset by the warmup phase's) would instead report each
        // hart's full warmup+measure retired count, summing closer to 120 than 100.
        long totalRetired = 0;
        foreach (RevolutionResult r in results) {
            Assert.True(r.TotalTicks > 0);
            DialBoardSnapshot? pipeline = r.Find("five_stage.pipeline");
            Assert.NotNull(pipeline);
            totalRetired += pipeline.Counters["retired"];
        }

        Assert.InRange(totalRetired, 90, 110);
    }

    [Fact]
    public void SyscallHandlerState_RoundTripsThroughMultiHartCheckpoint_IncludingChildCleartid() {
        // The composition the two hart-state-only tests above never exercise: MultiHartCheckpoint's
        // "one shared syscall-handler blob" path (Save's has-handler branch, RestoreInto's ReadState
        // call). A real pthread workload's checkpoint always has handler state — brk, mmap, fd table,
        // and per-hart CLONE_CHILD_CLEARTID bookkeeping from any prior clone() — so proving the
        // machinery only against a bare-metal counting loop (no handler at all) would miss exactly
        // the input shape 10a's actual target (a non-blocking region of a real musl workload) has.
        var mem = new FlatMemory(0x2000, 0x8000_0000UL);
        var handler = new LinuxSyscallEmulator(0x8000_0000UL) { Spawner = new FakeSpawner(), };

        const ulong ctidAddr = 0x8000_0100UL;
        mem.Write(ctidAddr, 0xDEADBEEF, 4); // sentinel — must become exactly 0 once hart 1 exits
        const ulong cloneChildCleartid = 0x0020_0000;

        // clone() with CLONE_CHILD_CLEARTID — records _childCleartid[1] = ctidAddr (FakeSpawner
        // always assigns hart id 1).
        var cloningHartState = new Rv32ArchState();
        cloningHartState.IntegerRegisters.Write(10, cloneChildCleartid); // a0 = flags
        cloningHartState.IntegerRegisters.Write(14, ctidAddr);           // a4 = ctid
        handler.Handle(220, cloningHartState, mem, 0, 0);

        // Move brk, exactly as pthread_probe.elf's musl startup would before any thread runs.
        var brkHartState = new Rv32ArchState();
        brkHartState.IntegerRegisters.Write(10, 0x8000_3000UL);
        ExecuteResult brkResult = handler.Handle(214, brkHartState, mem, 0, 0);
        brkResult.SideEffect?.Invoke(brkHartState);
        Assert.Equal(0x8000_3000UL, brkHartState.IntegerRegisters.Read(10));

        IArchState hart0State = new Rv32Mechanism().CreateArchState();
        IArchState hart1State = new Rv32Mechanism().CreateArchState();

        using var ms = new MemoryStream();
        MultiHartCheckpoint.Save(ms, [hart0State, hart1State,], mem, handler, 0);
        ms.Position = 0;
        MultiHartCheckpoint checkpoint = MultiHartCheckpoint.Load(ms);

        var restoreMem = new FlatMemory(0x2000, 0x8000_0000UL);
        var restoredHandler = new LinuxSyscallEmulator(0x8000_0000UL) { Spawner = new FakeSpawner(), };
        IArchState restoredHart0 = new Rv32Mechanism().CreateArchState();
        IArchState restoredHart1 = new Rv32Mechanism().CreateArchState();
        checkpoint.RestoreInto([restoredHart0, restoredHart1,], restoreMem, restoredHandler);

        // brk continued from the checkpointed value, not the fresh handler's own initialBreak.
        var brkQuery = new Rv32ArchState();
        brkQuery.IntegerRegisters.Write(10, 0);
        ExecuteResult brkQueryResult = restoredHandler.Handle(214, brkQuery, restoreMem, 0, 0);
        brkQueryResult.SideEffect?.Invoke(brkQuery);
        Assert.Equal(0x8000_3000UL, brkQuery.IntegerRegisters.Read(10));

        // _childCleartid[1] survived the round trip — hart 1's exit clears the recorded ctid address.
        var exitingHartState = new Rv32ArchState();
        ExecuteResult exitResult = restoredHandler.Handle(93, exitingHartState, restoreMem, 0, 1);
        Assert.True(exitResult.RequestHalt);
        Assert.Equal(0UL, restoreMem.Read(ctidAddr, 4));
    }

    private sealed class FakeSpawner(int nextHartId = 1) : IHartSpawner {
        public int SpawnHart(IArchState initialState) => nextHartId;
    }
}