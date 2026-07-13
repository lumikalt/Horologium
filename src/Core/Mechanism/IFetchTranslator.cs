namespace Mechanism;

/// <summary>
///     Translates a virtual instruction-fetch address to a physical address.
///     Returns (physAddr, 0) on success or (0, faultCause) on a page fault.
///     Bare-mode implementations return (vaddr, 0) with no walk.
/// </summary>
public interface IFetchTranslator {
    /// <summary>Translates <paramref name="virtualPc" /> to a physical address; returns fault code on page fault.</summary>
    (ulong PhysAddr, int FaultCause) Translate(ulong virtualPc);
}