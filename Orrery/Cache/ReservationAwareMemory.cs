using Mechanism;

namespace Orrery.Cache;

/// <summary>
/// Wraps a shared backing <see cref="IMemory"/> and cancels cross-hart LR/SC
/// reservations on every write.
///
/// Place this immediately above the shared physical memory (e.g. FlatMemory)
/// in the multi-hart memory stack.  Each hart's per-hart cache and TLB layers
/// sit above this wrapper; writes that penetrate to the shared backing
/// (write-through cache or committed-store path) automatically invalidate any
/// other hart's outstanding reservation that overlaps the written range.
/// </summary>
public sealed class ReservationAwareMemory(IMemory backing, ReservationTable table) : IMemory {
    public ulong Read(ulong address, int bytes) =>
        backing.Read(address, bytes);

    public void Write(ulong address, ulong value, int bytes) {
        table.InvalidateAt(address, bytes);
        backing.Write(address, value, bytes);
    }

    /// <summary>
    /// Pre-run program-image initialisation: loads bypass the reservation
    /// table (no hart is running yet) and go directly to the backing.
    /// </summary>
    public void Load(ulong address, ReadOnlySpan<byte> data) =>
        backing.Load(address, data);
}