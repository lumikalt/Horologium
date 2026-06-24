namespace RiscV;

/// <summary>RISC-V mcause codes for exception traps (bit 31 = 0).</summary>
public static class RvTrapCause {
    public const int InstructionAddressMisaligned = 0;
    public const int InstructionAccessFault = 1;
    public const int IllegalInstruction = 2;
    public const int Breakpoint = 3;
    public const int LoadAddressMisaligned = 4;
    public const int LoadAccessFault = 5;
    public const int StoreAddressMisaligned = 6;
    public const int StoreAccessFault = 7;
    public const int EnvironmentCallFromU = 8;
    public const int EnvironmentCallFromS = 9;
    public const int EnvironmentCallFromM = 11;
    public const int InstructionPageFault = 12;
    public const int LoadPageFault = 13;
    public const int StorePageFault = 15;
}