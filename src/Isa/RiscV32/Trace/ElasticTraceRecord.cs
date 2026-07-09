namespace RiscV32.Trace;

public enum ElasticTraceType : byte {
    Comp = 0, Load = 1, Store = 2,
}

/// <summary>
/// One node in the dynamic dependence graph: an instruction together with its
/// register RAW producers (<see cref="RobDeps"/>) and memory RAW producers
/// (<see cref="AddrDeps"/>). Seqnos are dense, zero-based, and monotonically
/// increasing in program order.
/// </summary>
public sealed class ElasticTraceRecord(
    ulong seqNo,
    ulong pc,
    uint rawEncoding,
    ElasticTraceType type,
    uint compDelay,
    ulong vAddr,
    int accessSize,
    ulong[] robDeps,
    ulong[] addrDeps
) {
    public ulong SeqNo { get; } = seqNo;
    public ulong Pc { get; } = pc;
    public uint RawEncoding { get; } = rawEncoding;
    public ElasticTraceType Type { get; } = type;

    /// <summary>Estimated execution latency in cycles (from FuLatencyConfig defaults).</summary>
    public uint CompDelay { get; } = compDelay;

    /// <summary>Virtual effective address; zero for COMP records.</summary>
    public ulong VAddr { get; } = vAddr;

    public int AccessSize { get; } = accessSize;

    /// <summary>Seqnos of instructions that last wrote a register this instruction reads.</summary>
    public IReadOnlyList<ulong> RobDeps { get; } = robDeps;

    /// <summary>Seqnos of stores whose written address overlaps this load's address.</summary>
    public IReadOnlyList<ulong> AddrDeps { get; } = addrDeps;
}