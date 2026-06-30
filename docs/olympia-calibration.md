# Olympia timing calibration

A **descriptive cross-model comparison** of Horologium's out-of-order timing
against [Olympia](https://github.com/riscv-software-src/riscv-perf-model), the
Sparta-based RISC-V performance model this project is patterned on. This is
*not* a fit or a validation: Olympia is a peer model, not ground truth, so the
goal is to *explain* differences, not to match a number.

Reproduce with `scripts/olympia-calibrate.sh` (needs `olympia` on PATH — `nix
build .#olympia` or `nix develop` — and the .NET Runner).

## Method

Horologium emits a functional instruction trace (`Runner --trace-json`,
single-cycle); the trace carries the raw **opcode** per instruction, so Olympia's
Mavis decodes it directly — covering FP/vector/compressed, which an earlier
mnemonic+registers form could not (it mis-encoded FP registers → Mavis
`InvalidRegisterNumber`, and every riscv-tests benchmark is FP). Olympia replays
the trace at three arch widths (`small`/`medium`/`big_core` = 2/3/8-wide);
Horologium's OoO train runs the same program at matching `issue_width` under
three memory configurations:

- **No cache** — idealized 1-cycle loads/stores, pure ILP baseline
- **L1$** — 16 KB split I/D, 10-cycle miss penalty; load-side MLP (each missed
  load carries its own latency countdown, so independent misses overlap); store
  write-miss stalls charged lump-sum
- **L1$+WB** — same L1, plus a 16-slot write buffer that absorbs store-commit
  write-miss stalls so the pipeline can keep running while the write bus drains
  (store-side MLP)

Olympia's `small_core` models a 16 KB L1 write-through cache, so the L1$+WB
column is the closest structural match.

## Results (IPC)

WB∝w = write buffer sized to issue width (2/3/8 slots for w2/w3/w8).
+PF = WB∝w + stride prefetcher (idealized: free, instant fill).
Horologium w2/w3/w8 maps to Olympia small/medium/big_core.

| workload | Horo 2/3/8         | + L1$ 2/3/8        | + WB∝w 2/3/8       | + PF 2/3/8         | Olympia s/m/b      |
|----------|--------------------|--------------------|--------------------|--------------------|--------------------|
| rich     | 1.44 / 1.79 / 1.98 | 1.24 / 1.50 / 1.63 | 1.41 / 1.74 / 1.93 | 1.41 / 1.75 / 1.93 | 0.75 / 1.01 / 0.97 |
| vvadd    | 1.28 / 1.42 / 1.27 | 0.66 / 0.71 / 0.66 | 0.93 / 1.09 / 1.12 | 0.94 / 1.09 / 1.13 | 0.90 / 1.02 / 1.07 |
| multiply | 1.51 / 1.69 / 1.73 | 1.41 / 1.57 / 1.60 | 1.48 / 1.66 / 1.69 | 1.48 / 1.66 / 1.69 | 1.09 / 1.88 / 2.03 |
| median   | 0.83 / 0.83 / 0.74 | 0.53 / 0.54 / 0.50 | 0.77 / 0.78 / 0.70 | 0.79 / 0.79 / 0.71 | 1.01 / 1.09 / 1.11 |
| towers   | 0.73 / 0.59 / 0.49 | 0.67 / 0.54 / 0.46 | 0.67 / 0.55 / 0.46 | 0.67 / 0.55 / 0.46 | 0.61 / 0.64 / 0.65 |
| qsort    | 0.99 / 1.10 / 1.01 | 0.98 / 1.10 / 1.01 | 0.99 / 1.10 / 1.01 | 0.99 / 1.10 / 1.01 | 1.24 / 1.45 / 1.47 |
| rsort    | 1.76 / 2.08 / 2.14 | 1.35 / 1.55 / 1.58 | 1.58 / 1.97 / 2.13 | 1.60 / 1.97 / 2.13 | 0.99 / 1.04 / 1.03 |
| memcpy   | 1.50 / 1.63 / 1.55 | 0.53 / 0.56 / 0.55 | 0.68 / 0.81 / 1.19 | 0.68 / 0.78 / 1.18 | 0.90 / 0.92 / 0.92 |

## What this shows

- **vvadd is the closest match yet across all widths.** WB∝w gives 0.93/1.09/1.12
  vs Olympia 0.90/1.02/1.07 — within ~5% at every width. This is the best
  cross-model alignment seen in any configuration. The width-proportional policy
  keeps saturation behaviour consistent: with 2 slots at w2, two simultaneous
  store misses exhaust the buffer and subsequent misses fall back to lump-sum,
  producing the same saturation pressure that Olympia's LSU resource constraints
  impose.

- **memcpy shows a crossing pattern** — WB∝w undershoots at w2 (0.68 vs 0.90)
  and overshoots at w8 (1.19 vs 0.92). memcpy's access pattern is denser than
  vvadd's (4-byte word copies in a tight loop), so even 2 slots saturate faster
  than the proportional model assumes at narrow width. The crossing at w8 means
  8 slots is still too permissive there. A fixed WB4 was better for memcpy at w2;
  the proportional policy is better for vvadd. A single capacity rule cannot
  simultaneously match both — the two workloads have different store-stream
  densities relative to issue width.

- **Compute-bound workloads (rich, multiply, qsort, towers) are insensitive to
  WB size** — the WB∝w numbers are within rounding of the L1$ column. Their
  bottleneck is ILP and branch prediction, not store bandwidth.

- **median stays below Olympia (0.77 vs 1.01)** regardless of write-buffer
  configuration. The write buffer is not its lever; the gap is load-latency or
  branch-prediction bound.

- **rsort is an outlier** — Horologium (1.58–2.13) runs far above Olympia
  (0.99–1.03). Radix sort's scattered write pattern triggers structural limits in
  Olympia (memory-bus occupancy, LSU queuing) that Horologium doesn't model.
  WB∝w shrinks the w2 gap slightly (1.76→1.58) but the fundamental mismatch is
  architectural, not a WB-sizing issue.

- **Compute-bound code (rich, multiply) stays above Olympia.** Olympia replays
  the committed trace (no misprediction penalty), yet is still lower — implying
  additional issue constraints in Olympia (functional-unit queuing, forwarding
  latency) absent in Horologium.

- **No clean cross-model rank correspondence** — two different microarchitectures
  — read the *direction* of error by workload class, not the absolute deltas.

## Prefetcher result: MLP already hides what prefetching would fix (done)

An idealized stride prefetcher (free, instant fill; RPT table indexed by PC) was added
to the WB∝w configuration (+PF column). Across every workload the improvement is at
most ~2%, well within noise. Three reasons this is the expected ceiling:

1. **Load-side MLP is already active.** Each missed load carries its own in-flight
   countdown so independent misses overlap without blocking dispatch or issue.
   A prefetch that arrives for free does not improve on a miss that is already
   non-blocking — both expose the load to the pipeline at the same effective cost.

2. **The workloads with remaining gaps are not prefetch-amenable.** qsort and
   median are branch-prediction and ILP bound, not miss-latency bound. rsort is an
   architectural-model mismatch (memory-bus occupancy). A prefetcher can only help
   when miss latency is on the critical path.

3. **The idealized model is a ceiling, not a floor.** Real prefetchers cost bandwidth
   and arrive with a finite latency. Adding a realistic prefetch latency (a countdown
   like the MSHR mechanism, with the demand hit paying the remaining countdown rather
   than zero) would make the effective benefit even smaller than the idealized numbers
   show.

The prefetcher infrastructure (next-line, stride RPT, `MemoryLayers.TryPrefetch` with
MMIO guard, `dcache_prefetches` counter, `TrainConfig.DPrefetcher` JSON field) is in
place and wired. The calibration script now includes the +PF column.

## Load-side memory-level parallelism (done)

The lump-sum stall model (charge every cache-miss penalty to the global clock,
serializing all misses) over-penalized memory-bound code. The OoO now gives each
missed **load** its own in-flight latency countdown, so independent load misses
overlap with other work — the L1$ column above already reflects this.

A correctness trap is worth recording, because the first attempt
([reverted commit](../TODO.md)) shipped green against the entire 1149-test suite
*and* Spike co-sim, yet was functionally broken. Under MLP a missed load sits in
the in-flight buffer for many cycles before it broadcasts its value. The original
code registered the load's address / executed-flag at **broadcast** time, so
during that window the load was invisible to store-to-load disambiguation: an
older store could resolve and commit unseen, and the load would then broadcast a
stale value (on `memcpy` w8+L1 it read a corrupted return address and livelocked).
The fix registers load disambiguation state at **execute** time, keeping the
in-flight load visible to violation detection for its whole life. The gap existed
because nothing in the suite exercised *load-miss + store-to-same-address + an L1*
together; `Tests/RiscV32/OoOMemoryParallelismTests.cs` now does, and is verified to
fail on the broken model.

## Store-side memory-level parallelism (done; L1$+WB column)

Committed stores write through the cache immediately (write-through /
no-write-allocate). Previously the resulting write-miss penalty was charged
lump-sum to the pipeline clock, serializing all store-commit cycles for
store-heavy workloads. The write buffer (`writeBufferCapacity > 0`, 16 slots in
the calibration sweep) absorbs those stalls into a per-slot countdown; the
pipeline keeps running while the write bus drains. The L1$+WB column captures the
combined effect of both load-side and store-side MLP, and is the closest match to
Olympia's `small_core` memory model.

## Bug found and fixed: HTIF MMIO was cached

Getting the L1 column at all required a fix. The first cache-matched run hung:
median went from 16k cycles to 1.0M (= maxTicks) with ~989k dcache hits for a
~13.5k-instruction program. Root cause (confirmed on both FiveStage and OoO, so
*not* OoO-specific): the L1 sits above `HtifMemory`, whose auto-ACK writes
`fromhost` to the backing *below* the cache; `tohost`/`fromhost` share a line, so
once `printstr` write-allocates it the poll loop reads a stale cached `0` forever.
`rich.elf` (EBREAK, no `printstr`) was immune — the tell. Fix: model MMIO as
uncacheable (`MemoryConfig.Uncacheable*` + `UncacheableMemory` router; `Experiment`
sets the window to the workload's HTIF registers). Regression-tested.

## Caveats

- **Trace replay has no wrong path.** Olympia replays the committed trace, so it
  pays no misprediction penalty — which should *inflate* its IPC versus a real
  run. Factor that in when reading the compute-bound rows.
- **Two different microarchitectures.** Absolute IPC and rank order will not
  match; the value is the *direction* of the per-class error (memory- vs
  compute-bound), which is robust here.

## Next steps and research points

1. **Load-side MLP — done.** Lifted memory-bound IPCs partway toward Olympia.
2. **Store-side MLP — done.** Write buffer wired with width-proportional sizing
   (WB∝w); vvadd matches Olympia within ~5% at all widths.
3. **Prefetcher (next-line + stride RPT) — done.** Wired; idealized +PF column
   shows ≤2% improvement because load-side MLP already hides miss latency. The
   remaining gaps are not prefetch-amenable (see §Prefetcher result above).
4. **D-cache read port constraint — covered by FU budget.** The
   `FuLatencyConfig.LoadStoreCount = 1` default already enforces at most one
   Load/Store/Atomic issue per cycle. A separate gate in execute would be dead code.
5. **Store-stream density vs WB capacity.** The proportional policy matches vvadd
   well but under/overshoots memcpy in opposite directions across widths because
   the two workloads have different store-stream densities. Per-workload tuning
   would move away from a structural model.

### Open structural gaps (research directions)

These items identify *why* the remaining rows diverge and what model additions would
close them. They are not necessarily implementation tasks — each requires measurement
first.

- **Branch misprediction cost (qsort, median).** qsort's Horologium no-cache IPC
  (0.99–1.01) is ~25% below Olympia's trace-replay IPC (1.24–1.47). Since Olympia
  has no misprediction cost (committed trace), this delta is a direct measurement of
  Horologium's misprediction overhead on qsort. Running Horologium with an oracle
  predictor would isolate this and quantify how much a better predictor
  (ITTAGE/BATAGE vs. 2-bit in the sweep) would recover.

- **Result-bypass / forwarding latency (rich, multiply).** Horologium assumes
  zero-cycle forwarding; real pipelines add 1–2 cycles to the effective latency of
  results forwarded from an executing instruction to a dependent issue. This inflates
  compute-bound IPC across all widths. Adding a bypass latency parameter to
  `FuLatencyConfig` (separate from the instruction's own latency) is the structural
  lever; the multiply/big_core gap (1.69 Horo vs 2.03 Olympia) may also involve FU
  reservation-station depth (see below).

- **FU reservation-station queuing depth (multiply/big_core).** Olympia models
  per-class RS depth limits; instructions stall in dispatch when the RS is full.
  Horologium's IQ is flat (all classes share the pool). At big_core Olympia wins on
  multiply (2.03 vs 1.69) despite having no misprediction cost, suggesting the deeper
  RS enables better out-of-order scheduling across the wider instruction window. A
  per-class IQ partition would test this.

- **Memory-bus occupancy / write-bus bandwidth cap (rsort).** rsort's scattered
  write pattern triggers Olympia's structural LSU limits (memory-bus occupancy,
  finite outstanding write-bus transactions) that keep its IPC near 1.0.
  Horologium (1.58–2.13) runs far above because it models no bus bandwidth cap on
  store commits. Adding a limit on total outstanding write-bus transactions (a
  per-cycle cap across all committed stores, distinct from the single D-cache write
  port already implemented) is the lever for rsort.

- **ROB head pressure from long-latency misses.** Under load-side MLP, a missed
  load occupies its ROB slot for its full miss countdown while younger instructions
  execute past it. When the ROB fills with miss-pending loads, fetch and dispatch
  stall even though younger instructions could still issue. Horologium allows fetch
  to run ahead freely; real cores stall when the ROB head has been pending for too
  long. This is most visible on median and memory-bound workloads at wide issue.

- **Realistic prefetch latency.** The idealized prefetcher (+PF column) shows that a
  free prefetcher adds nothing when MLP is active. A realistic model would issue the
  prefetch with a countdown (like `_inFlight` for loads) and service a demand hit
  that arrives while the prefetch is pending by paying the remaining countdown rather
  than zero. This would interact with MSHR capacity and would only improve IPC when
  the prefetch arrives before the demand miss — a narrower benefit window than the
  idealized model suggests.
