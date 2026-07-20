#region

using Mechanism;
using Orrery.Cache;

#endregion

namespace Pipeline;

/// <summary>
///     RAS-Directed Instruction Prefetching (Kolli, Saidi &amp; Wenisch, MICRO 2013).
///     Associates I-cache miss sequences with signatures derived from the commit-time
///     call stack; prefetches on call/return when a matching signature is found.
/// </summary>
public sealed class RdipPrefetcher(SetAssociativeCache iCache, IDecoder decoder) {
    // ── RDIP commit-time RAS (4 entries, per paper sensitivity study) ──────────
    private const int RasDepth = 4;

    // ── Current Signature Misses — plain buffer, flushed on every sig change ───
    private const int CsmCapacity = 16;

    // ── Miss table: 1024 sets × 4 ways (= 4096 entries, matching paper) ────────
    private const int Sets = 1024;
    private const int Ways = 4;
    private const int MaxTriggers = 3;
    private const int TriggerWindow = 8; // blocks covered by each 8-bit trigger mask

    // ── Hardware references ───────────────────────────────────────────────────
    private readonly int _blockBytes = iCache.BlockBytes;
    private readonly ulong[] _csm = new ulong[RdipPrefetcher.CsmCapacity];
    private readonly int[] _entryLruAge = new int[RdipPrefetcher.Sets * RdipPrefetcher.Ways];
    private readonly int[] _entryNextTrigger = new int[RdipPrefetcher.Sets * RdipPrefetcher.Ways];
    private readonly uint[] _entryTag = new uint[RdipPrefetcher.Sets * RdipPrefetcher.Ways];

    // Entry fields (parallel arrays, row-major [set*Ways+way])
    private readonly bool[] _entryValid = new bool[RdipPrefetcher.Sets * RdipPrefetcher.Ways];
    private readonly ulong[] _ras = new ulong[RdipPrefetcher.RasDepth];

    private readonly ulong[] _trigBase
        = new ulong[RdipPrefetcher.Sets * RdipPrefetcher.Ways * RdipPrefetcher.MaxTriggers];

    private readonly byte[] _trigMask
        = new byte[RdipPrefetcher.Sets * RdipPrefetcher.Ways * RdipPrefetcher.MaxTriggers];

    // Trigger fields (parallel arrays, row-major [set*Ways*MaxTriggers + way*MaxTriggers + t])
    private readonly bool[] _trigValid
        = new bool[RdipPrefetcher.Sets * RdipPrefetcher.Ways * RdipPrefetcher.MaxTriggers];

    private int _csmCount;

    // ── Signatures ─────────────────────────────────────────────────────────────
    private uint _curSig;
    private uint _prevSig;
    private int _rasSize;

    // ── Public interface ──────────────────────────────────────────────────────

    /// <summary>Called by FetchStage after each I-cache miss.</summary>
    public void OnIcacheMiss(ulong physAddress) {
        if (_csmCount < RdipPrefetcher.CsmCapacity) _csm[_csmCount++] = physAddress;
    }

    /// <summary>Called at commit for every instruction; routes to call/return handlers.</summary>
    public void OnCommit(ulong pc, uint rawEncoding) {
        FetchHint hint = decoder.GetFetchHint(pc, rawEncoding);
        if (hint.IsCall)
            OnCallCommit(pc + (ulong)hint.InstructionSize);
        else if (hint.IsReturn) OnReturnCommit();
    }

    // ── Internal commit handlers ──────────────────────────────────────────────

    private void OnCallCommit(ulong returnAddress) {
        if (_rasSize < RdipPrefetcher.RasDepth) { _ras[_rasSize++] = returnAddress; }
        else {
            // Overflow: drop oldest, push newest.
            Array.Copy(_ras, 1, _ras, 0, RdipPrefetcher.RasDepth - 1);
            _ras[RdipPrefetcher.RasDepth - 1] = returnAddress;
        }

        uint newSig = ComputeSig(false);
        FlushCsm(_prevSig);
        _prevSig = _curSig;
        _curSig = newSig;
        LookupAndPrefetch(_curSig);
    }

    private void OnReturnCommit() {
        // Signature computed BEFORE pop, direction bit = 1 (return).
        uint sigBeforePop = ComputeSig(true);
        FlushCsm(_prevSig);
        _prevSig = _curSig;
        if (_rasSize > 0) _rasSize--;
        _curSig = sigBeforePop;
        LookupAndPrefetch(_curSig);
    }

    // ── Signature ─────────────────────────────────────────────────────────────

    private uint ComputeSig(bool isReturn) {
        uint sig = 0;
        for (var i = 0; i < _rasSize; i++) sig ^= (uint)_ras[i];
        return (sig & ~1u) | (isReturn ? 1u : 0u);
    }

