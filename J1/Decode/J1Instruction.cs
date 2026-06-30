using Mechanism;

namespace J1.Decode;

public abstract record J1Op;

public record Literal(ushort Value) : J1Op;

public record Jump(int WordTarget) : J1Op;

public record CondJump(int WordTarget) : J1Op;

public record Call(int WordTarget) : J1Op;

public record Alu(
    int TOut,
    bool ReturnFromR,
    bool TtoN,
    bool TtoR,
    bool NtoMem,
    int DDelta,
    int RDelta
) : J1Op;

public sealed class J1Instruction(ulong pc, J1Op op, ToothClass cls) : ITooth {
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = op is Literal lit ? lit.Value : 0u;
    public int SizeBytes => 2;
    public int DestinationRegister => -1;
    public IReadOnlyList<int> SourceRegisters => [];
    public ToothClass Class { get; } = cls;
    public object? Payload => op;
}