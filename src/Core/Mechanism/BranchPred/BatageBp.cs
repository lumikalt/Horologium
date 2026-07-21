namespace Mechanism.BranchPred;

/// <summary>
///     BATAGE: Bimodal-Augmented TAGE.
///     <para>
///         Extends L-TAGE with a per-PC bias table (a single statistical-corrector
///         table with no history folding). The bias table's signed weight is summed
///         with TAGE's signed confidence score; if the combined magnitude exceeds
///         BiasThreshold, the combined sign overrides TAGE's direction prediction.
///     </para>
///     <para>
///         Note: this is the per-PC SC-table interpretation. Michaud's original BATAGE
///         paper uses a dual-counter bimodal structure; this simplified form follows
///         the single-weight-per-entry pattern used in TAGE-SC-L.
///     </para>
/// </summary>
public sealed class BatageBp : LTageBp {
    private const int BiasTableBits = 12; // 4096 entries
    private const int BiasThreshold = 8;

    private readonly sbyte[] _bias = new sbyte[1 << BatageBp.BiasTableBits];

    // ── LTageBp hooks ──────────────────────────────────────────────────


    /// <inheritdoc />
    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        int total = TageScore(pc, provider) + _bias[BiasIdx(pc)];
        if (Math.Abs(total) > BatageBp.BiasThreshold) return total >= 0;
        return tagePred;
    }

    /// <inheritdoc />
    protected override void OnAfterUpdate(
        ulong pc,
        bool taken,
        bool provPred,
        int preScore,
        bool loopWasConfident
    ) {
        if (loopWasConfident) return;

        int preTotal = preScore + _bias[BiasIdx(pc)];
        bool biasPred = Math.Abs(preTotal) > BatageBp.BiasThreshold ? preTotal >= 0 : provPred;

        if (biasPred != taken || Math.Abs(preTotal) <= BatageBp.BiasThreshold) {
            int idx = BiasIdx(pc);
            int delta = taken ? 1 : -1;
            _bias[idx] = (sbyte)Math.Clamp(_bias[idx] + delta, sbyte.MinValue, sbyte.MaxValue);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int BiasIdx(ulong pc) =>
        (int)((pc >> 2) & ((1u << BatageBp.BiasTableBits) - 1));

    /// <summary>Serializes the inherited LTageBp state (via <c>base</c>) plus the per-PC bias table.</summary>
    public override void WriteState(BinaryWriter w) {
        base.WriteState(w);
        foreach (sbyte b in _bias) w.Write(b);
    }

    /// <summary>Restores state written by <see cref="WriteState" />.</summary>
    public override void ReadState(BinaryReader r) {
        base.ReadState(r);
        for (var i = 0; i < _bias.Length; i++) _bias[i] = r.ReadSByte();
    }
}