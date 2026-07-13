using Mechanism;

namespace Orrery.Cache;

/// <summary>
///     Top-of-chain router that sends accesses inside a memory-mapped-I/O window
///     straight to <paramref name="backing" /> (uncached), and everything else through
///     the <paramref name="cached" /> chain.
///     <para>
///         MMIO must not be cached: a device updates the backing through side effects the
///         cache never sees (the HTIF auto-ACK writes <c>fromhost</c> to the backing,
///         which sits below the cache), so a cached copy goes stale — a poll loop then
///         spins on the stale value forever. Routing those addresses past the cache keeps
///         the device coherent.
///     </para>
/// </summary>
public sealed class UncacheableMemory(IMemory cached, IMemory backing, ulong baseAddr, ulong size) : IMemory {
    public ulong Read(ulong address, int bytes) =>
        IsMmio(address) ? backing.Read(address, bytes) : cached.Read(address, bytes);

    public void Write(ulong address, ulong value, int bytes) {
        if (IsMmio(address))
            backing.Write(address, value, bytes);
        else
            cached.Write(address, value, bytes);
    }

    // Program-image loading goes straight to the backing (pre-run initialisation).
    public void Load(ulong address, ReadOnlySpan<byte> data) => backing.Load(address, data);

    public void SetRequestPc(ulong pc) => cached.SetRequestPc(pc);
    private bool IsMmio(ulong address) => address >= baseAddr && address < baseAddr + size;
}