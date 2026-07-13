namespace Orrery.Cache;

/// <summary>
///     MRU (Most-Recently-Used) replacement policy. The way most recently hit is chosen
///     as the eviction candidate; newly installed lines are placed at the LRU position so
///     they survive until first use. Useful for sequential-scan workloads where the
///     just-accessed block is unlikely to be reused soon.
///     Age convention: 0 = MRU (next eviction candidate), ways−1 = oldest (most protected).
/// </summary>
public sealed class MruPolicy : IReplacementPolicy {
    private readonly int[][] _age;
    private readonly int _ways;

    public MruPolicy(int sets, int ways) {
        _ways = ways;
        _age = new int[sets][];
        for (var s = 0; s < sets; s++) {
            _age[s] = new int[ways];
            for (var w = 0; w < ways; w++) _age[s][w] = w;
        }
    }

    // Promote to MRU position (age 0 = next eviction candidate).
    public void RecordHit(int set, int way) {
        int age = _age[set][way];
        for (var w = 0; w < _ways; w++)
            if (_age[set][w] < age)
                _age[set][w]++;
        _age[set][way] = 0;
    }

    // Place at LRU position (age ways-1) so the new line isn't immediately evicted.
    public void RecordInstall(int set, int way) {
        int age = _age[set][way];
        for (var w = 0; w < _ways; w++)
            if (_age[set][w] > age)
                _age[set][w]--;
        _age[set][way] = _ways - 1;
    }

    // Evict the most-recently-used way (age 0).
    public int ChooseVictim(int set) {
        var mru = 0;
        for (var w = 1; w < _ways; w++)
            if (_age[set][w] < _age[set][mru])
                mru = w;
        return mru;
    }

    public int GetMetadata(int set, int way) => _age[set][way];
}