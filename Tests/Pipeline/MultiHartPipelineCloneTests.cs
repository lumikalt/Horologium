#region

using Mechanism;
using Orrery.Cache;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.Syscalls;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     <see cref="MultiHartPipeline" />'s dynamic hart activation: a real <c>clone()</c> call from a
///     detailed-pipeline hart (<see cref="FiveStageTrain" />) builds and appends a brand-new train via
///     the caller-supplied <c>spawnTrainFactory</c>, rather than assigning into a pre-allocated dormant
///     slot the way <c>MultiHartKernel</c> does — a detailed train owns its own pipeline latches and
///     fetch-address state, seeded once at construction, so there is no slot to reuse.
///     <para>
///         Same register-argument encoding as <c>CloneTests</c> (<c>MultiHartKernel</c>'s own clone()
///         test) — empirically confirmed against real compiled musl <c>__clone</c> there — but with two
///         NOPs inserted before each <c>ecall</c> (both the clone() itself and the child's gettid()),
///         unlike that test's back-to-back <c>addi a7, N; ecall</c>. <c>FiveStageTrain</c>'s ecall
///         dispatch reads the syscall number straight from architectural state
///         (<c>Rv32Executor</c>'s <c>state.IntegerRegisters.Read(17)</c>) rather than through the
///         pipeline's normal decoded-operand hazard/forwarding path — ecall has no decoded source
///         register for a7 to trigger a stall on — so an immediately-preceding write to a7 hasn't
///         retired yet when a back-to-back ecall reaches EX, reading a stale value instead. Confirmed
///         with an isolated repro (plain <c>FiveStageTrain</c>, <c>addi a7,220; ecall</c> with nothing
///         between them reads syscall number 0, not 220; two intervening NOPs fix it) — a real,
///         pre-existing gap, tracked in <c>TODO.md</c> rather than fixed here (orthogonal to hart
///         activation, and the right fix touches core executor/hazard code with a blast radius across
///         every train, not something to take on mid-feature). The padding here works around it in this
///         test the same way real compiled syscall sequences usually do incidentally (a0-a5 argument
///         setup between the last register write and ecall provides the same slack) — it isolates the
///         actual variable this test cares about (the driver) from that separate, already-tracked gap.
///     </para>
/// </summary>
public class MultiHartPipelineCloneTests {
    private const ulong ParentResultAddr = 0x100;
    private const ulong PtidAddr = 0x104;
    private const ulong ChildTpAddr = 0x108;
    private const ulong ChildSpAddr = 0x10C;
    private const ulong ChildMarkerAddr = 0x110;
    private const ulong ChildGettidAddr = 0x114;
    private const uint Ecall = 0x0000_0073;
    private const uint Ebreak = 0x0010_0073;
    private const uint Nop = 0x0000_0013; // addi x0, x0, 0

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Lui(int rd, int imm20) => (uint)(((imm20 & 0xFFFFF) << 12) | (rd << 7) | 0b0110111);

    private static uint Sw(int rs2, int rs1, int imm) {
        var immU = (uint)imm & 0xFFF;
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

    private static void Load(FlatMemory mem, params uint[] words) => LoadAt(mem, 0, words);

    private static void LoadAt(FlatMemory mem, ulong addr, params uint[] words) {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) {
            bytes[i * 4 + 0] = (byte)words[i];
            bytes[i * 4 + 1] = (byte)(words[i] >> 8);
            bytes[i * 4 + 2] = (byte)(words[i] >> 16);
            bytes[i * 4 + 3] = (byte)(words[i] >> 24);
        }

        mem.Load(addr, bytes);
    }

    private static FlatMemory BuildProgram() {
        var mem = new FlatMemory(0x400);
        const int cloneSetTls = 0x0008_0000;
        const int cloneParentSetTid = 0x0010_0000;
        Load(
            mem,
            Lui(10, (cloneSetTls | cloneParentSetTid) >> 12), // 0x00: a0 = flags
            Addi(11, 0, 0x300),                               // 0x04: a1 = newsp
            Addi(12, 0, (int)PtidAddr),                       // 0x08: a2 = ptid
            Lui(13, 0x123),                                   // 0x0C: a3 = tls
            Addi(14, 0, 0),                                   // 0x10: a4 = ctid (unused)
            Addi(17, 0, 220),                                 // 0x14: a7 = SYS_clone
            Nop,                                              // 0x18
            Nop,                                              // 0x1C
            Ecall,                                            // 0x20
            Bne(10, 0, 0x34),                                 // 0x24: parent (a0!=0) skips ahead to 0x58
            Addi(5, 4, 0),                                    // 0x28: child: t0 = tp
            Sw(5, 0, (int)ChildTpAddr),                       // 0x2C
            Addi(6, 2, 0),                                    // 0x30: child: t1 = sp
            Sw(6, 0, (int)ChildSpAddr),                       // 0x34
            Addi(7, 0, 777),                                  // 0x38: child: marker
            Sw(7, 0, (int)ChildMarkerAddr),                   // 0x3C
            Addi(17, 0, 178),                                 // 0x40: child: a7 = SYS_gettid
            Nop,                                              // 0x44
            Nop,                                              // 0x48
            Ecall,                                            // 0x4C
            Sw(10, 0, (int)ChildGettidAddr),                  // 0x50: child: store gettid()'s return
            Ebreak,                                           // 0x54: child halts
            Sw(10, 0, (int)ParentResultAddr),                 // 0x58: parent: store clone()'s return
            Ebreak                                            // 0x5C: parent halts
        );
        return mem;
    }

