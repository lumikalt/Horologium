# Olympia timing calibration (TODO "Phase 2b")

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
Horologium's OoO train runs the same program at matching `issue_width`, both
without a cache (idealized 1-cycle loads) and with a 16 KB L1 I/D (matching
Olympia's `small_core` L1). The L1 column models **load-side memory-level
parallelism**: a missed load carries its miss penalty in its own latency, so
independent misses overlap instead of freezing the clock (see below).

## Results (IPC)

| workload | Horologium 2/3/8   | + L1$ 2/3/8        | Olympia s/m/b      |
|----------|--------------------|--------------------|--------------------|
| rich     | 1.35 / 1.76 / 2.10 | 1.18 / 1.48 / 1.71 | 0.75 / 1.01 / 0.97 |
| vvadd    | 1.35 / 1.68 / 1.56 | 0.68 / 0.76 / 0.73 | 0.90 / 1.02 / 1.07 |
| multiply | 1.52 / 1.73 / 1.77 | 1.42 / 1.60 / 1.63 | 1.09 / 1.88 / 2.03 |
| median   | 0.84 / 0.90 / 0.83 | 0.54 / 0.57 / 0.54 | 1.01 / 1.09 / 1.11 |
| towers   | 0.88 / 0.91 / 0.77 | 0.78 / 0.81 / 0.69 | 0.61 / 0.64 / 0.65 |
| qsort    | 1.00 / 1.13 / 1.17 | 1.00 / 1.12 / 1.16 | 1.24 / 1.45 / 1.47 |
| rsort    | 1.85 / 2.32 / 2.37 | 1.39 / 1.66 / 1.70 | 0.99 / 1.04 / 1.03 |
| memcpy   | 1.71 / 2.09 / 1.94 | 0.56 / 0.60 / 0.59 | 0.90 / 0.92 / 0.92 |

## What this shows

- **Both models live in the same IPC band (~0.5–2.4)** — the opcode trace drives
  Olympia correctly across the whole suite, so the pipeline is sound.
- **No-cache Horologium (1-cycle loads) is optimistic** — higher than Olympia on
  6 of 8 workloads, as expected for an idealized memory model.
- **With a matched L1, the error goes bidirectional, and the pattern is the real
  finding.** Horologium is *higher* on compute-bound code (multiply 1.42 vs
  1.09, rich 1.18 vs 0.75) but still *lower* on memory-bound code (memcpy 0.56
  vs 0.90, median 0.54 vs 1.01, vvadd 0.68 vs 0.90). That residual signature is
  the **store-commit** miss penalty, still charged lump-sum: load-side
  memory-level parallelism (below) closed part of the gap — memcpy 0.50→0.56,
  vvadd 0.64→0.68, towers 0.63→0.69 at w8 — but memcpy/vvadd are store-heavy, and
  deferred-store commit misses do not yet overlap. There is still **no clean
  cross-model rank correspondence** — two different microarchitectures — so read
  the *pattern*, not the deltas.

## Load-side memory-level parallelism (done)

The lump-sum stall model (charge every cache-miss penalty to the global clock,
serializing all misses) over-penalized memory-bound code. The OoO now gives each
missed **load** its own in-flight latency countdown, so independent load misses
overlap with other work — the L1 column above already reflects this.

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
fail on the broken model. Store-commit MLP remains lump-sum (next steps).

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

## Next steps

1. **Load-side MLP — done** (above). Lifted memory-bound IPCs partway toward
   Olympia (memcpy 0.50→0.56, vvadd 0.64→0.68 at w2).
2. **Store-commit MLP** — deferred-store commit misses are still lump-sum, which
   is the bulk of the residual gap on store-heavy `memcpy`/`vvadd`. Overlapping
   them (a store buffer / write MSHRs) is the next lever; re-run this harness
   afterward to see whether the memory-bound gap closes further — descriptively,
   not as a fit.
