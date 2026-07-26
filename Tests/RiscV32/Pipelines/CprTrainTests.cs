#region

using Orrery.Cache;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.RiscV32.Pipelines;

/// <summary>
///     Pipeline-level tests for <see cref="CprTrain" /> — Checkpoint Processing and Recovery
///     (Akkary, Rajwar &amp; Srinivasan, MICRO 2003) with optional Continual Flow Pipelines
///     (Srinivasan et al., ASPLOS 2004). The train is a timing model over the same ISA semantics
///     as <see cref="OooTrain" />, so most tests assert final architectural state equality
///     against an <see cref="OooTrain" /> reference run plus CPR-specific counters.
/// </summary>
public class CprTrainTests {
    private static (CprTrain train, FlatMemory mem) Make(
        bool enableCfp = false,
        int issueWidth = 2,
        int iqCapacity = 8,
        int extraPhysRegs = 32,
        int checkpointCount = 8,
        int checkpointMaxInstructions = 256,
        MemoryConfig? dMemConfig = null,
        int memSize = 4096
    ) {
        var mem = new FlatMemory(memSize);
        var train = new CprTrain(
            new Rv32Mechanism(), mem,
            issueWidth: issueWidth,
            iqCapacity: iqCapacity,
            extraPhysRegs: extraPhysRegs,
            checkpointCount: checkpointCount,
            checkpointMaxInstructions: checkpointMaxInstructions,
            dMemConfig: dMemConfig,
            enableCfp: enableCfp
        );
        return (train, mem);
    }

    private static (OooTrain train, FlatMemory mem) MakeReference(
        MemoryConfig? dMemConfig = null,
        int memSize = 4096
    ) {
        var mem = new FlatMemory(memSize);
        var train = new OooTrain(new Rv32Mechanism(), mem, dMemConfig: dMemConfig);
        return (train, mem);
    }

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

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("cpr.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    private static void AssertIdenticalIntState(CprTrain cpr, OooTrain reference) {
        for (var r = 0; r < 32; r++)
            Assert.Equal(reference.ArchState.IntegerRegisters.Read(r), cpr.ArchState.IntegerRegisters.Read(r));
    }

    /// <summary>
    ///     Straight-line dependent/independent arithmetic retires correctly through bulk commit.
    ///     Assembled from:
    ///     <c>
    ///         addi x1,x0,5; addi x2,x1,3; add x3,x1,x2; mul x4,x2,x3; addi
    ///         x5,x0,9; addi x6,x5,-2; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void StraightLine_BulkCommits_MatchesOooeTrain() {
        uint[] program = [
            0x00500093, // addi x1, x0, 5
            0x00308113, // addi x2, x1, 3
            0x002081B3, // add  x3, x1, x2
            0x02310233, // mul  x4, x2, x3
            0x00900293, // addi x5, x0, 9
            0xFFE28313, // addi x6, x5, -2
            0x00100073, // ebreak
        ];

        (CprTrain cpr, FlatMemory memCpr) = Make();
        (OooTrain reference, FlatMemory memRef) = MakeReference();
        Load(memCpr, program);
        Load(memRef, program);

        RevolutionResult cprResult = cpr.Run(100_000);
        reference.Run(100_000);

        AssertIdenticalIntState(cpr, reference);
        Assert.Equal(5UL, cpr.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(8UL, cpr.ArchState.IntegerRegisters.Read(2));
        Assert.Equal(13UL, cpr.ArchState.IntegerRegisters.Read(3));
        Assert.Equal(104UL, cpr.ArchState.IntegerRegisters.Read(4));
        Assert.True(Counter(cprResult, "checkpoints_created") > 0);
        Assert.True(Counter(cprResult, "retired") >= program.Length);
    }

    /// <summary>
    ///     A countdown loop whose backward branch the default AlwaysNotTaken predictor mispredicts
    ///     on every taken iteration: each misprediction recovers through a checkpoint restore, not
    ///     a ROB walk. Assembled from:
    ///     <c>
    ///         addi x1,x0,5; loop: addi x2,x2,1; addi x1,x1,-1; bne
    ///         x1,x0,loop; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void MispredictedLoop_RecoversThroughCheckpoints() {
        uint[] program = [
            0x00500093, // addi x1, x0, 5
            0x00110113, // addi x2, x2, 1
            0xFFF08093, // addi x1, x1, -1
            0xFE009CE3, // bne  x1, x0, -8
            0x00100073, // ebreak
        ];

        (CprTrain cpr, FlatMemory memCpr) = Make();
        (OooTrain reference, FlatMemory memRef) = MakeReference();
        Load(memCpr, program);
        Load(memRef, program);

        RevolutionResult cprResult = cpr.Run(100_000);
        reference.Run(100_000);

