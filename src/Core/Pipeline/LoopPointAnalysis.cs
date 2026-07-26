#region

using Mechanism;

#endregion

namespace Pipeline;

/// <summary>
///     Loop-header region-boundary detection — the region-marker half of LoopPoint (Sabu, Patil,
///     Heirman &amp; Carlson, "LoopPoint: Checkpoint-driven Sampled Simulation for Multi-threaded
///     Applications", HPCA 2022). A lighter substitute for the paper's Pin DCFG/dominator analysis:
///     rather than build a full control-flow graph, a loop header is identified purely from the
///     dynamic commit stream as the target of a backward control transfer (a branch/jump landing at
///     or before its own source address), the same discontinuous-PC signal <see cref="Mechanism" />
///     already exposes to <c>BbvProfiler</c> for basic-block splitting.
///     <para>
///         A candidate transfer must be both direct and not a call, via
///         <see cref="IDecoder.GetFetchHint" /> (the same ISA-agnostic pre-decode hint branch
///         predictors already use for RAS/indirect handling): <see cref="FetchHint.BranchTarget" />
///         must have a statically known value (a conditional branch, or an unconditional jump
///         computed as PC + immediate), and <see cref="FetchHint.IsCall" /> must be false. Both
///         halves matter independently: excluding indirect transfers (JALR — returns, virtual calls,
///         computed gotos) rules out a <c>ret</c> landing at a lower address than its own call site;
///         excluding calls separately rules out a <em>direct</em> call (<c>jal ra, target</c>) to a
///         function placed, in link order, before its caller — direct and backward, but still not a
///         loop iteration. Neither check alone is sufficient (a direct backward call passes the
///         indirect check; an indirect non-call jump-table dispatch would pass a call-only check), so
///         both are required. Real loop back-edges are essentially always direct, non-call transfers
///         (a conditional branch, or a compiler-emitted unconditional jump for a <c>goto</c>-style
///         loop) — this is the disambiguation that makes backward-branch-target detection viable
///         without a real dominator analysis.
///     </para>
///     <para>
///         <c>count</c> is the number of times the backward edge has been <em>taken</em> to reach
///         that header, not the loop's total iteration count: an N-iteration loop's header is reached
///         once by falling into it from the code before the loop (not a discontinuous transfer, so
///         not observable as a distinct event here) and N-1 more times via the backward branch, so
///         its final marker reads <c>(header, N-1)</c>. This is a streaming, single-pass design (the
///         same constraint <c>BbvProfiler</c> already accepts) — a header cannot be recognized as such
///         until its first backward-taken visit, by which point the initial fall-through entry is
///         already in the past and unrecordable without buffering. Still monotonic and unique per
///         loop invocation, which is what downstream region-boundary consumers need.
///     </para>
///     <para>
///         <paramref name="rangeStart" />/<paramref name="rangeEnd" /> scope detection to the loaded
///         program's own address space (a basic sanity bound) — they do <em>not</em> by themselves
///         separate user code from statically-linked library code sharing the same segment.
///         <paramref name="excludedRanges" /> is the actual mechanism for that separation (see
///         <see cref="SyncLibrarySymbols" />): a header whose PC falls in an excluded range is never
///         counted or marked, mirroring the paper's spin-loop filtering — synchronization-library
///         busy-waits (a userspace lock retry loop, a spin-then-block wait) still execute exactly as
///         normal, they are simply not treated as representative program phases. Only the header's
///         own address is checked, not the source of the backward edge, matching the coarse,
///         whole-function granularity "exclude this library code" naturally has.
///     </para>
/// </summary>
public sealed class LoopHeaderTracker(
    IDecoder decoder,
    ulong rangeStart,
    ulong rangeEnd,
    IReadOnlyList<(ulong Start, ulong End)>? excludedRanges = null
) : ICommitObserver {
    private readonly IReadOnlyList<(ulong Start, ulong End)> _excludedRanges = excludedRanges ?? [];
    private readonly Dictionary<ulong, long> _headerIterationCounts = [];
    private readonly List<(ulong Pc, long Count)> _markers = [];
    private bool _hasPrevious;
    private bool _previousIsLoopEdgeCandidate;
    private ulong _previousPc;
    private int _previousSize;

    /// <summary>
    ///     Final per-header count of backward-taken re-entries (see the class doc comment for why
    ///     this is one less than the loop's total iteration count).
    /// </summary>
    public IReadOnlyDictionary<ulong, long> HeaderIterationCounts => _headerIterationCounts;

    /// <summary>
    ///     The paper's <c>(PC, count)</c> region-boundary markers, in execution order: one entry per
    ///     backward-taken re-entry to a loop header, <c>count</c> being that header's running
    ///     re-entry number (1-based).
    /// </summary>
    public IReadOnlyList<(ulong Pc, long Count)> Markers => _markers;

    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        if (_hasPrevious
            && pc != _previousPc + (ulong)_previousSize
            && pc <= _previousPc
            && pc >= rangeStart && pc < rangeEnd
            && _previousIsLoopEdgeCandidate
            && !IsExcluded(pc)) {
            long count = _headerIterationCounts.GetValueOrDefault(pc) + 1;
            _headerIterationCounts[pc] = count;
            _markers.Add((pc, count));
        }

        FetchHint hint = decoder.GetFetchHint(pc, rawEncoding);
        _previousPc = pc;
        _previousSize = hint.InstructionSize;
        _previousIsLoopEdgeCandidate = hint.BranchTarget.HasValue && !hint.IsCall;
        _hasPrevious = true;
    }

    // Linear scan: excludedRanges is a handful of library functions at most, not worth a sorted
    // structure for this.
    private bool IsExcluded(ulong pc) {
        foreach ((ulong start, ulong end) in _excludedRanges)
            if (pc >= start && pc < end)
                return true;

        return false;
    }
}

