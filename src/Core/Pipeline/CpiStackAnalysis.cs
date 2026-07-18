using System.Text;
using Orrery.Observation;

namespace Pipeline;

/// <summary>
///     Deepest memory-hierarchy level a load/atomic missed at execute time. Recorded in the
///     instruction's ROB entry and consulted when the instruction later blocks the head of a
///     full ROB, to attribute the blocked cycles to the right CPI-stack component
///     (Eyerman et al., ASPLOS 2006, section 4.2/4.3).
/// </summary>
public enum CpiMissClass : byte {
    None,
    L1D,
    L2D,
    L3D,
    DTlb,
}

/// <summary>
///     CPI stack computed via interval analysis — Eyerman, Eeckhout, Karkhanis &amp; Smith,
///     "A Performance Counter Architecture for Computing Accurate CPI Components",
///     ASPLOS 2006 (the accounting Sniper's CPI stacks build on; the underlying interval
///     model is Eyerman et al., ACM TOCS 2009).
///     <para>
///         Total CPI is broken into a base plus per-miss-event components, each measured in
///         lost cycles per retired instruction:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Frontend</b>: I-cache / I-TLB miss delays (posted only when the stalled
///             fetch turns out to be on the correct path — wrong-path fetch penalties are
///             absorbed into the branch misprediction component), and the branch
///             misprediction penalty measured per the interval model as the mispredicted
///             branch's ROB residency (dispatch → redirect, excluding cycles already
///             claimed by backend components) plus the pipeline refill time.
///         </item>
///         <item>
///             <b>Backend</b>: cycles a full ROB is blocked by an incomplete instruction at
///             its head, classified by the deepest cache level the blocking load missed
///             (short L1 misses vs long L2/L3/D-TLB misses), by post-commit store write
///             stalls, or — for non-loads and loads that hit — as long-latency unit /
///             dependence resource stalls.
///         </item>
///     </list>
///     <para>
///         Base is the residual: total cycles minus all component cycles, i.e. the cycles
///         where the machine streamed instructions at (or near) its steady-state rate.
///     </para>
/// </summary>
public sealed record CpiStack(
    long TotalCycles,
    long RetiredInstructions,
    double Total,
    double Base,
    double L1ICache,
    double L2ICache,
    double L3ICache,
    double ITlb,
    double BranchMisprediction,
    double L1DCache,
    double L2DCache,
    double L3DCache,
    double DTlb,
    double Store,
    double ResourceStall
) {
    // Counter names shared between the recording train and this analysis. A train that
    // records these (plus the pre-existing "cycles" and "retired" counters) gets the full
    // stack from FromSnapshot for free.
    public const string L1ICounter = "cpi_l1i_cycles";
    public const string L2ICounter = "cpi_l2i_cycles";
    public const string L3ICounter = "cpi_l3i_cycles";
    public const string ITlbCounter = "cpi_itlb_cycles";
    public const string BpredCounter = "cpi_bpred_cycles";
    public const string L1DCounter = "cpi_l1d_cycles";
    public const string L2DCounter = "cpi_l2d_cycles";
    public const string L3DCounter = "cpi_l3d_cycles";
    public const string DTlbCounter = "cpi_dtlb_cycles";
    public const string StoreCounter = "cpi_store_cycles";
    public const string ResourceCounter = "cpi_resource_cycles";

    /// <summary>Sum of all miss-event components (everything except Base).</summary>
    public double MissComponents =>
        L1ICache + L2ICache + L3ICache + ITlb + BranchMisprediction
      + L1DCache + L2DCache + L3DCache + DTlb + Store + ResourceStall;

    /// <summary>Computes the stack from raw per-component lost-cycle counts.</summary>
    public static CpiStack Compute(
        long cycles,
        long retired,
        long l1ICycles,
        long l2ICycles,
        long l3ICycles,
        long iTlbCycles,
        long bpredCycles,
        long l1DCycles,
        long l2DCycles,
        long l3DCycles,
        long dTlbCycles,
        long storeCycles,
        long resourceCycles
    ) {
        if (cycles <= 0 || retired <= 0)
            return new CpiStack(cycles, retired, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        double PerInstr(long c) => c / (double)retired;

        long missCycles = l1ICycles + l2ICycles + l3ICycles + iTlbCycles + bpredCycles
                        + l1DCycles + l2DCycles + l3DCycles + dTlbCycles + storeCycles + resourceCycles;

        return new CpiStack(
            cycles,
            retired,
            PerInstr(cycles),
            PerInstr(Math.Max(0, cycles - missCycles)),
            PerInstr(l1ICycles),
            PerInstr(l2ICycles),
            PerInstr(l3ICycles),
            PerInstr(iTlbCycles),
            PerInstr(bpredCycles),
            PerInstr(l1DCycles),
            PerInstr(l2DCycles),
            PerInstr(l3DCycles),
            PerInstr(dTlbCycles),
            PerInstr(storeCycles),
            PerInstr(resourceCycles)
        );
    }

    /// <summary>
    ///     Extracts the stack from a pipeline gear's snapshot, or returns null when the gear
    ///     does not record CPI-stack counters. Works on warmup-subtracted snapshots
    ///     (<see cref="DialBoardSnapshot.Subtract" />) because it reads only counters.
    /// </summary>
    public static CpiStack? FromSnapshot(DialBoardSnapshot snapshot) {
        if (!snapshot.Counters.ContainsKey(CpiStack.BpredCounter)) return null;

        long Get(string name) => snapshot.Counters.GetValueOrDefault(name);

        return CpiStack.Compute(
            Get("cycles"),
            Get("retired"),
            Get(CpiStack.L1ICounter),
            Get(CpiStack.L2ICounter),
            Get(CpiStack.L3ICounter),
            Get(CpiStack.ITlbCounter),
            Get(CpiStack.BpredCounter),
            Get(CpiStack.L1DCounter),
            Get(CpiStack.L2DCounter),
            Get(CpiStack.L3DCounter),
            Get(CpiStack.DTlbCounter),
            Get(CpiStack.StoreCounter),
            Get(CpiStack.ResourceCounter)
        );
    }

    public override string ToString() {
        var sb = new StringBuilder();
        sb.AppendLine($"CPI stack ({RetiredInstructions:N0} instructions, {TotalCycles:N0} cycles, CPI {Total:F3})");
        void Row(string name, double value) {
            if (value > 0) sb.AppendLine($"  {name,-12} {value,8:F4}");
        }

        Row("base", Base);
        Row("L1 I-cache", L1ICache);
        Row("L2 I-cache", L2ICache);
        Row("L3 I-cache", L3ICache);
        Row("I-TLB", ITlb);
        Row("bpred", BranchMisprediction);
        Row("L1 D-cache", L1DCache);
        Row("L2 D-cache", L2DCache);
        Row("L3 D-cache", L3DCache);
        Row("D-TLB", DTlb);
        Row("stores", Store);
        Row("resource", ResourceStall);
        return sb.ToString().TrimEnd();
    }
}
