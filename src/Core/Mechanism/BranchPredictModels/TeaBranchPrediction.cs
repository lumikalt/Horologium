namespace Mechanism.BranchPredictModels;

/// <summary>
///     TEA: Timely, Efficient, and Accurate branch precomputation, layered on top of
///     TAGE-SC-L. — Deshmukh, Cai &amp; Patt, "Timely, Efficient, and Accurate Branch
///     Precomputation", MICRO 2024.
///     <para>
///         <b>Scoping.</b> The paper's full mechanism duplicates the frontend and rename
///         stage to speculatively execute a dependence-chain "precomputation thread"
///         on-core, using synchronized branch-predictor timestamps to issue early
///         misprediction flushes, RAT-poisoning to detect when a chain read a value
///         produced outside itself, and partial-frontend flush support. None of that
///         duplicate-execution machinery is implemented: Horologium's <see cref="IBranchPredictor" />
///         contract only allows a predictor to override <c>Predict</c>'s direction/target
///         at fetch time, not to inject an independent instruction stream into the pipeline
///         or issue its own flushes. What is implemented is the paper's chain-construction
///         mechanism (Section III-A, Section IV-A/C): the H2P Branch Table, the Backward
///         Dataflow Walk over a Fill Buffer of retired instructions, and the per-branch
///         Block Cache of discovered dependence-chain instructions — followed by a
///         correlation lookup (rather than literal re-execution of the chain's arithmetic,
///         which the ISA-agnostic <c>Mechanism</c> layer has no way to perform) standing in
///         for "interpreting" the slice once its operands are ready.
///     </para>
///     <para>
///         <b>Offline chain construction.</b> <see cref="FromProfile" /> runs a functional
///         pre-pass (<see cref="TeaProfiler" />, an <see cref="ICommitObserver" />) that
///         decodes every committed instruction and appends it to a Fill Buffer, classifying
///         H2P branches against a scratch <see cref="TageScLPredictor" /> baseline the same
///         way <see cref="BranchNetPredictor" /> does (occurrence/mispredict-rate thresholds,
///         in place of the paper's 3-bit saturating-counter table with periodic decay — an
///         offline equivalent, since the whole pre-pass already only runs once). For each
///         H2P branch, a Backward Dataflow Walk starts a Source List at the branch's own
///         <see cref="ITooth.SourceRegisters" /> (its compare operands) and scans backward
///         through the Fill Buffer: any instruction whose <see cref="ITooth.DestinationRegister" />
///         is in the Source List is added to the dependence chain, and the Source List is
///         updated to remove that destination and add that instruction's own source
///         registers, exactly as Section III-A describes. The walk is repeated from the last
///         few dynamic instances of each H2P branch and the discovered producer PCs are
///         unioned together — the paper's "combining chains across multiple control flows"
///         (Section III-E), simplified from bit-mask OR-ing of basic-block segments down to
///         a plain union of producer PCs, since instruction-granularity chains (not uop/basic
///         -block segments) are all this predictor tracks. The result is the Block Cache: a
///         branch PC to ordered producer-PC-list map.
///     </para>
///     <para>
///         <b>Online use.</b> <see cref="NotifyRegisterResult" /> — the same
///         <see cref="IValueAwareBranchPredictor" /> hook <see cref="RunltsPredictor" /> and
///         <see cref="LvcpPredictor" /> use — records the most recent value produced by any
///         PC that appears in some branch's chain, tagged with a notification-count
///         timestamp. A branch's chain operands are considered "resident" (the paper's PRF-
///         readiness condition, which Horologium has no lower-level timing signal to observe
///         directly) when every producer in its chain has a value recorded within a recency
///         window; this is the same staleness-window proxy <see cref="RunltsPredictor" />
///         documents. When resident, the producer PCs and their values are folded into a
///         single hash that indexes a direct-mapped correlation table — identical in
///         structure (confidence counter, permanent retirement on a wrong-direction hit) to
///         <see cref="LvcpPredictor" />'s — standing in for the paper's literal re-execution
///         of the dependence chain's arithmetic. A branch with no Block Cache entry, or whose
///         chain operands are not yet resident, falls through to the TAGE-SC-L baseline
///         unchanged (the closest on-fetch-time analogue of falling back when the paper's
///         precomputation thread cannot supply a timely, verified result).
///     </para>
/// </summary>
public sealed class TeaPredictor : TageScLPredictor, IValueAwareBranchPredictor {
    private const int FreshnessWindow = 512; // NotifyRegisterResult notifications before a value goes stale
    private const int CorrIndexBits = 10;    // 1024 entries
    private const int CorrTableSize = 1 << TeaPredictor.CorrIndexBits;
    private const byte ConfMax = 31; // 5-bit confidence counter

