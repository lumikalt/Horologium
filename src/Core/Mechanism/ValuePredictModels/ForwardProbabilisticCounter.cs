namespace Mechanism.ValuePredictModels;

/// <summary>
///     Forward Probabilistic Counter (Perais &amp; Seznec, HPCA 2014 §5, after Riley &amp; Zilles,
///     HPCA 2006). A 3-bit saturating confidence counter (states 0–7) whose forward transition
///     (state <c>i</c> → <c>i+1</c>) is only taken with probability <see cref="IncrementProbability" />
///     <c>[i]</c> rather than unconditionally, mimicking a much wider counter with the storage of
///     a narrow one. A misprediction resets the counter hard to 0 (never decays gradually).
///     Prediction is trusted only once the counter is fully saturated (state 7).
///     <para>
///         Uses the paper's probability vector tuned for <em>squash-at-commit</em> recovery
///         (v = {1, 1/16, 1/16, 1/16, 1/16, 1/32, 1/32}), the recovery model this codebase uses
///         for value-prediction misprediction (see <c>OooeTrain.StepCommit</c>).
///     </para>
///     <para>
///         Seeded deterministically (default seed 0, mirroring <c>Orrery.Cache.RandomPolicy</c>)
///         so simulation runs — and tests — are reproducible.
///     </para>
/// </summary>
public sealed class ForwardProbabilisticCounter {
    /// <summary>Fully saturated confidence state — the only state at which a prediction is used.</summary>
    public const byte MaxState = 7;

    private static readonly double[] IncrementProbability =
        [1.0, 1.0 / 16, 1.0 / 16, 1.0 / 16, 1.0 / 16, 1.0 / 32, 1.0 / 32,];

    private readonly Random _rng;

    /// <param name="seed">Deterministic seed for the probabilistic forward transitions.</param>
    public ForwardProbabilisticCounter(int seed = 0) => _rng = new Random(seed);

    /// <summary>True once <paramref name="counter" /> is fully saturated — the gate for using a prediction.</summary>
    public static bool IsSaturated(byte counter) => counter >= ForwardProbabilisticCounter.MaxState;

    /// <summary>Probabilistically advances the counter one step on a correct outcome.</summary>
    public void OnCorrect(ref byte counter) {
        if (counter >= ForwardProbabilisticCounter.MaxState) return;
        if (_rng.NextDouble() < ForwardProbabilisticCounter.IncrementProbability[counter]) counter++;
    }

    /// <summary>Hard-resets the counter to 0 on a misprediction (no gradual decay).</summary>
    public void OnMispredict(ref byte counter) => counter = 0;
}