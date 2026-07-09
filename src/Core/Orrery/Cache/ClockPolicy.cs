namespace Orrery.Cache;

/// <summary>
/// CLOCK (Second-Chance) replacement policy. Each way holds a single reference bit.
/// On eviction the hand sweeps forward: if a way's bit is 1 it is cleared (second chance)
/// and the hand advances; the first way encountered with bit 0 is the victim. On hit or
/// install the bit is set to 1. The hand advances past the installed way so the next
/// ChooseVictim starts from the next position.
/// GetMetadata returns 0 for referenced (recently used) and 1 for unreferenced (candidate).
/// </summary>
public sealed class ClockPolicy : IReplacementPolicy {
    private readonly int _ways;
    private readonly bool[][] _ref; // reference bits per [set][way]
    private readonly int[] _hand;   // per-set clock hand

    public ClockPolicy(int sets, int ways) {
        _ways = ways;
        _ref = new bool[sets][];
        _hand = new int[sets];
        for (var s = 0; s < sets; s++) _ref[s] = new bool[ways]; // all start unreferenced
    }

    public void RecordHit(int set, int way) => _ref[set][way] = true;

    // Set bit on newly installed way and advance hand past it.
    public void RecordInstall(int set, int way) {
        _ref[set][way] = true;
        _hand[set] = (way + 1) % _ways;
    }

    // Sweep forward from the hand, clearing bits=1 (second chance), until a bit=0 is found.
    // Terminates in at most ways iterations: once every bit has been cleared to 0, the way
    // at the hand position is returned immediately.
    public int ChooseVictim(int set) {
        bool[] bits = _ref[set];
        while (bits[_hand[set]]) {
            bits[_hand[set]] = false;
            _hand[set] = (_hand[set] + 1) % _ways;
        }

        return _hand[set];
    }

    // 0 = referenced (recently used); 1 = unreferenced (eviction candidate).
    public int GetMetadata(int set, int way) => _ref[set][way] ? 0 : 1;
}