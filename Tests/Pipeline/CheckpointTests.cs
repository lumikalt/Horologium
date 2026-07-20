using Mechanism;
using Orrery.Train;
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
        FlatMemory mem1 = MakeMem(CheckpointTests.SimpleProgram);
        MachineHandle handle1 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem1);
        handle1.Run(1_000);

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, handle1.ArchState!, mem1, 100UL);

        FlatMemory mem2 = MakeMem(CheckpointTests.SimpleProgram);
        MachineHandle handle2 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint.Load(ms).RestoreInto(handle2.ArchState!, mem2);

        Assert.Equal(handle1.ArchState!.Pc, handle2.ArchState!.Pc);
        IRegisterFile r1 = handle1.ArchState.IntegerRegisters;
        IRegisterFile r2 = handle2.ArchState.IntegerRegisters;
        for (var i = 0; i < r1.Count; i++) Assert.Equal(r1.Read(i), r2.Read(i));
    }

    [Fact]
    public void FiveStage_SaveRestore_PcAndRegsMatch() {
        FlatMemory mem1 = MakeMem(CheckpointTests.SimpleProgram);
        MachineHandle handle1 = new MachineSpec(new FiveStageSpec(), () => new Rv32Mechanism()).Build(mem1);
        handle1.Run(10_000);

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, handle1.ArchState!, mem1, 0UL);

        FlatMemory mem2 = MakeMem(CheckpointTests.SimpleProgram);
        MachineHandle handle2 = new MachineSpec(new FiveStageSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint.Load(ms).RestoreInto(handle2.ArchState!, mem2);

        Assert.Equal(handle1.ArchState!.Pc, handle2.ArchState!.Pc);
        IRegisterFile r1 = handle1.ArchState.IntegerRegisters;
        IRegisterFile r2 = handle2.ArchState.IntegerRegisters;
        for (var i = 0; i < r1.Count; i++) Assert.Equal(r1.Read(i), r2.Read(i));
    }

    [Fact]
    public void OutOfOrder_SaveRestore_PcAndRegsMatch() {
        FlatMemory mem1 = MakeMem(CheckpointTests.SimpleProgram);
        MachineHandle handle1 = new MachineSpec(new OutOfOrderSpec(), () => new Rv32Mechanism()).Build(mem1);
        handle1.Run(10_000);

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, handle1.ArchState!, mem1, 0UL);

        FlatMemory mem2 = MakeMem(CheckpointTests.SimpleProgram);
        MachineHandle handle2 = new MachineSpec(new OutOfOrderSpec(), () => new Rv32Mechanism()).Build(mem2);
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
        FlatMemory mem1 = MakeMem(CheckpointTests.SimpleProgram);
        MachineHandle handle1 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem1);
        handle1.Run(1_000);
        IArchState s1 = handle1.ArchState!;

        // Read a few CSRs from the live state.
        ulong mcycle = s1.SystemRegisters.Read(0xB00, s1.PrivilegeLevel);  // mcycle
        ulong mstatus = s1.SystemRegisters.Read(0x300, s1.PrivilegeLevel); // mstatus

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, s1, mem1, 0UL);

        FlatMemory mem2 = MakeMem(CheckpointTests.SimpleProgram);
        MachineHandle handle2 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint.Load(ms).RestoreInto(handle2.ArchState!, mem2);

        IArchState s2 = handle2.ArchState!;
        Assert.Equal(mcycle, s2.SystemRegisters.Read(0xB00, s2.PrivilegeLevel));
        Assert.Equal(mstatus, s2.SystemRegisters.Read(0x300, s2.PrivilegeLevel));
    }

    // ── Memory round-trip ────────────────────────────────────────────────────

    [Fact]
    public void Memory_SurvivesRoundTrip() {
        // Program: addi x1, x0, 42  →  sw x1, 256(x0)  →  ebreak
        byte[] prog = Encode(0x02a00093u, 0x10102023u, 0x00100073u);
        FlatMemory mem1 = MakeMem(prog);
        MachineHandle handle1 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem1);
        handle1.Run(1_000);

        // mem1[256] should now contain 42 (written by sw).
        Assert.Equal(42uL, mem1.Read(256, 4));

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, handle1.ArchState!, mem1, 0UL);

        var mem2 = new FlatMemory(mem1.SizeBytes, mem1.BaseAddress);
        MachineHandle handle2 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint.Load(ms).RestoreInto(handle2.ArchState!, mem2);

        // mem2 must have the same contents as mem1 after restore.
        Assert.Equal(42uL, mem2.Read(256, 4));
    }

    // ── File-based save/load ─────────────────────────────────────────────────

    [Fact]
    public void FileSaveLoad_PcMatches() {
        FlatMemory mem = MakeMem(CheckpointTests.SimpleProgram);
        MachineHandle handle = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem);
        handle.Run(1_000);

        string path = Path.GetTempFileName();
        try {
            ArchitecturalCheckpoint.Save(path, handle.ArchState!, mem, 99UL);
            ArchitecturalCheckpoint chk = ArchitecturalCheckpoint.Load(path);
            Assert.Equal(handle.ArchState!.Pc, chk.Pc);
            Assert.Equal(99UL, chk.Tick);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SaveAsync_WritesCompleteFile_AfterAwait() {
        FlatMemory mem = MakeMem(CheckpointTests.SimpleProgram);
        MachineHandle handle = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem);
        handle.Run(1_000);

        string path = Path.GetTempFileName();
        try {
            await ArchitecturalCheckpoint.SaveAsync(path, handle.ArchState!, mem, 99UL);
            ArchitecturalCheckpoint chk = ArchitecturalCheckpoint.Load(path);
            Assert.Equal(handle.ArchState!.Pc, chk.Pc);
            Assert.Equal(99UL, chk.Tick);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SaveAsync_MutatingMemoryAfterCall_DoesNotAffectCheckpoint() {
        // The state copy must happen synchronously inside SaveAsync before it returns —
        // mutating memory afterward (while the background write is in flight) must not
        // be visible in the checkpoint.
        byte[] prog = Encode(0x02a00093u, 0x10102023u, 0x00100073u); // addi x1,x0,42; sw x1,256(x0); ebreak
        FlatMemory mem = MakeMem(prog);
        MachineHandle handle = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem);
        handle.Run(1_000);
        Assert.Equal(42uL, mem.Read(256, 4));

        string path = Path.GetTempFileName();
        try {
            Task save = ArchitecturalCheckpoint.SaveAsync(path, handle.ArchState!, mem, 0UL);
            mem.Write(256, 7, 4); // mutate after the call returns — must not leak into the checkpoint
            await save;

            ArchitecturalCheckpoint chk = ArchitecturalCheckpoint.Load(path);
            var mem2 = new FlatMemory(mem.SizeBytes, mem.BaseAddress);
            MachineHandle handle2 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem2);
            chk.RestoreInto(handle2.ArchState!, mem2);
            Assert.Equal(42uL, mem2.Read(256, 4));
        }
        finally { File.Delete(path); }
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
        MachineHandle h1 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem1);
        h1.Run(1_000);
        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, h1.ArchState!, mem1, 0UL);

        // Detailed run with OoO from the checkpoint.
        var mem2 = new FlatMemory(mem1.SizeBytes, mem1.BaseAddress);
        MachineHandle h2 = new MachineSpec(new OutOfOrderSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint.Load(ms).RestoreInto(h2.ArchState!, mem2);

        // mem2[256] should already be 3 (restored from single-cycle run).
        Assert.Equal(3uL, mem2.Read(256, 4));
    }

    /// <summary>
    ///     <see cref="ArchitecturalCheckpoint.RestoreInto" /> writes <c>ArchState.IntegerRegisters</c> — but OoOE execution
    ///     reads register operands from the physical register file (PRF), which starts zeroed at
    ///     construction and is otherwise only ever written by the pipeline itself at Complete. A
    ///     restore performed between construction and <c>Run()</c>/<c>BeginStepping()</c> would be
    ///     silently invisible to execution unless the PRF is re-seeded from ArchState at Wind()
    ///     time. The other OoO checkpoint tests above only assert on <c>ArchState</c> directly after
    ///     restore — they never run the restored train, so they cannot catch this.
    ///     <para>
    ///         This test does run the restored train, and uses a restored register (x1) as the
    ///         *address* operand of a store, not just the data operand — an earlier version of this
    ///         test used a restored data operand only (<c>sw x1, 256(x0)</c>) and it passed even
    ///         without the Wind() fix, because store data apparently resolves through a different
    ///         path than store address computation. Address computation goes through the same
    ///         PRF-read path that produced the real symptom this fix addresses (a garbage store
    ///         address computed from an unseeded stack-pointer register, crashing with
    ///         <c>AccessViolationException</c> on a real RV64 ELF run through Runner's ROI mode).
    ///     </para>
    /// </summary>
    [Fact]
    public void OutOfOrder_RestoreThenRun_UsesRestoredRegisterAsStoreAddress() {
        // addi x1, x0, 512  →  addi x2, x0, 99  →  sw x2, 0(x1)  →  ebreak
        byte[] prog = Encode(0x20000093u, 0x06300113u, 0x0020A023u, 0x00100073u);

        FlatMemory mem1 = MakeMem(prog);
        MachineHandle h1 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem1);
        h1.Train.BeginStepping();
        h1.Train.StepCycle(); // addi x1, x0, 512
        h1.Train.StepCycle(); // addi x2, x0, 99 — PC now points at the sw
        RevolutionResult r1 = h1.Train.FinishStepping();

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, h1.ArchState!, mem1, (ulong)r1.TotalTicks);
        ms.Position = 0;
        ArchitecturalCheckpoint chk = ArchitecturalCheckpoint.Load(ms);

        // Detailed OoO run from the checkpoint: fetch starts at the sw (chk.Pc), x1 = 512 and
        // x2 = 99 restored. entryPoint must be passed at Build() time — it seeds a separate
        // internal fetch-PC field that RestoreInto (which only writes ArchState.Pc) does not
        // touch — exactly how Runner's --checkpoint-load (spec.Build(memory, chk.Pc)) and
        // --roi-start (spec.Build(backing, roiStartPc, mmio)) already do it.
        var mem2 = new FlatMemory(mem1.SizeBytes, mem1.BaseAddress);
        MachineHandle h2 = new MachineSpec(new OutOfOrderSpec(), () => new Rv32Mechanism()).Build(mem2, chk.Pc);
        chk.RestoreInto(h2.ArchState!, mem2);

        h2.Run(1_000);

        // If x1 (the store's base address) were invisible to the OoO pipeline, this would
        // compute address 0 instead of 512 (the PRF's zeroed default), and mem2[512] would
        // remain unwritten.
        Assert.Equal(99uL, mem2.Read(512, 4));
    }

    // ── Error cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Load_InvalidMagic_Throws() {
        using var ms = new MemoryStream([0x01, 0x02, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00,]);
        Assert.Throws<CheckpointException>(() => ArchitecturalCheckpoint.Load(ms));
    }

    [Fact]
    public void RestoreInto_WrongMemorySize_Throws() {
        FlatMemory mem1 = MakeMem(CheckpointTests.SimpleProgram);
        MachineHandle h1 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem1);
        h1.Run(1_000);

        using var ms = new MemoryStream();
        ArchitecturalCheckpoint.Save(ms, h1.ArchState!, mem1, 0UL);

        // mem2 has a different size — should throw.
        var mem2 = new FlatMemory(0x2000);
        MachineHandle h2 = new MachineSpec(new SingleCycleSpec(), () => new Rv32Mechanism()).Build(mem2);
        ms.Position = 0;
        ArchitecturalCheckpoint chk = ArchitecturalCheckpoint.Load(ms);
        Assert.Throws<CheckpointException>(() => chk.RestoreInto(h2.ArchState!, mem2));
    }
}