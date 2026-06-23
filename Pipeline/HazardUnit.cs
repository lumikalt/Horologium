using Mechanism;

namespace Pipeline;

/// <summary>
/// Detects data and control hazards in the 5-stage pipeline.
///
/// Data hazards:
///   - RAW (Read After Write): a stage needs a value not yet written back.
///     Without forwarding: stall until the producing instruction reaches WB.
///     With forwarding: forward from EX/MEM or MEM/WB latch.
///
/// Control hazards:
///   - Branch/jump resolved in EX: flush IF and ID stages (2-cycle penalty).
/// </summary>
public sealed class HazardUnit(bool forwardingEnabled) {
    /// <summary>
    /// Determines whether the instruction about to enter Decode must stall.
    /// A stall freezes IF and ID and inserts a bubble into EX.
    /// </summary>
    /// <param name="incomingSources">
    /// Source registers of the instruction the Decode stage will read this
    /// cycle. Decode reads the register file, so the hazard is against this
    /// instruction's read — not against whatever is already past Decode.
    /// </param>
    /// <param name="exResident">Instruction currently occupying the EX stage.</param>
    /// <param name="memResident">Instruction currently occupying the MEM stage.</param>
    public bool MustStall(
        IReadOnlyList<int> incomingSources,
        IdExLatch exResident,
        ExMemLatch memResident
    ) {
        if (incomingSources.Count == 0) return false;

        bool Reads(int dest) =>
            dest > 0 && incomingSources.Any(src => src == dest);

        if (forwardingEnabled)
            // With forwarding, only stall on a load-use hazard: a load (or AMO)
            // in EX produces its value after MEM — too late to forward to a
            // consumer entering EX next cycle, so the consumer waits one cycle.
            return exResident is {
                       IsValid: true,
                       Instruction.Class: ToothClass.Load or ToothClass.Atomic,
                   }
                && Reads(exResident.DestinationRegister);

        // Without forwarding, Decode must wait until the producer reaches WB
        // (Writeback runs before Decode's read this cycle). So stall while the
        // producer is still in EX or MEM.
        return (exResident.IsValid && Reads(exResident.DestinationRegister))
            || (memResident.IsValid && Reads(memResident.DestinationRegister));
    }

    /// <summary>
    /// Computes forwarding mux selections for the EX stage.
    /// Returns the actual Rs1, Rs2, and Rs3 values after forwarding.
    /// Rs3 is used by R4-type instructions (FMADD family); zero for all others.
    /// </summary>
    public (ulong rs1, ulong rs2, ulong rs3) Forward(
        IdExLatch idEx,
        ExMemLatch exMem,
        MemWbLatch memWb
    ) {
        ulong rs1 = idEx.Rs1Value;
        ulong rs2 = idEx.Rs2Value;
        ulong rs3 = idEx.Rs3Value;

        if (!forwardingEnabled) return (rs1, rs2, rs3);

        IReadOnlyList<int>? sources = idEx.Instruction?.SourceRegisters;
        if (sources is null || sources.Count == 0) return (rs1, rs2, rs3);

        // Forward from MEM/WB first (lower priority — older), then EX/MEM (higher
        // priority — more recent) so that EX/MEM wins when both match the same register.
        if (memWb.IsValid && memWb is { DestinationRegister: > 0, WritebackValue: not null, }) {
            ulong fwd = memWb.WritebackValue!.Value;
            if (sources.Count > 0 && sources[0] == memWb.DestinationRegister) rs1 = fwd;
            if (sources.Count > 1 && sources[1] == memWb.DestinationRegister) rs2 = fwd;
            if (sources.Count > 2 && sources[2] == memWb.DestinationRegister) rs3 = fwd;
        }

        if (exMem is { IsValid: true, DestinationRegister: > 0, } &&
            exMem.Result?.RegisterResult.HasValue == true) {
            ulong fwd = exMem.Result.RegisterResult!.Value;
            if (sources.Count > 0 && sources[0] == exMem.DestinationRegister) rs1 = fwd;
            if (sources.Count > 1 && sources[1] == exMem.DestinationRegister) rs2 = fwd;
            if (sources.Count > 2 && sources[2] == exMem.DestinationRegister) rs3 = fwd;
        }

        return (rs1, rs2, rs3);
    }
}