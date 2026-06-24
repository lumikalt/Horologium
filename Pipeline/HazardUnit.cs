using Mechanism;

namespace Pipeline;

/// <summary>
/// Compact view of an in-flight instruction used by the hazard unit.
/// Ordered from newest (index 0, closest to EX) to oldest for stall detection;
/// oldest to newest for forwarding so that the newest source wins.
/// </summary>
/// <param name="IsValid">Whether this slot contains a real instruction.</param>
/// <param name="DestReg">Destination register, or ≤0 for none.</param>
/// <param name="Class">Instruction class — used to detect load-use hazards.</param>
/// <param name="ForwardValue">Forwarding value if already computed; null if not yet available.</param>
public readonly record struct PipelineResident(
    bool IsValid,
    int DestReg,
    ToothClass Class,
    ulong? ForwardValue
);

/// <summary>
/// Detects data and control hazards for a linear in-order pipeline of any depth.
///
/// Data hazards:
///   - RAW (Read After Write): a stage needs a value not yet written back.
///     Without forwarding: stall until the producing instruction reaches WB.
///     With forwarding: forward from any later stage that has the value ready.
///
/// Control hazards:
///   - Branch/jump resolved in EX: the caller flushes IF and ID (2-cycle penalty).
/// </summary>
public sealed class HazardUnit(bool forwardingEnabled) {
    /// <summary>
    /// Determines whether the instruction about to enter Decode must stall.
    /// A stall freezes IF and ID and inserts a bubble into EX.
    /// </summary>
    /// <param name="incomingSources">
    /// Source registers of the instruction about to enter Decode.
    /// </param>
    /// <param name="residents">
    /// In-flight instructions ordered newest-first (index 0 = currently in EX,
    /// index 1 = currently in MEM, etc.).
    /// </param>
    public bool MustStall(IReadOnlyList<int> incomingSources, IReadOnlyList<PipelineResident> residents) {
        if (incomingSources.Count == 0 || residents.Count == 0) return false;

        bool Reads(int dest) => dest > 0 && incomingSources.Any(src => src == dest);

        if (forwardingEnabled)
            // Load-use hazard: a load (or AMO) in the EX stage (index 0) produces its
            // value after MEM — too late to forward to a consumer entering EX next cycle.
            return residents[0] is { IsValid: true, Class: ToothClass.Load or ToothClass.Atomic, }
                && Reads(residents[0].DestReg);

        // Without forwarding, stall while any producer is still ahead in the pipeline.
        return residents.Any(r => r.IsValid && Reads(r.DestReg));
    }

    /// <summary>
    /// Computes forwarded Rs1, Rs2, Rs3 values for the instruction entering EX.
    /// </summary>
    /// <param name="rs1">Current Rs1 value (from the register file).</param>
    /// <param name="rs2">Current Rs2 value.</param>
    /// <param name="rs3">Rs3 for R4-type instructions (FMADD family); 0 otherwise.</param>
    /// <param name="sources">Source register indices of the instruction entering EX.</param>
    /// <param name="providers">
    /// Forwarding candidates ordered oldest-first (index 0 = closest to WB, lowest
    /// priority). Iterating oldest-to-newest ensures the most-recent result wins.
    /// </param>
    public (ulong rs1, ulong rs2, ulong rs3) Forward(
        ulong rs1,
        ulong rs2,
        ulong rs3,
        IReadOnlyList<int> sources,
        IReadOnlyList<PipelineResident> providers
    ) {
        if (!forwardingEnabled || sources.Count == 0) return (rs1, rs2, rs3);

        // Apply oldest to newest so the freshest result overwrites stale ones.
        foreach (PipelineResident p in providers) {
            if (!p.IsValid || p.DestReg <= 0 || !p.ForwardValue.HasValue) continue;
            ulong fwd = p.ForwardValue.Value;
            if (sources.Count > 0 && sources[0] == p.DestReg) rs1 = fwd;
            if (sources.Count > 1 && sources[1] == p.DestReg) rs2 = fwd;
            if (sources.Count > 2 && sources[2] == p.DestReg) rs3 = fwd;
        }

        return (rs1, rs2, rs3);
    }
}