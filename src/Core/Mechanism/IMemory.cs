namespace Mechanism;

/// <summary>
///     The memory subsystem visible to the ISA executor.
///     <para>
///         Addresses are byte-addressed. Width is specified per-access.
///         The implementation handles alignment, endianness, and memory-mapped I/O.
///     </para>
/// </summary>
public interface IMemory {
    /// <summary>Reads <paramref name="bytes" /> bytes from <paramref name="address" />.</summary>
    ulong Read(ulong address, int bytes);

    /// <summary>
    ///     Reads <paramref name="bytes" /> bytes from <paramref name="address" /> without changing
    ///     any cache state — no hit/miss counters, no replacement-policy update, no line fill or
    ///     eviction, no MSHR allocation (InvisiSpec's speculative-buffer peek, Yan et al., MICRO
    ///     2018). Default forwards to <see cref="Read" />, correct for any implementation with no
    ///     cache state to protect (backing DRAM, capturing/wrapper memories); cache implementations
    ///     override it to do a true non-mutating peek.
    /// </summary>
    ulong PeekRead(ulong address, int bytes) => Read(address, bytes);

    /// <summary>Writes <paramref name="value" /> (<paramref name="bytes" /> wide) to <paramref name="address" />.</summary>
    void Write(ulong address, ulong value, int bytes);

    /// <summary>
    ///     Loads a byte array into memory starting at <paramref name="address" />.
    ///     Used to initialise memory with a program image before a Revolution.
    /// </summary>
    void Load(ulong address, ReadOnlySpan<byte> data);

    /// <summary>
    ///     Invalidates the cache block containing <paramref name="address" />, discarding any
    ///     dirty data without writing it back to backing (cbo.inval).
    ///     No-op on non-cache implementations.
    /// </summary>
    void InvalidateLine(ulong address) { }

    /// <summary>
    ///     Writes back the cache block containing <paramref name="address" /> if it is dirty,
    ///     leaving it valid in the cache (cbo.clean).
    ///     No-op on non-cache implementations.
    /// </summary>
    void CleanLine(ulong address) { }

    /// <summary>
    ///     Writes back the cache block containing <paramref name="address" /> if it is dirty,
    ///     then invalidates it (cbo.flush).
    ///     No-op on non-cache implementations.
    /// </summary>
    void FlushLine(ulong address) { }

    /// <summary>
    ///     Notifies the memory subsystem of the PC of the instruction about to issue
    ///     a memory request.  Used by SHiP-PC to index the SHCT by load PC rather than
    ///     by memory address.  Must be called before the corresponding Read/Write.
    ///     No-op on implementations that do not use PC-based signatures.
    /// </summary>
    void SetRequestPc(ulong pc) { }

    /// <summary>
    ///     Notifies the memory subsystem of the Security-Domain ID (SDID) of the requester about to
    ///     issue a memory request. Used by ScatterCache to compute a domain-dependent mapping. Must
    ///     be called before the corresponding Read/Write. No-op on implementations that do not use
    ///     SDID-based indexing.
    /// </summary>
    void SetRequestSdid(int sdid) { }
}