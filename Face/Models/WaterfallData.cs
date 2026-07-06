using Orrery.Observation;

namespace Face.Models;

/// <summary>A contiguous stage bar for one instruction: stage, start cycle (inclusive), end cycle (exclusive).</summary>
public readonly record struct PSpan(PEventKind Stage, long Start, long End) {
    public long Duration => End - Start;
}

public record WaterfallRow(
    ulong InstrId,
    ulong Pc,
    ulong SpecPc,
    string Disassembly,
    IReadOnlyList<PSpan> Spans
);

public record WaterfallData(
    IReadOnlyList<WaterfallRow> Rows,
    long MinCycle,
    long MaxCycle,
    IReadOnlyDictionary<long, ulong> FetchPcPerCycle,
    IReadOnlySet<long> FlushCycles,
    IReadOnlySet<long> FetchStallCycles,
    ulong BasePc
);
