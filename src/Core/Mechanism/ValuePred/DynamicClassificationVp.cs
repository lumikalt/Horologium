namespace Mechanism.ValuePred;

/// <summary>
///     Dynamic classification hybrid predictor (Rychlik, Faistl, Krug, Kurland, Sung, Velev &amp;
///     Shen, "Efficient and Accurate Value Prediction Using Dynamic Classification", CMU CMuART
///     tech report CMuART-1998-01, §3.2.3 "Efficient Dynamic Scheme (Zero Overlap)"). While
///     <see cref="HybridVp" /> always queries and always trains both components,
///     gating only on agreement, this assigns each static instruction to <em>at most one</em>
///     part, matching the paper's stated goal of avoiding wasted table space in components
///     that would never help a given PC.
///     <para>
///         <b>3-predictor paper mapped onto Horologium's 2-component hybrid:</b> the paper classifies
///         each instruction, after a 3-value learning period (its Unclassified-instruction Table,
///         "UT"), by the deltas between consecutive values: all-zero deltas &#8594; Popular Last Value,
///         equal (possibly zero) deltas &#8594; Stride+, otherwise &#8594; FCM (the general, most
///         expensive predictor, used as the catch-all for anything the cheaper two can't capture).
///         Horologium's <see cref="HybridVp" /> composition only has two component roles
///         (context-based and computational) rather than three, and <see cref="VtageVp" />
///         already composes a tagless LVP base internally — so Popular Last Value has no separate
///         existence here to classify into. This predictor therefore folds the paper's 3-way split
///         into 2: <em>equal consecutive deltas (including zero)</em> &#8594; the computational
///         component (typically <see cref="StrideVp" />), <em>anything else</em> &#8594; the
///         context-based component (typically <see cref="VtageVp" />, playing the paper's
///         FCM/general-catch-all role). This is a defensible reduction, not an attempt to reproduce a
///         3-way split Horologium's component set doesn't have.
///     </para>
///     <para>
///         <b>Classify / Predict / Update / Evict</b> (paper's own operation names): a PC starts
///         Unclassified and accumulates a rolling 3-value history (<c>h=3</c>, matching the paper) on
///         each <see cref="Update" /> — the classified component sees nothing yet, exactly as
///         described ("the UT entry is deallocated" once classified; no history replay into the
///         newly assigned component is modeled, since the paper doesn't describe one). Once
///         classified, every future <see cref="TryPredict" />/<see cref="Update" /> for that PC routes
///         to exactly one part. If a classified PC's own component predicts confidently
///         (arming eviction — see below) and then, on some later occurrence, fails to predict at all,
///         it is evicted: per the paper, eviction from the FCM-role component (context) is permanent
///         ("Don't Predict" forever, since FCM is the most general predictor and if even that fails
///         the PC is deemed unpredictable by anything), while eviction from the cheaper component
///         (computational) sends the PC back to Unclassified, relearning against whatever new pattern
///         emerged.
///     </para>
///     <para>
///         <b>Eviction trigger (an adaptation, not the paper's literal mechanism):</b> the paper
///         detects "confidence reaches zero" using each predictor's own internal confidence counter,
///         which <see cref="IValuePredictor" /> doesn't expose. Tracking "what I predicted at Rename
///         vs. what actually committed" per dynamic instance to detect a real misprediction would need
///         per-instance state a single PC-indexed slot can't hold once multiple occurrences are
///         in flight (see <see cref="StrideVp" />'s own doc comment for exactly this class of
///         problem). Instead, eviction here fires when the assigned component's own
///         <see cref="IValuePredictor.TryPredict" /> returns <c>false</c>, for a PC that has predicted
///         confidently (<c>true</c>) at least once since being classified, <em>N</em> times in a row
///         (<c>evictThreshold</c>, default 2). Cheap, observable purely from the outside at Predict
///         time, and immune to the in-flight-overlap problem since it never needs to match a specific
///         dynamic instance to its outcome.
///     </para>
///     <para>
///         <b>
///             Consecutive-miss threshold -- tightened, still an adapted proxy, not the paper's literal
///             mechanism.
///         </b>
///         The paper's Predict operation has three outcomes: confident (predict),
///         low-but-nonzero confidence (no prediction, no eviction), and confidence exactly zero
///         (evict). Collapsing "low but nonzero" and "zero" into a single boolean
///         <see cref="IValuePredictor.TryPredict" /> result still means a single ordinary,
///         ungraceful miss looks identical to a genuinely collapsed component from this class's own
///         vantage point -- there is no way to ask <see cref="IValuePredictor" /> "was that confidence
///         near zero or merely nonzero." What the threshold buys back is the paper's actual shape of
///         the failure: a context-classified PC backed by <see cref="VtageVp" /> (whose whole premise
///         is ~95%, not 100%, accuracy) is no longer permanently dropped to Don't Predict on its first
///         ordinary miss -- it now needs <c>evictThreshold</c> consecutive ones, tracking "confidence
///         has actually bottomed out" more closely than the previous evict-on-first-miss rule did.
///         <c>_missStreak</c> resets to zero on the next confident prediction (a single recovered
///         guess is evidence the component hasn't collapsed) and is cleared by the same squash-reset
///         discipline as <c>_armed</c> (see <see cref="RecoverSpeculativeHistory" />/
///         <see cref="RestoreHistory" />), since a wrong-path miss shouldn't count toward a real
///         eviction decision.
///     </para>
///     <para>
///         Tagless and PC-indexed throughout (classification table, history table, and the "armed"
///         bit), matching this file family's existing convention (see <see cref="LvpVp" />):
///         no tag-checked associative tables like the paper's actual CT/UT, so aliasing between
///         static instructions is possible and treated like any other misprediction.
///     </para>
///     <para>
///         Not modeled/known limitation: a PC that reclassifies while occurrences of its old
///         component are still in flight (predicted under the old assignment, not yet committed) can
///         desync that component's own internal bookkeeping (e.g. <see cref="StrideVp" />'s
///         in-flight-depth counter never sees the matching <see cref="Update" />, since the commit
///         routes to the new component instead) — bounded by the squash-reset already in place and by
///         eviction being rare, not chased further.
///     </para>
/// </summary>
public sealed class DynamicClassificationVp : IValuePredictor {
    private readonly bool[] _armed;
    private readonly Classification[] _classification;
    private readonly IValuePredictor _computational;
    private readonly IValuePredictor _context;
    private readonly int _evictThreshold;
    private readonly ulong[] _h1;
    private readonly ulong[] _h2;
    private readonly ulong[] _h3;
    private readonly int[] _historyCount;
    private readonly int _mask;
    private readonly int[] _missStreak;

