using Mechanism;
using Orrery.Observation;

namespace RiscV32.Analysis;

// Hooks into the commit stream to measure the kernel-only interval demarcated by
// the riscv-tests benchmark setStats(1) / setStats(0) calls.  On the first commit
// at setStatsPc (setStats(1) entry) a DialBoard snapshot is taken as the baseline;
// on the second (setStats(0) entry) a second snapshot is taken.  KernelDelta
// returns the counter-subtracted snapshot with ipc/cpi recomputed from the delta.
internal sealed class SetStatsObserver(ulong setStatsPc, Func<DialBoardSnapshot> snapshotFunc)
    : ICommitObserver {
    private int _callCount;
    private DialBoardSnapshot? _baseline;
    private DialBoardSnapshot? _kernelEnd;

    public DialBoardSnapshot? KernelDelta {
        get {
            if (_baseline is null || _kernelEnd is null) return null;
            var delta = _kernelEnd.Subtract(_baseline);
            long r = delta.Counters.GetValueOrDefault("retired");
            long c = delta.Counters.GetValueOrDefault("cycles");
            var dials = delta.Dials.ToDictionary(kv => kv.Key, kv => kv.Key switch {
                "ipc" => c == 0 ? 0.0 : r / (double)c,
                "cpi" => r == 0 ? 0.0 : c / (double)r,
                _     => kv.Value,
            });
            return delta with { Dials = dials };
        }
    }

    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        if (pc != setStatsPc) return;
        _callCount++;
        if (_callCount == 1) _baseline  = snapshotFunc();
        else if (_callCount == 2) _kernelEnd = snapshotFunc();
    }
}
