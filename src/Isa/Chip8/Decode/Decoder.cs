#region

using Mechanism;

#endregion

namespace Chip8.Decode;

public class Decoder : IDecoder {
    public int InstructionSize(ulong pc, IMemory memory) => 2;

    public FetchHint GetFetchHint(ulong pc, uint firstWord) => new() { InstructionSize = 2, };

    public ITooth Decode(ulong pc, IMemory memory) => Decode(pc, (uint)memory.Read(pc, 2));

    public ITooth Decode(ulong pc, uint raw) {
        var opcode = (byte)((raw >> 12) & 0xF); // First nibble
        var x = (int)((raw >> 8) & 0xF);
        var y = (int)((raw >> 4) & 0xF);
        var n = (byte)(raw & 0xF);
        var nn = (byte)(raw & 0xFF);
        var nnn = (ushort)(raw & 0xFFF);

        int dest = -1;
        Op op;
        List<int> read = [];
        var cls = ToothClass.System;

        switch (opcode) {
            case 0x0 when raw == 0x00E0: op = new ClearDisplay(); break;
            case 0x0 when raw == 0x00EE:
                op = new Return();
                cls = ToothClass.Branch;
                break;
            case 0x0: op = new Call(); break;
            case 0x1:
                op = new Goto(nnn);
                cls = ToothClass.Branch;
                break;
            case 0x2:
                op = new CallSub(nnn);
                cls = ToothClass.Branch;
                break;
            case 0x3:
                op = new SkipEqImm(x, nn);
                cls = ToothClass.ConditionalBranch;
                read.Add(x);
                break;
            case 0x4:
                op = new SkipNeqImm(x, nn);
                cls = ToothClass.ConditionalBranch;
                read.Add(x);
                break;
            case 0x5 when n == 0:
                op = new SkipEq(x, y);
                cls = ToothClass.ConditionalBranch;
                read.Add(x);
                read.Add(y);
                break;
            case 0x6:
                op = new SetImm(x, nn);
                cls = ToothClass.IntegerAlu;
                dest = x;
                break;
            case 0x7:
                op = new AddImm(x, nn);
                cls = ToothClass.IntegerAlu;
                read.Add(x);
                dest = x;
                break;
            case 0x8 when n == 0:
                op = new Set(x, y);
                cls = ToothClass.IntegerAlu;
                read.Add(x);
                read.Add(y);
                dest = x;
                break;
            case 0x8 when n == 1:
                op = new BitOr(x, y);
                cls = ToothClass.IntegerAlu;
                read.Add(x);
                read.Add(y);
                dest = x;
                break;
            case 0x8 when n == 2:
                op = new BitAnd(x, y);
                cls = ToothClass.IntegerAlu;
                read.Add(x);
                read.Add(y);
                dest = x;
                break;
            case 0x8 when n == 3:
                op = new BitXor(x, y);
                cls = ToothClass.IntegerAlu;
                read.Add(x);
                read.Add(y);
                dest = x;
                break;
            case 0x8 when n == 4:
                op = new Add(x, y);
                cls = ToothClass.IntegerAlu;
                read.Add(x);
                read.Add(y);
                dest = x;
                break;
            case 0x8 when n == 5:
                op = new Sub(x, y);
                cls = ToothClass.IntegerAlu;
                read.Add(x);
                read.Add(y);
                dest = x;
                break;
            case 0x8 when n == 6:
                op = new ShiftRight1(x);
                cls = ToothClass.IntegerAlu;
                read.Add(x);
                dest = x;
                break;
            case 0x8 when n == 7:
                op = new SubYx(x, y);
                cls = ToothClass.IntegerAlu;
                read.Add(x);
                read.Add(y);
                dest = x;
                break;
            case 0x8 when n == 0xE:
                op = new ShiftLeft1(x);
                cls = ToothClass.IntegerAlu;
                read.Add(x);
                dest = x;
                break;
            case 0x9 when n == 0:
                op = new SkipNeq(x, y);
                cls = ToothClass.ConditionalBranch;
                read.Add(x);
                read.Add(y);
                break;
            case 0xA:
                op = new SetIImm(nnn);
                cls = ToothClass.IntegerAlu;
                break;
            case 0xB:
                op = new JumpV0Offset(nnn);
                cls = ToothClass.Branch;
                read.Add(0);
                break;
            case 0xC:
                op = new RandAnd(x, nn);
                cls = ToothClass.IntegerAlu;
                dest = x;
                break;
            case 0xD:
                op = new Draw(x, y, n);
                read.Add(x);
                read.Add(y);
                break;
            case 0xE when nn == 0x9E:
                op = new SkipKeyPressed(x);
                cls = ToothClass.ConditionalBranch;
                read.Add(x);
                break;
            case 0xE when nn == 0xA1:
                op = new SkipKeyNotPressed(x);
                cls = ToothClass.ConditionalBranch;
                read.Add(x);
                break;
            case 0xF when nn == 0x07:
                op = new GetDelayTimer(x);
                dest = x;
                break;
            case 0xF when nn == 0x0A:
                op = new GetKey(x);
                dest = x;
                break;
            case 0xF when nn == 0x15:
                op = new SetDelayTimer(x);
                read.Add(x);
                break;
            case 0xF when nn == 0x18:
                op = new SetSoundTimer(x);
                cls = ToothClass.System;
                read.Add(x);
                break;
            case 0xF when nn == 0x1E:
                op = new AddToI(x);
                cls = ToothClass.System;
                read.Add(x);
                break;
            case 0xF when nn == 0x29:
                op = new SetISprite(x);
                cls = ToothClass.System;
                read.Add(x);
                break;
            case 0xF when nn == 0x33:
                op = new Bcd(x);
                cls = ToothClass.Store;
                read.Add(x);
                break;
            case 0xF when nn == 0x55:
                op = new RegDump(x);
                cls = ToothClass.Store;
                read.AddRange(Enumerable.Range(0, x + 1));
                break;
            case 0xF when nn == 0x65:
                op = new RegLoad(x);
                cls = ToothClass.Load;
                read.AddRange(Enumerable.Range(0, x + 1));
                break;
            default: throw new IllegalInstructionException(raw, $"Unknown CHIP-8 instruction: 0x{raw:X4}");
        }

        return new Instruction((ushort)pc, (ushort)raw, dest, read, cls, op);
    }
}