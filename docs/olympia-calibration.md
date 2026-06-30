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
+Matched = WB∝w + 4-cycle load-hit latency (`LoadHitLatency=4`) + D$ sized to Olympia's
per-width defaults (16/32/64 KB for small/medium/big). Replaced the earlier +PF column.
Horologium w2/w3/w8 maps to Olympia small/medium/big_core.

| workload | Horo 2/3/8         | + L1$ 2/3/8        | + WB∝w 2/3/8       | + Matched 2/3/8    | Olympia s/m/b      |
|----------|--------------------|--------------------|--------------------|--------------------|--------------------|
| rich     | 1.44 / 1.79 / 1.98 | 1.24 / 1.50 / 1.63 | 1.41 / 1.74 / 1.93 | 1.28 / 1.70 / 1.88 | 0.75 / 1.01 / 0.97 |
| vvadd    | 1.28 / 1.42 / 1.27 | 0.66 / 0.71 / 0.66 | 0.93 / 1.09 / 1.12 | 0.89 / 1.07 / 1.11 | 0.90 / 1.02 / 1.07 |
| multiply | 1.51 / 1.69 / 1.73 | 1.41 / 1.57 / 1.60 | 1.48 / 1.66 / 1.69 | 1.46 / 1.64 / 1.69 | 1.09 / 1.88 / 2.03 |
| median   | 0.83 / 0.83 / 0.74 | 0.53 / 0.54 / 0.50 | 0.77 / 0.78 / 0.70 | 0.68 / 0.71 / 0.66 | 1.01 / 1.09 / 1.11 |
| towers   | 0.73 / 0.59 / 0.49 | 0.67 / 0.54 / 0.46 | 0.67 / 0.55 / 0.46 | 0.64 / 0.54 / 0.45 | 0.61 / 0.64 / 0.65 |
| qsort    | 0.99 / 1.10 / 1.01 | 0.98 / 1.10 / 1.01 | 0.99 / 1.10 / 1.01 | 0.88 / 0.95 / 0.87 | 1.24 / 1.45 / 1.47 |
| rsort    | 1.76 / 2.08 / 2.14 | 1.35 / 1.55 / 1.58 | 1.58 / 1.97 / 2.13 | 1.45 / 1.78 / 1.89 | 0.99 / 1.04 / 1.03 |
| memcpy   | 1.50 / 1.63 / 1.55 | 0.53 / 0.56 / 0.55 | 0.68 / 0.81 / 1.19 | 0.67 / 0.79 / 1.19 | 0.90 / 0.92 / 0.92 |

## What this shows

- **vvadd matches Olympia at w2.** +Matched gives 0.89 vs Olympia 0.90 at small\_core
  width — the first exact cross-model alignment in this study. At medium/big the
  overestimate shrinks to 5% (1.07 vs 1.02) and 4% (1.11 vs 1.07). The 4-cycle load-hit
  latency is the primary driver: vvadd's load→add→store dependency chain is fully
  serialized through the load pipeline, so each 3-extra-cycle hit directly lengthens
  the critical path.

- **memcpy is insensitive to LoadHitLatency.** +Matched (0.67/0.79/1.19) is nearly
  identical to WB∝w (0.68/0.81/1.19) despite adding 3 extra load cycles. The write
  buffer is already the binding constraint; the load hit latency is off the critical
  path when store-buffer saturation stalls dominate. Horologium still undershoots at
  w2 (0.67 vs 0.90) and overshoots at w8 (1.19 vs 0.92) — the store-stream density
  crossing pattern from the WB analysis persists.

- **Compute-bound workloads (multiply, rich) are slightly deflated by +Matched** but
  still well above Olympia. multiply drops 0.01–0.02 IPC (cache-insensitive, load
  latency not on critical path); rich drops 0.05–0.13 IPC. The remaining gap for rich
  (1.28 vs 0.75 at w2) and multiply (1.46 vs 1.09 at w2) is structural: IQ
  partitioning, forwarding latency, and FU RS depth.

- **qsort and median move further from Olympia in +Matched.** qsort: WB∝w 0.99 →
  +Matched 0.88 at w2, while Olympia is 1.24. median: WB∝w 0.77 → +Matched 0.68 at w2,
  Olympia 1.01. Both workloads are already below Olympia because Horologium pays real
  misprediction penalties (Olympia replays the committed trace, paying none). Adding load
  latency further serializes dependent loads after predicted-taken branches, widening the
  gap. The correct lever for these workloads is branch-prediction quality, not load
  latency.

- **rsort remains an outlier** — +Matched (1.45/1.78/1.89) is lower than WB∝w
  (1.58/1.97/2.13) but still far above Olympia (0.99/1.04/1.03). Memory-bus bandwidth
  cap is the structural lever.

- **D-cache size change has minimal effect.** The 16→32/64 KB enlargement for w3/w8 in
  +Matched produces no measurable IPC change on any workload. These benchmarks' working
  sets fit comfortably in 16 KB, so the larger cache does not reduce miss rates.

