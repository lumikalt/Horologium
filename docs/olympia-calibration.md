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
Olympia's `small_core` L1).

## Results (IPC)

| workload | Horologium 2/3/8   | + L1$ 2/3/8        | Olympia s/m/b      |
|----------|--------------------|--------------------|--------------------|
| rich     | 1.35 / 1.76 / 2.10 | 1.17 / 1.46 / 1.69 | 0.75 / 1.01 / 0.97 |
| vvadd    | 1.35 / 1.68 / 1.56 | 0.64 / 0.70 / 0.68 | 0.90 / 1.02 / 1.07 |
| multiply | 1.52 / 1.73 / 1.77 | 1.41 / 1.58 / 1.61 | 1.09 / 1.88 / 2.03 |
| median   | 0.84 / 0.90 / 0.83 | 0.53 / 0.55 / 0.52 | 1.01 / 1.09 / 1.11 |
| towers   | 0.88 / 0.91 / 0.77 | 0.75 / 0.77 / 0.63 | 0.61 / 0.64 / 0.65 |
| qsort    | 1.00 / 1.13 / 1.17 | 0.99 / 1.11 / 1.15 | 1.24 / 1.45 / 1.47 |
| rsort    | 1.85 / 2.32 / 2.37 | 1.37 / 1.61 / 1.64 | 0.99 / 1.04 / 1.03 |
| memcpy   | 1.71 / 2.09 / 1.94 | 0.50 / 0.53 / 0.52 | 0.90 / 0.92 / 0.92 |

## What this shows

- **Both models live in the same IPC band (~0.5–2.4)** — the opcode trace drives
  Olympia correctly across the whole suite, so the pipeline is sound.
- **No-cache Horologium (1-cycle loads) is optimistic** — higher than Olympia on
  6 of 8 workloads, as expected for an idealized memory model.
- **With a matched L1, the error goes bidirectional, and the pattern is the real
  finding.** Horologium is now *higher* on compute-bound code (multiply 1.41 vs
  1.09, rich 1.17 vs 0.75) but markedly *lower* on memory-bound code (memcpy 0.50
  vs 0.90, median 0.53 vs 1.01, vvadd 0.64 vs 0.90). That signature points at
  Horologium's OoO charging cache-miss stalls **lump-sum, with no memory-level
  parallelism** (`OooeTrain.RunCycle`: "Lump-sum: does not model memory-level
  parallelism"): independent misses are summed instead of overlapped, so
  memory-bound workloads are over-penalized. The existing *non-blocking cache /
  MSHR* TODO is exactly the fix; this study gives it a concrete, measured
  motivation. There is still **no clean cross-model rank correspondence** — two
  different microarchitectures — so read the *pattern*, not the deltas.

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

1. **Non-blocking cache / MSHR in the OoO** — overlap independent misses (model
   memory-level parallelism) instead of the lump-sum stall. This study predicts
   it would lift the memory-bound IPCs (memcpy/median/vvadd) toward Olympia.
2. Re-run this harness after that change to see whether the memory-bound gap
   closes — descriptively, not as a fit.
