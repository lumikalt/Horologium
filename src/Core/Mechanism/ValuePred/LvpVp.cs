namespace Mechanism.ValuePred;

/// <summary>
///     Load Value Prediction Table (Lipasti &amp; Shen, MICRO 1996, "Exceeding the Dataflow Limit
///     via Value Prediction" — the LVPT scheme; extends the load-only LVP of Lipasti, Wilkerson
///     &amp; Shen, ASPLOS 1996 to any destination-register write). Direct-mapped, tagless,
///     PC-indexed table of last-seen values, gated by a <see cref="ForwardProbabilisticCounter" />
///     so a prediction is trusted only once the table has seen the same value recur enough times
///     in a row (probabilistically) to saturate confidence.
///     <para>
///         Tagless by design (matches the paper's practical "Simple" configuration and the VTAGE
///         paper's base component): aliasing between static instructions that map to the same
///         index is possible, and is treated the same as any other misprediction — replace the
///         value and reset confidence — since the producing instruction always executes for real
///         and any misprediction is caught and squashed regardless of the cause.
///     </para>
/// </summary>
public sealed class LvpVp : IValuePredictor {
    private readonly byte[] _confidence;
    private readonly ForwardProbabilisticCounter _fpc;
    private readonly int _mask;
    private readonly bool[] _valid;
    private readonly ulong[] _value;

    /// <param name="entries">Table size. Must be a power of two.</param>
    /// <param name="seed">Deterministic seed for the FPC's probabilistic transitions.</param>
    public LvpVp(int entries = 4096, int seed = 0) {
        _value = new ulong[entries];
        _confidence = new byte[entries];
        _valid = new bool[entries];
        _mask = entries - 1;
        _fpc = new ForwardProbabilisticCounter(seed);
    }

    /// <inheritdoc />
    public bool TryPredict(ulong pc, ValueHistoryCheckpoint history, out ulong value) {
        int idx = Idx(pc);
        if (_valid[idx] && ForwardProbabilisticCounter.IsSaturated(_confidence[idx])) {
            value = _value[idx];
            return true;
        }

        value = 0;
        return false;
    }

    /// <inheritdoc />
    public void Update(ulong pc, ValueHistoryCheckpoint history, ulong actualValue) {
        int idx = Idx(pc);
        if (!_valid[idx]) {
            // First encounter: seed the value history, no prediction was made to confirm.
            _valid[idx] = true;
            _value[idx] = actualValue;
            _confidence[idx] = 0;
            return;
        }

        if (_value[idx] == actualValue) { _fpc.OnCorrect(ref _confidence[idx]); }
        else {
            _value[idx] = actualValue;
            ForwardProbabilisticCounter.OnMispredict(ref _confidence[idx]);
        }
    }

    /// <summary>
    ///     Serializes the PC-indexed value/confidence table. Deliberately does not serialize
    ///     <see cref="_fpc" />'s internal RNG state — it is not exposed for serialization, and
    ///     re-seeding fresh only causes a benign statistical divergence in confidence-transition
    ///     timing, the same accepted tradeoff as every other RNG-backed component this checkpoint
    ///     format skips (see README.md).
    /// </summary>
    public void WriteState(BinaryWriter w) {
        w.Write(_value.Length);
        foreach (ulong v in _value) w.Write(v);
        foreach (byte c in _confidence) w.Write(c);
        foreach (bool valid in _valid) w.Write(valid);
    }

    /// <summary>Restores state written by <see cref="WriteState" />. Table size must match.</summary>
    public void ReadState(BinaryReader r) {
        int size = r.ReadInt32();
        int n = Math.Min(size, _value.Length);
        for (var i = 0; i < size; i++) {
            ulong v = r.ReadUInt64();
            if (i < n) _value[i] = v;
        }

        for (var i = 0; i < size; i++) {
            byte c = r.ReadByte();
            if (i < n) _confidence[i] = c;
        }

        for (var i = 0; i < size; i++) {
            bool valid = r.ReadBoolean();
            if (i < n) _valid[i] = valid;
        }
    }

    private int Idx(ulong pc) => (int)((pc >> 2) & (uint)_mask);
}