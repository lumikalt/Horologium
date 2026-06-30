using Mechanism;

namespace Move.Decode;

public sealed class MoveDecoder : IDecoder {
    public int InstructionSize(ulong pc, IMemory memory) => 4;

    public FetchHint GetFetchHint(ulong pc, uint firstWord) {
        byte   dst = (byte)(firstWord >> 24);
        byte   src = (byte)((firstWord >> 16) & 0xFF);
        ushort imm = (ushort)(firstWord & 0xFFFF);
        bool isBranch = dst is 0x31 or 0xFF;
        (ulong Value, bool HasValue) target = (dst == 0x31 && src == 0xFE)
            ? ((ulong)imm, true)
            : default;
        return new FetchHint {
            InstructionSize = 4,
            IsBranch        = isBranch,
            BranchTarget    = target,
        };
    }

    public ITooth Decode(ulong pc, IMemory memory) =>
        Decode(pc, (uint)memory.Read(pc, 4));

    public ITooth Decode(ulong pc, uint firstWord) {
        byte   dst = (byte)(firstWord >> 24);
        byte   src = (byte)((firstWord >> 16) & 0xFF);
        ushort imm = (ushort)(firstWord & 0xFFFF);
        return new MoveInstruction(pc, dst, src, imm);
    }
}