/// <summary>
///     Classifies ELF symbols as synchronization-library code by name prefix, for
///     <see cref="LoopHeaderTracker" />'s spin-loop filtering (LoopPoint's exclusion of
///     busy-waiting from loop-based work counting). Name-prefix matching, not a real call-graph or
///     binary analysis — a best-effort, non-exhaustive list of musl/libpthread/libgomp internal
///     symbol prefixes, verified against a real compiled binary's own symbol table
///     (<c>TestBinaries/pthread_probe.c</c>) rather than guessed.
/// </summary>
public static class SyncLibrarySymbols {
    /// <summary>
    ///     Prefixes covering musl's internal thread-list/VM/futex-wait locking (<c>__tl_*</c>,
    ///     <c>__vm_*</c>, <c>__wait</c>/<c>__timedwait*</c>, <c>__lock</c>/<c>__unlock</c> and their
    ///     file/open-file-list variants), the public pthread API (<c>pthread_*</c>/<c>__pthread_*</c>),
    ///     POSIX semaphores, and GNU OpenMP's runtime (<c>gomp_*</c>/<c>GOMP_*</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultNamePrefixes = [
        "__tl_", "__vm_", "__wait", "__timedwait", "__lock", "__unlock", "__ofl_", "__lockfile",
        "__unlockfile", "pthread_", "__pthread_", "sem_", "gomp_", "GOMP_",
    ];

    /// <summary>
    ///     Builds the <c>[Start, End)</c> address ranges of every symbol in <paramref name="workload" />
    ///     whose name starts with one of <paramref name="namePrefixes" /> (default
    ///     <see cref="DefaultNamePrefixes" />).
    /// </summary>
    public static IReadOnlyList<(ulong Start, ulong End)> ExcludedRanges(
        IElfWorkload workload,
        IReadOnlyList<string>? namePrefixes = null
    ) {
        IReadOnlyList<string> prefixes = namePrefixes ?? DefaultNamePrefixes;
        return [
            .. workload.EnumerateSymbols()
                .Where(sym => prefixes.Any(sym.Name.StartsWith))
                .Select(sym => (sym.Address, sym.Address + sym.Size)),
        ];
    }
}