    /// <param name="context">The context-based (history-driven) component, e.g. <see cref="VtageVp" />.</param>
    /// <param name="computational">The computational component, e.g. <see cref="StrideVp" />.</param>
    /// <param name="entries">Classification/history table size. Must be a power of two.</param>
    /// <param name="evictThreshold">
    ///     Consecutive non-confident <see cref="TryPredict" /> calls (since the assigned component
    ///     last predicted confidently) required to evict a classified PC. See this class's doc
    ///     comment for why this exists as an adapted proxy for the paper's own confidence-reaches-zero
    ///     trigger. Must be at least 1 (1 reproduces the previous evict-on-first-miss behavior).
    /// </param>
    public DynamicClassificationVp(
        IValuePredictor context,
        IValuePredictor computational,
        int entries = 8192,
        int evictThreshold = 2
    ) {
        _context = context;
        _computational = computational;
        _classification = new Classification[entries];
        _historyCount = new int[entries];
        _h1 = new ulong[entries];
        _h2 = new ulong[entries];
        _h3 = new ulong[entries];
        _armed = new bool[entries];
        _missStreak = new int[entries];
        _mask = entries - 1;
        _evictThreshold = Math.Max(1, evictThreshold);
    }

    /// <inheritdoc />
    public bool TryPredict(ulong pc, ValueHistoryCheckpoint history, out ulong value) {
        int idx = Idx(pc);
        IValuePredictor? component = _classification[idx] switch {
            Classification.Context       => _context,
            Classification.Computational => _computational,
            _                            => null, // Unclassified or DontPredict
        };
        if (component is null) {
            value = 0;
            return false;
        }

        if (component.TryPredict(pc, history, out value)) {
            _armed[idx] = true;
            _missStreak[idx] = 0;
            return true;
        }

        if (_armed[idx] && ++_missStreak[idx] >= _evictThreshold) Evict(idx);
        return false;
    }

    /// <inheritdoc />
    public void Update(ulong pc, ValueHistoryCheckpoint history, ulong actualValue) {
        int idx = Idx(pc);
        switch (_classification[idx]) {
            case Classification.Context:
                _context.Update(pc, history, actualValue);
                return;
            case Classification.Computational:
                _computational.Update(pc, history, actualValue);
                return;
            case Classification.DontPredict:
                return; // permanently unpredictable (paper: evicted from the FCM/general role)
            default:    // Unclassified: still accumulating the 3-value learning history
                RecordHistoryAndMaybeClassify(idx, actualValue);
                return;
        }
    }

    /// <inheritdoc />
    public void OnBranchFetched(bool predictedTaken) => _context.OnBranchFetched(predictedTaken);

    /// <inheritdoc />
    public void RecoverSpeculativeHistory() {
        _context.RecoverSpeculativeHistory();
        _computational.RecoverSpeculativeHistory();
        // A wrong-path Predict can arm an entry (or observe a false that would otherwise evict it)
        // before the squash that discards it is processed; clearing every entry's arming bit -- and
        // its miss streak, for the same reason -- here bounds that to the same squash-reset
        // discipline as the components' own speculative state.
        Array.Clear(_armed);
        Array.Clear(_missStreak);
    }

    /// <inheritdoc />
    public ValueHistoryCheckpoint CaptureHistory() => _context.CaptureHistory();

