using System.Numerics;

namespace Orrery.Cache;

/// <summary>
/// IP Classifier-based Spatial Prefetcher (Pakalapati &amp; Panda, ISCA 2020).
/// Classifies each load PC into one of three classes and issues spatially-targeted prefetches:
/// <list type="bullet">
///   <item><b>CS (Constant Stride)</b> — repeated stride detected via per-IP confidence.</item>
///   <item><b>CPLX (Complex Stride)</b> — rolling-signature history indexes a CSPT table.</item>
///   <item><b>GS (Global Stream)</b> — 2 KB region density tracked in an 8-entry RST; dense
///     regions trigger a stream-direction prefetch.</item>
/// </list>
/// Priority: GS &gt; CS &gt; CPLX. No prefetch crosses a page boundary.
/// </summary>
public sealed class IpcpPrefetcher : IPrefetcher {
    // ── IP Table entry (shared by all three classifiers) ───────────────────────
    private struct IpEntry {
        public ulong Tag;             // PC >> (IpIndexBits + 2)
        public ulong LastPage;        // page of last access
        public int LastLineInPage;    // line-in-page of last access (0..linesPerPage-1)
        public int CsStride;          // CS stride in lines (signed)
        public int CsConf;            // 0–3 saturating counter; prefetch when ≥ 2
        public byte CplxSig;          // 7-bit rolling signature for CPLX
        public bool Valid;
        public bool LastRegionDense;  // was the IP's last region dense? (GS tentative)
        public ulong LastRegion;      // last accessed region number
    }

    // ── Complex Stride Prediction Table entry ──────────────────────────────────
    private struct CsptEntry {
        public int Stride;       // predicted next stride (lines)
        public int Confidence;   // 0–3 saturating; prefetch when ≥ 1
    }

    // ── Region Stream Table entry ──────────────────────────────────────────────
    private struct RstEntry {
        public ulong Region;
        public ulong Bitvector;         // one bit per cache-line slot in the 2 KB region
        public int Direction;           // net direction counter (+→forward, -→backward)
        public int PrevLineInRegion;    // last newly-seen line (for direction tracking)
        public int LruAge;
        public bool Valid;
        public bool Dense;
    }

    // ── Geometry ───────────────────────────────────────────────────────────────
    private readonly int _lineShift;
    private readonly int _pageShift;
    private readonly int _regionShift;
    private readonly int _linesPerPage;
    private readonly int _linesPerRegion;
    private readonly int _denseThreshold;  // ceil(75% of linesPerRegion)
    private readonly int _blockBytes;

    // ── Hardware tables ────────────────────────────────────────────────────────
    private const int IpTableSize = 64;
    private const int IpIndexBits = 6;   // log2(IpTableSize)
    private const int CsptSize    = 128;
    private const int RstSize     = 8;
    private const int RrSize      = 32;  // recent-request filter to suppress duplicate prefetches

    private readonly IpEntry[]   _ip   = new IpEntry[IpTableSize];
    private readonly CsptEntry[] _cspt = new CsptEntry[CsptSize];
    private readonly RstEntry[]  _rst  = new RstEntry[RstSize];
    private readonly ulong[]     _rr   = new ulong[RrSize];  // stores line addresses

    private int _tick;
    private int _rrHead;

    // ── Per-class prefetch degrees (paper §3 defaults) ─────────────────────────
    private const int DegreeCs   = 3;
    private const int DegreeCplx = 3;
    private const int DegreeGs   = 6;

    public IpcpPrefetcher(int blockBytes = 32, int pageBytes = 4096, int regionBytes = 2048) {
        if (!BitOperations.IsPow2(blockBytes))
            throw new ArgumentException("blockBytes must be a power of 2.", nameof(blockBytes));
        if (!BitOperations.IsPow2(pageBytes))
            throw new ArgumentException("pageBytes must be a power of 2.", nameof(pageBytes));
        if (!BitOperations.IsPow2(regionBytes))
            throw new ArgumentException("regionBytes must be a power of 2.", nameof(regionBytes));

        _blockBytes     = blockBytes;
        _lineShift      = BitOperations.Log2((uint)blockBytes);
        _pageShift      = BitOperations.Log2((uint)pageBytes);
        _regionShift    = BitOperations.Log2((uint)regionBytes);
        _linesPerPage   = pageBytes   / blockBytes;
        _linesPerRegion = regionBytes / blockBytes;
        _denseThreshold = (_linesPerRegion * 3 + 3) / 4; // ceil(75 %)

        if (_linesPerRegion > 64)
            throw new ArgumentException(
                "regionBytes / blockBytes must be ≤ 64 to fit in a 64-bit bitvector.",
                nameof(regionBytes));
    }

    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        ulong lineAddr       = address >> _lineShift;
        ulong page           = address >> _pageShift;
        int   lineInPage     = (int)(lineAddr & (ulong)(_linesPerPage   - 1));
        ulong region         = address >> _regionShift;
        int   lineInRegion   = (int)(lineAddr & (ulong)(_linesPerRegion - 1));
        ulong lineBase       = lineAddr << _lineShift;

        // ── IP table lookup ────────────────────────────────────────────────────
        int   ipIdx = (int)((pc >> 2) & (IpTableSize - 1));
        ulong ipTag = pc >> (IpIndexBits + 2);
        ref IpEntry ip = ref _ip[ipIdx];
        bool  ipHit = ip.Valid && ip.Tag == ipTag;

