using JetBrains.Annotations;
using Mechanism;

namespace Pdp8.Decode;

public abstract record Pdp8Op;

public enum MriCode {
    And,
    Tad,
    Isz,
    Dca,
    Jms,
    Jmp,
}

// DirectEa is the word address after page resolution (before indirect).
public sealed record MriOp(MriCode Code, int DirectEa, bool Indirect) : Pdp8Op;

// IOT: device number + pulse bits
public sealed record IotOp([UsedImplicitly] int Device, [UsedImplicitly] int Pulses) : Pdp8Op;

// OPR Group 1: micro-ops in fixed hardware order CLA/CLL → CMA/CML → rotate/BSW → IAC
// Bit assignments (C# LSB-0 notation):
//   bit7=CLA  bit6=CLL  bit5=CMA  bit4=CML
//   bit3=RotRight  bit2=RotLeft  bit1=TwoStep/BSW  bit0=IAC
// RTR = bit3+bit1; RTL = bit2+bit1; BSW = bit1 alone (no direction bits)
public sealed record Opr1Op(
    bool Cla,
    bool Cll,
    bool Cma,
    bool Cml,
    bool RotRight,
    bool RotLeft,
    bool TwoStep,
    bool Iac,
    bool Bsw
) : Pdp8Op;

// OPR Group 2: RSS bit (bit3) complements the natural OR skip sense.
//   OrMode=false (RSS=0): skip if any enabled condition is true   [SMA, SZA, SNL]
//   OrMode=true  (RSS=1): skip if none of the enabled conditions is true  [SPA, SNA, SZL, SKP]
public sealed record Opr2Op(
    bool Cla,
    bool Sma,
    bool Sza,
    bool Snl,
    bool Hlt,
    bool Osr,
    bool OrMode
) : Pdp8Op;

public sealed class Pdp8Instruction(
    ulong pc,
    int dest,
    IReadOnlyList<int> srcs,
    ToothClass cls,
    Pdp8Op op
) : ITooth {
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = (uint)((op as MriOp)?.DirectEa ?? 0);
    public int SizeBytes => 2;
    public int DestinationRegister => dest;
    public IReadOnlyList<int> SourceRegisters => srcs;
    public ToothClass Class => cls;
    public object Payload => op;
}