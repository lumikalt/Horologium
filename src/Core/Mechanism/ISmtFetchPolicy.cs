namespace Mechanism;

/// <summary>
///     Selects which hart receives each issue slot in an SMT (barrel-processor) core —
///     the fetch/issue arbitration policy studied by Tullsen et al., ISCA 1996
///     ("Exploiting Choice: Instruction Fetch and Issue on an Implementable Simultaneous
///     Multithreading Processor").
///     <para>
///         Called once per candidate issue slot, in the order the core is trying to fill them
///         for the current cycle. Implementations track only per-hart priority state — they
///         have no access to architectural state or memory.
///     </para>
/// </summary>
public interface ISmtFetchPolicy {
    /// <summary>
    ///     Called once per cycle before any slot selection, so the policy can (re)size internal
    ///     per-hart state and roll forward any once-per-cycle bookkeeping (e.g. round-robin's
    ///     rotating start hart).
    /// </summary>
    void BeginCycle(int hartCount);

    /// <summary>
    ///     Picks the next hart to receive an issue slot. <paramref name="available" /> is indexed
    ///     by hart id; a hart is available if it hasn't issued yet this cycle, isn't halted, and
    ///     hasn't been cut off by an earlier issue this cycle (e.g. a taken branch). Returns -1 if
    ///     no hart is available.
    /// </summary>
    int SelectHart(ReadOnlySpan<bool> available);

    /// <summary>
    ///     Notifies the policy that <paramref name="hart" /> issued this cycle and incurred
    ///     <paramref name="stallCycles" /> of cache/TLB stall as a result. Round-robin ignores
    ///     this; ICOUNT uses it as a stand-in for front-end occupancy (see
    ///     <c>Mechanism.SmtFetchPolicies.IcountFetchPolicy</c>).
    /// </summary>
    void OnIssued(int hart, long stallCycles);
}