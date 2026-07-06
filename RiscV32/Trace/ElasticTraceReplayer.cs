namespace RiscV32.Trace;

/// <summary>
/// The result of a critical-path replay.
/// </summary>
/// <param name="TotalCycles">
/// Length of the critical path in cycles. This is an <b>upper-bound IPC</b> estimate:
/// the model is infinite-width with zero structural hazards — only register and memory
/// dataflow latency limits throughput. Real-hardware IPC will be lower.
/// </param>
/// <param name="InstructionCount">Total instructions replayed.</param>
public record ReplayResult(ulong TotalCycles, long InstructionCount) {
    public double Ipc => TotalCycles == 0 ? 0 : (double)InstructionCount / TotalCycles;
}

/// <summary>
/// Computes the critical path through a Horologium elastic DDG trace.
/// <para>
/// Each instruction's completion time is:
/// <c>max(completion[dep] for dep in robDeps ∪ addrDeps) + compDelay</c>
/// The returned <see cref="ReplayResult.TotalCycles"/> is the maximum completion time
/// across all instructions — the dataflow critical path length.
/// </para>
/// <para>
/// Memory: O(n) in the number of instructions. For very large traces (billions of
/// instructions), consider streaming with periodic horizon pruning.
/// </para>
/// </summary>
public static class ElasticTraceReplayer {
    public static ReplayResult Replay(IEnumerable<ElasticTraceRecord> records) {
        var completion = new Dictionary<ulong, ulong>();
        ulong criticalPath = 0;
        long  count        = 0;

        foreach (ElasticTraceRecord rec in records) {
            ulong readyAt = 0;
            foreach (ulong dep in rec.RobDeps)
                if (completion.TryGetValue(dep, out ulong t) && t > readyAt) readyAt = t;
            foreach (ulong dep in rec.AddrDeps)
                if (completion.TryGetValue(dep, out ulong t) && t > readyAt) readyAt = t;

            ulong done = readyAt + rec.CompDelay;
            completion[rec.SeqNo] = done;
            if (done > criticalPath) criticalPath = done;
            count++;
        }

        return new ReplayResult(criticalPath, count);
    }
}
