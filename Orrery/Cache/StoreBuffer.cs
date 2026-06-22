using Mechanism;
using Orrery.Scheduling;

namespace Orrery.Cache;

/// <summary>
/// A store buffer that defers writes to backing memory and forwards recent
/// stores to loads at the same address.
///
/// Entries are tagged with the tick they were written. DrainEligible() (called
/// at Phase.Collection) commits entries from previous ticks to backing storage,
/// giving loads in the same tick a forwarding window of exactly one cycle.
///
/// Partial-overlap reads (e.g., a byte load from a word-store address) force
/// an immediate DrainAll so backing memory is consistent.
/// </summary>
public sealed class StoreBuffer : IMemory {
    private readonly record struct Entry(ulong Address, ulong Value, int Bytes, long Tick);

    private readonly IMemory _backing;
    private readonly Escapement _esc;
    private readonly Entry[] _ring;
    private int _head;
    private int _count;

    public int Capacity => _ring.Length;
    public long Forwards { get; private set; }

    public StoreBuffer(IMemory backing, Escapement esc, int capacity) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _backing = backing;
        _esc = esc;
        _ring = new Entry[capacity];
    }

    public ulong Read(ulong address, int bytes) {
        // Scan newest-to-oldest for a forwarding match.
        for (int i = _count - 1; i >= 0; i--) {
            ref readonly Entry e = ref _ring[(_head + i) % Capacity];
            if (!Overlaps(e.Address, e.Bytes, address, bytes)) continue;
            if (e.Address == address && e.Bytes == bytes) {
                Forwards++;
                return e.Value;
            }
            // Partial overlap: drain so backing is coherent, then re-read.
            DrainAll();
            return _backing.Read(address, bytes);
        }
        return _backing.Read(address, bytes);
    }

    public void Write(ulong address, ulong value, int bytes) {
        if (_count == Capacity) DrainAll(); // capacity safety valve
        _ring[(_head + _count) % Capacity] = new Entry(address, value, bytes, _esc.CurrentTick);
        _count++;
    }

    /// <summary>
    /// Commits entries written in earlier ticks to backing memory.
    /// Call at Phase.Collection so stores from the current tick stay buffered
    /// for one more cycle and can be forwarded to loads in the next tick.
    /// </summary>
    public void DrainEligible() {
        long t = _esc.CurrentTick;
        while (_count > 0) {
            ref readonly Entry e = ref _ring[_head];
            if (e.Tick >= t) break;
            _backing.Write(e.Address, e.Value, e.Bytes);
            _head = (_head + 1) % Capacity;
            _count--;
        }
    }

    /// <summary>Commits all buffered entries to backing memory immediately.</summary>
    // Bulk load bypasses the buffer and goes directly to backing (initialization path).
    public void Load(ulong address, ReadOnlySpan<byte> data) => _backing.Load(address, data);

    public void DrainAll() {
        while (_count > 0) {
            ref readonly Entry e = ref _ring[_head];
            _backing.Write(e.Address, e.Value, e.Bytes);
            _head = (_head + 1) % Capacity;
            _count--;
        }
    }

    private static bool Overlaps(ulong aAddr, int aBytes, ulong bAddr, int bBytes) {
        ulong aEnd = aAddr + (ulong)aBytes;
        ulong bEnd = bAddr + (ulong)bBytes;
        return aAddr < bEnd && bAddr < aEnd;
    }
}
