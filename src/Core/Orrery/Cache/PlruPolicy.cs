#region

using System.Numerics;

#endregion

namespace Orrery.Cache;

/// <summary>
///     Tree-PLRU (Pseudo-LRU) replacement policy. Each set holds a complete binary
///     tree of <c>ways−1</c> bits. On every access (hit or install) the bits on the
///     root-to-leaf path are set to point <em>away</em> from the accessed subtree,
///     making its sibling subtree the replacement candidate. Victim selection follows
///     bits root-to-leaf. Exact LRU for 2-way; a hardware-friendly approximation
///     for higher associativity (as used in Intel P6 and later designs).
/// </summary>
public sealed class PlruPolicy : IReplacementPolicy {
    private readonly bool[][] _bits; // [set][ways-1 nodes], indexed as complete binary tree
    private readonly int _depth;     // log2(ways)
    private readonly int _ways;

    public PlruPolicy(int sets, int ways) {
        if (!BitOperations.IsPow2(ways) || ways < 2)
            throw new ArgumentException("PLRU requires ways to be a power of 2 and >= 2.");
        _ways = ways;
        _depth = BitOperations.Log2((uint)ways);
        _bits = new bool[sets][];
        for (var s = 0; s < sets; s++) _bits[s] = new bool[ways - 1];
    }

    public void RecordHit(int set, int way) => Update(_bits[set], way);

    public void RecordInstall(int set, int way) => Update(_bits[set], way);

    public int ChooseVictim(int set) => GetVictim(_bits[set]);

    public int GetMetadata(int set, int way) {
        var copy = (bool[])_bits[set].Clone();
        for (int rank = _ways - 1; rank >= 0; rank--) {
            int v = GetVictim(copy);
            if (v == way) return rank;
            Update(copy, v);
        }

        return 0;
    }

    // On every access, walk root→leaf and set each node's bit to point AWAY from
    // the subtree that contains `way`, so the other subtree becomes the candidate.
    private void Update(bool[] bits, int way) {
        var node = 0;
        for (var d = 0; d < _depth; d++) {
            int goRight = (way >> (_depth - 1 - d)) & 1;
            bits[node] = goRight == 0; // true → right is next candidate (point right)
            node = 2 * node + 1 + goRight;
        }
    }

    // Walk root→leaf following each node's bit to find the replacement candidate.
    private int GetVictim(bool[] bits) {
        int node = 0, victim = 0;
        for (var d = 0; d < _depth; d++) {
            int goRight = bits[node] ? 1 : 0;
            victim = (victim << 1) | goRight;
            node = 2 * node + 1 + goRight;
        }

        return victim;
    }
}