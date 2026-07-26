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