    private readonly Dictionary<ulong, ulong[]> _chains;
    private readonly CorrEntry[] _corr = new CorrEntry[TeaPredictor.CorrTableSize];
    private readonly Dictionary<ulong, List<ulong>> _producerToBranches = new();
    private readonly Dictionary<ulong, (ulong Value, long Seq)> _producerValues = new();

    private long _clock;
    private bool _haveChainKey;
    private bool _havePredicted;

    // Cached across Predict → Update for the same PC, mirroring LvcpPredictor's identically
    // documented out-of-order residual: harmless if two dynamic instances of the same PC are
    // predicted before either commits.
    private bool _lastBaselinePred;
    private ulong _lastChainKey;
    private ulong _lastPredictedPc;

    /// <summary>Constructs a TEA predictor with an empty Block Cache (pure TAGE-SC-L fallback).</summary>
    public TeaPredictor() : this(new Dictionary<ulong, ulong[]>()) { }

    private TeaPredictor(Dictionary<ulong, ulong[]> chains) {
        _chains = chains;
        foreach ((ulong branchPc, ulong[] chain) in chains)
        foreach (ulong producerPc in chain) {
            if (!_producerToBranches.TryGetValue(producerPc, out List<ulong>? list))
                _producerToBranches[producerPc] = list = [];
            list.Add(branchPc);
        }
    }

    /// <summary>Number of H2P branches with a discovered dependence chain in the Block Cache.</summary>
    public int TrackedBranchCount => _chains.Count;

    /// <summary>Number of predictions where TEA's correlation lookup overrode the TAGE-SC-L baseline.</summary>
    public int TeaOverrides { get; private set; }

    // ── IValueAwareBranchPredictor ────────────────────────────────────────────

    /// <inheritdoc />
    public void NotifyRegisterResult(ulong pc, int destReg, ulong value, bool isLoad) {
        _clock++;
        if (_producerToBranches.ContainsKey(pc)) _producerValues[pc] = (value, _clock);
    }

    // ── IBranchPredictor overrides ────────────────────────────────────────────

    /// <inheritdoc />
    protected override bool ResolvePrediction(ulong pc, int provider, bool tagePred) {
        bool baseline = base.ResolvePrediction(pc, provider, tagePred);
        _lastBaselinePred = baseline;
        _lastPredictedPc = pc;
        _havePredicted = true;
        _haveChainKey = false;

        if (_chains.TryGetValue(pc, out ulong[]? chain) && TryGatherReadyKey(pc, chain, out ulong key)) {
            _lastChainKey = key;
            _haveChainKey = true;
            if (TryTeaPredict(key, out bool dir)) {
                TeaOverrides++;
                return dir;
            }
        }

        return baseline;
    }

    /// <inheritdoc />
    protected override void OnAfterUpdate(
        ulong pc,
        bool taken,
        bool provPred,
        int preScore,
        bool loopWasConfident
    ) {
        base.OnAfterUpdate(pc, taken, provPred, preScore, loopWasConfident);

        bool baselineMispredicted = _havePredicted && pc == _lastPredictedPc && _lastBaselinePred != taken;
        if (_haveChainKey && pc == _lastPredictedPc) TrainCorrTable(_lastChainKey, taken, baselineMispredicted);
    }

