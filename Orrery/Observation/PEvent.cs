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
    private readonly Dictionary<ulong, (IReadOnlyList<int> RegIdxs, IReadOnlyList<ulong> Values)> _srcVals = [];
    private readonly Dictionary<ulong, (int RegIdx, ulong Value)> _destVals = [];

    public void Record(ulong instrId, ulong pc, long cycle, PEventKind kind) =>
        _events.Add(new PEvent(instrId, pc, cycle, kind));

    public void RecordDisasm(ulong instrId, string disasm) =>
        _disasm[instrId] = disasm;

    public void RecordSourceValues(ulong instrId, IReadOnlyList<int> regIdxs, IReadOnlyList<ulong> values) =>
        _srcVals[instrId] = (regIdxs, values);

    public void RecordDestValue(ulong instrId, int regIdx, ulong value) =>
        _destVals[instrId] = (regIdx, value);

    public IReadOnlyList<PEvent> Events => _events;

    public IReadOnlyDictionary<ulong, string> Disassembly => _disasm;

    public bool TryGetSourceValues(ulong instrId, out IReadOnlyList<int> regIdxs, out IReadOnlyList<ulong> values) {
        if (_srcVals.TryGetValue(instrId, out (IReadOnlyList<int> RegIdxs, IReadOnlyList<ulong> Values) v)) {
            regIdxs = v.RegIdxs;
            values = v.Values;
            return true;
        }

        regIdxs = [];
        values = [];
        return false;
    }

    public bool TryGetDestValue(ulong instrId, out int regIdx, out ulong value) {
        if (_destVals.TryGetValue(instrId, out (int RegIdx, ulong Value) v)) {
            regIdx = v.RegIdx;
            value = v.Value;
            return true;
        }

        regIdx = -1;
        value = 0;
        return false;
    }

    public IEnumerable<PEvent> ForInstruction(ulong instrId) =>
        _events.Where(e => e.InstrId == instrId);

    public IEnumerable<PEvent> OfKind(PEventKind kind) =>
        _events.Where(e => e.Kind == kind);

    public IEnumerable<PEvent> InCycleRange(long from, long to) =>
        _events.Where(e => e.Cycle >= from && e.Cycle <= to);

    public void Reset() {
        _events.Clear();
        _disasm.Clear();
        _srcVals.Clear();
        _destVals.Clear();
    }
}