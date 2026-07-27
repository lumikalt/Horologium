#region

using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     <see cref="SmtTrain" />'s <see cref="ExecuteResult.RequestBlock" /> support. Unlike the other five
///     detailed trains — each of which is one train per hart — <c>SmtTrain</c> is genuinely multi-hart
///     *within a single train*: N harts share one core's fetch/issue bandwidth, interleaved cycle-by-
///     cycle via an <see cref="ISmtFetchPolicy" />. Each hart executes exactly one instruction fully to
///     completion (fetch → decode → execute → SideEffect → register write → Pc update) per turn, with no
///     cross-cycle in-flight state — the same completion model as <see cref="SingleCycleTrain" />. A
///     still-blocked instruction therefore needs no rollback, no squash, and no explicit PC redirect:
///     <c>ctx.ArchState.Pc</c> is already the blocked instruction's own Pc, so leaving it untouched and
///     returning <c>true</c> (cutting only this hart's slot for the rest of the current cycle, exactly
///     like a halt/trap/branch does) is the entire fix. Every <see cref="HartContext" /> is disjoint, so
///     sibling harts — including whichever one is expected to clear the block — are never touched and
///     keep issuing normally in the same and later cycles. See <c>SmtCore.IssueOne</c>'s
///     <c>RequestBlock</c> branch.
/// </summary>
public class SmtRequestBlockGuardTests {
    private const uint Ecall = 0x0000_0073;
    private const uint AddiX1Plus1 = 0x0010_8093; // addi x1, x1, 1
    private const uint Ebreak = 0x0010_0073;

    [Fact]
    public void BlockingEcall_RetriesInPlace_ThenProceedsNormallyOnceCleared() {
        var mem = new FlatMemory(0x100);
        var bytes = new byte[12];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), SmtRequestBlockGuardTests.Ecall);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), SmtRequestBlockGuardTests.AddiX1Plus1);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), SmtRequestBlockGuardTests.Ebreak);
        mem.Load(0x00, bytes);

        var handler = new BlockThenClearHandler(3);
        var mechanism = new Rv32Mechanism(syscallHandler: handler);
        var train = new SmtTrain([mechanism,], [mem,]);

        train.Run(1000);

        Assert.True(train.IsIdle);
        // Executed (IssueOne → handler.Handle) four times — three blocked attempts plus the
        // one that finally cleared.
        Assert.Equal(4, handler.CallCount);
        // addi ran exactly once, after unblocking — not skipped, not run early, not run
        // repeatedly alongside the blocked retries.
        Assert.Equal(1UL, train.StateOf(0).IntegerRegisters.Read(1));
    }

    [Fact]
    public void NeverClearingBlockingEcall_NeverAdvances_WithinTickBudget() {
        var mem = new FlatMemory(0x100);
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), SmtRequestBlockGuardTests.Ecall);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), SmtRequestBlockGuardTests.AddiX1Plus1);
        mem.Load(0x00, bytes);

        var handler = new BlockThenClearHandler(int.MaxValue); // never clears
        var mechanism = new Rv32Mechanism(syscallHandler: handler);
        var train = new SmtTrain([mechanism,], [mem,]);

        train.Run(200);

        Assert.False(train.IsIdle);
        Assert.True(handler.CallCount > 1, "expected the blocked ecall to be retried more than once");
        Assert.Equal(0UL, train.StateOf(0).IntegerRegisters.Read(1)); // addi never got past the still-blocked ecall
    }

    private static uint Addi(int rd, int rs1, int imm) =>
        (uint)(((imm & 0xFFF) << 20) | (rs1 << 15) | (0b000 << 12) | (rd << 7) | 0b0010011);

    private static uint Sw(int rs2, int rs1, int imm) =>
        (uint)((((imm >> 5) & 0x7F) << 25) | (rs2 << 20) | (rs1 << 15) | (0b010 << 12) | ((imm & 0x1F) << 7)
             | 0b0100011);

    /// <summary>
    ///     Every other test here uses a single hart with a self-clearing stub handler — that proves
    ///     retry-in-place and resume-on-clear, but not the thing <see cref="ExecuteResult.RequestBlock" />
    ///     actually exists for: a hart blocked on a futex resuming because a *different* hart — sharing
    ///     the same core, not a separate train — wrote the word it's waiting on. Unlike the other five
    ///     trains' cross-hart tests, this is ONE <see cref="SmtTrain" /> with two internal harts, not
    ///     <see cref="MultiHartPipeline" /> wrapping two separate trains — that shape doesn't apply here,
    ///     since the whole point is that both harts share one core's issue bandwidth.
    /// </summary>
    [Fact]
    public void BlockedEcall_ResumesWhenAnotherHartClearsTheSharedFutexWord() {
        const ulong waitAddr = 0x80;
        var mem = new FlatMemory(0x200);

        var hart0Bytes = new byte[12];
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(0), SmtRequestBlockGuardTests.Ecall);
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(4), Addi(2, 2, 1));
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(8), SmtRequestBlockGuardTests.Ebreak);
        mem.Load(0x00, hart0Bytes);

        var hart1Bytes = new byte[12];
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(0), Addi(1, 0, 1)); // x1 = 1
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(4), Sw(1, 3, 0));   // mem[x3] = x1  (x3 = waitAddr)
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(8), SmtRequestBlockGuardTests.Ebreak);
        mem.Load(0x40, hart1Bytes);

        var handler = new MemoryWaitHandler(waitAddr);
        var mech0 = new Rv32Mechanism(syscallHandler: handler);
        var mech1 = new Rv32Mechanism();

        var train = new SmtTrain([mech0, mech1,], [mem, mem,], [0x00UL, 0x40UL,]);
        train.StateOf(1).IntegerRegisters.Write(3, waitAddr);

        train.Run(1000);

        // Hart 0's ecall must have actually blocked (retried) before hart 1 ever ran far enough
        // to clear the word — otherwise this proves nothing about the cross-hart hand-off.
        Assert.True(handler.CallCount > 1, "expected hart 0's ecall to have blocked at least once");
        Assert.Equal(1UL, mem.Read(waitAddr, 4));
        Assert.Equal(1UL, train.StateOf(0).IntegerRegisters.Read(2));
    }

    private sealed class BlockThenClearHandler(int blockCount) : ISyscallHandler {
        public int CallCount { get; private set; }

        public ExecuteResult Handle(ulong syscallNum, IArchState state, IMemory memory, ulong pc, int hartId) {
            CallCount++;
            return new ExecuteResult { RequestBlock = CallCount <= blockCount, };
        }
    }

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
}