    [Fact]
    public void Clone_BuildsAndAppendsABrandNewDetailedTrain_WithCorrectSpAndTp() {
        FlatMemory mem = BuildProgram();
        var handler = new LinuxSyscallEmulator(0x400);
        var mech0 = new Rv32Mechanism(syscallHandler: handler, hartId: 0);
        var train0 = new FiveStageTrain(mech0, mem, entryPoint: 0x00);

        // spawnTrainFactory mirrors the "read Pc before construction, thread it through as the
        // constructor's own entryPoint" pattern every checkpoint-restore path in this codebase uses —
        // FiveStageTrain's fetch-address state is seeded once at construction and never re-read from
        // IArchState afterward. hartId 1 must match the slot this train lands in (index 1, the second
        // train appended) for a later gettid() to agree with clone()'s own returned tid, exactly the
        // same invariant CloneTests documents for MultiHartKernel.
        ISteppableTrain SpawnTrainFactory(IArchState initialState) =>
            new FiveStageTrain(new Rv32Mechanism(syscallHandler: handler, hartId: 1), mem, entryPoint: initialState.Pc);

        var pipeline = new MultiHartPipeline(SpawnTrainFactory, train0);
        handler.Spawner = pipeline;

        Assert.Equal(1, pipeline.HartCount);

        pipeline.Run(200);

        Assert.Equal(2, pipeline.HartCount); // clone() actually appended a second, real train
        Assert.Equal(2UL, mem.Read(ParentResultAddr, 4)); // parent saw the new hart's tid (slot 1 -> tid 2)
        Assert.Equal(2UL, mem.Read(PtidAddr, 4));         // CLONE_PARENT_SETTID wrote the same tid
        Assert.Equal(0x1230_00UL, mem.Read(ChildTpAddr, 4)); // tls (a3) became the child's tp
        Assert.Equal(0x300UL, mem.Read(ChildSpAddr, 4));     // newsp (a1) became the child's sp
        Assert.Equal(777UL, mem.Read(ChildMarkerAddr, 4));   // child actually executed on its own train
        // Decisive consistency check: the child's own gettid() must reproduce the exact tid clone()
        // handed the parent — the same cross-check CloneTests uses for MultiHartKernel, here proving
        // it holds for a genuinely constructed detailed-pipeline train too, not just an IArchState.
        Assert.Equal(2UL, mem.Read(ChildGettidAddr, 4));
    }

    [Fact]
    public void Clone_WithNoSpawnFactoryConfigured_ThrowsRatherThanSilentlyIgnoringTheCall() {
        FlatMemory mem = BuildProgram();
        var handler = new LinuxSyscallEmulator(0x400);
        var mech0 = new Rv32Mechanism(syscallHandler: handler, hartId: 0);
        var train0 = new FiveStageTrain(mech0, mem, entryPoint: 0x00);
        var pipeline = new MultiHartPipeline(train0); // no spawnTrainFactory
        handler.Spawner = pipeline;

        Assert.Throws<InvalidOperationException>(() => pipeline.Run(200));
    }

    /// <summary>
    ///     <see cref="MultiHartPipeline.SpawnHart" /> must refuse to append to the shared hart list while
    ///     <see cref="MultiHartPipeline.RunConcurrent" /> has real host threads concurrently reading it via
    ///     <c>Parallel.For</c> — a live clone() call mid-<c>RunConcurrent</c> would otherwise race a list
    ///     mutation against those reads. Because the offending <c>SpawnHart</c> call happens on one of
    ///     <c>Parallel.For</c>'s own worker threads (hart 0's own <c>StepCycle</c>), the
    ///     <see cref="InvalidOperationException" /> it throws surfaces wrapped in an
    ///     <see cref="AggregateException" />, not as a bare <see cref="InvalidOperationException" /> the way
    ///     <see cref="Clone_WithNoSpawnFactoryConfigured_ThrowsRatherThanSilentlyIgnoringTheCall" />'s
    ///     sequential-<see cref="MultiHartPipeline.Run" /> case does — asserted explicitly here so that
    ///     distinction is documented by a test, not just prose.
    /// </summary>
    [Fact]
    public void Clone_DuringRunConcurrent_ThrowsRatherThanRacingTheHartList() {
        FlatMemory mem = BuildProgram();
        var handler = new LinuxSyscallEmulator(0x400);
        var mech0 = new Rv32Mechanism(syscallHandler: handler, hartId: 0);

        LoadAt(mem, 0x200, Ebreak); // hart 1: a plain, unrelated program that just halts immediately

        var bus = new MoesifBus(mem);
        var def0 = new DeferredBus(bus);
        var def1 = new DeferredBus(bus);
        var train0 = new FiveStageTrain(mech0, new MoesifCache(def0, 256, 2, 64), 0x00);
        var train1 = new FiveStageTrain(new Rv32Mechanism(), new MoesifCache(def1, 256, 2, 64), 0x200);

        ISteppableTrain SpawnTrainFactory(IArchState initialState) =>
            new FiveStageTrain(new Rv32Mechanism(syscallHandler: handler, hartId: 1), mem, entryPoint: initialState.Pc);

        var pipeline = new MultiHartPipeline(SpawnTrainFactory, train0, train1);
        handler.Spawner = pipeline;

        var ex = Assert.Throws<AggregateException>(() => pipeline.RunConcurrent([def0, def1,], 200));
        Assert.IsType<InvalidOperationException>(ex.InnerExceptions.Single());
        Assert.Equal(2, pipeline.HartCount); // the spawn never actually landed
    }
}
