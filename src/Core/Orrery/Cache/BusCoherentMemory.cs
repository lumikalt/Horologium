#region

using Mechanism;

#endregion

namespace Orrery.Cache;

/// <summary>
///     <see cref="IMemory" /> adapter for a hart with no private cache sitting on the same
///     coherence bus as harts that do have one.
///     <para>
///         Wiring an uncached hart straight to the bus's backing memory is unsound whenever a
///         peer holds a line in a dirty (Modified/Owned) state: that peer's write never reaches
///         backing until eviction, so a plain backing read observes stale data, and a plain
///         backing write silently overwrites the peer's cached copy without telling it to
///         invalidate — a real coherence hole, not merely an LR/SC edge case. This wrapper routes
///         every access through the bus's snoop paths first, mirroring exactly what
///         <see cref="MoesifCache" /> already does for its own block-boundary-crossing accesses
///         (<see cref="IBus.BusSyncToBacking" /> before a direct read, <see cref="IBus.BusReadInvalidate" />
///         before a direct write), just applied unconditionally rather than only at cache-line
///         boundaries.
///     </para>
///     <para>
///         When <see cref="IBus.BlockBytes" /> is 0 (no cache has ever registered on this bus —
///         every hart is uncached) the snoop calls are no-ops and this class behaves exactly like
///         wiring straight to <see cref="IBus.Backing" />.
///     </para>
/// </summary>
public sealed class BusCoherentMemory(IBus bus) : IMemory {
    public ulong Read(ulong address, int bytes) {
        ForEachLine(address, bytes, bus.BusSyncToBacking);
        return bus.Backing.Read(address, bytes);
    }

    public void Write(ulong address, ulong value, int bytes) {
        ForEachLine(address, bytes, lineBase => bus.BusReadInvalidate(null, lineBase));
        bus.Backing.Write(address, value, bytes);
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        bus.Backing.Load(address, data);
        ForEachLine(address, data.Length, bus.BusLoad);
    }

    public void SetRequestPc(ulong pc) => bus.Backing.SetRequestPc(pc);

    private void ForEachLine(ulong address, int length, Action<ulong> perLine) {
        int blockBytes = bus.BlockBytes;
        if (blockBytes <= 0) return;
        ulong lineBase = address & ~(ulong)(blockBytes - 1);
        ulong end = address + (ulong)length;
        for (ulong a = lineBase; a < end; a += (ulong)blockBytes) perLine(a);
    }
}
