using Mechanism;
using Mechanism.BranchPredictModels;
using Orrery.Cache;

namespace Pipeline;

/// <summary>
/// Fetch Directed Instruction Prefetching (FDIP) — Reinman, Calder &amp; Austin, MICRO 1999.
/// <para>
/// A Fetch Target Queue (FTQ) decouples the branch predictor from the instruction cache.
/// Each cycle the predictor steps ahead using the raw backing memory (bypassing the cache
/// hierarchy to avoid charging stall latency to the pipeline), filling the FTQ with
/// predicted cache-line addresses. A prefetch window (entries 1..N, skipping entry 0 which
/// is too close) drives <see cref="SetAssociativeCache.Prefetch"/> on the I-cache.
/// </para>
/// <para>
/// The FTQ is drained position-by-position (one head entry per cache-line boundary crossed
/// by fetch), not by address comparison, so it remains correct across backward branches
/// (loops). Mispredictions are handled by <see cref="Flush"/>, which resets lookahead state
/// to the recovery target. The lookahead does not maintain a RAS; call/return targets are
/// predicted by the BTB and may be wrong, which the flush mechanism will recover.
/// FDIP is bare-mode only: lookahead PCs are not translated through the TLB.
/// </para>
/// </summary>
public sealed class FdipPrefetcher {
    private readonly IBranchPredictor _predictor;
    private readonly IDecoder _decoder;
    private readonly IMemory _backing;
    private readonly SetAssociativeCache _iCache;
    private readonly int _blockBytes;
    private readonly int _ftqCapacity;
    private readonly int _prefetchWindow;

    private readonly Queue<ulong> _ftq = new();
    private ulong _lookAheadPc;
    private ulong _lastEnqueuedLine = ulong.MaxValue;
    private ulong _prevFetchLine = ulong.MaxValue;

    /// <param name="predictor">Branch predictor; <c>Predict()</c> is called for lookahead only — training state is unaffected.</param>
    /// <param name="decoder">ISA decoder; used to determine instruction size and branch class.</param>
    /// <param name="backing">Raw backing memory; reads bypass the cache so no miss stalls are charged.</param>
    /// <param name="iCache">L1 I-cache; <see cref="SetAssociativeCache.Prefetch"/> is called for each prefetch target.</param>
    /// <param name="entryPoint">Initial lookahead PC.</param>
    /// <param name="ftqCapacity">FTQ entries (default 32 per the paper).</param>
    /// <param name="prefetchWindow">How many FTQ entries ahead to prefetch (default 10 per the paper; entry 0 is skipped).</param>
    public FdipPrefetcher(
        IBranchPredictor predictor,
        IDecoder decoder,
        IMemory backing,
        SetAssociativeCache iCache,
        ulong entryPoint,
        int ftqCapacity = 32,
        int prefetchWindow = 10
    ) {
        _predictor = predictor;
        _decoder = decoder;
        _backing = backing;
        _iCache = iCache;
        _blockBytes = iCache.BlockBytes;
        _ftqCapacity = ftqCapacity;
        _prefetchWindow = prefetchWindow;
        _lookAheadPc = entryPoint;
    }

    /// <summary>
    /// Advance FDIP by one simulated cycle. Call once per cycle with the current fetch PC.
    /// </summary>
    public void Tick(ulong fetchPc) {
        ulong currentLine = LineOf(fetchPc);

        // Drain one FTQ head entry each time fetch crosses into a new cache line.
        if (_prevFetchLine == ulong.MaxValue) {
            _prevFetchLine = currentLine;
        } else if (currentLine != _prevFetchLine) {
            if (_ftq.Count > 0) _ftq.Dequeue();
            _prevFetchLine = currentLine;
        }

        // Step the branch predictor ahead to keep FTQ full.
        // Guard against spin: at most capacity × (blockBytes/2 + 2) iterations.
        int maxSteps = _ftqCapacity * (_blockBytes / 2 + 2);
        for (int s = 0; _ftq.Count < _ftqCapacity && s < maxSteps; s++) {
            ulong line = LineOf(_lookAheadPc);
            if (line != _lastEnqueuedLine) {
                _ftq.Enqueue(line);
                _lastEnqueuedLine = line;
            }
            StepLookAhead();
        }

        // Issue prefetches for FTQ positions 1.._prefetchWindow.
        // Position 0 = the cache line currently being fetched — too close to benefit from a prefetch.
        int pos = 0;
        foreach (ulong addr in _ftq) {
            if (pos > 0 && pos <= _prefetchWindow)
                _iCache.Prefetch(addr);
            if (++pos > _prefetchWindow) break;
        }
    }

    /// <summary>Reset on branch misprediction or trap redirect.</summary>
    public void Flush(ulong target) {
        _ftq.Clear();
        _lookAheadPc = target;
        _lastEnqueuedLine = ulong.MaxValue;
        _prevFetchLine = ulong.MaxValue;
    }

    private void StepLookAhead() {
        try {
            var raw = (uint)_backing.Read(_lookAheadPc, 4);
            FetchHint hint = _decoder.GetFetchHint(_lookAheadPc, raw);
            BranchPrediction pred = hint.IsBranch
                ? _predictor.Predict(_lookAheadPc, hint.BranchTarget)
                : BranchPrediction.NotTaken(_lookAheadPc + (ulong)hint.InstructionSize);
            _lookAheadPc = pred.PredictedTaken ? pred.PredictedTarget : _lookAheadPc + (ulong)hint.InstructionSize;
        } catch {
            // Lookahead reached unmapped memory (e.g. end of program image); skip forward.
            _lookAheadPc += (ulong)_blockBytes;
        }
    }

    private ulong LineOf(ulong addr) => addr & ~(ulong)(_blockBytes - 1);
}
