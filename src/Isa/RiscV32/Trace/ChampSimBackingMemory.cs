using Mechanism;

namespace RiscV32.Trace;

/// <summary>
///     Backing store for a <see cref="Orrery.Cache.SetAssociativeCache" /> driven standalone by a
///     ChampSim trace replay. ChampSim addresses are foreign virtual addresses spanning the full
///     64-bit space with no relation to any Horologium-simulated program image, and trace replay
///     only needs cache hit/miss accounting — not real data — so reads return zero and writes are
///     discarded rather than backing the whole address space with real storage.
/// </summary>
public sealed class ChampSimBackingMemory : IMemory {
    public ulong Read(ulong address, int bytes) => 0;
    public void Write(ulong address, ulong value, int bytes) { }
    public void Load(ulong address, ReadOnlySpan<byte> data) { }
}