namespace RiscV32.Trace;

/// <summary>
///     One decoded record from a ChampSim binary trace (the <c>input_instr</c> struct from
///     ChampSim's <c>inc/trace_instruction.h</c>). ChampSim traces carry no static decode
///     information — only the dynamic PC, branch outcome, and up to two store / four load
///     addresses per instruction (0 = unused slot).
/// </summary>
public readonly record struct ChampSimTraceRecord(
    ulong Ip,
    bool IsBranch,
    bool BranchTaken,
    byte[] DestinationRegisters,
    byte[] SourceRegisters,
    ulong[] DestinationMemory,
    ulong[] SourceMemory
);