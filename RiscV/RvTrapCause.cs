namespace RiscV;

/// <summary>RISC-V mcause/scause codes. Bit 31 = 0 for exceptions, 1 for interrupts.</summary>
public static class RvTrapCause {
    // Exceptions (bit 31 = 0)
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

    // Interrupts (bit 31 = 1)
    public const int SupervisorSoftwareInterrupt = unchecked((int)0x80000001u);
    public const int MachineSoftwareInterrupt = unchecked((int)0x80000003u);
    public const int SupervisorTimerInterrupt = unchecked((int)0x80000005u);
    public const int MachineTimerInterrupt = unchecked((int)0x80000007u);
    public const int SupervisorExternalInterrupt = unchecked((int)0x80000009u);
    public const int MachineExternalInterrupt = unchecked((int)0x8000000Bu);

    /// <summary>Maps a mip/mie bit index to its interrupt cause code.</summary>
    public static int InterruptCause(int bit) => unchecked((int)(0x80000000u | (uint)bit));
}