        // ── RST update (always, before any early return) ───────────────────────
        int rstSlot = FindRst(region);
        if (rstSlot < 0) {
            rstSlot = AllocateRst();
            _rst[rstSlot] = new RstEntry {
                Region           = region,
                Bitvector        = 0,
                Direction        = 0,
                PrevLineInRegion = lineInRegion,
                LruAge           = ++_tick,
                Valid            = true,
                Dense            = false,
            };
        }
        else {
            _rst[rstSlot].LruAge = ++_tick;
        }

        ref RstEntry rst = ref _rst[rstSlot];
        ulong bit = 1UL << lineInRegion;
        if ((rst.Bitvector & bit) == 0) {
            rst.Bitvector |= bit;
            int delta = lineInRegion - rst.PrevLineInRegion;
            if (delta > 0) rst.Direction++;
            else if (delta < 0) rst.Direction--;
            rst.PrevLineInRegion = lineInRegion;
        }
        rst.Dense = BitOperations.PopCount(rst.Bitvector) >= _denseThreshold;

        // ── IP miss: initialise entry; no prefetch yet ─────────────────────────
        if (!ipHit) {
            ip = new IpEntry {
                Tag             = ipTag,
                LastPage        = page,
                LastLineInPage  = lineInPage,
                CsStride        = 0,
                CsConf          = 0,
                CplxSig         = 0,
                Valid           = true,
                LastRegionDense = rst.Dense,
                LastRegion      = region,
            };
            return 0;
        }

        // ── Compute stride (same-page accesses only) ───────────────────────────
        int stride = (page == ip.LastPage) ? (lineInPage - ip.LastLineInPage) : 0;

        // ── CS confidence update ───────────────────────────────────────────────
        if (page == ip.LastPage && stride != 0) {
            if (stride == ip.CsStride) { if (ip.CsConf < 3) ip.CsConf++; }
            else                        { ip.CsStride = stride; if (ip.CsConf > 0) ip.CsConf--; }
        }
        else if (page != ip.LastPage) {
            if (ip.CsConf > 0) ip.CsConf--;
        }

        // ── CPLX signature and CSPT update ────────────────────────────────────
        byte oldSig = ip.CplxSig;
        if (stride != 0) {
            ref CsptEntry csptTrain = ref _cspt[oldSig & (CsptSize - 1)];
            if (stride == csptTrain.Stride) { if (csptTrain.Confidence < 3) csptTrain.Confidence++; }
            else                             { csptTrain.Stride = stride; if (csptTrain.Confidence > 0) csptTrain.Confidence--; }
        }
        byte newSig = (byte)(((oldSig << 1) ^ stride) & 0x7F);

        // ── GS state ──────────────────────────────────────────────────────────
        bool enteredNewRegion = ip.LastRegion != region;
        bool prevDense        = ip.LastRegionDense;

        // ── Update IP entry ────────────────────────────────────────────────────
        ip.LastPage        = page;
        ip.LastLineInPage  = lineInPage;
        ip.CplxSig         = newSig;
        ip.LastRegionDense = rst.Dense;
        ip.LastRegion      = region;

        // ── Classify and issue prefetches ──────────────────────────────────────
        bool isGs = rst.Dense || (enteredNewRegion && prevDense);
        int  count = 0;

        if (isGs) {
            int streamDir = rst.Direction >= 0 ? 1 : -1;
            ulong addr = lineBase;
            for (int k = 0; k < DegreeGs && count < targets.Length; k++) {
                addr = (ulong)((long)addr + streamDir * _blockBytes);
                if (!SamePage(addr, lineBase)) break;
                if (!InRrFilter(addr)) { targets[count++] = addr; AddToRrFilter(addr); }
            }
        }
        else if (ip.CsConf >= 2 && ip.CsStride != 0) {
            ulong addr = lineBase;
            for (int k = 0; k < DegreeCs && count < targets.Length; k++) {
                addr = (ulong)((long)addr + (long)ip.CsStride * _blockBytes);
                if (!SamePage(addr, lineBase)) break;
                if (!InRrFilter(addr)) { targets[count++] = addr; AddToRrFilter(addr); }
            }
        }
        else {
            ref CsptEntry csptPred = ref _cspt[newSig & (CsptSize - 1)];
            if (csptPred.Confidence >= 1 && csptPred.Stride != 0) {
                ulong addr = lineBase;
                for (int k = 0; k < DegreeCplx && count < targets.Length; k++) {
                    addr = (ulong)((long)addr + (long)csptPred.Stride * _blockBytes);
                    if (!SamePage(addr, lineBase)) break;
                    if (!InRrFilter(addr)) { targets[count++] = addr; AddToRrFilter(addr); }
                }
            }
        }

        return count;
    }

    private bool SamePage(ulong a, ulong b) => (a >> _pageShift) == (b >> _pageShift);

    private bool InRrFilter(ulong addr) {
        ulong line = addr >> _lineShift;
        for (int i = 0; i < RrSize; i++)
            if (_rr[i] == line) return true;
        return false;
    }

    private void AddToRrFilter(ulong addr) {
        _rr[_rrHead] = addr >> _lineShift;
        _rrHead = (_rrHead + 1) & (RrSize - 1);
    }

    private int FindRst(ulong region) {
        for (int i = 0; i < RstSize; i++)
            if (_rst[i].Valid && _rst[i].Region == region) return i;
        return -1;
    }

    private int AllocateRst() {
        for (int i = 0; i < RstSize; i++)
            if (!_rst[i].Valid) return i;
        int oldest = 0;
        for (int i = 1; i < RstSize; i++)
            if (_rst[i].LruAge < _rst[oldest].LruAge) oldest = i;
        return oldest;
    }
}
