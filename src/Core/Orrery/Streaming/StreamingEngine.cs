using Mechanism;

namespace Orrery.Streaming;

/// <summary>
/// ISA-agnostic streaming prefetch engine.
/// <para>
/// Manages up to <see cref="MaxStreams"/> independently configured affine memory streams.
/// Each call to <see cref="Step"/> advances every active stream by one prefetch step,
/// filling each stream's buffer up to the configured <c>prefetchDepth</c>. Software (or
/// executor SideEffects) configures streams; compute instructions consume elements via
/// <see cref="Consume"/>.
/// </para>
/// <para>
/// Streams are architectural state: they survive pipeline flushes. The pipeline should
/// call <see cref="Step"/> unconditionally every cycle, even during flush cycles.
/// </para>
/// </summary>
public sealed class StreamingEngine {
    public const int MaxStreams = 8;

    private readonly int _prefetchDepth;
    private readonly StreamState[] _streams;
    private int _activeCount; // tracks how many streams are currently active

    public StreamingEngine(int prefetchDepth = 4) {
        if (prefetchDepth < 1) throw new ArgumentOutOfRangeException(nameof(prefetchDepth));
        _prefetchDepth = prefetchDepth;
        _streams = new StreamState[StreamingEngine.MaxStreams];
        for (var i = 0; i < StreamingEngine.MaxStreams; i++) _streams[i] = new StreamState();
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

    /// <summary>Returns true when the stream was configured in vector delivery mode (ss.sta.ld.*_v).</summary>
    public bool IsVectorMode(int streamId) {
        Validate(streamId);
        return _streams[streamId].IsVectorMode;
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
    /// Advances each active stream by one prefetch step. Scalar streams read one element;
    /// vector-mode streams read up to <paramref name="vectorLength"/> elements, stopping at
    /// the vecCfgDim boundary so each Step delivers at most one complete vector slice.
    /// Call once per pipeline cycle.
    /// </summary>
    public void Step(IMemory memory, int vectorLength = 1) {
        if (_activeCount == 0) return;
        foreach (StreamState s in _streams) s.Step(memory, _prefetchDepth, vectorLength, _streams);
    }

    private static void Validate(int id) {
        if ((uint)id >= StreamingEngine.MaxStreams)
            throw new ArgumentOutOfRangeException(nameof(id), $"Stream ID must be 0–{StreamingEngine.MaxStreams - 1}.");
    }

    // ── Per-stream state ───────────────────────────────────────────────────────

    private sealed class StreamState {
        private StreamDescriptor _desc;

        // Per-dimension fetch and consume indices. Innermost = index 0.
        private long[] _fetchIndices = [];
        private long[] _consumeIndices = [];

        // Mutable per-odometer copies of dimension counts; updated by modifiers as each dim wraps.
        private long[] _fetchDimCounts = [];
        private long[] _consumeDimCounts = [];

        // Mutable per-dimension strides for the fetch side; updated by Stride modifiers.
        private long[] _fetchDimStrides = [];

        // Original configured values — base for Add/Sub indirect modifier calculations.
        private long[] _fetchDimCountsBase = [];
        private long[] _fetchDimStridesBase = [];
        private long _fetchBaseOffsetBase;

        // Cumulative base-address displacement for the fetch side; updated by Offset modifiers.
        private long _fetchBaseOffset;

        // True once the outermost fetch dimension has wrapped (all elements fetched).
        private bool _fetchDone;

        // True when indirect modifiers need their initial application (before the first fetch).
        private bool _needsInitialModApply;

        // Set by Consume() for each dimension that wraps; cleared at the start of the next Consume().
        private bool[] _dimPassComplete = [];

        // Per-modifier application counts (indexed by Modifiers[i]); separate for fetch/consume.
        private int[] _fetchModApplyCounts = [];
        private int[] _consumeModApplyCounts = [];

        // Per-modifier queue for indirect Size modifiers: fetch side enqueues the new count
        // so the consume-side odometer can apply matching updates when the dimension wraps.
        // Null entries = static modifier or non-Size indirect modifier.
        private Queue<long>?[] _indModSizeQueues = [];

        // Vector-mode coupling dimension. -1 = not vector mode; 0..N-1 = dimension that acts as the
        // vector boundary (resolved from IsVectorMode/VecCfgDim at Configure time; innermost = 0).
        private int _vecCfgDim = -1;

        private readonly Queue<ulong> _buffer = new();

        public bool Active { get; private set; }
        public bool HasElement => _buffer.Count > 0;
        public bool IsVectorMode => _vecCfgDim >= 0;

        public bool IsExhausted => Active && _fetchDone && _buffer.Count == 0;

        public bool IsDimPassComplete(int dim) =>
            Active && (uint)dim < (uint)_dimPassComplete.Length && _dimPassComplete[dim];

        public void Configure(StreamDescriptor desc) {
            _desc = desc;
            int ndim = desc.Dimensions.Length;
            _fetchIndices = new long[ndim];
            _consumeIndices = new long[ndim];
            _fetchDimCounts = new long[ndim];
            _consumeDimCounts = new long[ndim];
            _fetchDimStrides = new long[ndim];
            _fetchDimCountsBase = new long[ndim];
            _fetchDimStridesBase = new long[ndim];
            _dimPassComplete = new bool[ndim];
            for (var d = 0; d < ndim; d++) {
                _fetchDimCounts[d] = desc.Dimensions[d].Count;
                _consumeDimCounts[d] = desc.Dimensions[d].Count;
                _fetchDimStrides[d] = desc.Dimensions[d].Stride;
                _fetchDimCountsBase[d] = desc.Dimensions[d].Count;
                _fetchDimStridesBase[d] = desc.Dimensions[d].Stride;
            }

            _fetchBaseOffset = 0;
            _fetchBaseOffsetBase = 0;
            _fetchDone = false;
            _buffer.Clear();
            int nmod = desc.Modifiers?.Length ?? 0;
            _fetchModApplyCounts = nmod > 0 ? new int[nmod] : [];
            _consumeModApplyCounts = nmod > 0 ? new int[nmod] : [];

            // Allocate per-modifier size queues for indirect Size modifiers.
            var hasIndirect = false;
            _indModSizeQueues = new Queue<long>?[nmod];
            if (desc.Modifiers != null)
                for (var i = 0; i < nmod; i++) {
                    StreamModifier m = desc.Modifiers[i];
                    if (m.SourceStreamId >= 0) {
                        hasIndirect = true;
                        if (m.Target == StreamModifierTarget.Size) _indModSizeQueues[i] = new Queue<long>();
                    }
                }

            _needsInitialModApply = hasIndirect;

            // Resolve vector coupling dim: VecCfgDim=-1 (innermost) → dim 0 (Horologium innermost convention).
            _vecCfgDim = desc.IsVectorMode ? desc.VecCfgDim < 0 ? 0 : desc.VecCfgDim : -1;
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
            return val;
        }

        public void Step(IMemory memory, int prefetchDepth, int vectorLength, StreamState[] allStreams) {
            if (!Active) return;
            if (_fetchDone) return;
            if (_needsInitialModApply && !ApplyInitialIndirectModifiers(allStreams)) return;
            if (_buffer.Count >= prefetchDepth) return;

            int toFetch = _vecCfgDim >= 0 ? vectorLength : 1;
            for (var i = 0; i < toFetch; i++) {
                if (_buffer.Count >= prefetchDepth) break;
                if (_fetchDone) break;
                _buffer.Enqueue(memory.Read((ulong)((long)_desc.BaseAddress + FetchOffset()), _desc.ElementBytes));
                if (AdvanceFetchIndex(allStreams)) break;
            }
        }

        // Applies all indirect modifiers using their initial IndSource values.
        // Returns false (and defers) if any source stream has no element ready yet.
        private bool ApplyInitialIndirectModifiers(StreamState[] allStreams) {
            if (_desc.Modifiers is not { Length: > 0, } mods) {
                _needsInitialModApply = false;
                return true;
            }

            for (var i = 0; i < mods.Length; i++)
                if (mods[i].SourceStreamId >= 0 && !allStreams[mods[i].SourceStreamId].HasElement)
                    return false;
            for (var i = 0; i < mods.Length; i++) {
                StreamModifier m = mods[i];
                if (m.SourceStreamId < 0) continue;
                var rawVal = (long)(int)allStreams[m.SourceStreamId].Consume();
                long newVal = CalculateIndirectValue(m, rawVal, m.DimIndex);
                ApplyToFetchField(m.Target, m.DimIndex, newVal);
                if (m.Target == StreamModifierTarget.Size) _consumeDimCounts[m.DimIndex] = Math.Max(0, newVal);
            }

            _needsInitialModApply = false;
            return true;
        }

        private long FetchOffset() {
            long offset = _fetchBaseOffset;
            for (var d = 0; d < _fetchIndices.Length; d++) offset += _fetchIndices[d] * _fetchDimStrides[d];
            return offset;
        }

        // Returns true when _vecCfgDim wrapped (vector slice boundary) or stream is done.
        // The carry always propagates fully so _fetchIndices is consistent for the next call.
        private bool AdvanceFetchIndex(StreamState[] allStreams) {
            var boundary = false;
            for (var d = 0; d < _fetchDimCounts.Length; d++) {
                if (++_fetchIndices[d] < _fetchDimCounts[d]) return boundary;
                _fetchIndices[d] = 0;
                ApplyFetchModifiers(d, allStreams);
                if (d == _fetchDimCounts.Length - 1) {
                    _fetchDone = true;
                    return true;
                }

                if (d == _vecCfgDim) boundary = true;
            }

            return boundary;
        }

        private void AdvanceConsumeIndex() {
            Array.Fill(_dimPassComplete, false);
            for (var d = 0; d < _consumeDimCounts.Length; d++) {
                if (++_consumeIndices[d] < _consumeDimCounts[d]) return; // no wrap
                _consumeIndices[d] = 0;
                _dimPassComplete[d] = true;
                ApplyConsumeModifiers(d);
                // continue loop to carry into d+1
            }
        }

        private void ApplyFetchModifiers(int wrappedDim, StreamState[] allStreams) {
            if (_desc.Modifiers is not { Length: > 0, } mods) return;
            for (var i = 0; i < mods.Length; i++) {
                StreamModifier m = mods[i];
                if (m.DimIndex != wrappedDim) continue;
                if (m.MaxApplications > 0 && _fetchModApplyCounts[i] >= m.MaxApplications) continue;
                _fetchModApplyCounts[i]++;
                if (m.SourceStreamId >= 0) {
                    // Indirect modifier: consume one element from the IndSource stream.
                    if (!allStreams[m.SourceStreamId].HasElement) continue;
                    var rawVal = (long)(int)allStreams[m.SourceStreamId].Consume();
                    long newVal = CalculateIndirectValue(m, rawVal, wrappedDim);
                    ApplyToFetchField(m.Target, wrappedDim, newVal);
                    if (m.Target == StreamModifierTarget.Size && _indModSizeQueues[i] is { } q) q.Enqueue(newVal);
                }
                else {
                    // Static modifier.
                    long delta = m.Behavior == StreamModifierBehavior.Inc ? m.Displacement : -m.Displacement;
                    switch (m.Target) {
                        case StreamModifierTarget.Size:
                            _fetchDimCounts[wrappedDim] = Math.Max(0, _fetchDimCounts[wrappedDim] + delta);
                            break;
                        case StreamModifierTarget.Stride: _fetchDimStrides[wrappedDim] += delta; break;
                        case StreamModifierTarget.Offset: _fetchBaseOffset += delta; break;
                    }
                }
            }
        }

        private void ApplyConsumeModifiers(int wrappedDim) {
            if (_desc.Modifiers is not { Length: > 0, } mods) return;
            for (var i = 0; i < mods.Length; i++) {
                StreamModifier m = mods[i];
                if (m.DimIndex != wrappedDim || m.Target != StreamModifierTarget.Size) continue;
                if (m.MaxApplications > 0 && _consumeModApplyCounts[i] >= m.MaxApplications) continue;
                _consumeModApplyCounts[i]++;
                if (m.SourceStreamId >= 0) {
                    // Indirect: dequeue the count pre-computed by the fetch side.
                    // Queue may be empty if IndSource was exhausted when the fetch side wrapped.
                    if (_indModSizeQueues[i] is { Count: > 0, } q)
                        _consumeDimCounts[wrappedDim] = Math.Max(0, q.Dequeue());
                }
                else {
                    // Static modifier.
                    long delta = m.Behavior == StreamModifierBehavior.Inc ? m.Displacement : -m.Displacement;
                    _consumeDimCounts[wrappedDim] = Math.Max(0, _consumeDimCounts[wrappedDim] + delta);
                }
            }
        }

        // Computes the new field value for an indirect modifier given the raw IndSource element.
        private long CalculateIndirectValue(StreamModifier m, long rawVal, int dim) => m.Behavior switch {
            StreamModifierBehavior.Add => GetBase(m.Target, dim) + rawVal,
            StreamModifierBehavior.Sub => GetBase(m.Target, dim) - rawVal,
            StreamModifierBehavior.Set => rawVal,
            StreamModifierBehavior.Inc => GetCurrent(m.Target, dim) + rawVal,
            StreamModifierBehavior.Dec => GetCurrent(m.Target, dim) - rawVal,
            _                          => rawVal,
        };

        private long GetBase(StreamModifierTarget target, int dim) => target switch {
            StreamModifierTarget.Size   => _fetchDimCountsBase[dim],
            StreamModifierTarget.Stride => _fetchDimStridesBase[dim],
            _                           => _fetchBaseOffsetBase,
        };

        private long GetCurrent(StreamModifierTarget target, int dim) => target switch {
            StreamModifierTarget.Size   => _fetchDimCounts[dim],
            StreamModifierTarget.Stride => _fetchDimStrides[dim],
            _                           => _fetchBaseOffset,
        };

        private void ApplyToFetchField(StreamModifierTarget target, int dim, long newVal) {
            switch (target) {
                case StreamModifierTarget.Size:   _fetchDimCounts[dim] = Math.Max(0, newVal); break;
                case StreamModifierTarget.Stride: _fetchDimStrides[dim] = newVal; break;
                case StreamModifierTarget.Offset: _fetchBaseOffset = newVal; break;
            }
        }
    }
}