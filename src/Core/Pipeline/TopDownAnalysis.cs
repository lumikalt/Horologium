using System.Text;
using Orrery.Observation;

namespace Pipeline;

/// <summary>
///     Top-Down Microarchitecture Analysis (TMA) breakdown — Yasin, "A Top-Down Method for
///     Performance Analysis and Counters Architecture", ISPASS 2014.
///     <para>
///         Level 1 classifies every issue-pipeline slot (issueWidth × cycles, observed at the
///         dispatch stage — the frontend/backend border) into one of four categories per
///         Table 2 of the paper:
///     </para>
///     <code>
///         Frontend Bound  = FetchBubbles / TotalSlots
///         Bad Speculation = (SlotsIssued − SlotsRetired + RecoveryBubbles) / TotalSlots
///         Retiring        = SlotsRetired / TotalSlots
///         Backend Bound   = 1 − (Frontend Bound + Bad Speculation + Retiring)
///     </code>
///     <para>
///         Level 2 refines each category: fetch latency vs bandwidth within Frontend Bound,
///         branch mispredicts vs machine clears within Bad Speculation, memory vs core bound
///         within Backend Bound. Level-1 fractions are slot ratios and sum to 1. The level-2
///         memory/core/fetch-latency fractions are cycle ratios computed at a different
///         pipeline stage — per the paper's hierarchical-safety rule, only sibling nodes are
///         comparable, and an inner node is meaningful only when its parent is flagged.
///     </para>
/// </summary>
public sealed record TopDownBreakdown(
    long TotalSlots,
    double FrontendBound,
    double BadSpeculation,
    double Retiring,
    double BackendBound,
    double FetchLatencyBound,
    double FetchBandwidthBound,
    double BranchMispredicts,
    double MachineClears,
    double MemoryBound,
    double CoreBound
) {
    // Counter names shared between the recording train and this analysis. A train that
    // registers these (plus the pre-existing "cycles", "retired", "branch_misses" and
    // "flushes" counters) gets the full breakdown from FromSnapshot for free.
    public const string TotalSlotsCounter = "td_total_slots";
    public const string SlotsIssuedCounter = "td_slots_issued";
    public const string FetchBubblesCounter = "td_fetch_bubbles";
    public const string RecoveryBubblesCounter = "td_recovery_bubbles";
    public const string FetchLatencyCyclesCounter = "td_fetch_latency_cycles";
    public const string ExecStallCyclesCounter = "td_exec_stall_cycles";
    public const string MemStallLoadCyclesCounter = "td_memstall_load_cycles";
    public const string MemStallStoreCyclesCounter = "td_memstall_store_cycles";

    /// <summary>
    ///     Computes the breakdown from raw Top-Down event counts (Table 1 of the paper).
    ///     All slot-denominated inputs are in issue-pipeline slots; all cycle-denominated
    ///     inputs are in machine cycles.
    /// </summary>
    public static TopDownBreakdown Compute(
        long totalSlots,
        long slotsIssued,
        long slotsRetired,
        long fetchBubbles,
        long recoveryBubbles,
        long cycles,
        long fetchLatencyCycles,
        long execStallCycles,
        long memStallLoadCycles,
        long memStallStoreCycles,
        long branchMispredicts,
        long flushes
    ) {
        if (totalSlots <= 0 || cycles <= 0)
            return new TopDownBreakdown(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        double slots = totalSlots;
        double frontend = fetchBubbles / slots;
        double badSpec = Math.Max(0.0, (slotsIssued - slotsRetired + recoveryBubbles) / slots);
        double retiring = slotsRetired / slots;
        double backend = Math.Max(0.0, 1.0 - (frontend + badSpec + retiring));

        // Frontend split (Table 2, FetchBubbles[≥ MIW] / Clocks): whole-cycle fetch
        // starvation is latency; the remaining frontend share is bandwidth.
        double fetchLatency = Math.Min(frontend, fetchLatencyCycles / (double)cycles);
        double fetchBandwidth = Math.Max(0.0, frontend - fetchLatency);

        // Bad Speculation split (Table 2, #BrMispredFraction): every flush that was not a
        // branch misprediction — memory-order violation, trap, interrupt — is a machine clear.
        long clears = Math.Max(0, flushes - branchMispredicts);
        double mispredFraction = branchMispredicts + clears > 0
            ? branchMispredicts / (double)(branchMispredicts + clears)
            : 1.0;
        double branchShare = mispredFraction * badSpec;

        // Backend split (cycle ratios, per the paper's Memory Bound heuristic).
        double memory = Math.Min(1.0, (memStallLoadCycles + memStallStoreCycles) / (double)cycles);
        double core = Math.Max(0.0, execStallCycles / (double)cycles - memory);

        return new TopDownBreakdown(
            totalSlots,
            frontend,
            badSpec,
            retiring,
            backend,
            fetchLatency,
            fetchBandwidth,
            branchShare,
            badSpec - branchShare,
            memory,
            core
        );
    }

    /// <summary>
    ///     Extracts the breakdown from a pipeline gear's snapshot, or returns null when the
    ///     gear does not record Top-Down counters (in-order trains, cache gears, …).
    ///     Works on warmup-subtracted snapshots (<see cref="DialBoardSnapshot.Subtract" />)
    ///     because it reads only counters, never the run-lifetime dial values.
    /// </summary>
    public static TopDownBreakdown? FromSnapshot(DialBoardSnapshot snapshot) {
        if (!snapshot.Counters.TryGetValue(TopDownBreakdown.TotalSlotsCounter, out long totalSlots))
            return null;

        long Get(string name) => snapshot.Counters.GetValueOrDefault(name);

        return TopDownBreakdown.Compute(
            totalSlots,
            Get(TopDownBreakdown.SlotsIssuedCounter),
            Get("retired"),
            Get(TopDownBreakdown.FetchBubblesCounter),
            Get(TopDownBreakdown.RecoveryBubblesCounter),
            Get("cycles"),
            Get(TopDownBreakdown.FetchLatencyCyclesCounter),
            Get(TopDownBreakdown.ExecStallCyclesCounter),
            Get(TopDownBreakdown.MemStallLoadCyclesCounter),
            Get(TopDownBreakdown.MemStallStoreCyclesCounter),
            Get("branch_misses"),
            Get("flushes")
        );
    }

    public override string ToString() {
        var sb = new StringBuilder();
        sb.AppendLine($"Top-Down breakdown ({TotalSlots:N0} slots)");
        sb.AppendLine($"  Frontend Bound   {FrontendBound,7:P1}");
        sb.AppendLine($"    Fetch Latency    {FetchLatencyBound,7:P1}");
        sb.AppendLine($"    Fetch Bandwidth  {FetchBandwidthBound,7:P1}");
        sb.AppendLine($"  Bad Speculation  {BadSpeculation,7:P1}");
        sb.AppendLine($"    Branch Mispredicts {BranchMispredicts,7:P1}");
        sb.AppendLine($"    Machine Clears     {MachineClears,7:P1}");
        sb.AppendLine($"  Retiring         {Retiring,7:P1}");
        sb.AppendLine($"  Backend Bound    {BackendBound,7:P1}");
        sb.AppendLine($"    Memory Bound     {MemoryBound,7:P1} (of cycles)");
        sb.Append($"    Core Bound       {CoreBound,7:P1} (of cycles)");
        return sb.ToString();
    }
}
