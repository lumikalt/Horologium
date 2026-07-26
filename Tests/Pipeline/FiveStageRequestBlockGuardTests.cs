#region

using Mechanism;
using Pipeline;
using RiscV32;
using RiscV32.Memory;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     <see cref="FiveStageTrain" /> has no <see cref="ExecuteResult.RequestBlock" /> support (see
///     TODO.md's "RequestBlock support in detailed pipeline trains" — real support needs
///     squash-and-refetch, since ecall isn't serialized here). Rather than let a blocked instruction
///     silently corrupt Writeback or wrongly retire, <c>ExecuteStage.Cycle</c> fails loudly and
///     specifically the moment a blocking result reaches EX.
/// </summary>
public class FiveStageRequestBlockGuardTests {
    private const uint Ecall = 0x0000_0073;

    private sealed class AlwaysBlockingHandler : ISyscallHandler {
        public ExecuteResult Handle(ulong syscallNum, IArchState state, IMemory memory, ulong pc, int hartId) =>
            new() { RequestBlock = true, };
    }

    [Fact]
    public void BlockingEcall_ThrowsClearNotSupportedException_InsteadOfCorruptingWriteback() {
        var mem = new FlatMemory(0x100);
        mem.Load(0x00, BitConverter.GetBytes(Ecall));

        var mechanism = new Rv32Mechanism(syscallHandler: new AlwaysBlockingHandler());
        var train = new FiveStageTrain(mechanism, mem);

        NotSupportedException ex = Assert.Throws<NotSupportedException>(() => train.Run(10));
        Assert.Contains("RequestBlock", ex.Message);
        Assert.Contains("0x0", ex.Message);
    }
}