## Phase 4: Load hit latency and matched D-cache (done)

Olympia's LSU is a 5-stage dedicated pipeline (addr\_calc → MMU → cache\_lookup →
cache\_read → complete), giving **4 cycles** from IQ issue to scoreboard broadcast on a
cache hit. Horologium previously treated a cache-hit load as a 1-cycle FU operation
(`LoadStoreLatency = 1` in `FuLatencyConfig`), with only the miss penalty added on top.

Two changes were made:

1. **`LoadHitLatency` parameter** added to `FuLatencyConfig` (default 1; set to 4 in the
   +Matched column). `LatencyFor(ToothClass.Load)` now returns `LoadHitLatency`; stores
   and atomics still use `LoadStoreLatency`. The MLP miss-countdown model is unchanged:
   a missed load starts with `LoadHitLatency - 1` cycles, then adds the miss penalty on
   top, so hits and misses both pay the base pipeline depth. A cache-hit load at
   `LoadHitLatency=4` has `countdown=3` and enters `_inFlight` with `holdsMshr=false`
   (no MSHR slot consumed for hits).

2. **D-cache sizing** in the calibration script updated: the `+Matched` config uses
   16/32/64 KB D$ for w2/w3/w8 matching Olympia's small/medium/big\_core defaults.

**Result:** vvadd at small\_core width converges to within 1% of Olympia (0.89 vs 0.90).
Medium and big remain within 5%. The D-cache enlargement had no measurable effect because
these benchmark working sets fit in 16 KB.

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

## Olympia execution model: structural comparison

Source: `/tmp/olympia` (cloned from
`https://github.com/riscv-software-src/riscv-perf-model`). Files read:
`core/lsu/LSU.cpp`, `core/lsu/LSU.hpp`, `core/lsu/DCache.hpp`,
`core/execute/ExecutePipe.cpp`, `core/execute/IssueQueue.cpp`,
`core/dispatch/Dispatch.cpp`, `core/dispatch/Dispatch.hpp`,
`core/ROB.hpp`, `core/fetch/Fetch.hpp`, `core/fetch/SimpleBranchPred.cpp`,
`arches/small_core.yaml`, `arches/isa_json/olympia_uarch_rv64g.json`.

### Instruction latencies

From `olympia_uarch_rv64g.json` (vs Horologium `FuLatencyConfig.Default`):

| class        | Olympia cycles | Horologium cycles | note |
|--------------|---------------|-------------------|------|
| INT ALU      | 1              | 1                 | match |
| Branch       | 1              | 1                 | match |
| MUL          | 3              | 3                 | match |
| DIV/REM      | 23             | 3 (MulDiv)        | **Horo 7.7× faster** |
| FLOAT (move/cmp) | 2          | 4                 | Horo 2× slower |
| FADDSUB      | 4              | 4 (Float)         | match |
| FMUL         | 4              | 4 (Float)         | match |
| FDIV.S       | 30             | 16 (FloatDivSqrt) | Horo 1.9× faster |
| FDIV.D       | 63             | 16 (FloatDivSqrt) | Horo 3.9× faster |
| Load (cache hit) | **4** (LSU pipeline) | **1** (FU latency) | **Horo 4× faster** |

The load latency gap is the biggest mismatch for memory-bound workloads. Olympia's LSU
is a dedicated 5-stage pipelined unit (addr\_calc → MMU\_lookup → cache\_lookup →
cache\_read → complete, each 1 cycle), giving 4 cycles from IQ issue to scoreboard
broadcast. The `"latency": 1` field in the uarch JSON for `lw`/`ld` belongs to the
`ExecutePipe` dispatch path and is not consumed by the LSU unit. Horologium treats a
cache-hit load as a 1-cycle FU operation (consistent with the comment in
`FuLatencyConfig.cs` that miss penalty is charged separately). This inflates
Horologium's IPC for load-heavy workloads (vvadd, memcpy) relative to Olympia.

### Issue queue structure

Olympia uses **per-class bounded issue queues** dispatched directly to functional units,
not a flat shared pool:

| arch   | IQs | classes (example small\_core) |
|--------|-----|-------------------------------|
| small  | 4   | INT+SYS+MUL+DIV+VSET / FPU / BR / Vector |
| medium | 5   | INT×2 / FPU / BR / Vector / LSU-dedicated |
| big    | 6   | INT×3 / FPU×2 / BR / Vector |

LSU is a **separate unit** dispatched via its own credit port (not part of any IQ),
with a bounded `ldst_inst_queue_size = 8` (default). Each IQ also has a bounded
`scheduler_size`; dispatch stalls when any target queue is full, even if other queues
have capacity. Horologium uses a single flat IQ pool per width.

### Cache-miss model

| model       | on miss |
|-------------|---------|
| Olympia     | Instruction **invalidated** from LSU pipeline; put back in ready queue after `replay_issue_delay = 3` cycles; re-issues when replay delay expires + LSU not busy |
| Horologium  | Load gets an **in-flight countdown** (non-blocking IQ slot freed immediately); other instructions continue to issue past it; result broadcast when countdown reaches 0 |

