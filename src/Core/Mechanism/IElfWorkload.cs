namespace Mechanism;

/// <summary>
///     An <see cref="IWorkload" /> loaded from an ELF image, exposing symbol lookup. ISA-agnostic —
///     any ELF-based workload (RV32, RV64, …) can implement this; it lets callers that need a
///     symbol address (e.g. Region-of-Interest fast-forward) accept any ELF workload without
///     matching on a specific ISA's concrete workload type.
/// </summary>
public interface IElfWorkload : IWorkload {
    /// <summary>Returns the virtual address of a named ELF symbol, or throws if not found.</summary>
    ulong FindSymbol(string name);
}
