namespace Orrery.Cache;

/// <summary>
/// Random replacement: evicts a uniformly random way on each miss.
/// Hit promotion and install are no-ops — no recency tracking of any kind.
/// A useful lower-bound baseline; occasionally competitive with LRU on
/// heavily thrashing workloads where recency is uncorrelated with reuse.
/// Seeded deterministically so simulation runs are reproducible.
/// </summary>
public sealed class RandomPolicy : IReplacementPolicy {
    private readonly int _ways;
    private readonly Random _rng;

    public RandomPolicy(int sets, int ways, int seed = 0) {
        _ways = ways;
        _rng = new Random(seed);
        _ = sets; // no per-set state needed
    }

    public void RecordHit(int set, int way) { }
    public void RecordInstall(int set, int way) { }
    public int ChooseVictim(int set) => _rng.Next(_ways);
    public int GetMetadata(int set, int way) => 0;
}
