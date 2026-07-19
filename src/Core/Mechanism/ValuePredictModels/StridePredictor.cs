namespace Mechanism.ValuePredictModels;

/// <summary>
///     A stride ("computational", per Sazeides &amp; Smith's predictor taxonomy as summarized in
///     Perais &amp; Seznec, HPCA 2014 §2) value predictor, named "2-delta" after the classic
///     stride-predictor family (Eickemeyer &amp; Vassiliadis, IBM J. Res. Dev. 1993) that hybridizes
///     with VTAGE in that paper's own experiments (§7.1.1/§7.1.2). This is not a reproduction of
///     that 1993 paper's specific mechanism — it wasn't consulted directly — but a confidence-FSM
///     stride predictor built to the same "computational predictor to hybridize with VTAGE" brief,
///     using a state machine in the spirit of 2-delta stride's namesake requirement (see below).
///     Rather than recognizing a repeated value like <see cref="LvpPredictor" />, it tracks the last
///     value produced by a static instruction and the constant stride between successive
///     occurrences, predicting <c>lastValue + stride</c>. This makes it complementary to
///     value-repetition predictors (LVP/VTAGE): a monotonically incrementing counter never repeats a
///     value (so LVP/VTAGE's confidence never saturates) but has a trivially constant stride.
///     <para>
///         Confidence is the FSM itself (no separate saturating counter layered on top): 4 states
///         <c>Init</c>, <c>Transient</c>, <c>Steady</c>, <c>NoPred</c>. A match advances <c>Init -&gt;
///         Transient -&gt; Steady</c> (predicting only once <c>Steady</c> is reached — two consecutive
///         matching strides, the "2-delta" in the name) and <c>NoPred -&gt; Transient</c>; a mismatch
///         drops <c>Steady -&gt; Init</c> and <c>Transient -&gt; NoPred</c> while leaving
///         <c>Init</c>/<c>NoPred</c> in place — so one bad guess right after warmup costs nothing, but
///         breaking an established (<c>Steady</c>) or nearly-established (<c>Transient</c>) pattern is
///         punished immediately rather than gradually.
///     </para>
///     <para>
///         Tagless and PC-indexed, like <see cref="LvpPredictor" />: aliasing between static
///         instructions mapping to the same index is possible and is treated like any other
///         misprediction, since the producing instruction always executes for real regardless.
///     </para>
/// </summary>
public sealed class StridePredictor : IValuePredictor {
    private readonly ulong[] _lastValue;
    private readonly int _mask;
    private readonly State[] _state;
    private readonly long[] _stride;
    private readonly bool[] _valid;

    /// <param name="entries">Table size. Must be a power of two.</param>
    public StridePredictor(int entries = 8192) {
        _valid = new bool[entries];
        _lastValue = new ulong[entries];
        _stride = new long[entries];
        _state = new State[entries];
        _mask = entries - 1;
    }

    /// <inheritdoc />
    public bool TryPredict(ulong pc, ValueHistoryCheckpoint history, out ulong value) {
        int idx = Idx(pc);
        if (_valid[idx] && _state[idx] == State.Steady) {
            value = unchecked(_lastValue[idx] + (ulong)_stride[idx]);
            return true;
        }

        value = 0;
        return false;
    }

    /// <inheritdoc />
    public void Update(ulong pc, ValueHistoryCheckpoint history, ulong actualValue) {
        int idx = Idx(pc);
        if (!_valid[idx]) {
            // First encounter: nothing to compute a stride against yet.
            _valid[idx] = true;
            _lastValue[idx] = actualValue;
            _stride[idx] = 0;
            _state[idx] = State.Init;
            return;
        }

        var newStride = unchecked((long)(actualValue - _lastValue[idx]));
        bool matched = newStride == _stride[idx];
        _state[idx] = _state[idx] switch {
            State.Init => matched ? State.Transient : State.Init,
            State.Transient => matched ? State.Steady : State.NoPred,
            State.Steady => matched ? State.Steady : State.Init,
            State.NoPred => matched ? State.Transient : State.NoPred,
            _ => State.Init,
        };
        _stride[idx] = newStride;
        _lastValue[idx] = actualValue;
    }

    private int Idx(ulong pc) => (int)((pc >> 2) & (uint)_mask);

    private enum State : byte { Init, Transient, Steady, NoPred, }
}
