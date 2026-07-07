namespace Orrery.Spec;

/// <summary>
/// Geometry for one TLB level on a cache path (I or D).
/// The TLB sits closest to the CPU, wrapping the entire cache stack.
/// For bare-metal use (VA = PA) it installs identity mappings on miss.
/// </summary>
public sealed record TlbSpec(
    int Entries,
    int PageBytes = 4096,
    int MissLatency = 20
);