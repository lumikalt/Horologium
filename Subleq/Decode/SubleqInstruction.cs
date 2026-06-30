using Mechanism;

namespace Subleq.Decode;

public sealed record SubleqOp(int A, int B, int C);

public sealed class SubleqInstruction(ulong pc, SubleqOp op) : ITooth {
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = (uint)op.A;
    public int SizeBytes => 12;
    public int DestinationRegister => -1;
    public IReadOnlyList<int> SourceRegisters => [];
    public ToothClass Class => ToothClass.ConditionalBranch;
    public object? Payload => op;
}