/// <summary>
///     Multi-hart region-boundary + BBV collection — the rest of LoopPoint's profiling pass, built
///     on <see cref="LoopHeaderTracker" /> (region markers) and <see cref="BbvProfiler" /> (per-block
///     fingerprints), one pair per hart. Wire <see cref="HartObserver" /> into each of
///     <c>MultiHartKernel</c>'s per-hart slots (<c>SetObserver</c>) for the profiling-pass run, then
///     read <see cref="RegionBbvs" /> — feed it straight into <c>SimPointAnalysis.Analyze</c>, unchanged.
///     <para>
///         A region's length target is <paramref name="targetGlobalInstructions" /> — the paper's
///         "approximately N × 100 million <em>global</em> (all-threads) instructions" for an
///         N-threaded application (Section III-A) — measured as a single counter shared across every
///         hart, incremented once per <em>non-excluded</em> commit on <em>any</em> hart (spin-loop
///         instructions are not "work done", so they don't count toward the target any more than they
///         count toward a BBV's weight). A region does not end the instant that target is reached: it
///         ends at the <em>next</em> loop-header hit afterward, on whichever hart reaches one first —
///         "we do not restrict specific threads to indicate loop boundaries" (Section III-D). Because
///         <see cref="LoopHeaderTracker" /> already refuses to mark a header inside an excluded range,
///         a region can only ever close on a header "present in the main image of the application",
///         automatically, with no extra check needed here.
///     </para>
///     <para>
///         At that instant every hart's accumulated <see cref="BbvProfiler" /> interval (since the
///         previous boundary, however little or much progress that hart made) is closed at once and
///         concatenated into one region vector, each hart's contribution first normalized to sum to
///         the same fixed total (<see cref="NormalizationScale" />) and namespaced into a disjoint key
///         range (<see cref="NamespaceKey" />) so identical-binary threads' identical PCs don't
///         collide. Per-thread normalization first, then concatenation — not one normalization over
///         the combined vector — is what makes a thread's own relative time-in-block fingerprint
///         survive the concatenation undiluted by how much more or less work a busier or lazier
///         sibling thread happened to do in the same region (Section III-E: "per-region BBVs of each
///         thread are concatenated into a longer, global BBV"). A hart that made zero progress this
///         region (halted, not yet spawned, or futex-blocked throughout) contributes nothing rather
///         than a stale reused interval.
///     </para>
/// </summary>
public sealed class MultiHartLoopPointProfiler {
    // Large enough that every real region's proportional weights survive integer rounding with
    // negligible error, small enough to keep intermediate longs far from overflow when concatenated.
    private const long NormalizationScale = 1_000_000;

    private readonly BbvProfiler[] _bbvProfilers;
    private readonly IReadOnlyList<(ulong Start, ulong End)> _excludedRanges;
    private readonly LoopHeaderTracker[] _loopTrackers;
    private readonly List<IReadOnlyDictionary<ulong, long>> _regionBbvs = [];
    private readonly long _targetGlobalInstructions;
    private long _instructionsSinceLastBoundary;

    public MultiHartLoopPointProfiler(
        IReadOnlyList<IDecoder> hartDecoders,
        ulong rangeStart,
        ulong rangeEnd,
        long targetGlobalInstructions,
        IReadOnlyList<(ulong Start, ulong End)>? excludedRanges = null
    ) {
        if (hartDecoders.Count == 0)
            throw new ArgumentException("At least one hart decoder required.", nameof(hartDecoders));
        if (targetGlobalInstructions <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetGlobalInstructions));

