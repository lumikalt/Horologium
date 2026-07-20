#region

using System.Numerics;

#endregion

namespace Orrery.Cache;

/// <summary>
///     Multi-way sequential stream buffer prefetcher (Jouppi, ISCA 1990).
///     Maintains <see cref="StreamCount" /> independent stream buffers in parallel.
///     When a cache miss hits the head of a stream, that stream advances its
///     prefetch frontier; when no stream matches, the LRU stream is evicted and
///     restarted at the missed address.  On a new stream creation, <see cref="Depth" />
///     lines are issued at once to fill the conceptual buffer; on each subsequent
///     sequential access, one more line is issued to keep the frontier at exactly
///     <see cref="Depth" /> lines ahead of the demand pointer.
/// </summary>
public sealed class StreamPrefetcher : IPrefetcher {
    private readonly ulong _blockBytes;
    private readonly ulong _blockMask;

    private readonly StreamEntry[] _streams;
    private int _tick;

    public StreamPrefetcher(int streamCount = 4, int depth = 8, int blockBytes = 32) {
        if (!BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("blockBytes must be a power of 2.", nameof(blockBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(streamCount);
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        _streams = new StreamEntry[streamCount];
        Depth = depth;
        _blockBytes = (ulong)blockBytes;
        _blockMask = ~(_blockBytes - 1);
    }

    public int StreamCount => _streams.Length;
    public int Depth { get; }

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        if (Depth == 0) return 0;
        ulong lineBase = address & _blockMask;

        // Check all stream buffers for a sequential match.  We advance on both
        // hits and misses: in our model prefetched lines land in the L1 cache
        // rather than a separate buffer, so a demand hit on a prefetched line
        // is the equivalent of Jouppi's "stream buffer hit".  Not advancing on
        // hits would break the stream as soon as prefetching succeeds.
        for (var i = 0; i < _streams.Length; i++) {
            if (!_streams[i].Valid) continue;
            if (lineBase != _streams[i].DemandLine + _blockBytes) continue;

            _streams[i].DemandLine = lineBase;
            _streams[i].LruAge = ++_tick;

            // Issue one prefetch to keep the frontier exactly Depth lines ahead.
            ulong front = _streams[i].PrefetchFront;
            if (front <= lineBase + (ulong)Depth * _blockBytes && !targets.IsEmpty) {
                targets[0] = front;
                _streams[i].PrefetchFront = front + _blockBytes;
                return 1;
            }

            return 0; // frontier already Depth+ lines ahead — buffer full
        }

        // No stream matched.  On a cache miss, allocate a new stream and issue
        // Depth lines at once to fill the buffer (Jouppi §4.1: "begins prefetching
        // successive lines starting at the miss target").
        if (wasHit) return 0;

        int slot = FindLruSlot();
        _streams[slot].Valid = true;
        _streams[slot].DemandLine = lineBase;
        _streams[slot].LruAge = ++_tick;

        var count = 0;
        ulong next = lineBase + _blockBytes;
        while (count < Depth && count < targets.Length) {
            targets[count++] = next;
            next += _blockBytes;
        }

        _streams[slot].PrefetchFront = next;
        return count;
    }

    private int FindLruSlot() {
        for (var i = 0; i < _streams.Length; i++)
            if (!_streams[i].Valid)
                return i;
        var oldest = 0;
        for (var i = 1; i < _streams.Length; i++)
            if (_streams[i].LruAge < _streams[oldest].LruAge)
                oldest = i;
        return oldest;
    }

    private struct StreamEntry {
        public ulong DemandLine;    // last accessed (hit or miss) line base address
        public ulong PrefetchFront; // next line base to issue as a prefetch
        public int LruAge;
        public bool Valid;
    }
}