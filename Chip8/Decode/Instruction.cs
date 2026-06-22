using Mechanism;

namespace Chip8.Decode;

public class Instruction(
    ushort pc,
    ushort raw,
    int dest,
    IReadOnlyList<int> read,
    InstructionClass cls,
    object? payload
) : IInstruction {
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = raw;
    public int SizeBytes => 2; // 16-bit instructions
    public int DestinationRegister => dest;
    public IReadOnlyList<int> SourceRegisters { get; } = read;
    public InstructionClass Class { get; } = cls;
    public object? Payload => payload;
}

public abstract record Op;

public record Call : Op;

public record ClearDisplay : Op;

public record Return : Op;

public record Goto(ushort Imm) : Op;

public record CallSub(ushort Imm) : Op;

public record SkipEqImm(int Vx, ushort imm) : Op;

public record SkipNeqImm(int Vx, ushort imm) : Op;

public record SkipEq(int Vx, int vy) : Op;

public record SetImm(int vx, ushort imm) : Op;

public record AddImm(int vx, ushort imm) : Op;

public record Set(int vx, int vy) : Op;

public record BitOr(int vx, int vy) : Op;

public record BitAnd(int vx, int vy) : Op;

public record BitXor(int vx, int vy) : Op;

public record Add(int vx, int vy) : Op;

public record Sub(int vx, int vy) : Op;

public record ShiftRight1(int vx) : Op;

public record SubYx(int Vx, int Vy) : Op;

public record ShiftLeft1(int Vx) : Op;

public record SkipNeq(int Vx, int vy) : Op;

public record SetIImm(ushort imm) : Op;

public record JumpV0Offset(ushort imm) : Op;

public record RandAnd(int Vx, byte imm) : Op;

public record Draw(int Vx, int vy, byte imm) : Op;

public record SkipKeyPressed(int vx) : Op;

public record SkipKeyNotPressed(int vx) : Op;

public record GetDelayTimer(int vx) : Op;

public record GetKey(int vx) : Op;

public record SetDelayTimer(int vx) : Op;

public record SetSoundTimer(int vx) : Op;

public record AddToI(int vx) : Op;

public record SetISprite(int vx) : Op;

public record BCD(int vx) : Op;

public record RegDump(int vx) : Op;

public record RegLoad(int vx) : Op;