    // ── CSM flush → miss table ────────────────────────────────────────────────

    private void FlushCsm(uint sig) {
        if (_csmCount == 0) return;

        var setIdx = (int)(sig & (RdipPrefetcher.Sets - 1));
        uint tag = sig >> 10;
        int entryIdx = FindOrAllocate(setIdx, tag);

        for (var i = 0; i < _csmCount; i++) MergeMiss(entryIdx, _csm[i]);

        _csmCount = 0;
    }

    // ── Lookup + prefetch ─────────────────────────────────────────────────────

    private void LookupAndPrefetch(uint sig) {
        var setIdx = (int)(sig & (RdipPrefetcher.Sets - 1));
        uint tag = sig >> 10;
        int entryIdx = FindWay(setIdx, tag);
        if (entryIdx < 0) return;

        TouchLru(setIdx, entryIdx - setIdx * RdipPrefetcher.Ways);

        int trigBase = entryIdx * RdipPrefetcher.MaxTriggers;
        for (var t = 0; t < RdipPrefetcher.MaxTriggers; t++) {
            if (!_trigValid[trigBase + t]) continue;
            ulong blockBase = _trigBase[trigBase + t];
            byte mask = _trigMask[trigBase + t];
            for (var b = 0; b < RdipPrefetcher.TriggerWindow; b++)
                if ((mask & (1 << b)) != 0)
                    iCache.Prefetch(blockBase + (ulong)(b * _blockBytes));
        }
    }

    // ── Miss table helpers ────────────────────────────────────────────────────

    private int FindOrAllocate(int setIdx, uint tag) {
        int found = FindWay(setIdx, tag);
        if (found >= 0) {
            TouchLru(setIdx, found - setIdx * RdipPrefetcher.Ways);
            return found;
        }

        // LRU victim: way with the highest age within this set.
        int @base = setIdx * RdipPrefetcher.Ways;
        var victimWay = 0;
        int maxAge = _entryLruAge[@base];
        for (var w = 1; w < RdipPrefetcher.Ways; w++) {
            int age = _entryLruAge[@base + w];
            if (age > maxAge) {
                maxAge = age;
                victimWay = w;
            }
        }

        int idx = @base + victimWay;
        _entryValid[idx] = true;
        _entryTag[idx] = tag;
        _entryNextTrigger[idx] = 0;

        // Clear triggers for the evicted entry.
        int trigBase = idx * RdipPrefetcher.MaxTriggers;
        for (var t = 0; t < RdipPrefetcher.MaxTriggers; t++) _trigValid[trigBase + t] = false;

        TouchLru(setIdx, victimWay);
        return idx;
    }

    // Returns flat index into the table arrays, or -1 if not found.
    private int FindWay(int setIdx, uint tag) {
        int @base = setIdx * RdipPrefetcher.Ways;
        for (var w = 0; w < RdipPrefetcher.Ways; w++) {
            int idx = @base + w;
            if (_entryValid[idx] && _entryTag[idx] == tag) return idx;
        }

        return -1;
    }

    private void TouchLru(int setIdx, int accessedWay) {
        int @base = setIdx * RdipPrefetcher.Ways;
        for (var w = 0; w < RdipPrefetcher.Ways; w++) _entryLruAge[@base + w]++;
        _entryLruAge[@base + accessedWay] = 0;
    }

    private void MergeMiss(int entryIdx, ulong physAddress) {
        ulong missBlock = physAddress & ~(ulong)(_blockBytes - 1);
        int trigBase = entryIdx * RdipPrefetcher.MaxTriggers;

        for (var t = 0; t < RdipPrefetcher.MaxTriggers; t++) {
            int ti = trigBase + t;
            if (!_trigValid[ti]) {
                _trigValid[ti] = true;
                _trigBase[ti] = missBlock;
                _trigMask[ti] = 1;
                return;
            }

            ulong windowEnd = _trigBase[ti] + (ulong)(RdipPrefetcher.TriggerWindow * _blockBytes);
            if (missBlock >= _trigBase[ti] && missBlock < windowEnd) {
                var bit = (int)((missBlock - _trigBase[ti]) / (ulong)_blockBytes);
                _trigMask[ti] |= (byte)(1 << bit);
                return;
            }
        }

        // All triggers occupied and miss doesn't fit — replace round-robin.
        int replace = trigBase + _entryNextTrigger[entryIdx];
        _trigBase[replace] = missBlock;
        _trigMask[replace] = 1;
        _trigValid[replace] = true;
        _entryNextTrigger[entryIdx] = (_entryNextTrigger[entryIdx] + 1) % RdipPrefetcher.MaxTriggers;
    }
}