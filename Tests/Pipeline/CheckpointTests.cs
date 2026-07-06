using Mechanism;
using Pipeline.Spec;
using RiscV32;
using RiscV32.Memory;

namespace Tests.Pipeline;

public class CheckpointTests {
    // addi x1, x0, 42  →  addi x2, x0, 17  →  ebreak
    private static readonly byte[] SimpleProgram = Encode(
        0x02a00093u, // addi x1, x0, 42
        0x01100113u, // addi x2, x0, 17
        0x00100073u  // ebreak
    );

    private static byte[] Encode(params uint[] words) {
        var b = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), words[i]);
        return b;
    }

    private static FlatMemory MakeMem(byte[] program) {
        var mem = new FlatMemory(0x1000);
        mem.Load(0x00, program);
        return mem;
    }

    // ── Round-trip: save after run, restore, verify state matches ────────────

    [Fact]
    public void SingleCycle_SaveRestore_PcAndRegsMatch() {
        FlatMemory mem1 = MakeMem(SimpleProgram);
        var handle1 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem1);
        handle1.Run(1_000);

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, handle1.ArchState!, mem1, 100UL);

        FlatMemory mem2 = MakeMem(SimpleProgram);
        var handle2 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint.Load(ms).RestoreInto(handle2.ArchState!, mem2);

        Assert.Equal(handle1.ArchState!.Pc, handle2.ArchState!.Pc);
        IRegisterFile r1 = handle1.ArchState.IntegerRegisters;
        IRegisterFile r2 = handle2.ArchState.IntegerRegisters;
        for (var i = 0; i < r1.Count; i++) Assert.Equal(r1.Read(i), r2.Read(i));
    }

    [Fact]
    public void FiveStage_SaveRestore_PcAndRegsMatch() {
        FlatMemory mem1 = MakeMem(SimpleProgram);
        var handle1 = new MachineSpec(new FiveStageSpec(), () => new Rv32Mechanism()).Build(mem1);
        handle1.Run(10_000);

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, handle1.ArchState!, mem1, 0UL);

        FlatMemory mem2 = MakeMem(SimpleProgram);
        var handle2 = new MachineSpec(new FiveStageSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint.Load(ms).RestoreInto(handle2.ArchState!, mem2);

        Assert.Equal(handle1.ArchState!.Pc, handle2.ArchState!.Pc);
        IRegisterFile r1 = handle1.ArchState.IntegerRegisters;
        IRegisterFile r2 = handle2.ArchState.IntegerRegisters;
        for (var i = 0; i < r1.Count; i++) Assert.Equal(r1.Read(i), r2.Read(i));
    }

    [Fact]
    public void OutOfOrder_SaveRestore_PcAndRegsMatch() {
        FlatMemory mem1 = MakeMem(SimpleProgram);
        var handle1 = new MachineSpec(new OutOfOrderSpec(), () => new Rv32Mechanism()).Build(mem1);
        handle1.Run(10_000);

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, handle1.ArchState!, mem1, 0UL);

        FlatMemory mem2 = MakeMem(SimpleProgram);
        var handle2 = new MachineSpec(new OutOfOrderSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint.Load(ms).RestoreInto(handle2.ArchState!, mem2);

        Assert.Equal(handle1.ArchState!.Pc, handle2.ArchState!.Pc);
        IRegisterFile r1 = handle1.ArchState.IntegerRegisters;
        IRegisterFile r2 = handle2.ArchState.IntegerRegisters;
        for (var i = 0; i < r1.Count; i++) Assert.Equal(r1.Read(i), r2.Read(i));
    }

    // ── CSR round-trip (Rv32-specific) ───────────────────────────────────────

    [Fact]
    public void CsrState_SurvivesRoundTrip() {
        FlatMemory mem1 = MakeMem(SimpleProgram);
        var handle1 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem1);
        handle1.Run(1_000);
        IArchState s1 = handle1.ArchState!;

        // Read a few CSRs from the live state.
        ulong mcycle  = s1.SystemRegisters.Read(0xB00, s1.PrivilegeLevel); // mcycle
        ulong mstatus = s1.SystemRegisters.Read(0x300, s1.PrivilegeLevel); // mstatus

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, s1, mem1, 0UL);

        FlatMemory mem2 = MakeMem(SimpleProgram);
        var handle2 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint.Load(ms).RestoreInto(handle2.ArchState!, mem2);

        IArchState s2 = handle2.ArchState!;
        Assert.Equal(mcycle,  s2.SystemRegisters.Read(0xB00, s2.PrivilegeLevel));
        Assert.Equal(mstatus, s2.SystemRegisters.Read(0x300, s2.PrivilegeLevel));
    }

    // ── Memory round-trip ────────────────────────────────────────────────────

    [Fact]
    public void Memory_SurvivesRoundTrip() {
        // Program: addi x1, x0, 42  →  sw x1, 256(x0)  →  ebreak
        byte[] prog = Encode(0x02a00093u, 0x10102023u, 0x00100073u);
        FlatMemory mem1 = MakeMem(prog);
        var handle1 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem1);
        handle1.Run(1_000);

        // mem1[256] should now contain 42 (written by sw).
        Assert.Equal(42uL, mem1.Read(256, 4));

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, handle1.ArchState!, mem1, 0UL);

        var mem2 = new FlatMemory(mem1.SizeBytes, mem1.BaseAddress);
        var handle2 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint.Load(ms).RestoreInto(handle2.ArchState!, mem2);

        // mem2 must have the same contents as mem1 after restore.
        Assert.Equal(42uL, mem2.Read(256, 4));
    }

    // ── File-based save/load ─────────────────────────────────────────────────

    [Fact]
    public void FileSaveLoad_PcMatches() {
        FlatMemory mem = MakeMem(SimpleProgram);
        var handle = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem);
        handle.Run(1_000);

        string path = Path.GetTempFileName();
        try {
            ArchitecturalCheckpoint.Save(path, handle.ArchState!, mem, 99UL);
            ArchitecturalCheckpoint chk = ArchitecturalCheckpoint.Load(path);
            Assert.Equal(handle.ArchState!.Pc, chk.Pc);
            Assert.Equal(99UL, chk.Tick);
        } finally {
            File.Delete(path);
        }
    }

    // ── Cross-pipeline checkpoint handoff ────────────────────────────────────

    [Fact]
    public void FastForwardThenDetailed_SameFinalMemory() {
        // addi x1, x0, 1  →  addi x1, x1, 1  (×10 effectively via loop body)
        // sw x1, 256(x0)  →  ebreak
        // Simple linear sequence without loop — just run and verify memory.
        byte[] prog = Encode(
            0x00100093u, // addi x1, x0, 1
            0x00108093u, // addi x1, x1, 1
            0x00108093u, // addi x1, x1, 1
            0x10102023u, // sw x1, 256(x0)
            0x00100073u  // ebreak
        );

        // Fast-forward with single-cycle, save.
        FlatMemory mem1 = MakeMem(prog);
        var h1 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem1);
        h1.Run(1_000);
        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, h1.ArchState!, mem1, 0UL);

        // Detailed run with OoO from the checkpoint.
        FlatMemory mem2 = new FlatMemory(mem1.SizeBytes, mem1.BaseAddress);
        var h2 = new MachineSpec(new OutOfOrderSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint.Load(ms).RestoreInto(h2.ArchState!, mem2);

        // mem2[256] should already be 3 (restored from single-cycle run).
        Assert.Equal(3uL, mem2.Read(256, 4));
    }

    // ── Error cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Load_InvalidMagic_Throws() {
        using var ms = new MemoryStream([0x01, 0x02, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00]);
        Assert.Throws<CheckpointException>(() => ArchitecturalCheckpoint.Load(ms));
    }

    [Fact]
    public void RestoreInto_WrongMemorySize_Throws() {
        FlatMemory mem1 = MakeMem(SimpleProgram);
        var h1 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem1);
        h1.Run(1_000);

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, h1.ArchState!, mem1, 0UL);

        // mem2 has a different size — should throw.
        var mem2 = new FlatMemory(0x2000);
        var h2 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        var chk = ArchitecturalCheckpoint.Load(ms);
        Assert.Throws<CheckpointException>(() => chk.RestoreInto(h2.ArchState!, mem2));
    }
}
