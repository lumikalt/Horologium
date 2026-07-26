#region

using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     <see cref="FiveStageTrain" />'s <see cref="ExecuteResult.RequestBlock" /> support: a still-
///     blocked syscall (e.g. futex(FUTEX_WAIT)) is squashed and re-fetched from its own Pc every
///     cycle — mirroring <c>MultiHartKernel</c>'s functional retry-in-place (<c>StepHart</c> returns
///     without advancing <c>state.Pc</c> when <c>RequestBlock</c> is true) — until a later
///     re-execution finally clears, at which point it retires normally and younger instructions
///     proceed. See <c>WritebackStage.BlockRedirect</c> and the squash-and-refetch logic in
///     <c>FiveStageTrain.RunCycle</c>.
/// </summary>
public class FiveStageRequestBlockGuardTests {
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

    private sealed class PcCommitCounter : ICommitObserver {
        private readonly Dictionary<ulong, int> _counts = new();
        public int CountAt(ulong pc) => _counts.GetValueOrDefault(pc);

        public void OnCommit(ulong pc, uint rawEncoding, IArchState state) =>
            _counts[pc] = _counts.GetValueOrDefault(pc) + 1;
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
        var counter = new PcCommitCounter();
        var train = new FiveStageTrain(mechanism, mem, commitObserver: counter);

        train.Run(1000);

        Assert.True(train.IsIdle);
        // Executed (Execute.Cycle → handler.Handle) four times — three blocked attempts plus the
        // one that finally cleared — but every blocked attempt contributes zero committed work,
        // so the commit observer (fed only from WritebackStage's real retire path) only ever sees
        // the ecall's own Pc once.
        Assert.Equal(4, handler.CallCount);
        Assert.Equal(1, counter.CountAt(0x00));
        Assert.Equal(1, counter.CountAt(0x04));
        // addi ran exactly once, after unblocking — not skipped, not run early, not run repeatedly
        // alongside the blocked retries (which would leave x1 > 1 if the squash/flush let it slip
        // through to EX/WB while the ecall was still blocked ahead of it).
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
        var counter = new PcCommitCounter();
        var train = new FiveStageTrain(mechanism, mem, commitObserver: counter);

        train.Run(200);

        Assert.False(train.IsIdle);
        Assert.True(handler.CallCount > 1, "expected the blocked ecall to be retried more than once");
        Assert.Equal(0, counter.CountAt(0x00));
        Assert.Equal(0, counter.CountAt(0x04)); // addi never got past the still-blocked ecall
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
    ///     word it's waiting on, under <see cref="MultiHartPipeline" />'s round-robin interleaving. Hart 0
    ///     spins on a blocking ecall reading a shared memory word; hart 1 (a plain, unrelated
    ///     <see cref="Rv32Mechanism" /> with no blocking handler) writes that word via its own <c>sw</c>
    ///     and halts. Only the real cross-hart hand-off through shared <see cref="FlatMemory" /> makes
    ///     hart 0's addi retire.
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

        var hart1Bytes = new byte[12];
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(0), Addi(1, 0, 1)); // x1 = 1
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(4), Sw(1, 3, 0)); // mem[x3] = x1  (x3 = waitAddr)
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(8), Ebreak);
        mem.Load(0x40, hart1Bytes);

        var handler = new MemoryWaitHandler(waitAddr);
        var mech0 = new Rv32Mechanism(syscallHandler: handler);
        var mech1 = new Rv32Mechanism();

        var counter0 = new PcCommitCounter();
        var train0 = new FiveStageTrain(mech0, mem, entryPoint: 0x00, commitObserver: counter0);
        var train1 = new FiveStageTrain(mech1, mem, entryPoint: 0x40, commitObserver: new PcCommitCounter());
        train1.ArchState.IntegerRegisters.Write(3, waitAddr);

        new MultiHartPipeline(train0, train1).Run(1000);

        // Hart 0's ecall must have actually blocked (retried) before hart 1 ever ran far enough
        // to clear the word — otherwise this proves nothing about the cross-hart hand-off.
        Assert.True(handler.CallCount > 1, "expected hart 0's ecall to have blocked at least once");
        Assert.Equal(1UL, mem.Read(waitAddr, 4));
        Assert.Equal(1, counter0.CountAt(0x04));
        Assert.Equal(1UL, train0.ArchState.IntegerRegisters.Read(2));
    }
}