        _targetGlobalInstructions = targetGlobalInstructions;
        _excludedRanges = excludedRanges ?? [];
        _bbvProfilers = new BbvProfiler[hartDecoders.Count];
        _loopTrackers = new LoopHeaderTracker[hartDecoders.Count];
        for (var i = 0; i < hartDecoders.Count; i++) {
            // long.MaxValue: this profiler's own region-boundary logic decides when to cut an
            // interval (via Complete()), not BbvProfiler's built-in fixed-instruction-count slicing.
            _bbvProfilers[i] = new BbvProfiler(hartDecoders[i], long.MaxValue);
            _loopTrackers[i] = new LoopHeaderTracker(hartDecoders[i], rangeStart, rangeEnd, _excludedRanges);
        }
    }

    /// <summary>Completed multi-thread region BBVs, in region order. Feed to <c>SimPointAnalysis.Analyze</c>.</summary>
    public IReadOnlyList<IReadOnlyDictionary<ulong, long>> RegionBbvs => _regionBbvs;

    /// <summary>
    ///     The commit observer for hart <paramref name="hartId" /> — wire into
    ///     <c>MultiHartKernel.SetObserver(hartId, ...)</c> before running the profiling pass.
    /// </summary>
    public ICommitObserver HartObserver(int hartId) => new HartObserverAdapter(this, hartId);

    /// <summary>Flushes the trailing partial region after the run ends. Call once after the run.</summary>
    public void Complete() {
        if (_instructionsSinceLastBoundary > 0) CloseRegion();
    }

    private void CloseRegion() {
        var combined = new Dictionary<ulong, long>();
        for (var h = 0; h < _bbvProfilers.Length; h++) {
            // CutInterval (not Complete): a hart's basic block may still be open — no control-flow
            // instruction seen yet — right at the region boundary. CutInterval credits the portion
            // already executed to this region and keeps tracking the remainder for the next region
            // under its own continuation address, exactly like BbvProfiler's own fixed-instruction-
            // count auto-cut. Complete() would instead terminate tracking outright, silently
            // misattributing that remainder's first post-boundary instructions to a spurious new
            // block. Always appends (even an empty interval for a hart with zero non-excluded
            // commits since the last boundary — halted, not yet spawned, or futex-blocked
            // throughout), so Intervals[^1] is always this region's entry, never a stale one.
            _bbvProfilers[h].CutInterval();
            IReadOnlyDictionary<ulong, long> raw = _bbvProfilers[h].Intervals[^1];

            foreach ((ulong key, long value) in NormalizeAndNamespace(raw, h))
                combined[key] = combined.GetValueOrDefault(key) + value;
        }

        _regionBbvs.Add(combined);
        _instructionsSinceLastBoundary = 0;
    }

    // Normalizes one hart's raw block-instruction counts to sum to exactly NormalizationScale
    // (largest-remainder rounding keeps the sum exact despite integer truncation), then namespaces
    // each block's key by hart so identical PCs across identical-binary threads never collide.
    private static Dictionary<ulong, long> NormalizeAndNamespace(IReadOnlyDictionary<ulong, long> raw, int hartId) {
        var result = new Dictionary<ulong, long>();
        long total = raw.Values.Sum();
        if (total == 0) return result;

        long allocated = 0;
        ulong keyOfLargest = 0;
        long largestRaw = -1;
        foreach ((ulong pc, long count) in raw) {
            var scaled = (long)Math.Round((double)count / total * MultiHartLoopPointProfiler.NormalizationScale);
            ulong key = MultiHartLoopPointProfiler.NamespaceKey(hartId, pc);
            result[key] = scaled;
            allocated += scaled;
            if (count > largestRaw) {
                largestRaw = count;
                keyOfLargest = key;
            }
        }

        // Correct rounding drift on the largest entry so every hart's contribution sums to exactly
        // the same total — required for Project()'s divide-by-region-total to recover each thread's
        // true per-block proportion instead of a thread-activity-weighted blend.
        result[keyOfLargest] += MultiHartLoopPointProfiler.NormalizationScale - allocated;
        return result;
    }

    // Real RISC-V (and every other ISA this codebase targets) addresses fit comfortably under 2^48;
    // the top 16 bits are otherwise always zero, so they're free to carry the hart index.
    private static ulong NamespaceKey(int hartId, ulong pc) => ((ulong)hartId << 48) | (pc & 0xFFFF_FFFF_FFFFUL);

    private bool IsExcluded(ulong pc) {
        foreach ((ulong start, ulong end) in _excludedRanges)
            if (pc >= start && pc < end)
                return true;
        return false;
    }

    private sealed class HartObserverAdapter(MultiHartLoopPointProfiler owner, int hartId) : ICommitObserver {
        public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
            // Spin-loop/sync-library instructions still execute normally but aren't "work done":
            // excluded from both the BBV fingerprint and the global region-length target.
            if (!owner.IsExcluded(pc)) {
                owner._bbvProfilers[hartId].OnCommit(pc, rawEncoding, state);
                owner._instructionsSinceLastBoundary++;
            }

            // The loop tracker sees every commit unconditionally — it needs the full stream for its
            // own discontinuity detection, and it already refuses to mark an excluded-range header.
            int markersBefore = owner._loopTrackers[hartId].Markers.Count;
            owner._loopTrackers[hartId].OnCommit(pc, rawEncoding, state);
            bool markerHit = owner._loopTrackers[hartId].Markers.Count > markersBefore;

            if (markerHit && owner._instructionsSinceLastBoundary >= owner._targetGlobalInstructions)
                owner.CloseRegion();
        }
    }
}
