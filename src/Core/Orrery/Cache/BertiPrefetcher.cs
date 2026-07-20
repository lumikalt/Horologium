#region

using System.Numerics;

#endregion

namespace Orrery.Cache;

/// <summary>
///     Berti: per-IP timely local-delta L1D prefetcher (Navarro-Torres et al., MICRO 2022).
///     <para>
///         For each load IP Berti maintains a small History Table (HT, 8 sets × 16 ways, FIFO)
///         of recent (line address, tick) pairs.  On a demand miss (<c>wasHit=false</c>) it
///         searches that IP's HT set for entries whose tick satisfies
///         <c>entry.Tick + latency ≤ current_tick</c> — i.e., a prefetch issued then would
///         have arrived before the miss.  The signed line-count deltas from those "timely"
///         entries to the miss address are accumulated in a per-IP Table of Deltas (ToD,
///         16-entry fully-associative, FIFO).
///     </para>
///     <para>
///         The per-IP event counter increments once per training search.  When it reaches 16
///         the accumulated coverage fractions drive status assignments:
///         coverage &gt; 10/16 → <c>L1DPref</c>;  6–10/16 → <c>L2Pref</c>
///         (or <c>L2PrefRepl</c> when coverage &lt; 8/16);  ≤ 5/16 → <c>NoPref</c>.
///         At most 12 deltas may be L1DPref/L2Pref/L2PrefRepl combined; excess entries
///         are downgraded to NoPref in ascending coverage order.
///     </para>
///     <para>
///         On every L1D access all deltas whose status is L1DPref, L2Pref, or L2PrefRepl
///         generate a prefetch target.  During warmup (before the first counter overflow)
///         a delta is issued only when the epoch counter ≥ 8 <em>and</em> its coverage
///         exceeds 80 % of the current counter value.
///     </para>
///     <para>
///         The reference design measures fill latency via MSHR timestamps.  Here it is
///         approximated by a fixed <c>latency</c> in ticks (one tick per
///         <see cref="OnAccess" /> call), defaulting to 10.  The HT is updated on every
///         access rather than only on misses/Hitp, which is a conservative superset of the
///         paper's write condition.  No page-boundary guard is imposed; cross-page prefetching
///         is allowed (the pipeline's uncacheable-region guard remains in force).
///     </para>
/// </summary>
public sealed class BertiPrefetcher : IPrefetcher {
    // ── History Table: 8 sets × 16 ways, FIFO ────────────────────────────────
    private const int HtSets = 8;
    private const int HtWays = 16;
    private const int HtSetBits = 3;    // log2(HtSets)
    private const int HtTagMask = 0x7F; // 7-bit tag

    // ── Table of Deltas (ToD): 16-entry fully-associative, FIFO ─────────────
    private const int TodSize = 16;
    private const int MaxDeltas = 16;     // delta slots per ToD entry
    private const int MaxGoodDeltas = 12; // max L1DPref + L2Pref (incl. L2PrefRepl)

    private const byte SNoPref = 0;
    private const byte Sl2PrefRepl = 1;
    private const byte Sl2Pref = 2;
    private const byte Sl1DPref = 3;

    // ── Scratch buffer for timely-delta collection (stack of HtEntry matches) ─
    private const int MaxTimelyPerSearch = 8;

    private readonly HtEntry[,] _ht = new HtEntry[BertiPrefetcher.HtSets, BertiPrefetcher.HtWays];
    private readonly int[] _htFifo = new int[BertiPrefetcher.HtSets]; // next FIFO write slot per set
    private readonly int _latency;

    // ── Geometry and config ───────────────────────────────────────────────────
    private readonly int _lineShift;

    private readonly TimelyEntry[] _tbuf = new TimelyEntry[BertiPrefetcher.HtWays];
    private readonly int[] _todCount = new int[BertiPrefetcher.TodSize];
    private readonly DeltaSlot[,] _todDeltas = new DeltaSlot[BertiPrefetcher.TodSize, BertiPrefetcher.MaxDeltas];

    private readonly TodMeta[] _todMeta = new TodMeta[BertiPrefetcher.TodSize];
    private int _tick;
    private int _todAge;

