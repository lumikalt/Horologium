#region

using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     <see cref="SingleCycleTrain" />'s <see cref="ExecuteResult.RequestBlock" /> support — the
///     seventh and last (of the trains identified as needing it) train to gain this: it shares
///     <c>MultiHartKernel</c>'s/<c>SmtTrain</c>'s "one instruction fully completes per call" model
///     (no pipeline latches at all), so the fix is the simplest of all seven — a check right after
///     <c>Execute()</c> that charges the cycle (real hardware time passes even on a blocked retry)
///     but skips retire/writeback/PC-advance entirely, leaving the same instruction to be re-decoded
///     and re-executed next tick. No squash-and-refetch machinery needed, since there is no younger
///     in-flight state to squash in the first place.
/// </summary>
public class SingleCycleRequestBlockGuardTests {
    private const uint Ecall = 0x0000_0073;
    private const uint AddiX1Plus1 = 0x0010_8093; // addi x1, x1, 1
    private const uint Ebreak = 0x0010_0073;

    [Fact]
    public void BlockingEcall_RetriesInPlace_ThenProceedsNormallyOnceCleared() {
        var mem = new FlatMemory(0x100);
        var bytes = new byte[12];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), SingleCycleRequestBlockGuardTests.Ecall);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), SingleCycleRequestBlockGuardTests.AddiX1Plus1);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), SingleCycleRequestBlockGuardTests.Ebreak);
        mem.Load(0x00, bytes);

        var handler = new BlockThenClearHandler(3);
        var mechanism = new Rv32Mechanism(syscallHandler: handler);
        var counter = new PcCommitCounter();
        var train = new SingleCycleTrain(mechanism, mem, commitObserver: counter);

        train.Run(1000);

        Assert.True(train.IsIdle);
        Assert.Equal(4, handler.CallCount);
        Assert.Equal(1, counter.CountAt(0x00));
        Assert.Equal(1, counter.CountAt(0x04));
        // addi ran exactly once, after unblocking, not once per blocked retry too.
        Assert.Equal(1UL, train.ArchState.IntegerRegisters.Read(1));
    }

    [Fact]
    public void NeverClearingBlockingEcall_NeverRetiresOrAdvances_WithinTickBudget() {
        var mem = new FlatMemory(0x100);
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), SingleCycleRequestBlockGuardTests.Ecall);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), SingleCycleRequestBlockGuardTests.AddiX1Plus1);
        mem.Load(0x00, bytes);

        var handler = new BlockThenClearHandler(int.MaxValue); // never clears
        var mechanism = new Rv32Mechanism(syscallHandler: handler);
        var counter = new PcCommitCounter();
        var train = new SingleCycleTrain(mechanism, mem, commitObserver: counter);

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
        (uint)((((imm >> 5) & 0x7F) << 25) | (rs2 << 20) | (rs1 << 15) | (0b010 << 12) | ((imm & 0x1F) << 7)
             | 0b0100011);

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
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(0), SingleCycleRequestBlockGuardTests.Ecall);
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(4), Addi(2, 2, 1));
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(8), SingleCycleRequestBlockGuardTests.Ebreak);
        mem.Load(0x00, hart0Bytes);

        var hart1Bytes = new byte[12];
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(0), Addi(1, 0, 1)); // x1 = 1
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(4), Sw(1, 3, 0));   // mem[x3] = x1  (x3 = waitAddr)
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(8), SingleCycleRequestBlockGuardTests.Ebreak);
        mem.Load(0x40, hart1Bytes);

        var handler = new MemoryWaitHandler(waitAddr);
        var mech0 = new Rv32Mechanism(syscallHandler: handler);
        var mech1 = new Rv32Mechanism();

        var counter0 = new PcCommitCounter();
        var train0 = new SingleCycleTrain(mech0, mem, commitObserver: counter0);
        var train1 = new SingleCycleTrain(mech1, mem, 0x40, commitObserver: new PcCommitCounter());
        train1.ArchState.IntegerRegisters.Write(3, waitAddr);

        new MultiHartPipeline(train0, train1).Run(1000);

        Assert.True(handler.CallCount > 1, "expected hart 0's ecall to have blocked at least once");
        Assert.Equal(1UL, mem.Read(waitAddr, 4));
        Assert.Equal(1, counter0.CountAt(0x04));
        Assert.Equal(1UL, train0.ArchState.IntegerRegisters.Read(2));
    }

    private sealed class BlockThenClearHandler(int blockCount) : ISyscallHandler {
        public int CallCount { get; private set; }

        public ExecuteResult Handle(ulong syscallNum, IArchState state, IMemory memory, ulong pc, int hartId) {
            CallCount++;
            return new ExecuteResult { RequestBlock = CallCount <= blockCount, };
        }
    }

    private sealed class PcCommitCounter : ICommitObserver {
        private readonly Dictionary<ulong, int> _counts = new();

        public void OnCommit(ulong pc, uint rawEncoding, IArchState state) =>
            _counts[pc] = _counts.GetValueOrDefault(pc) + 1;

        public int CountAt(ulong pc) => _counts.GetValueOrDefault(pc);
    }

    // Reads the wait word fresh from shared memory every call — never caches — since the point of
    // this test is that hart 1's write, through the SAME FlatMemory instance, must be visible to
    // hart 0's handler on its very next retry.
    private sealed class MemoryWaitHandler(ulong waitAddr) : ISyscallHandler {
        public int CallCount { get; private set; }

        public ExecuteResult Handle(ulong syscallNum, IArchState state, IMemory memory, ulong pc, int hartId) {
            CallCount++;
            return new ExecuteResult { RequestBlock = memory.Read(waitAddr, 4) == 0, };
        }
    }
}