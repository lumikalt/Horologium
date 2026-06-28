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
Horologium's OoO train runs the same program at matching `issue_width`.

## Results (IPC), no-cache Horologium vs Olympia

| workload | Horologium OoO 2/3/8 | Olympia s/m/b      |
|----------|----------------------|--------------------|
| rich     | 1.35 / 1.76 / 2.10   | 0.75 / 1.01 / 0.97 |
| vvadd    | 1.35 / 1.68 / 1.56   | 0.90 / 1.02 / 1.07 |
| multiply | 1.52 / 1.73 / 1.77   | 1.09 / 1.88 / 2.03 |
| median   | 0.84 / 0.90 / 0.83   | 1.01 / 1.09 / 1.11 |
| towers   | 0.88 / 0.91 / 0.77   | 0.61 / 0.64 / 0.65 |
| qsort    | 1.00 / 1.13 / 1.17   | 1.24 / 1.45 / 1.47 |
| rsort    | 1.85 / 2.32 / 2.37   | 0.99 / 1.04 / 1.03 |
| memcpy   | 1.71 / 2.09 / 1.94   | 0.90 / 0.92 / 0.92 |

This is the **unmatched** comparison: Horologium here has 1-cycle loads
(`FuLatencyConfig.LoadStoreLatency = 1`, no cache); Olympia models a real L1. A
cache-matched run is currently **not possible** — see the bug below.

## What this shows

- **Both models live in the same IPC band (~0.6–2.4)** — the opcode trace drives
  Olympia correctly across the whole suite, so the pipeline is sound.
- **Horologium's idealized-memory OoO runs optimistic on most workloads**
  (higher on 6 of 8 at 2-wide) but not uniformly (lower on median, qsort). With
  1-cycle loads it over-extracts ILP where Olympia's load-use latency throttles
  it — but the per-workload spread is wide and there is **no clean cross-model
  rank correspondence** at this config. That is expected: the memory model is
  unmatched, and matching it is blocked.

## Bug surfaced: OoO + L1 cache inflates cycles

Adding an L1 to the OoO train to *match* Olympia produced nonsense: median goes
from 16,138 cycles (no cache) to **1,009,570** with an L1, reporting ~989k
dcache hits and ~995k stall cycles for a ~13.5k-instruction program (≈73 cache
accesses/instruction). The cache column was constant ~1.9–2.0 across all
workloads. This is a Horologium **OoO+cache modelling bug** (a miss appears to
re-poll/replay the load every cycle instead of waiting), independent of Olympia
— the cross-model exercise just surfaced it. Filed in TODO; until it is fixed the
cache-matched comparison cannot be run.

## Caveats

- **Trace replay has no wrong path.** Olympia replays the committed trace, so it
  pays no misprediction penalty — which should *inflate* its IPC versus a real
  run. So where Horologium is higher, the direction is robust; the magnitude is
  not.
- **Benchmarks vary in size** (vvadd ~9k → rsort ~368k instructions). Olympia's
  trace replay and Horologium's OoO both scale fine, but absolute IPC is still
  config-sensitive; read the band and the spread, not individual deltas.

## Next steps

1. **Fix the OoO+L1 cache bug** (above) — prerequisite for any cache-matched
   comparison, and a real correctness issue in its own right.
2. Then re-run cache-matched and look at directional/rank agreement across the
   (now broad) suite, plus a width sweep — descriptively, not as a fit.
