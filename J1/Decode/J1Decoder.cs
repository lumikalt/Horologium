using Mechanism;

namespace J1.Decode;

public sealed class J1Decoder : IDecoder {
    public int InstructionSize(ulong pc, IMemory memory) => 2;

    public FetchHint GetFetchHint(ulong pc, uint firstWord) {
        var raw = (ushort)firstWord;
        bool isLiteral = (raw & 0x8000) != 0;
        int type = (raw >> 13) & 7;
        bool isJump = !isLiteral && type == 0;
        bool isCondJump = !isLiteral && type == 1;
        bool isCall = !isLiteral && type == 2;
        bool isAluReturn = !isLiteral && type == 3 && (raw & 0x1000) != 0;

        return new FetchHint {
            InstructionSize = 2,
            IsBranch = isJump || isCondJump || isCall || isAluReturn,
            IsCall = isCall,
            IsReturn = isAluReturn,
            BranchTarget = isJump || isCall
                ? ((ulong)(raw & 0x1FFF) * 2, true)
                : default((ulong, bool)),
        };
    }

    public ITooth Decode(ulong pc, IMemory memory) =>
        Decode(pc, (uint)memory.Read(pc, 2));

    public ITooth Decode(ulong pc, uint firstWord) {
        var raw = (ushort)firstWord;

        if ((raw & 0x8000) != 0)
            return new J1Instruction(pc, new Literal((ushort)(raw & 0x7FFF)), ToothClass.IntegerAlu);

        int type = (raw >> 13) & 7;
        int target = raw & 0x1FFF;

        return type switch {
            0 => new J1Instruction(pc, new Jump(target), ToothClass.Branch),
            1 => new J1Instruction(pc, new CondJump(target), ToothClass.ConditionalBranch),
            2 => new J1Instruction(pc, new Call(target), ToothClass.Branch),
            3 => DecodeAlu(pc, raw),
            _ => throw new IllegalInstructionException(pc, raw, $"Unknown J1 type {type}"),
        };
    }

    private static J1Instruction DecodeAlu(ulong pc, ushort raw) {
        static int Delta(int bits) => bits switch { 0 => 0, 1 => 1, 2 => -2, 3 => -1, _ => 0, };

        var op = new Alu(
            (raw >> 8) & 0xF,
            (raw & 0x1000) != 0,
            (raw & 0x0080) != 0,
            (raw & 0x0040) != 0,
            (raw & 0x0020) != 0,
            Delta((raw >> 2) & 3),
            Delta(raw & 3)
        );

        ToothClass cls = op.NtoMem ? ToothClass.Store
            : op.TOut == 0xC       ? ToothClass.Load
            : op.ReturnFromR       ? ToothClass.Branch
                                     : ToothClass.IntegerAlu;
        return new J1Instruction(pc, op, cls);
    }
}