namespace Orrery.Observation;

public enum PEventKind {
    Fetch,
    Decode,
    Dispatch,
    Issue,
    Execute,
    Retire,
    Flush,
    FetchStall,
}

public readonly record struct PEvent(
    ulong InstrId,
    ulong Pc,
    long Cycle,
    PEventKind Kind
);

/// <summary>
/// Accumulates per-instruction lifecycle events (Fetch/Dispatch/Issue/Execute/Retire/Flush)
/// emitted by pipeline cores during simulation. Pass an instance to a pipeline train
/// constructor to enable recording; null means zero overhead.
/// </summary>
public sealed class PEventLog {
    private readonly List<PEvent> _events = [];
    private readonly Dictionary<ulong, string> _disasm = [];

    public void Record(ulong instrId, ulong pc, long cycle, PEventKind kind) =>
        _events.Add(new PEvent(instrId, pc, cycle, kind));

    public void RecordDisasm(ulong instrId, string disasm) =>
        _disasm[instrId] = disasm;

    public IReadOnlyList<PEvent> Events => _events;

    public IReadOnlyDictionary<ulong, string> Disassembly => _disasm;

    public IEnumerable<PEvent> ForInstruction(ulong instrId) =>
        _events.Where(e => e.InstrId == instrId);

    public IEnumerable<PEvent> OfKind(PEventKind kind) =>
        _events.Where(e => e.Kind == kind);

    public IEnumerable<PEvent> InCycleRange(long from, long to) =>
        _events.Where(e => e.Cycle >= from && e.Cycle <= to);

    public void Reset() { _events.Clear(); _disasm.Clear(); }
}