using Mechanism;

namespace F18A.Decode;

public sealed class F18ADecoder : IDecoder {
    public int InstructionSize(ulong pc, IMemory memory) => 4;

    public FetchHint GetFetchHint(ulong pc, uint firstWord) {
        uint raw = firstWord & 0x3FFFF;
        var s0 = (byte)((raw >> 13) & 0x1F);
        bool isBranch = IsControl(s0);
        (ulong Value, bool HasValue) target = s0 is F18AOp.Jump or F18AOp.Call
            ? ((raw & 0x1FFF & 0x1FF) * 4ul, true)
            : default((ulong, bool));
        return new FetchHint { InstructionSize = 4, IsBranch = isBranch, BranchTarget = target, };
    }

    public ITooth Decode(ulong pc, IMemory memory) =>
        Decode(pc, (uint)memory.Read(pc, 4));

    public ITooth Decode(ulong pc, uint firstWord) {
        uint raw = firstWord & 0x3FFFF;
        ToothClass cls = ClassOf((byte)((raw >> 13) & 0x1F));
        return new F18AInstruction(pc, raw) { Class = cls, };
    }

    private static bool IsControl(byte op) =>
        op is F18AOp.Return or F18AOp.Jump or F18AOp.Call
           or F18AOp.Unext or F18AOp.Next or F18AOp.If or F18AOp.MinusIf;

    private static ToothClass ClassOf(byte op) => op switch {
        F18AOp.Jump or F18AOp.Call or F18AOp.Return                => ToothClass.Branch,
        F18AOp.If or F18AOp.MinusIf or F18AOp.Next or F18AOp.Unext => ToothClass.ConditionalBranch,
        F18AOp.FetchP or F18AOp.FetchAp
                      or F18AOp.FetchB or F18AOp.FetchA => ToothClass.Load,
        F18AOp.StoreP or F18AOp.StoreAp
                      or F18AOp.StoreB or F18AOp.StoreA => ToothClass.Store,
        _ => ToothClass.IntegerAlu,
    };
}