    public BertiPrefetcher(int blockBytes = 32, int latency = 10) {
        if (!BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("blockBytes must be a power of 2.", nameof(blockBytes));
        if (latency < 1) throw new ArgumentOutOfRangeException(nameof(latency), "latency must be ≥ 1.");
        _lineShift = BitOperations.Log2((uint)blockBytes);
        _latency = latency;
    }

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        ulong lineAddr = address >> _lineShift;
        var htSet = (int)((pc >> 2) & (BertiPrefetcher.HtSets - 1));
        var htTag = (byte)((pc >> 2 >> BertiPrefetcher.HtSetBits) & BertiPrefetcher.HtTagMask);
        ushort todTag = TodHash(pc);

        // ── 1. Training: search HT for timely deltas on demand miss ──────────
        if (!wasHit) Train(htSet, htTag, lineAddr, todTag);

        // ── 2. Write current access to HT (FIFO) ─────────────────────────────
        int ws = _htFifo[htSet];
        _ht[htSet, ws] = new HtEntry { Tag = htTag, LineAddr = lineAddr, Tick = _tick, Valid = true, };
        _htFifo[htSet] = (ws + 1) & (BertiPrefetcher.HtWays - 1);

        // ── 3. Issue prefetches from ToD ─────────────────────────────────────
        int count = IssuePrefetches(todTag, lineAddr, targets);

        _tick++;
        return count;
    }

    // ── 10-bit IP hash for ToD tag ────────────────────────────────────────────
    private static ushort TodHash(ulong pc) =>
        (ushort)(((uint)(pc >> 2) * 2654435761u) >> 22);

    // ── Training: find timely HT entries, update ToD ──────────────────────────
    private void Train(int htSet, byte htTag, ulong lineAddr, ushort todTag) {
        int threshold = _tick - _latency; // entry.Tick <= threshold → timely
        var n = 0;

        for (var w = 0; w < BertiPrefetcher.HtWays; w++) {
            ref HtEntry e = ref _ht[htSet, w];
            if (e.Valid && e.Tag == htTag && e.Tick <= threshold)
                _tbuf[n++] = new TimelyEntry { LineAddr = e.LineAddr, Tick = e.Tick, };
        }

        if (n == 0) return;

        // Keep up to 8 youngest (largest Tick) — insertion-sort descending.
        for (var i = 1; i < n; i++) {
            TimelyEntry x = _tbuf[i];
            int j = i;
            while (j > 0 && _tbuf[j - 1].Tick < x.Tick) {
                _tbuf[j] = _tbuf[j - 1];
                j--;
            }

            _tbuf[j] = x;
        }

        if (n > BertiPrefetcher.MaxTimelyPerSearch) n = BertiPrefetcher.MaxTimelyPerSearch;

        int todIdx = FindOrAllocTod(todTag);
        _todMeta[todIdx].Counter++;

        for (var i = 0; i < n; i++) {
            long delta = (long)lineAddr - (long)_tbuf[i].LineAddr;
            if (delta < -4096 || delta > 4095) continue; // 13-bit signed range
            AccumulateDelta(todIdx, (int)delta);
        }

        if (_todMeta[todIdx].Counter >= 16) {
            ComputeStatuses(todIdx);
            _todMeta[todIdx].HasStatus = true;
            _todMeta[todIdx].Counter = 0;
            int cnt = _todCount[todIdx];
            for (var i = 0; i < cnt; i++) _todDeltas[todIdx, i].Cov = 0;
        }
    }

    // ── Accumulate a timely delta into the ToD entry ──────────────────────────
    private void AccumulateDelta(int todIdx, int delta) {
        int cnt = _todCount[todIdx];

        for (var i = 0; i < cnt; i++)
            if (_todDeltas[todIdx, i].Delta == delta) {
                if (_todDeltas[todIdx, i].Cov < 15) _todDeltas[todIdx, i].Cov++;
                return;
            }

        if (cnt < BertiPrefetcher.MaxDeltas) {
            _todDeltas[todIdx, cnt] = new DeltaSlot { Delta = delta, Cov = 1, Status = BertiPrefetcher.SNoPref, };
            _todCount[todIdx]++;
            return;
        }

        // All 16 slots full: evict lowest-coverage L2PrefRepl or NoPref candidate.
        int victim = -1;
        var vCov = byte.MaxValue;
        for (var i = 0; i < BertiPrefetcher.MaxDeltas; i++) {
            byte s = _todDeltas[todIdx, i].Status;
            if ((s == BertiPrefetcher.Sl2PrefRepl || s == BertiPrefetcher.SNoPref)
             && _todDeltas[todIdx, i].Cov < vCov) {
                vCov = _todDeltas[todIdx, i].Cov;
                victim = i;
            }
        }

        if (victim >= 0)
            _todDeltas[todIdx, victim] = new DeltaSlot { Delta = delta, Cov = 1, Status = BertiPrefetcher.SNoPref, };
    }