The replay model serializes the missing load through the LSU pipeline twice: first
attempt detects the miss; second attempt (≥3 cycles later) completes. The IQ slot is
freed immediately after first issue, so the replay slot is re-used. Horologium's MLP
model also frees the IQ slot immediately but keeps the result in-flight — meaning
dependent instructions may issue before the value is ready and must detect the hazard.
Both models expose the miss to overlap, but Olympia's replay adds 3 + 4 = 7+ extra
cycles to the critical path per miss vs Horologium's pure countdown.

### Load speculation

```
Olympia default: allow_speculative_load_exec = false
```

With speculation disabled, loads wait until all older stores have resolved their
addresses before issuing. This prevents store-to-load forwarding errors at the cost of
IPC. Horologium speculates by default: a load can issue as long as
`HasPrecedingPendingStore` returns false for its address range (stores with unknown
addresses are assumed non-conflicting). The speculation adds IPC but requires the store
violation check.

### D-cache sizes (calibration mismatch)

| arch   | Olympia D-cache | Horologium (calibration) |
|--------|-----------------|--------------------------|
| small  | 16 KB           | 16 KB ← match            |
| medium | 32 KB           | 16 KB ← 2× smaller       |
| big    | 64 KB           | 16 KB ← 4× smaller       |

The Horologium calibration sweep uses 16 KB L1 at all three widths to isolate the IPC
effect of width, not cache size. Olympia's medium and big cores have larger caches,
which reduces miss rate and inflates their relative IPC for miss-prone workloads. This
partially explains why Olympia's medium/big IPCs are not always lower than its small
IPC despite the same pipeline depth — a wider core with a proportionally larger cache
sees fewer misses per cycle.

### Branch predictor

Both Olympia and Horologium use a **2-bit saturating counter BHT + BTB**, indexed by
fetch PC (local history, no GHR). Initial bias is weakly not-taken. This is a close
structural match; branch prediction quality should not be a primary driver of IPC
divergence. However, Olympia's `SimpleBranchPred` is disabled in trace-replay mode
(no wrong-path execution), so the *misprediction penalty itself* is zero in Olympia
runs — Horologium's predictor pays real penalties even when replaying the same trace.

### ROB and commit

| parameter | Olympia default | Horologium default |
|-----------|----------------|-------------------|
| ROB depth | 30             | need to check `OooeConfig.RobSize` |
| retire/cycle | 4           | `IssueWidth` (no separate retire cap) |

Olympia's `num_to_retire = 4` caps how many instructions commit per cycle independent
of dispatch width; Horologium retires up to `IssueWidth` per cycle.

### Summary of structural gaps (priority order)

1. **Load hit latency** — Olympia 4 cycles, Horologium 1 cycle. Single largest driver
   of Horologium's inflated IPC on load-heavy workloads (vvadd, memcpy, rsort reads).
   Adding a `LoadHitLatency` parameter to `FuLatencyConfig` and setting it to 4 would
   directly reduce these gaps.

2. **D-cache size mismatch** — medium/big Olympia runs use 2–4× larger caches.
   Running Horologium at 32/64 KB for medium/big comparisons would equalize miss rates.

3. **Integer div latency** — Olympia 23 cycles, Horologium 3. No current calibration
   workload is div-heavy, but the gap will matter for any division-intensive program.

4. **IQ partitioning** — per-class bounded queues in Olympia vs flat pool. Becomes
   relevant when one class backs up: in Horologium, a stalled MUL doesn't block INT
   issues; in Olympia it may.

5. **Replay vs countdown** — Olympia replays miss → 7+ extra cycles; Horologium
   countdown → miss penalty only. Replay adds more structural pressure on the LSU
   queue, which is separately bounded.

6. **Load speculation** — Olympia conservative (no speculative loads by default);
   Horologium speculates. Reduces Olympia IPC on store-then-load patterns.

## Next steps and research points

1. **Load-side MLP — done.** Lifted memory-bound IPCs partway toward Olympia.
2. **Store-side MLP — done.** Write buffer wired with width-proportional sizing
   (WB∝w); vvadd matches Olympia within ~5% at all widths.
3. **Prefetcher (next-line + stride RPT) — done.** Wired; idealized +PF column
   showed ≤2% improvement because load-side MLP already hides miss latency. Column
   replaced by +Matched in the current script; the infrastructure remains in place.
4. **Load hit latency — done.** `LoadHitLatency=4` in +Matched converges vvadd
   to within 1% of Olympia at w2. See §Phase 4 above.
5. **D-cache size matching — done.** +Matched uses 16/32/64 KB per Olympia's
   per-width defaults; no measurable effect because working sets fit in 16 KB.
6. **D-cache read port constraint — covered by FU budget.** The
   `FuLatencyConfig.LoadStoreCount = 1` default already enforces at most one
   Load/Store/Atomic issue per cycle. A separate gate in execute would be dead code.
7. **Store-stream density vs WB capacity.** The proportional policy matches vvadd
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
