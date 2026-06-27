using Mechanism;

namespace Orrery.Streaming;

/// <summary>
/// ISA-agnostic streaming prefetch engine.
///
/// Manages up to <see cref="MaxStreams"/> independently configured affine memory streams.
/// Each call to <see cref="Step"/> advances every active stream by one prefetch step,
/// filling each stream's buffer up to the configured <c>prefetchDepth</c>. Software (or
/// executor SideEffects) configures streams; compute instructions consume elements via
/// <see cref="Consume"/>.
///
/// Streams are architectural state: they survive pipeline flushes. The pipeline should
/// call <see cref="Step"/> unconditionally every cycle, even during flush cycles.
/// </summary>
public sealed class StreamingEngine {
    public const int MaxStreams = 8;

    private readonly int _prefetchDepth;
    private readonly StreamState[] _streams;
    private int _activeCount; // tracks how many streams are currently active

    public StreamingEngine(int prefetchDepth = 4) {
        if (prefetchDepth < 1) throw new ArgumentOutOfRangeException(nameof(prefetchDepth));
        _prefetchDepth = prefetchDepth;
        _streams = new StreamState[MaxStreams];
        for (var i = 0; i < MaxStreams; i++) _streams[i] = new StreamState();
    }

    /// <summary>
    /// Configures and activates a stream. Replaces any existing configuration on
    /// <paramref name="streamId"/> and resets the read position to the first element.
    /// </summary>
    public void Configure(int streamId, StreamDescriptor descriptor) {
        Validate(streamId);
        StreamState s = _streams[streamId];
        if (!s.Active) _activeCount++;
        s.Configure(descriptor);
    }

    /// <summary>Deactivates a stream and discards its prefetch buffer.</summary>
    public void Deactivate(int streamId) {
        Validate(streamId);
        StreamState s = _streams[streamId];
        if (s.Active) _activeCount--;
        s.Deactivate();
    }

    /// <summary>Returns true if the stream has been configured and not yet deactivated.</summary>
    public bool IsActive(int streamId) {
        Validate(streamId);
        return _streams[streamId].Active;
    }

    /// <summary>Returns true if the prefetch buffer holds at least one element ready to consume.</summary>
    public bool HasElement(int streamId) {
        Validate(streamId);
        return _streams[streamId].HasElement;
    }

    /// <summary>Returns true when all elements across all dimensions have been fetched AND consumed.</summary>
    public bool IsExhausted(int streamId) {
        Validate(streamId);
        return _streams[streamId].IsExhausted;
    }

    /// <summary>
    /// Returns true when dimension <paramref name="dim"/> wrapped during the most recent
    /// <see cref="Consume"/> call on this stream (consume-side odometer, not fetch-side).
    /// False if the stream is inactive or <paramref name="dim"/> is out of range.
    /// </summary>
    public bool IsDimPassComplete(int streamId, int dim) {
        Validate(streamId);
        return _streams[streamId].IsDimPassComplete(dim);
    }

    /// <summary>Returns the next buffered element without advancing the consume pointer.</summary>
    /// <exception cref="InvalidOperationException">The buffer is empty.</exception>
    public ulong Peek(int streamId) {
        Validate(streamId);
        return _streams[streamId].Peek();
    }

    /// <summary>Removes and returns the next buffered element.</summary>
    /// <exception cref="InvalidOperationException">The buffer is empty.</exception>
    public ulong Consume(int streamId) {
        Validate(streamId);
        return _streams[streamId].Consume();
    }

    /// <summary>
    /// Advances each active stream by one prefetch step: reads one element from memory into
    /// the buffer if the buffer has room and elements remain. Call once per pipeline cycle.
    /// </summary>
    public void Step(IMemory memory) {
        if (_activeCount == 0) return;
        foreach (StreamState s in _streams) s.Step(memory, _prefetchDepth);
    }

    private static void Validate(int id) {
        if ((uint)id >= MaxStreams)
            throw new ArgumentOutOfRangeException(nameof(id), $"Stream ID must be 0–{MaxStreams - 1}.");
    }

    // ── Per-stream state ───────────────────────────────────────────────────────

    private sealed class StreamState {
        private StreamDescriptor _desc;
        // Per-dimension fetch and consume indices. Innermost = index 0.
        private long[] _fetchIndices  = [];
        private long[] _consumeIndices = [];
        // Set by Consume() for each dimension that wraps; cleared at the start of the next Consume().
        private bool[] _dimPassComplete = [];
        private long _totalFetched;
        private long _totalConsumed;
        private readonly Queue<ulong> _buffer = new();

        public bool Active { get; private set; }
        public bool HasElement => _buffer.Count > 0;

        public bool IsExhausted {
            get {
                if (!Active) return false;
                long total = TotalCount();
                return _totalFetched >= total && _buffer.Count == 0;
            }
        }

        public bool IsDimPassComplete(int dim) =>
            Active && (uint)dim < (uint)_dimPassComplete.Length && _dimPassComplete[dim];

        public void Configure(StreamDescriptor desc) {
            _desc = desc;
            int ndim = desc.Dimensions.Length;
            _fetchIndices    = new long[ndim];
            _consumeIndices  = new long[ndim];
            _dimPassComplete = new bool[ndim];
            _totalFetched  = 0;
            _totalConsumed = 0;
            _buffer.Clear();
            Active = true;
        }

        public void Deactivate() {
            Active = false;
            _buffer.Clear();
        }

        public ulong Peek() {
            if (_buffer.Count == 0) throw new InvalidOperationException("Stream buffer is empty.");
            return _buffer.Peek();
        }

        public ulong Consume() {
            if (_buffer.Count == 0) throw new InvalidOperationException("Stream buffer is empty.");
            ulong val = _buffer.Dequeue();
            AdvanceConsumeIndex();
            _totalConsumed++;
            return val;
        }

        public void Step(IMemory memory, int prefetchDepth) {
            if (!Active) return;
            if (_buffer.Count >= prefetchDepth) return;
            if (_totalFetched >= TotalCount()) return;

            ulong addr = (ulong)((long)_desc.BaseAddress + FetchOffset());
            ulong element = memory.Read(addr, _desc.ElementBytes);
            _buffer.Enqueue(element);
            _totalFetched++;
            AdvanceFetchIndex();
        }

        private long TotalCount() {
            long total = 1;
            foreach (StreamDimension d in _desc.Dimensions) total *= d.Count;
            return total;
        }

        private long FetchOffset() {
            long offset = 0;
            for (int d = 0; d < _fetchIndices.Length; d++)
                offset += _fetchIndices[d] * _desc.Dimensions[d].Stride;
            return offset;
        }

        private void AdvanceFetchIndex() {
            for (int d = 0; d < _fetchIndices.Length; d++) {
                if (++_fetchIndices[d] < _desc.Dimensions[d].Count) break;
                _fetchIndices[d] = 0;
            }
        }

        private void AdvanceConsumeIndex() {
            Array.Fill(_dimPassComplete, false);
            for (int d = 0; d < _consumeIndices.Length; d++) {
                if (++_consumeIndices[d] < _desc.Dimensions[d].Count) break;
                _consumeIndices[d] = 0;
                _dimPassComplete[d] = true;
            }
        }
    }
}
