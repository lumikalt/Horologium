namespace Orrery.Cache;

/// <summary>
///     Standard LRU replacement. Age 0 = MRU; higher age = older. Victim is the way with the
///     highest age. Extracted from the original <see cref="SetAssociativeCache" /> logic so the
///     pluggable <see cref="IReplacementPolicy" /> interface preserves identical default behaviour.
/// </summary>
public sealed class LruPolicy : IReplacementPolicy {
    private readonly int[][] _age;
    private readonly int _ways;

    public LruPolicy(int sets, int ways) {
        _ways = ways;
        _age = new int[sets][];
        for (var s = 0; s < sets; s++) {
            _age[s] = new int[ways];
            for (var w = 0; w < ways; w++) _age[s][w] = w;
        }
    }

    public void RecordHit(int set, int way) {
        int age = _age[set][way];
        for (var w = 0; w < _ways; w++)
            if (_age[set][w] < age)
                _age[set][w]++;
        _age[set][way] = 0;
    }

    public int ChooseVictim(int set) {
        var oldest = 0;
        for (var w = 1; w < _ways; w++)
            if (_age[set][w] > _age[set][oldest])
                oldest = w;
        return oldest;
    }

    public void RecordInstall(int set, int way) => RecordHit(set, way);

    public int GetMetadata(int set, int way) => _age[set][way];

    /// <inheritdoc />
    public void WriteState(BinaryWriter writer) {
        writer.Write(_age.Length);
        foreach (int[] set in _age)
        foreach (int age in set)
            writer.Write(age);
    }

    /// <inheritdoc />
    public void ReadState(BinaryReader reader) {
        int sets = reader.ReadInt32();
        int n = Math.Min(sets, _age.Length);
        for (var s = 0; s < sets; s++)
        for (var w = 0; w < _ways; w++) {
            int age = reader.ReadInt32();
            if (s < n) _age[s][w] = age;
        }
    }
}