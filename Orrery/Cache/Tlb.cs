using System.Numerics;
using Mechanism;

namespace Orrery.Cache;

/// <summary>
/// Direct-mapped TLB wrapping an IMemory.
/// For bare-metal use (VA = PA) it installs identity mappings on miss.
/// A TLB miss charges MissLatency stall cycles recorded in PendingStalls.
/// </summary>
public sealed class Tlb : IMemory {
    private readonly IMemory _physical;
    private readonly int _indexMask;
    private readonly int _pageBits; // log2(pageSize)
    private readonly ulong _pageMask;

    private readonly ulong?[] _vpns; // null = invalid
    private readonly ulong[] _ppns;

    private long _pendingStalls;

    public int MissLatency { get; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }

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

    /// <summary>Returns and clears the accumulated miss-penalty cycle count.</summary>
    public long ConsumePendingStalls() {
        long s = _pendingStalls;
        _pendingStalls = 0;
        return s;
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

    // ── IMemory ──────────────────────────────────────────────────────────────────

    public ulong Read(ulong address, int bytes) => _physical.Read(Translate(address), bytes);
    public void Write(ulong address, ulong value, int bytes) => _physical.Write(Translate(address), value, bytes);

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
}