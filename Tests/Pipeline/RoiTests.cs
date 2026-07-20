#region

using Mechanism;
using Pipeline.Spec;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     Region-of-Interest simulation: fast-forward to a symbol with SingleCycleTrain,
///     then switch to a detailed pipeline for the measurement window.
/// </summary>
public class RoiTests {
    // Instruction layout (little-endian RISC-V):
    //   0x00: addi x1, x0, 1
    //   0x04: addi x2, x0, 2
    //   0x08: addi x3, x0, 3   ← roiStartPc
    //   0x0C: addi x4, x0, 4
    //   0x10: ebreak
    private static readonly byte[] Program = Encode(
        0x00100093u, // addi x1, x0, 1
        0x00200113u, // addi x2, x0, 2
        0x00300193u, // addi x3, x0, 3
        0x00400213u, // addi x4, x0, 4
        0x00100073u  // ebreak
    );

    private static byte[] Encode(params uint[] words) {
        var b = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), words[i]);
        return b;
    }

    private static (FlatMemory Mem, MachineHandle Handle) Make(PipelineSpec pipeline, ulong entryPoint = 0) {
        var mem = new FlatMemory(0x1000);
        mem.Load(0, RoiTests.Program);
        MachineHandle handle = new MachineSpec(pipeline, () => new Rv32Mechanism()).Build(mem, entryPoint);
        return (mem, handle);
    }

    // Drives the fast-forward step loop and returns (ticks, reached).
    private static (long Ticks, bool Reached) FastForwardTo(
        MachineHandle handle,
        ulong targetPc,
        long limit = 100_000
    ) {
        handle.Train.BeginStepping();
        long ticks = 0;
        bool reached = handle.Train.ArchState!.Pc == targetPc;
        while (!reached && ticks < limit && handle.Train.StepCycle()) {
            ticks++;
            reached = handle.Train.ArchState!.Pc == targetPc;
        }

        handle.Train.FinishStepping();
        return (ticks, reached);
    }

    // Saves and reloads a checkpoint from the given handle and memory (in-memory round-trip).
    private static ArchitecturalCheckpoint Checkpoint(MachineHandle handle, FlatMemory mem, ulong tick) {
        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, handle.Train.ArchState!, mem, tick);
        ms.Position = 0;
        return ArchitecturalCheckpoint.Load(ms);
    }

    [Fact]
    public void FastForward_StopsAtTargetPc() {
        const ulong roiStart = 0x08;
        (FlatMemory _, MachineHandle ffHandle) = Make(new SingleCycleSpec());
        (long ticks, bool reached) = FastForwardTo(ffHandle, roiStart);

        Assert.True(reached, "Fast-forward should have reached roiStartPc");
        Assert.Equal(2L, ticks); // executed: addi x1 (→Pc=4), addi x2 (→Pc=8)
        Assert.Equal(roiStart, ffHandle.Train.ArchState!.Pc);
    }

    [Fact]
    public void FastForward_EntryEqualsRoi_ZeroSteps() {
        // When entry point IS the ROI start, no fast-forward steps occur.
        const ulong roiStart = 0x00;
        (FlatMemory _, MachineHandle ffHandle) = Make(new SingleCycleSpec());
        (long ticks, bool reached) = FastForwardTo(ffHandle, roiStart);

        Assert.True(reached);
        Assert.Equal(0L, ticks);
        Assert.Equal(0x00UL, ffHandle.Train.ArchState!.Pc);
    }

    [Fact]
    public void FastForwardThenDetailed_RegistersMatchStraightThrough() {
        // Straight-through run
        (FlatMemory _, MachineHandle refHandle) = Make(new SingleCycleSpec());
        refHandle.Run(10_000);
        IRegisterFile refRegs = refHandle.ArchState!.IntegerRegisters;

        // ROI run: fast-forward 2 instructions with SingleCycle, then SingleCycle from 0x08
        const ulong roiStart = 0x08;
        (FlatMemory ffMem, MachineHandle ffHandle) = Make(new SingleCycleSpec());
        (long ffTicks, bool reached) = FastForwardTo(ffHandle, roiStart);
        Assert.True(reached);

        ArchitecturalCheckpoint chk = Checkpoint(ffHandle, ffMem, (ulong)ffTicks);

        (FlatMemory detMem, MachineHandle detHandle) = Make(new SingleCycleSpec(), roiStart);
        chk.RestoreInto(detHandle.ArchState!, detMem);
        detHandle.Run(10_000);

        IRegisterFile detRegs = detHandle.ArchState!.IntegerRegisters;
        for (var i = 1; i <= 4; i++) Assert.Equal(refRegs.Read(i), detRegs.Read(i));
    }

    [Fact]
    public void FastForwardSingleCycle_ThenOoO_RegistersMatchStraightThrough() {
        // Straight-through with OoO
        (FlatMemory _, MachineHandle refHandle) = Make(new OutOfOrderSpec());
        refHandle.Run(10_000);
        IRegisterFile refRegs = refHandle.ArchState!.IntegerRegisters;

        // ROI: fast-forward with SingleCycle, then OoO from roiStart
        const ulong roiStart = 0x08;
        (FlatMemory ffMem, MachineHandle ffHandle) = Make(new SingleCycleSpec());
        (long ffTicks, bool reached) = FastForwardTo(ffHandle, roiStart);
        Assert.True(reached);

        ArchitecturalCheckpoint chk = Checkpoint(ffHandle, ffMem, (ulong)ffTicks);

        (FlatMemory detMem, MachineHandle detHandle) = Make(new OutOfOrderSpec(), roiStart);
        chk.RestoreInto(detHandle.ArchState!, detMem);
        detHandle.Run(10_000);

        IRegisterFile detRegs = detHandle.ArchState!.IntegerRegisters;
        for (var i = 1; i <= 4; i++) Assert.Equal(refRegs.Read(i), detRegs.Read(i));
    }

    [Fact]
    public void RoiEnd_StopsBeforeEndSymbol() {
        // Layout: 0x00 addi x1,1 | 0x04 addi x2,2 (roiStart) | 0x08 addi x3,3 (roiEnd) | 0x0C addi x4,4
        const ulong roiStart = 0x04;
        const ulong roiEnd = 0x08;

        // Fast-forward to roiStart
        (FlatMemory ffMem, MachineHandle ffHandle) = Make(new SingleCycleSpec());
        (long ffTicks, bool reached) = FastForwardTo(ffHandle, roiStart);
        Assert.True(reached);

        ArchitecturalCheckpoint chk = Checkpoint(ffHandle, ffMem, (ulong)ffTicks);

        // Detailed phase — stop when Pc hits roiEnd
        (FlatMemory detMem, MachineHandle detHandle) = Make(new SingleCycleSpec(), roiStart);
        chk.RestoreInto(detHandle.ArchState!, detMem);

        detHandle.Train.BeginStepping();
        long roiTicks = 0;
        var endReached = false;
        while (roiTicks < 100_000 && detHandle.Train.StepCycle()) {
            roiTicks++;
            if (detHandle.Train.ArchState!.Pc == roiEnd) {
                endReached = true;
                break;
            }
        }

        detHandle.Train.FinishStepping();

        Assert.True(endReached);
        Assert.Equal(1L, roiTicks); // only addi x2 executed
        IRegisterFile regs = detHandle.Train.ArchState!.IntegerRegisters;
        Assert.Equal(1UL, regs.Read(1)); // from fast-forward, restored
        Assert.Equal(2UL, regs.Read(2)); // executed in ROI
        Assert.Equal(0UL, regs.Read(3)); // NOT executed (roi-end is 0x08)
        Assert.Equal(0UL, regs.Read(4)); // NOT executed
    }

    [Fact]
    public void FastForward_SymbolNotReached_ReturnsFalse() {
        // Use a target PC that's way beyond the program — train halts before reaching it.
        const ulong roiStart = 0xDEAD_BEEF;
        (FlatMemory _, MachineHandle ffHandle) = Make(new SingleCycleSpec());
        (_, bool reached) = FastForwardTo(ffHandle, roiStart);

        Assert.False(reached);
    }
}