        AssertIdenticalIntState(cpr, reference);
        Assert.Equal(5UL, cpr.ArchState.IntegerRegisters.Read(2));
        Assert.Equal(0UL, cpr.ArchState.IntegerRegisters.Read(1));
        Assert.True(Counter(cprResult, "recoveries") >= 4, "taken-branch mispredictions must recover via checkpoints");
        Assert.True(Counter(cprResult, "branch_misses") >= 4);
    }

    /// <summary>
    ///     Store-to-load forwarding through the hierarchical store queue, including the byte-merge
    ///     path: a word store overlaid by a younger byte store, read back by a halfword load while
    ///     both stores are still in flight. Assembled from:
    ///     <c>
    ///         addi x1,x0,128; addi x2,x0,42; sw
    ///         x2,0(x1); addi x4,x0,17; sb x4,1(x1); lh x5,0(x1); ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void HierarchicalStoreQueue_MergedForwarding() {
        uint[] program = [
            0x08000093, // addi x1, x0, 128
            0x02A00113, // addi x2, x0, 42
            0x0020A023, // sw   x2, 0(x1)
            0x01100213, // addi x4, x0, 17
            0x004080A3, // sb   x4, 1(x1)
            0x00009283, // lh   x5, 0(x1)
            0x00100073, // ebreak
        ];

        (CprTrain cpr, FlatMemory mem) = Make();
        Load(mem, program);
        cpr.Run(100_000);

        // 0x2A from the word store in byte 0, 0x11 from the younger byte store in byte 1.
        Assert.Equal(0x112AUL, cpr.ArchState.IntegerRegisters.Read(5));
        Assert.Equal(42UL, (byte)mem.Read(128, 1));
        Assert.Equal(17UL, (byte)mem.Read(129, 1));
    }

    /// <summary>
    ///     Memory-order violation: a load issues speculatively past an older store whose address
    ///     hangs on a multiply chain; when the store resolves to the same address, the load's
    ///     checkpoint rolls back and re-executes (MICRO 2003 §4.2.4). Assembled from:
    ///     <c>
    ///         addi x6,x0,10; mul x7,x6,x6; mul x7,x7,x6; addi x2,x0,42; sw x2,0(x7); addi
    ///         x5,x0,1000; lw x4,0(x5); ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void SpeculativeLoad_ViolatingStore_RecoversToCheckpoint() {
        uint[] program = [
            0x00A00313, // addi x6, x0, 10
            0x026303B3, // mul  x7, x6, x6      (100)
            0x026383B3, // mul  x7, x7, x6      (1000)
            0x02A00113, // addi x2, x0, 42
            0x0023A023, // sw   x2, 0(x7)
            0x3E800293, // addi x5, x0, 1000
            0x0002A203, // lw   x4, 0(x5)
            0x00100073, // ebreak
        ];

        (CprTrain cpr, FlatMemory mem) = Make();
        Load(mem, program);
        RevolutionResult result = cpr.Run(100_000);

        Assert.Equal(42UL, cpr.ArchState.IntegerRegisters.Read(4));
        Assert.True(Counter(result, "mem_order_violations") >= 1, "the early load must be caught and rolled back");
        Assert.True(Counter(result, "recoveries") >= 1);
    }

    /// <summary>
    ///     CFP slice-out: a cache-cold load (L2-class miss under the configured D-cache) and its
    ///     dependent drain into the Slice Data Buffer, re-enter via back-end renaming when the
    ///     miss returns, and the final state matches a CFP-off run exactly. Assembled from:
    ///     <c>
    ///         addi x1,x0,400; lw x2,0(x1); addi x3,x2,1; addi x4,x0,2; addi x5,x0,3; addi
    ///         x6,x0,4; addi x7,x0,5; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void Cfp_MissSliceDrainsAndReinserts_StateMatchesCfpOff() {
        uint[] program = [
            0x19000093, // addi x1, x0, 400
            0x0000A103, // lw   x2, 0(x1)
            0x00110193, // addi x3, x2, 1
            0x00200213, // addi x4, x0, 2
            0x00300293, // addi x5, x0, 3
            0x00400313, // addi x6, x0, 4
            0x00500393, // addi x7, x0, 5
            0x00100073, // ebreak
        ];

        (CprTrain off, FlatMemory memOff) = Make(dMemConfig: new MemoryConfig(1024));
        (CprTrain on, FlatMemory memOn) = Make(true, dMemConfig: new MemoryConfig(1024));
        Load(memOff, program);
        Load(memOn, program);

        RevolutionResult offResult = off.Run(100_000);
        RevolutionResult onResult = on.Run(100_000);

        for (var r = 0; r < 32; r++)
            Assert.Equal(off.ArchState.IntegerRegisters.Read(r), on.ArchState.IntegerRegisters.Read(r));
        Assert.Equal(1UL, on.ArchState.IntegerRegisters.Read(3)); // x3 = x2(0) + 1 through the slice
        Assert.Equal(0L, Counter(offResult, "cfp_slice_instructions"));
        Assert.True(Counter(onResult, "cfp_slice_instructions") >= 2, "the miss load and its dependent must drain");
        Assert.True(Counter(onResult, "cfp_reinsertions") >= 2, "the slice must re-enter the pipeline");
    }

    /// <summary>
    ///     A branch whose condition depends on miss data joins the slice; its misprediction can
    ///     only be discovered after the slice re-inserts and the branch finally executes — the
    ///     late-resolution path (ASPLOS 2004 §5.2: only miss-dependent mispredicted branches
    ///     disrupt the window). Memory at 400 holds 7, so <c>bne x2,x0,+8</c> is taken while
    ///     AlwaysNotTaken predicted fall-through. Assembled from:
    ///     <c>
    ///         addi x1,x0,400; lw x2,0(x1);
    ///         bne x2,x0,+8; addi x3,x0,1; addi x4,x0,2; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void Cfp_MissDependentBranch_ResolvesLate_AndRecovers() {
        uint[] program = [
            0x19000093, // addi x1, x0, 400
            0x0000A103, // lw   x2, 0(x1)
            0x00011463, // bne  x2, x0, +8   (skips the addi x3)
            0x00100193, // addi x3, x0, 1    (skipped on the taken path)
            0x00200213, // addi x4, x0, 2
            0x00100073, // ebreak
        ];

        (CprTrain on, FlatMemory mem) = Make(true, dMemConfig: new MemoryConfig(1024));
        Load(mem, program);
        mem.Load(400, [7, 0, 0, 0,]);
        RevolutionResult result = on.Run(100_000);

        Assert.Equal(7UL, on.ArchState.IntegerRegisters.Read(2));
        Assert.Equal(0UL, on.ArchState.IntegerRegisters.Read(3)); // the taken branch skipped it
        Assert.Equal(2UL, on.ArchState.IntegerRegisters.Read(4));
        Assert.True(Counter(result, "cfp_slice_instructions") >= 2, "load + dependent branch must drain");
        Assert.True(
            Counter(result, "recoveries") >= 1, "the slice branch's mispredict must recover after re-insertion"
        );
    }

    /// <summary>
    ///     Repeated cold-miss loads (stride 64 = one miss per iteration) interleaved with
    ///     mispredicted loop branches: recoveries fire while slices are parked in the SDB, and
    ///     the sum must still come out exact. Assembled from:
    ///     <c>
    ///         addi x1,x0,1024; addi x5,x0,8;
    ///         loop: lw x2,0(x1); add x6,x6,x2; addi x1,x1,64; addi x5,x5,-1; bne x5,x0,loop;
    ///         ebreak
    ///     </c>
    ///     with mem[1024 + 64i] = i+1.
    /// </summary>
    [Fact]
    public void Cfp_MissLoopWithMispredicts_SumExact() {
        uint[] program = [
            0x40000093, // addi x1, x0, 1024
            0x00800293, // addi x5, x0, 8
            0x0000A103, // lw   x2, 0(x1)
            0x00230333, // add  x6, x6, x2
            0x04008093, // addi x1, x1, 64
            0xFFF28293, // addi x5, x5, -1
            0xFE0298E3, // bne  x5, x0, -16
            0x00100073, // ebreak
        ];

        (CprTrain off, FlatMemory memOff) = Make(dMemConfig: new MemoryConfig(1024));
        (CprTrain on, FlatMemory memOn) = Make(true, dMemConfig: new MemoryConfig(1024));
        foreach (FlatMemory m in new[] { memOff, memOn, }) {
            Load(m, program);
            for (var i = 0; i < 8; i++) m.Load((ulong)(1024 + 64 * i), [(byte)(i + 1), 0, 0, 0,]);
        }

        off.Run(200_000);
        RevolutionResult onResult = on.Run(200_000);

        Assert.Equal(36UL, off.ArchState.IntegerRegisters.Read(6));
        Assert.Equal(36UL, on.ArchState.IntegerRegisters.Read(6));
        Assert.True(on.CurrentTick < 200_000, "the CFP run must halt, not wedge");
        Assert.True(Counter(onResult, "cfp_slice_instructions") > 0);
        Assert.True(Counter(onResult, "recoveries") >= 1);
    }

    /// <summary>Unconditional jump-to-self halts the train (bare-metal terminator).</summary>
    [Fact]
    public void JumpToSelf_HaltsAtCommit() {
        uint[] program = [
            0x00700093, // addi x1, x0, 7
            0x0000006F, // jal  x0, 0 (self-loop)
        ];

        (CprTrain cpr, FlatMemory mem) = Make();
        Load(mem, program);
        cpr.Run(10_000);

        Assert.Equal(7UL, cpr.ArchState.IntegerRegisters.Read(1));
        Assert.True(cpr.CurrentTick < 10_000, "the self-jump must halt the train, not spin to maxTicks");
    }

    /// <summary>
    ///     Aggressive register reclamation: a long dependent rename chain with only 4 extra
    ///     physical registers completes because registers free as soon as they are overwritten
    ///     and fully read — long before their checkpoint retires. A use-counter leak anywhere
    ///     starves the free list and wedges rename forever.
    /// </summary>
    [Fact]
    public void TinyRegisterFile_ReclamationSustainsRenameChain() {
        var program = new uint[26];
        program[0] = 0x00100093;                              // addi x1, x0, 1
        for (var i = 1; i < 24; i++) program[i] = 0x00108093; // addi x1, x1, 1
        program[24] = 0x00108113;                             // addi x2, x1, 1
        program[25] = 0x00100073;                             // ebreak

        (CprTrain cpr, FlatMemory mem) = Make(extraPhysRegs: 4);
        Load(mem, program);
        cpr.Run(50_000);

        Assert.Equal(24UL, cpr.ArchState.IntegerRegisters.Read(1));
        Assert.Equal(25UL, cpr.ArchState.IntegerRegisters.Read(2));
        Assert.True(cpr.CurrentTick < 50_000, "free-list starvation: use-counter reclamation is leaking references");
    }

    /// <summary>
    ///     Serialized CSR ordering: the second CSR instruction must observe the first one's
    ///     commit-time side effect — both are forced into their own checkpoints and issue only
    ///     when architecturally oldest. Assembled from:
    ///     <c>
    ///         addi x5,x0,99; csrrw
    ///         x0,mscratch,x5; csrrs x6,mscratch,x0; ebreak
    ///     </c>
    ///     .
    /// </summary>
    [Fact]
    public void SerializedCsr_ObservesOlderCsrWrite() {
        uint[] program = [
            0x06300293, // addi  x5, x0, 99
            0x34029073, // csrrw x0, mscratch, x5
            0x34002373, // csrrs x6, mscratch, x0
            0x00100073, // ebreak
        ];

        (CprTrain cpr, FlatMemory mem) = Make();
        Load(mem, program);
        cpr.Run(100_000);

        Assert.Equal(99UL, cpr.ArchState.IntegerRegisters.Read(6));
    }

    /// <summary>
    ///     A branchy loop with a small checkpoint budget and small per-checkpoint instruction cap
    ///     still executes correctly — checkpoints recycle through the FIFO as they bulk-commit.
    /// </summary>
    [Fact]
    public void SmallCheckpointBudget_LoopStillCorrect() {
        uint[] program = [
            0x00A00093, // addi x1, x0, 10
            0x00110113, // addi x2, x2, 1
            0xFFF08093, // addi x1, x1, -1
            0xFE009CE3, // bne  x1, x0, -8
            0x00100073, // ebreak
        ];

        (CprTrain cpr, FlatMemory mem) = Make(checkpointCount: 2, checkpointMaxInstructions: 4);
        Load(mem, program);
        RevolutionResult result = cpr.Run(100_000);

        Assert.Equal(10UL, cpr.ArchState.IntegerRegisters.Read(2));
        Assert.True(cpr.CurrentTick < 100_000);
        Assert.True(Counter(result, "checkpoints_retired") > 2);
    }

    /// <summary>
    ///     Registers written via <c>ArchState.IntegerRegisters</c> between construction and
    ///     <c>Run()</c> — the same thing a checkpoint restore does — must be visible to execution.
    ///     The PRF starts zeroed at construction; without seeding it from
    ///     <c>State.IntegerRegisters</c> at <c>Wind()</c> (mirroring the identical fix already made
    ///     for <see cref="OooTrain" /> in commit 832f1ab), a store sourcing a pre-set register would
    ///     silently read 0 instead.
    /// </summary>
    [Fact]
    public void PreRunRegisterWrite_IsVisibleToExecution() {
        uint[] program = [
            0x0021A023, // sw   x2, 0(x3)
            0x00100073, // ebreak
        ];

        (CprTrain cpr, FlatMemory mem) = Make();
        Load(mem, program);
        cpr.ArchState.IntegerRegisters.Write(2, 42);
        cpr.ArchState.IntegerRegisters.Write(3, 200);

        cpr.Run(100_000);

        Assert.Equal(42UL, mem.Read(200, 4));
    }
}