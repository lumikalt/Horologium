using Mechanism;
using Orrery.Train;
using Pipeline;
using Pipeline.Spec;
using RiscV32;
using RiscV32.Memory;

namespace Tests.Pipeline;

/// <summary>
///     The real correctness check for the SimPoint-checkpoint machinery (<see cref="InstructionCounter" />,
///     <see cref="WarmupMeasureDriver" />, <see cref="ISteppableTrain.SnapshotDials" />): independent of any
///     BBV clustering, it proves that checkpointing at instruction K and resuming for N−K instructions on a
///     fresh OoO train produces bit-identical final architectural state to running straight through to
///     instruction N on one continuous OoO train. If this doesn't hold, nothing built on top of it (SimPoint
///     sampling, or any other checkpoint-then-detailed-run workflow) can be trusted.
/// </summary>
public class InstructionCountedCheckpointTests {
    // addi x1, x0, 20        — loop counter
    // addi x2, x0, 0         — accumulator
    // loop: addi x2, x2, 3   — accumulate
    //       addi x1, x1, -1  — decrement
    //       bne  x1, x0, loop
    // ebreak
    private static readonly byte[] LoopProgram = Encode(
        0x01400093u, 0x00000113u, 0x00310113u, 0xFFF08093u, 0xFE009EE3u, 0x00100073u
    );

    private static byte[] Encode(params uint[] words) {
        var b = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), words[i]);
        return b;
    }

    private static FlatMemory MakeMem() {
        var mem = new FlatMemory(0x1000);
        mem.Load(0x00, InstructionCountedCheckpointTests.LoopProgram);
        return mem;
    }

    private static (ulong Pc, ulong[] Regs) Signature(IArchState state) {
        var regs = new ulong[state.IntegerRegisters.Count];
        for (var i = 0; i < regs.Length; i++) regs[i] = state.IntegerRegisters.Read(i);
        return (state.Pc, regs);
    }

    [Fact]
    public void OutOfOrder_CheckpointRestoreThenMeasure_MatchesStraightThrough() {
        const long k = 25; // checkpoint here — mid-loop, well past the first few renames
        const long n = 55; // total instructions to compare at — near the end, still mid-loop

        // ── A: straight through to instruction N on one continuous OoO train ────────────
        var counterA = new InstructionCounter();
        FlatMemory memA = MakeMem();
        MachineHandle handleA = new MachineSpec(
            new OutOfOrderSpec(CommitObserver: counterA), () => new Rv32Mechanism()
        ).Build(memA);
        WarmupMeasureDriver.RunWarmupThenMeasure(handleA.Train, counterA, 0, n);
        (ulong pcA, ulong[] regsA) = Signature(handleA.ArchState!);

        // ── B: fast-forward functionally to K, checkpoint, restore into a fresh OoO train,
        //      measure the remaining N-K instructions ──────────────────────────────────
        var counterFf = new InstructionCounter();
        FlatMemory memFf = MakeMem();
        MachineHandle ffHandle = new MachineSpec(
            new SingleCycleSpec(counterFf), () => new Rv32Mechanism()
        ).Build(memFf);
        ffHandle.Train.BeginStepping();
        while (counterFf.Count < k && ffHandle.Train.StepCycle()) { }

        RevolutionResult ffResult = ffHandle.Train.FinishStepping();

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, ffHandle.ArchState!, memFf, (ulong)ffResult.TotalTicks);
        ms.Position = 0;
        ArchitecturalCheckpoint chk = ArchitecturalCheckpoint.Load(ms);

        var counterB = new InstructionCounter();
        FlatMemory memB = MakeMem();
        MachineHandle handleB = new MachineSpec(
            new OutOfOrderSpec(CommitObserver: counterB), () => new Rv32Mechanism()
        ).Build(memB, chk.Pc); // entryPoint must be the checkpoint's PC — RestoreInto only writes ArchState.Pc
        chk.RestoreInto(handleB.ArchState!, memB);

        WarmupMeasureDriver.RunWarmupThenMeasure(handleB.Train, counterB, 0, n - k);
        (ulong pcB, ulong[] regsB) = Signature(handleB.ArchState!);

        Assert.Equal(pcA, pcB);
        Assert.Equal(regsA, regsB);
    }
}