#region

using JetBrains.Annotations;
using Mechanism;

#endregion

namespace Chip8.Decode;

public class Instruction(
    ushort pc,
    ushort raw,
    int dest,
    IReadOnlyList<int> read,
    ToothClass cls,
    object? payload
) : ITooth {
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = raw;
    public int SizeBytes => 2; // 16-bit instructions
    public int DestinationRegister => dest;
    public IReadOnlyList<int> SourceRegisters { get; } = read;
    public ToothClass Class { get; } = cls;
    public object? Payload => payload;
}

public abstract record Op;

public record Call : Op;

public record ClearDisplay : Op;

public record Return : Op;

public record Goto(ushort Imm) : Op;

public record CallSub(ushort Imm) : Op;

public record SkipEqImm(int Vx, ushort Imm) : Op;

public record SkipNeqImm(int Vx, ushort Imm) : Op;

public record SkipEq(int Vx, int Vy) : Op;

[UsedImplicitly]
public record SetImm(int Vx, ushort Imm) : Op;

public record AddImm(int Vx, ushort Imm) : Op;

[UsedImplicitly]
public record Set(int Vx, int Vy) : Op;

public record BitOr(int Vx, int Vy) : Op;

public record BitAnd(int Vx, int Vy) : Op;

public record BitXor(int Vx, int Vy) : Op;

public record Add(int Vx, int Vy) : Op;

public record Sub(int Vx, int Vy) : Op;

public record ShiftRight1(int Vx) : Op;

public record SubYx(int Vx, int Vy) : Op;

public record ShiftLeft1(int Vx) : Op;

public record SkipNeq(int Vx, int Vy) : Op;

public record SetIImm(ushort Imm) : Op;

public record JumpV0Offset(ushort Imm) : Op;

[UsedImplicitly]
public record RandAnd(int Vx, byte Imm) : Op;

[UsedImplicitly]
public record Draw(int Vx, int Vy, byte Imm) : Op;

[UsedImplicitly]
public record SkipKeyPressed(int Vx) : Op;

[UsedImplicitly]
public record SkipKeyNotPressed(int Vx) : Op;

[UsedImplicitly]
public record GetDelayTimer(int Vx) : Op;

[UsedImplicitly]
public record GetKey(int Vx) : Op;

public record SetDelayTimer(int Vx) : Op;

public record SetSoundTimer(int Vx) : Op;

public record AddToI(int Vx) : Op;

public record SetISprite(int Vx) : Op;

public record Bcd(int Vx) : Op;

public record RegDump(int Vx) : Op;

public record RegLoad(int Vx) : Op;