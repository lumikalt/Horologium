#region

using Mechanism;
using Orrery.Train;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     <see cref="MultiHartWarmupMeasureDriver.RunWarmupThenMeasure" />'s stall-detection guard: a hart
///     spinning on a still-blocked <see cref="ExecuteResult.RequestBlock" /> never halts (it's retrying,
///     not halted, so <see cref="ISteppableTrain.StepCycle" /> always returns <c>true</c>). If the hart
///     that could ever clear that wait has already halted, the global instruction count can never
///     advance again — without a stall bound, the measure loop would spin forever. See
///     <c>MultiHartWarmupMeasureDriver.StallTickLimit</c> / <c>RunUntil</c>.
/// </summary>
public class MultiHartWarmupMeasureDriverDeadlockTests {
    private const uint Ecall = 0x0000_0073;
    private const uint AddiX1Plus1 = 0x0010_8093; // addi x1, x1, 1
    private const uint Ebreak = 0x0010_0073;

    /// <summary>
    ///     Hart 0 blocks forever on an ecall no one will ever clear; hart 1 halts almost immediately.
    ///     The measure target (a huge instruction count neither hart can ever reach) would hang the
    ///     driver forever without the stall-tick bound — this test would time out and fail without the
    ///     fix, and complete quickly with it.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task PermanentlyBlockedHart_WithAlreadyHaltedWaker_DoesNotHangTheDriver() {
        await Task.Run(RunScenario);
    }

    private static void RunScenario() {
        var mem0 = new FlatMemory(0x100);
        var hart0Bytes = new byte[12];
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(0), MultiHartWarmupMeasureDriverDeadlockTests.Ecall);
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(4), MultiHartWarmupMeasureDriverDeadlockTests.AddiX1Plus1);
        BitConverter.TryWriteBytes(hart0Bytes.AsSpan(8), MultiHartWarmupMeasureDriverDeadlockTests.Ebreak);
        mem0.Load(0x00, hart0Bytes);

        var mem1 = new FlatMemory(0x100);
        var hart1Bytes = new byte[4];
        BitConverter.TryWriteBytes(hart1Bytes.AsSpan(0), MultiHartWarmupMeasureDriverDeadlockTests.Ebreak);
        mem1.Load(0x00, hart1Bytes);

        var handler = new NeverClearingHandler();
        var counter0 = new InstructionCounter();
        var counter1 = new InstructionCounter();
        var train0 = new FiveStageTrain(new Rv32Mechanism(syscallHandler: handler), mem0, commitObserver: counter0);
        var train1 = new FiveStageTrain(new Rv32Mechanism(), mem1, commitObserver: counter1);

        RevolutionResult[] results = MultiHartWarmupMeasureDriver.RunWarmupThenMeasure(
            [train0, train1,], [counter0, counter1,], 0, 1_000_000_000
        );

        Assert.Equal(2, results.Length);
        // Hart 0 never got past its permanently-blocked ecall; hart 1 halted at its own ebreak
        // (which does retire, per FiveStageTrain's halt semantics) and contributed no more after.
        Assert.True(handler.CallCount > 1, "expected the blocked ecall to have been retried more than once");
        Assert.Equal(0UL, train0.ArchState.IntegerRegisters.Read(1));
    }

    private sealed class NeverClearingHandler : ISyscallHandler {
        public int CallCount { get; private set; }

        public ExecuteResult Handle(ulong syscallNum, IArchState state, IMemory memory, ulong pc, int hartId) {
            CallCount++;
            return new ExecuteResult { RequestBlock = true, };
        }
    }
}