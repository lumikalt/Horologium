using Orrery.Observation;

namespace Face.Models;

public record WaterfallRow(ulong InstrId, ulong Pc, ulong SpecPc, IReadOnlyDictionary<long, PEventKind> Events);

public record WaterfallData(
    IReadOnlyList<WaterfallRow> Rows,
    long MinCycle,
    long MaxCycle,
    IReadOnlyDictionary<long, ulong> FetchPcPerCycle,
    IReadOnlySet<long> FlushCycles,
    IReadOnlySet<long> FetchStallCycles,
    ulong BasePc
);