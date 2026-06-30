using Mechanism;

namespace Subleq.Decode;

public sealed class SubleqDecoder : IDecoder {
    public int InstructionSize(ulong pc, IMemory memory) => 12;

    public FetchHint GetFetchHint(ulong pc, uint firstWord) => new() {
        InstructionSize = 12,
        IsBranch = true,
    };

    public ITooth Decode(ulong pc, IMemory memory) {
        var a = (int)memory.Read(pc, 4);
        var b = (int)memory.Read(pc + 4, 4);
        var c = (int)memory.Read(pc + 8, 4);
        return new SubleqInstruction(pc, new SubleqOp(a, b, c));
    }

    public ITooth Decode(ulong pc, uint raw) => throw new NotSupportedException(
        "SUBLEQ requires three words; use Decode(pc, memory) instead."
    );
}