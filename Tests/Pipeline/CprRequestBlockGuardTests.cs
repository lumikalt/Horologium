#region

using Mechanism;
using Orrery.Observation;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     <see cref="CprTrain" />'s <see cref="ExecuteResult.RequestBlock" /> support. Unlike
///     <see cref="FiveStageTrain" /> (trap-shaped fix) or <see cref="OooTrain" /> (load-violation-shaped
///     fix on a classical ROB), <c>CprTrain</c> has no ROB at all — instructions are tracked in
///     program-order <c>Checkpoint</c> epochs. Syscalls are always <c>IsSerialized</c>
///     (<c>ToothClass.System</c>), which forces a fresh checkpoint both before and after one, so a
///     still-blocked <c>ecall</c> is always the sole entry of its own checkpoint. Scheduling an ordinary
///     checkpoint recovery back to that same checkpoint (the same mechanism a memory-order violation
///     uses) therefore squashes exactly this instruction — and everyone younger, in later checkpoints —
///     and refetches precisely its own Pc. See <c>CprTrain.StepComplete</c>'s <c>RequestBlock</c> branch.
/// </summary>
public class CprRequestBlockGuardTests {
    private const uint Ecall = 0x0000_0073;
    private const uint AddiX1Plus1 = 0x0010_8093; // addi x1, x1, 1
    private const uint Ebreak = 0x0010_0073;

    private sealed class BlockThenClearHandler(int blockCount) : ISyscallHandler {
        public int CallCount { get; private set; }

        public ExecuteResult Handle(ulong syscallNum, IArchState state, IMemory memory, ulong pc, int hartId) {
            CallCount++;
            return new ExecuteResult { RequestBlock = CallCount <= blockCount, };
        }
    }

    private static long Counter(RevolutionResult result, string name) {
        DialBoardSnapshot? snap = result.Find("cpr.pipeline");
        Assert.NotNull(snap);
        return snap.Counters.GetValueOrDefault(name);
    }

    [Fact]
    public void BlockingEcall_RetriesInPlace_ThenProceedsNormallyOnceCleared() {
        var mem = new FlatMemory(0x100);
        var bytes = new byte[12];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), Ecall);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), AddiX1Plus1);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), Ebreak);
        mem.Load(0x00, bytes);

        var handler = new BlockThenClearHandler(3);
        var mechanism = new Rv32Mechanism(syscallHandler: handler);
        var train = new CprTrain(mechanism, mem);

        RevolutionResult result = train.Run(1000);

        Assert.True(train.IsIdle);
        // Executed (Execute → handler.Handle) four times — three blocked attempts plus the one
        // that finally cleared.
        Assert.Equal(4, handler.CallCount);
        // Each blocked attempt is its own checkpoint recovery (RequestBlock always squashes back
        // to its own sole-entry checkpoint), never a bulk commit.
        Assert.Equal(3, Counter(result, "recoveries"));
        // All three real instructions (ecall, addi, ebreak) retire exactly once each — the three
        // blocked attempts contributed zero retirements.
        Assert.Equal(3, Counter(result, "retired"));
        // addi ran exactly once, after unblocking — not skipped, not run early, not run
        // repeatedly alongside the blocked retries.
        Assert.Equal(1UL, train.ArchState.IntegerRegisters.Read(1));
    }

    [Fact]
    public void NeverClearingBlockingEcall_NeverRetiresOrAdvances_WithinTickBudget() {
        var mem = new FlatMemory(0x100);
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), Ecall);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), AddiX1Plus1);
        mem.Load(0x00, bytes);

        var handler = new BlockThenClearHandler(int.MaxValue); // never clears
        var mechanism = new Rv32Mechanism(syscallHandler: handler);
        var train = new CprTrain(mechanism, mem);

        RevolutionResult result = train.Run(200);

        Assert.False(train.IsIdle);
        Assert.True(handler.CallCount > 1, "expected the blocked ecall to be retried more than once");
        Assert.Equal(0, Counter(result, "retired")); // addi never got past the still-blocked ecall
        Assert.Equal(0UL, train.ArchState.IntegerRegisters.Read(1));
    }

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Sw(int rs2, int rs1, int imm) =>
        (uint)((((imm >> 5) & 0x7F) << 25) | (rs2 << 20) | (rs1 << 15) | (0b010 << 12) | ((imm & 0x1F) << 7) | 0b0100011);

    // Reads the wait word fresh from shared memory on every call — never caches — because the
    // whole point of this test is that hart 1's write, made through the SAME FlatMemory instance,
    // must be visible to hart 0's handler on its very next retry.
    private sealed class MemoryWaitHandler(ulong waitAddr) : ISyscallHandler {
        public int CallCount { get; private set; }

        public ExecuteResult Handle(ulong syscallNum, IArchState state, IMemory memory, ulong pc, int hartId) {
            CallCount++;
            return new ExecuteResult { RequestBlock = memory.Read(waitAddr, 4) == 0, };
        }
    }

    /// <summary>
    ///     Every other test here is single-hart with a self-clearing stub handler — that proves
    ///     retry-in-place and resume-on-clear, but not the thing <see cref="ExecuteResult.RequestBlock" />
    ///     actually exists for: a hart blocked on a futex resuming because a *different* hart wrote the
    ///     word it's waiting on, under <see cref="MultiHartPipeline" />'s round-robin interleaving.
    /// </summary>
    [Fact]
    public void BlockedEcall_ResumesWhenAnotherHartClearsTheSharedFutexWord() {
        const ulong waitAddr = 0x80;
        var mem = new FlatMemory(0x200);

        var hart0Bytes = new byte[12];
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(0), Ecall);
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(4), Addi(2, 2, 1));
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(8), Ebreak);
        mem.Load(0x00, hart0Bytes);

        // x3 is synthesized in-program (addi) rather than pre-set via ArchState before Run() —
        // CprTrain's rename/PRF path doesn't observe a pre-Run() architectural register write (a
        // separate, pre-existing bug unrelated to RequestBlock; see TODO.md), so a store sourcing
        // its address register that way would silently write to address 0 instead of waitAddr.
        var hart1Bytes = new byte[16];
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(0), Addi(3, 0, (int)waitAddr)); // x3 = waitAddr
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(4), Addi(1, 0, 1)); // x1 = 1
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(8), Sw(1, 3, 0)); // mem[x3] = x1
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(12), Ebreak);
        mem.Load(0x40, hart1Bytes);

        var handler = new MemoryWaitHandler(waitAddr);
        var mech0 = new Rv32Mechanism(syscallHandler: handler);
        var mech1 = new Rv32Mechanism();

        var train0 = new CprTrain(mech0, mem, entryPoint: 0x00);
        var train1 = new CprTrain(mech1, mem, entryPoint: 0x40);

        new MultiHartPipeline(train0, train1).Run(1000);

        // Hart 0's ecall must have actually blocked (retried) before hart 1 ever ran far enough
        // to clear the word — otherwise this proves nothing about the cross-hart hand-off.
        Assert.True(handler.CallCount > 1, "expected hart 0's ecall to have blocked at least once");
        Assert.Equal(1UL, mem.Read(waitAddr, 4));
        Assert.Equal(1UL, train0.ArchState.IntegerRegisters.Read(2));
    }
}
