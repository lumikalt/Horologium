namespace Orrery.Cache;

public interface IReplacementPolicy {
    /// <summary>Called on a demand hit to update the block's re-reference prediction.</summary>
    void RecordHit(int set, int way);

    /// <summary>
    /// Selects the way to evict. May update internal state (e.g. increment all RRPVs in the
    /// set for RRIP). Always returns a valid way in [0, ways).
    /// </summary>
    int ChooseVictim(int set);

    /// <summary>
    /// Called after a new line has been filled into <paramref name="way"/>. Sets the initial
    /// re-reference prediction for the block (e.g. LRU age = MRU; RRIP RRPV = long or distant).
    /// For DRRIP, also updates the PSEL counter when the set is an SDM.
    /// </summary>
    void RecordInstall(int set, int way);

    /// <summary>
    /// Returns the per-way metadata for inspection (LRU age or RRPV). Used by
    /// <see cref="SetAssociativeCache.GetSnapshot"/> to populate <c>CacheLine.LruAge</c>.
    /// </summary>
    int GetMetadata(int set, int way);

    /// <summary>
    /// Supplies the access signature for the next <see cref="RecordInstall"/> call.
    /// Called by <see cref="SetAssociativeCache"/> with <c>address &gt;&gt; offsetBits</c> before
    /// each block fill. The default is a no-op; only SHiP-style policies act on it.
    /// </summary>
    void SetPendingSignature(ulong signature) { }

    /// <summary>
    /// Supplies the cache-line tag of the line about to be installed, and the PC of the
    /// demand access that caused the fill. Called by <see cref="SetAssociativeCache"/> just
    /// before <see cref="RecordInstall"/>. The default is a no-op; only Hawkeye uses it.
    /// </summary>
    void SetPendingAddress(ulong lineTag, ulong pc) { }

    /// <summary>
    /// Called on a demand hit before <see cref="RecordHit"/> to supply the PC of the
    /// access and the tag of the resident line. Used by Hawkeye to feed OPTgen on hits.
    /// The default is a no-op.
    /// </summary>
    void RecordHitPc(int set, int way, ulong lineTag, ulong pc) { }
}