    // ── TEA internals ─────────────────────────────────────────────────────────

    // "Operands resident" proxy: every producer PC in the chain must have a value recorded
    // within the freshness window. Folds (branchPc, chain producer PCs and values) into a
    // single hash that stands in for "interpreting the slice" — see class doc comment.
    private bool TryGatherReadyKey(ulong branchPc, ulong[] chain, out ulong key) {
        key = branchPc * 0x9E3779B97F4A7C15UL;
        foreach (ulong producerPc in chain) {
            if (!_producerValues.TryGetValue(producerPc, out (ulong Value, long Seq) pv)
             || _clock - pv.Seq > TeaPredictor.FreshnessWindow) {
                key = 0;
                return false;
            }

            key ^= producerPc * 0xC2B2AE3D27D4EB4FUL;
            key ^= pv.Value * 0x165667B19E3779F9UL;
            key ^= key >> 33;
            key *= 0xFF51AFD7ED558CCDUL;
            key ^= key >> 33;
        }

        return chain.Length > 0;
    }

    private bool TryTeaPredict(ulong key, out bool dir) {
        ushort tag = CorrTag(key);
        ref CorrEntry e = ref _corr[CorrIdx(key)];
        if (e is { Valid: true, DirChanged: false, } && e.Tag == tag && e.Conf >= TeaPredictor.ConfMax) {
            dir = e.Dir;
            return true;
        }

        dir = false;
        return false;
    }

    private void TrainCorrTable(ulong key, bool taken, bool baselineMispredicted) {
        ushort tag = CorrTag(key);
        ref CorrEntry e = ref _corr[CorrIdx(key)];

        if (e is { Valid: true, DirChanged: false, } && e.Tag == tag) {
            if (e.Dir == taken) {
                if (e.Conf < TeaPredictor.ConfMax) e.Conf++;
            }
            else {
                e.DirChanged = true; // wrong-direction hit: permanently retire this entry
            }

            return;
        }

        // Never reclaim a retired (DirChanged) slot — retirement is permanent, mirroring
        // LvcpPredictor's identical rule.
        if (baselineMispredicted && !e.Valid) e = new CorrEntry { Tag = tag, Valid = true, Dir = taken, Conf = 1, };
    }

    private static int CorrIdx(ulong key) => (int)(key & (TeaPredictor.CorrTableSize - 1));

    private static ushort CorrTag(ulong key) => (ushort)(key >> 48);

    // ── Offline chain construction ────────────────────────────────────────────

    /// <summary>
    ///     Builds a TEA predictor from a completed <see cref="TeaProfiler" /> pass. The
    ///     profiler itself must be driven through a functional pre-pass (a
    ///     <c>SingleCycleTrain</c> run) by the caller — <c>Mechanism</c> has no dependency on
    ///     the pipeline/memory layer that requires, so that driving code lives at the config
    ///     layer (mirroring how <c>TrueOracleConfig</c> drives <c>BranchTraceRecorder</c>, and
    ///     how <see cref="BranchNetPredictor.FromProfile" /> drives <c>BranchProfiler</c>).
    /// </summary>
    public static TeaPredictor FromProfile(TeaProfiler profiler) => new(profiler.BuildBlockCache());

    /// <summary>
    ///     Functional-run commit observer: decodes every committed instruction into a Fill
    ///     Buffer, classifies H2P branches against a scratch TAGE-SC-L baseline, and — for
    ///     each H2P branch — performs the Backward Dataflow Walk to discover its dependence
    ///     chain. Attach to a <c>SingleCycleTrain</c> pre-pass; feed the result to
    ///     <see cref="TeaPredictor.FromProfile" />.
    /// </summary>
    public sealed class TeaProfiler(IDecoder decoder) : ICommitObserver {
        private const int MinOccurrences = 30;
        private const double MinMispredictRate = 0.08;
        private const int MaxH2PBranches = 32;
        private const int MaxChainLength = 4;
        private const int MaxWalkInstances = 5;
        private const int FillBufferWindow = 512; // paper Table II's Fill Buffer size
        private readonly List<(ulong Pc, int DestReg, int[] SourceRegs)> _log = [];
        private readonly Dictionary<ulong, List<int>> _occurrenceIndices = new();

