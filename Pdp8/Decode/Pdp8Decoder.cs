using Mechanism;

namespace Pdp8.Decode;

public sealed class Pdp8Decoder : IDecoder {
    public int InstructionSize(ulong pc, IMemory memory) => 2;

    public FetchHint GetFetchHint(ulong pc, uint firstWord) {
        int opcode = (int)((firstWord & 0xFFF) >> 9) & 7;
        return new FetchHint {
            InstructionSize = 2,
            IsBranch = opcode is 2 or 4 or 5, // ISZ, JMS, JMP
            IsCall = opcode == 4,             // JMS
        };
    }

    public ITooth Decode(ulong pc, IMemory memory) =>
        Decode(pc, (uint)memory.Read(pc, 2));

    public ITooth Decode(ulong pc, uint raw) {
        var word = (int)(raw & 0xFFF);
        int opcode = (word >> 9) & 7;
        bool ind = (word & 0x100) != 0;
        bool curPg = (word & 0x80) != 0;
        int offset = word & 0x7F;
        var wordPc = (int)(pc >> 1); // byte PC → word PC

        int directEa = curPg ? (wordPc & ~0x7F) | offset : offset;

        return opcode switch {
            0 => MriTooth(pc, MriCode.And, directEa, ind, ToothClass.IntegerAlu, 0, [0,]),
            1 => MriTooth(pc, MriCode.Tad, directEa, ind, ToothClass.IntegerAlu, 0, [0,]),
            2 => MriTooth(pc, MriCode.Isz, directEa, ind, ToothClass.ConditionalBranch, -1, []),
            3 => MriTooth(pc, MriCode.Dca, directEa, ind, ToothClass.Store, 0, [0,]),
            4 => MriTooth(pc, MriCode.Jms, directEa, ind, ToothClass.Branch, -1, []),
            5 => MriTooth(pc, MriCode.Jmp, directEa, ind, ToothClass.Branch, -1, []),
            6 => new Pdp8Instruction(
                pc, -1, [],
                ToothClass.System, new IotOp((word >> 3) & 0x3F, word & 7)
            ),
            7 => DecodeOpr(pc, word),
            _ => throw new IllegalInstructionException(pc, raw, $"impossible opcode {opcode}"),
        };
    }

    private static Pdp8Instruction MriTooth(
        ulong pc,
        MriCode code,
        int ea,
        bool ind,
        ToothClass cls,
        int dest,
        int[] srcs
    ) =>
        new(pc, dest, srcs, cls, new MriOp(code, ea, ind));

    private static Pdp8Instruction DecodeOpr(ulong pc, int word) {
        bool group2 = (word & 0x100) != 0;

        if (!group2) {
            // OPR Group 1
            // bit3=RotRight, bit2=RotLeft, bit1=TwoStep/BSW, bit0=IAC
            bool rotR = (word & 0x08) != 0;
            bool rotL = (word & 0x04) != 0;
            bool twoStep = (word & 0x02) != 0 && (rotR || rotL);
            bool bsw = (word & 0x02) != 0 && !rotR && !rotL;

            var op = new Opr1Op(
                (word & 0x80) != 0,
                (word & 0x40) != 0,
                (word & 0x20) != 0,
                (word & 0x10) != 0,
                rotR,
                rotL,
                twoStep,
                (word & 0x01) != 0,
                bsw
            );

            bool readsAc = !op.Cla || op.Cma || op.RotRight || op.RotLeft || op.Bsw;
            bool readsL = op.Cml || op.RotRight || op.RotLeft;
            int[] srcs = readsAc && readsL ? [0, 1,] : readsAc ? [0,] : readsL ? [1,] : [];
            return new Pdp8Instruction(pc, 0, srcs, ToothClass.IntegerAlu, op);
        }
        else {
            // OPR Group 2 (bit0=1 → Group 3 EAE, not implemented)
            if ((word & 0x01) != 0)
                throw new IllegalInstructionException(
                    (ulong)pc, (uint)word,
                    "EAE (OPR Group 3) is not implemented."
                );

            var op = new Opr2Op(
                (word & 0x80) != 0,
                (word & 0x40) != 0,
                (word & 0x20) != 0,
                (word & 0x10) != 0,
                OrMode: (word & 0x08) != 0, // RSS=1: complement the natural skip sense
                Osr: (word & 0x04) != 0,
                Hlt: (word & 0x02) != 0
            );

            if (op.Hlt) return new Pdp8Instruction(pc, -1, [], ToothClass.Halt, op);

            bool testsAc = op.Sma || op.Sza;
            bool testsL = op.Snl;
            int[] srcs = testsAc && testsL ? [0, 1,] : testsAc ? [0,] : testsL ? [1,] : [];
            int dest = op.Cla ? 0 : -1;
            return new Pdp8Instruction(pc, dest, srcs, ToothClass.ConditionalBranch, op);
        }
    }
}