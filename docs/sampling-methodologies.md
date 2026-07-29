# Sampling Methodologies: SimPoint and SMARTS

Two representative-sampling techniques for estimating whole-program performance from a small fraction of
simulated instructions. See [loop-point.md](loop-point.md) for LoopPoint, the multi-hart counterpart to SimPoint.

## SimPoint phase analysis (Pipeline/SimPointAnalysis)

Representative-sampling substrate after Sherwood, Perelman, Hamerly & Calder (ASPLOS 2002). `BbvProfiler` is an
`ICommitObserver` (typically attached to a functional `SingleCycleTrain`) that splits the committed stream into
fixed-length intervals and records per-interval basic-block vectors — block-entry counts weighted by block length,
with blocks identified dynamically (start = first instruction after a control-flow instruction or a trap
discontinuity) and per-PC decode info memoised. `SimPointAnalysis.Analyze` then normalizes each BBV, projects it to
15 dimensions through a seeded random linear projection (the matrix is derived from a hash of block PC × dimension,
never materialised), runs k-means for k = 1…10, scores each clustering with the Pelleg–Moore spherical-Gaussian BIC
(variance floored at a fraction of the global variance so duplicated interval vectors cannot drag k to the maximum),
and picks the smallest k whose score reaches 90% of the BIC spread. The result carries the per-interval phase
labels, one simulation point per phase (the interval closest to its cluster centroid) with its weight, and the
single simulation point closest to the whole-run centroid. `runner --simpoint <intervalSize> prog.elf` profiles and
prints the phase table; the simulation points feed the checkpoint/ROI handoff flows for detailed-model sampling
(on CoreMark at 20 K-instruction intervals this finds the iteration's interleaved kernels as ~7 recurring phases).

## SMARTS sampling (Pipeline/SmartsDriver)

Systematic statistical sampling after Wunderlich, Wenisch, Falsafi & Hoe (ISCA 2003) — the sibling methodology to
SimPoint above, trading SimPoint's few large clustered intervals for many small, evenly-spaced ones with a
statistically quantified confidence interval instead of a phase classification. `SmartsDriver.Run` alternates a
functional fast-forward train (`SingleCycleTrain`) with a detailed warm-then-measure window per sampling unit —
`W` unmeasured instructions to rebuild pipeline-internal state a functional pass can't warm, then `U` measured
instructions — spaced `K` instructions apart, starting at offset `J`. Unlike SimPoint's per-point
`ArchitecturalCheckpoint` (a full memory snapshot, fine for ~10 points but far too costly at SMARTS's own n≈10,000
scale), state moves between the two trains via a cheap in-memory register-level copy
(`ArchStateTransfer.CopyInto`) — both trains share the same `MemoryLayers` (cache/TLB) and `IBranchPredictor`
instances for the whole run, so cache/TLB/branch-predictor state stays continuously warm through the
fast-forwarded majority of the stream rather than needing to be rebuilt from cold at every window (the paper's
"functional warming", Section 3.1) — `SingleCycleTrain` already ticks a shared cache/TLB on every access, and,
given a predictor, trains it on every resolved branch even though it never itself speculates. `FiveStageTrain` and
`OooTrain` are both supported as the detailed pipeline (`SmartsDriver.FiveStage`/`SmartsDriver.Ooo`); the OoO case
additionally drains in-flight ROB/IQ/LQ/SQ state (`OooTrain.Drain`) after each measured window, since an OoO train
can still hold not-yet-retired instructions at the exact tick the window ends. `SmartsStatistics` computes the
sample mean CPI, its coefficient of variation, the achieved confidence interval at a given z (95%/99.7%), and the
sample size needed for a target confidence — the paper's own two-step procedure (run with an initial n, check the
achieved confidence, rerun with a computed `n_tuned` if it falls short) is left to the caller rather than
auto-looped, matching how the paper itself describes it as a manual step.
`runner --smarts <U> <W> <K> [--smarts-n <n>] [--smarts-offset <j>] [--smarts-argv "<args>"] prog.elf` runs it
against the `--sweep` configs (or the default sweep), printing mean CPI/IPC, coefficient of variation, 95%/99.7%
confidence intervals, and the recommended `n` for ±3% at 99.7% confidence. `--smarts-argv` mirrors
`--simpoint-argv`: it opts into Linux-ABI entry for a real compiled binary (a psABI initial stack, argv[0] the
ELF's file name plus extra space-separated entries from the flag's value, and a `LinuxSyscallEmulator`), so real
compiled binaries — not just bare-metal HTIF ELFs — can be sampled. Unlike `--simpoint-argv`, which needs a fresh
`LinuxSyscallEmulator` per functional pass (`CaptureSimPointCheckpoints` recreates its mechanism at every
checkpoint/measure boundary) and therefore serializes brk/mmap/fd/stdin state through
`ICheckpointableSyscallHandler`, SMARTS needs none of that: `SmartsDriver.Run` reuses one mechanism instance for
the run's entire lifetime, so whatever `ISyscallHandler` it carries persists across every functional/detailed
switch by plain object identity — sound by construction, not a serialize/restore round-trip that could silently
no-op. The one-time seam this needs — seeding the psABI stack pointer into whichever train is constructed first,
since `ArchStateTransfer.CopyInto` only fires once something has already been handed off — is
`SmartsDriver.Run`'s `seedInitialState` hook. Because SMARTS is strictly forward (never re-executes a region:
`warmStart` is clamped to never precede wherever the stream already is), a real syscall a sampled run passes
through fires exactly once, the same as an unsampled run — safe under sampling with no double-`write()`. Known
gaps: the return-address stack isn't warmed by the functional pass (it lives outside `IBranchPredictor`); and
timing-dependent CSRs (e.g. `mcycle`) can't be sampled faithfully, since functional fast-forward doesn't advance
cycle count the way detailed windows do — an inherent boundary of the sampling approach itself, not a gap to close.