    // ── Assign L1DPref / L2Pref / L2PrefRepl / NoPref statuses ──────────────
    private void ComputeStatuses(int todIdx) {
        int cnt = _todCount[todIdx];

        var good = 0;
        for (var i = 0; i < cnt; i++) {
            ref DeltaSlot d = ref _todDeltas[todIdx, i];
            if (d.Cov > 10) {
                // > 65% of 16
                d.Status = BertiPrefetcher.Sl1DPref;
                good++;
            }
            else if (d.Cov > 5) {
                // 35–65%
                d.Status = d.Cov < 8 ? BertiPrefetcher.Sl2PrefRepl : BertiPrefetcher.Sl2Pref; // < 50% → repl candidate
                good++;
            }
            else { d.Status = BertiPrefetcher.SNoPref; }
        }

        // Enforce max 12 "good" deltas: downgrade lowest-coverage ones to NoPref.
        if (good > BertiPrefetcher.MaxGoodDeltas) {
            int toDowngrade = good - BertiPrefetcher.MaxGoodDeltas;
            for (var pass = 0; pass < toDowngrade; pass++) {
                int worstIdx = -1;
                var worstCov = byte.MaxValue;
                for (var i = 0; i < cnt; i++)
                    if (_todDeltas[todIdx, i].Status != BertiPrefetcher.SNoPref &&
                        _todDeltas[todIdx, i].Cov < worstCov) {
                        worstCov = _todDeltas[todIdx, i].Cov;
                        worstIdx = i;
                    }

                if (worstIdx >= 0) _todDeltas[todIdx, worstIdx].Status = BertiPrefetcher.SNoPref;
            }
        }
    }

    // ── Issue prefetches from the ToD entry for this IP ───────────────────────
    private int IssuePrefetches(ushort todTag, ulong lineAddr, Span<ulong> targets) {
        if (targets.IsEmpty) return 0;
        int todIdx = FindTod(todTag);
        if (todIdx < 0) return 0;

        ref TodMeta meta = ref _todMeta[todIdx];
        int cnt = _todCount[todIdx];
        var count = 0;

        for (var i = 0; i < cnt && count < targets.Length; i++) {
            ref DeltaSlot d = ref _todDeltas[todIdx, i];
            bool issue;
            if (meta.HasStatus)
                issue = d.Status == BertiPrefetcher.Sl1DPref || d.Status == BertiPrefetcher.Sl2Pref
                                                             || d.Status == BertiPrefetcher.Sl2PrefRepl;
            else
                // Warmup: issue if ≥ 8 training events AND coverage > 80 % of counter.
                issue = meta.Counter >= 8 && d.Cov * 10 > meta.Counter * 8;

            if (issue) {
                ulong targetAddr = (ulong)((long)lineAddr + d.Delta) << _lineShift;
                targets[count++] = targetAddr;
            }
        }

        return count;
    }

    // ── ToD lookup helpers ────────────────────────────────────────────────────
    private int FindTod(ushort tag) {
        for (var i = 0; i < BertiPrefetcher.TodSize; i++)
            if (_todMeta[i].Valid && _todMeta[i].IpTag == tag)
                return i;
        return -1;
    }

    private int FindOrAllocTod(ushort tag) {
        for (var i = 0; i < BertiPrefetcher.TodSize; i++)
            if (_todMeta[i].Valid && _todMeta[i].IpTag == tag)
                return i;

        // FIFO replacement: find oldest (smallest Age) or first invalid slot.
        var slot = 0;
        for (var i = 1; i < BertiPrefetcher.TodSize; i++)
            if (!_todMeta[i].Valid || _todMeta[i].Age < _todMeta[slot].Age)
                slot = i;

        for (var i = 0; i < BertiPrefetcher.MaxDeltas; i++) _todDeltas[slot, i] = default(DeltaSlot);
        _todCount[slot] = 0;
        _todMeta[slot] = new TodMeta { IpTag = tag, Age = ++_todAge, Valid = true, };
        return slot;
    }

    private struct HtEntry {
        public byte Tag;
        public ulong LineAddr;
        public int Tick;
        public bool Valid;
    }

    private struct DeltaSlot {
        public int Delta;   // signed line-count offset
        public byte Cov;    // coverage count within current epoch (0-15)
        public byte Status; // SNoPref / SL2PrefRepl / SL2Pref / SL1DPref
    }

    private struct TodMeta {
        public ushort IpTag; // 10-bit hash of IP
        public byte Counter; // training-event counter; trips status update at 16
        public int Age;      // monotone age for FIFO replacement
        public bool Valid;
        public bool HasStatus; // true after first counter overflow
    }

    private struct TimelyEntry {
        public ulong LineAddr;
        public int Tick;
    }
}