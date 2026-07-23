namespace Mechanism.BranchPred;

/// <summary>
///     IMLI (Inter-Mediated Loop Iteration) branch predictor — Jiménez, IEEE CAL 2018.
///     <para>
///         Maintains a single shared loop-iteration counter (<c>_imli</c>) driven by
///         backward conditional branches: a taken backward branch increments the counter
///         (loop is iterating); a not-taken backward branch resets it to zero (loop exit).
///         All branch predictions use this counter as an additional index dimension via
///         <c>PHT[hash(pc, _imli)]</c>, allowing the predictor to make iteration-specific
///         predictions for any branch whose outcome correlates with the innermost loop's
///         iteration count — including body branches, not just the loop-closing branch.
///     </para>
///     <para>
///         The "backward" classification for not-taken branches is cached from the first
///         time the branch was seen taken (at which point the static target is available).
///         Unconditional backward jumps (JAL) increment the counter like any other taken
///         backward branch and are not explicitly excluded; they rarely form loops in
///         compiler-generated code.
///     </para>
/// </summary>
public sealed class ImliPredictor : IBranchPredictor {
    private readonly HashSet<ulong> _backwardBranches;
    private readonly ulong[] _btb;
    private readonly int _btbMask;
    private readonly byte[] _pht;
    private readonly int _phtMask;
    private int _committedImli; // architectural shadow, advanced at commit
    private int _imli;          // working (speculative) loop counter, used by Predict
    private bool _speculative;  // latches on first speculative update; keeps in-order bit-identical

    /// <summary>Initializes an <see cref="ImliPredictor" /> with the given PHT and BTB sizes (must be powers of two).</summary>
    public ImliPredictor(int phtSize = 65536, int btbSize = 1024) {
        _phtMask = phtSize - 1;
        _btbMask = btbSize - 1;
        _pht = new byte[phtSize];
        _btb = new ulong[btbSize];
        _backwardBranches = [];
        Array.Fill(_pht, (byte)1); // weakly not-taken
    }

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        // Cache backward-branch classification when static target is available at fetch
        if (knownTarget.HasValue && knownTarget.Value < pc) _backwardBranches.Add(pc);
        int phtIdx = PhtIndex(pc);
        bool taken = _pht[phtIdx] >= 2;
        return new BranchPrediction(taken, taken ? _btb[BtbIndex(pc)] : pc + 4);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        // Index the PHT with the committed (predict-time) counter, then advance the shadow.
        int working = _imli;
        _imli = _committedImli;

        int phtIdx = PhtIndex(pc);
        if (taken) _btb[BtbIndex(pc)] = actualTarget;
        switch (taken) {
            case true when _pht[phtIdx] < 3:  _pht[phtIdx]++; break;
            case false when _pht[phtIdx] > 0: _pht[phtIdx]--; break;
        }

        switch (taken) {
            case true when
                // IMLI counter: taken backward → iterating; not-taken backward → loop exit
                actualTarget < pc:
                _backwardBranches.Add(pc);
                _committedImli++;
                break;
            case false when _backwardBranches.Contains(pc): _committedImli = 0; break;
        }

        _imli = _speculative ? working : _committedImli;
    }

    /// <inheritdoc />
    public void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) {
        _speculative = true;
        // Only backward branches drive the loop counter. The classification is populated by
        // Predict (when the static target is known) and Update; an as-yet-unseen backward
        // branch simply doesn't advance speculatively until then — a one-time drift that the
        // next flush heals.
        if (!_backwardBranches.Contains(pc)) return;
        if (predictedTaken)
            _imli++;
        else
            _imli = 0;
    }

    /// <inheritdoc />
    public void RecoverSpeculativeHistory() => _imli = _committedImli;

    /// <summary>
    ///     Serializes the PHT, BTB, backward-branch classification set, and both IMLI counter
    ///     shadows (<see cref="_imli" />/<see cref="_committedImli" />) — mirroring RAS/CRAS and
    ///     <c>VtageVp</c>'s speculative/committed pair rather than assuming the two have
    ///     converged at the drain boundary.
    /// </summary>
    public void WriteState(BinaryWriter w) {
        foreach (byte c in _pht) w.Write(c);
        foreach (ulong t in _btb) w.Write(t);

        w.Write(_backwardBranches.Count);
        foreach (ulong pc in _backwardBranches) w.Write(pc);

        w.Write(_imli);
        w.Write(_committedImli);
        w.Write(_speculative);
    }

    /// <summary>Restores state written by <see cref="WriteState" />. Table geometry must match.</summary>
    public void ReadState(BinaryReader r) {
        for (var i = 0; i < _pht.Length; i++) _pht[i] = r.ReadByte();
        for (var i = 0; i < _btb.Length; i++) _btb[i] = r.ReadUInt64();

        _backwardBranches.Clear();
        int count = r.ReadInt32();
        for (var i = 0; i < count; i++) _backwardBranches.Add(r.ReadUInt64());

        _imli = r.ReadInt32();
        _committedImli = r.ReadInt32();
        _speculative = r.ReadBoolean();
    }

    private int PhtIndex(ulong pc) =>
        (int)(((pc >> 2) ^ (uint)_imli) & (uint)_phtMask);

    private int BtbIndex(ulong pc) =>
        (int)((pc >> 2) & (uint)_btbMask);
}