        private readonly TageScLPredictor _scratchBaseline = new();

        private Dictionary<ulong, BranchStats> Stats { get; } = new();

        /// <inheritdoc />
        public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
            ITooth tooth = decoder.Decode(pc, rawEncoding);

            if (tooth.Class == ToothClass.ConditionalBranch) {
                bool taken = state.Pc != pc + (ulong)tooth.SizeBytes;
                BranchPrediction pred = _scratchBaseline.Predict(pc);
                bool mispredicted = pred.PredictedTaken != taken;
                _scratchBaseline.Update(pc, taken, state.Pc);

                if (!Stats.TryGetValue(pc, out BranchStats? st)) Stats[pc] = st = new BranchStats();
                st.Occurrences++;
                if (mispredicted) st.Mispredicts++;
            }

            int idx = _log.Count;
            _log.Add((pc, tooth.DestinationRegister, tooth.SourceRegisters.ToArray()));
            if (!_occurrenceIndices.TryGetValue(pc, out List<int>? indices)) _occurrenceIndices[pc] = indices = [];
            indices.Add(idx);
        }

        /// <summary>Selects H2P branches and builds their Block Cache dependence-chain entries.</summary>
        internal Dictionary<ulong, ulong[]> BuildBlockCache() {
            List<ulong> h2P = Stats
                             .Where(kv => kv.Value.Occurrences >= TeaProfiler.MinOccurrences
                                       && (double)kv.Value.Mispredicts / kv.Value.Occurrences
                                       >= TeaProfiler.MinMispredictRate
                              )
                             .OrderByDescending(kv => kv.Value.Mispredicts)
                             .Take(TeaProfiler.MaxH2PBranches)
                             .Select(kv => kv.Key)
                             .ToList();

            var result = new Dictionary<ulong, ulong[]>();
            foreach (ulong branchPc in h2P) {
                if (!_occurrenceIndices.TryGetValue(branchPc, out List<int>? indices)) continue;

                var producers = new List<ulong>();
                foreach (int idx in indices.TakeLast(TeaProfiler.MaxWalkInstances)) WalkBackward(idx, producers);
                if (producers.Count > 0) result[branchPc] = producers.Take(TeaProfiler.MaxChainLength).ToArray();
            }

            return result;
        }

        // Backward Dataflow Walk (paper Section III-A): Source List starts at the branch's
        // own compare operands; any instruction found scanning backward whose destination
        // register is in the Source List joins the chain, and its own source registers
        // replace that destination in the Source List.
        private void WalkBackward(int branchIdx, List<ulong> producers) {
            var sourceList = new HashSet<int>(_log[branchIdx].SourceRegs);
            int start = Math.Max(0, branchIdx - TeaProfiler.FillBufferWindow);
            for (int i = branchIdx - 1;
                 i >= start && sourceList.Count > 0 && producers.Count < TeaProfiler.MaxChainLength;
                 i--) {
                (ulong pc, int destReg, int[] srcRegs) = _log[i];
                if (destReg < 0 || !sourceList.Remove(destReg)) continue;
                if (!producers.Contains(pc)) producers.Add(pc);
                foreach (int r in srcRegs) sourceList.Add(r);
            }
        }

        private sealed class BranchStats {
            public int Mispredicts;
            public int Occurrences;
        }
    }

    private struct CorrEntry {
        public ushort Tag;
        public bool Valid;
        public bool Dir;
        public byte Conf;
        public bool DirChanged;
    }
}