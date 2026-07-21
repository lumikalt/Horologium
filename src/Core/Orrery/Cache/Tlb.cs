#region

using System.Numerics;
using Mechanism;

#endregion

namespace Orrery.Cache;

/// <summary>
///     Direct-mapped TLB wrapping an IMemory.
///     For bare-metal use (VA = PA) it installs identity mappings on miss.
///     A TLB miss charges MissLatency stall cycles recorded in PendingStalls.
/// </summary>
public sealed class Tlb : IMemory {
    private readonly int _indexMask;
    private readonly int _pageBits; // log2(pageSize)
    private readonly ulong _pageMask;
    private readonly IMemory _physical;
    private readonly ulong[] _ppns;

    private readonly ulong?[] _vpns; // null = invalid

    private long _pendingStalls;

    /// <param name="physical">Backing memory.</param>
    /// <param name="entries">Number of TLB entries. Must be a power of 2.</param>
    /// <param name="pageSizeBytes">Page size in bytes. Must be a power of 2.</param>
    /// <param name="missLatency">Extra cycles charged per miss.</param>
    public Tlb(IMemory physical, int entries, int pageSizeBytes, int missLatency) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(missLatency);
        if (!BitOperations.IsPow2(entries) || !BitOperations.IsPow2(pageSizeBytes))
            throw new ArgumentException("TLB entries and page size must be powers of 2.");

        _physical = physical;
        _indexMask = entries - 1;
        _pageBits = BitOperations.Log2((uint)pageSizeBytes);
        _pageMask = ~((1UL << _pageBits) - 1);
        MissLatency = missLatency;

        _vpns = new ulong?[entries];
        _ppns = new ulong[entries];
    }

    public int MissLatency { get; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    // ── IMemory ──────────────────────────────────────────────────────────────────

    public ulong Read(ulong address, int bytes) => _physical.Read(Translate(address), bytes);
    public void Write(ulong address, ulong value, int bytes) => _physical.Write(Translate(address), value, bytes);
    public void SetRequestPc(ulong pc) => _physical.SetRequestPc(pc);

    // Pass cache-maintenance ops through translation to the physical chain — otherwise a TLB
    // sitting above the D-cache in MemoryLayers.Accessor would silently swallow cbo.* calls
    // via IMemory's default no-op bodies before they ever reach the cache.
    public void InvalidateLine(ulong address) => _physical.InvalidateLine(Translate(address));
    public void CleanLine(ulong address) => _physical.CleanLine(Translate(address));
    public void FlushLine(ulong address) => _physical.FlushLine(Translate(address));

    public void Load(ulong address, ReadOnlySpan<byte> data) {
        // Invalidate TLB entries whose pages overlap the loaded region.
        ulong end = address + (ulong)data.Length;
        for (ulong a = address & _pageMask; a < end; a += 1UL << _pageBits) {
            ulong vpn = a >> _pageBits;
            var index = (int)(vpn & (ulong)_indexMask);
            if (_vpns[index] == vpn) _vpns[index] = null;
        }

        _physical.Load(address, data);
    }

    /// <summary>Returns and clears the accumulated miss-penalty cycle count.</summary>
    public long ConsumePendingStalls() {
        long s = _pendingStalls;
        _pendingStalls = 0;
        return s;
    }

    /// <summary>Serializes this TLB's entries for a microarchitectural checkpoint.</summary>
    public void WriteState(BinaryWriter w) {
        w.Write(_vpns.Length);
        foreach (ulong? vpn in _vpns) {
            w.Write(vpn.HasValue);
            if (vpn.HasValue) w.Write(vpn.Value);
        }

        foreach (ulong ppn in _ppns) w.Write(ppn);
    }

    /// <summary>Restores state written by <see cref="WriteState" />. Entry count must match.</summary>
    public void ReadState(BinaryReader r) {
        int entries = r.ReadInt32();
        int n = Math.Min(entries, _vpns.Length);
        for (var i = 0; i < entries; i++) {
            bool has = r.ReadBoolean();
            ulong vpn = has ? r.ReadUInt64() : 0;
            if (i < n) _vpns[i] = has ? vpn : null;
        }

        for (var i = 0; i < entries; i++) {
            ulong ppn = r.ReadUInt64();
            if (i < n) _ppns[i] = ppn;
        }
    }

    // ── Translation ─────────────────────────────────────────────────────────────

    private ulong Translate(ulong vAddress) {
        ulong vpn = vAddress >> _pageBits;
        var index = (int)(vpn & (ulong)_indexMask);

        if (_vpns[index] == vpn) {
            Hits++;
            return (_ppns[index] << _pageBits) | (vAddress & ~_pageMask);
        }

        // Miss: install identity mapping (PPN = VPN).
        Misses++;
        _pendingStalls += MissLatency;
        _vpns[index] = vpn;
        _ppns[index] = vpn;
        return vAddress; // identity
    }
}