    /// <inheritdoc />
    public void RestoreHistory(in ValueHistoryCheckpoint checkpoint, bool actualTaken) {
        _context.RestoreHistory(checkpoint, actualTaken);
        _computational.RestoreHistory(checkpoint, actualTaken);
        Array.Clear(_armed);
        Array.Clear(_missStreak);
    }

    /// <inheritdoc />
    public void AdvanceCommittedHistory(bool taken) => _context.AdvanceCommittedHistory(taken);

    /// <summary>
    ///     Serializes the classification/history table and delegates to each composed component's
    ///     own <see cref="IValuePredictor.WriteState" />. Unlike <see cref="StrideVp" />'s
    ///     <c>_inFlight</c> or the OoO <c>StoreSetPredictor</c>'s LFST,
    ///     <see cref="_armed" />/<see cref="_missStreak" /> are <em>not</em> transient
    ///     renamed-but-uncommitted counters — <see cref="Update" /> never clears them, only
    ///     <see cref="Evict" />/<see cref="Classify" />/a squash do — so they are real trained
    ///     state (whether this PC's component has ever predicted confidently since classification)
    ///     that must round-trip, not skip, across a checkpoint.
    /// </summary>
    public void WriteState(BinaryWriter w) {
        w.Write(_classification.Length);
        foreach (Classification c in _classification) w.Write((byte)c);
        foreach (int hc in _historyCount) w.Write(hc);
        foreach (ulong h in _h1) w.Write(h);
        foreach (ulong h in _h2) w.Write(h);
        foreach (ulong h in _h3) w.Write(h);
        foreach (bool a in _armed) w.Write(a);
        foreach (int m in _missStreak) w.Write(m);
        _context.WriteState(w);
        _computational.WriteState(w);
    }

    /// <summary>Restores state written by <see cref="WriteState" />. Table size must match.</summary>
    public void ReadState(BinaryReader r) {
        int size = r.ReadInt32();
        int n = Math.Min(size, _classification.Length);
        for (var i = 0; i < size; i++) {
            var c = (Classification)r.ReadByte();
            if (i < n) _classification[i] = c;
        }

        for (var i = 0; i < size; i++) {
            int hc = r.ReadInt32();
            if (i < n) _historyCount[i] = hc;
        }

        for (var i = 0; i < size; i++) {
            ulong h = r.ReadUInt64();
            if (i < n) _h1[i] = h;
        }

        for (var i = 0; i < size; i++) {
            ulong h = r.ReadUInt64();
            if (i < n) _h2[i] = h;
        }

        for (var i = 0; i < size; i++) {
            ulong h = r.ReadUInt64();
            if (i < n) _h3[i] = h;
        }

        for (var i = 0; i < size; i++) {
            bool a = r.ReadBoolean();
            if (i < n) _armed[i] = a;
        }

        for (var i = 0; i < size; i++) {
            int m = r.ReadInt32();
            if (i < n) _missStreak[i] = m;
        }

        _context.ReadState(r);
        _computational.ReadState(r);
    }

    private void RecordHistoryAndMaybeClassify(int idx, ulong actualValue) {
        switch (_historyCount[idx]) {
            case 0:
                _h1[idx] = actualValue;
                _historyCount[idx] = 1;
                return;
            case 1:
                _h2[idx] = actualValue;
                _historyCount[idx] = 2;
                return;
            default:
                _h3[idx] = actualValue;
                Classify(idx);
                return;
        }
    }

    /// <summary>
    ///     Classify (paper §3.2.3): examines the 3-value history and assigns exactly one part.
    ///     Equal consecutive deltas (including zero, folding in the paper's Popular Last Value case —
    ///     see this class's doc comment) go to the computational component; anything else goes to the
    ///     context component, playing the paper's general/FCM catch-all role.
    /// </summary>
    private void Classify(int idx) {
        var delta1 = unchecked((long)(_h2[idx] - _h1[idx]));
        var delta2 = unchecked((long)(_h3[idx] - _h2[idx]));
        _classification[idx] = delta1 == delta2 ? Classification.Computational : Classification.Context;
        _armed[idx] = false;
        _missStreak[idx] = 0;
        _historyCount[idx] = 0;
    }

    /// <summary>
    ///     Evict (paper §3.2.3): a classified PC whose component's confidence has collapsed (see this
    ///     class's doc comment for the adapted trigger) either becomes permanently unpredictable
    ///     (evicted from the context/FCM-role component) or returns to Unclassified, relearning
    ///     against a new pattern (evicted from the computational component).
    /// </summary>
    private void Evict(int idx) {
        _classification[idx] = _classification[idx] == Classification.Context
            ? Classification.DontPredict
            : Classification.Unclassified;
        _armed[idx] = false;
        _missStreak[idx] = 0;
    }

    private int Idx(ulong pc) => (int)((pc >> 2) & (uint)_mask);

    private enum Classification : byte {
        Unclassified,
        DontPredict,
        Context,
        Computational,
    }
}