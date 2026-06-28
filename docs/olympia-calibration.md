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
single-cycle); Olympia replays it through its timing model at three arch widths
(`small`/`medium`/`big_core` = 2/3/8-wide). Horologium's own OoO train runs the
same program at matching `issue_width` (2/3/8), with and without an L1 cache.
IPC is read from each side's stats.

## Results (IPC)

| workload          | Horologium OoO 2/3/8 | + L1 I/D cache 2/3/8 | Olympia s/m/b |
|-------------------|----------------------|----------------------|---------------|
| test.elf (882 i)  | 1.03 / 0.95 / 1.03   | 0.71 / 0.74 / 0.81   | 0.98 / 0.99 / 0.98 |
| rich.elf (4813 i) | 1.35 / 1.76 / 2.10   | 1.17 / 1.46 / 1.69   | 0.76 / 0.98 / 0.95 |

## What this shows

- **The cross-model IPC gap is dominated by the memory configuration, not a
  fixed fidelity difference.** Horologium's default OoO has no cache and a
  1-cycle load (`FuLatencyConfig.LoadStoreLatency = 1`); Olympia's arches model a
  real hierarchy. Adding an L1 moves Horologium's IPC substantially — *past*
  Olympia on the tiny test.elf (cold misses dominate an 882-instruction run) and
  *toward but above* it on rich.elf. So an unmatched run measures the missing
  cache, not "Horologium is optimistic."
- **Internal consistency holds (the safe signal).** The cache knob moves IPC the
  expected direction (down, introducing miss latency); width increases IPC on
  both models. The model responds correctly to configuration.
- **Residual gap on rich.elf** (cache: 1.17 vs Olympia 0.76 at 2-wide) is partly
  Horologium modelling an L1 *hit* as 1-cycle load-use (vs Olympia's multi-cycle)
  and rich.elf's tiny working set (a 48-int array → mostly hits, so the cache
  barely bites). Load-use latency is the natural next modelling refinement.

## Caveats

- **Trace replay has no wrong path.** Olympia replays the committed trace, so it
  pays no misprediction penalty — which should *inflate* its IPC relative to a
  real run. Direction (Horologium higher) is therefore robust; magnitude is not.
  (Negligible here anyway: rich.elf had ~66 mispredicts.)
- **Two tiny workloads.** 882 and 4813 instructions, with visible noise (test
  dips at 3-wide; Olympia dips at big). No trend *law* should be read from this —
  e.g. "Olympia saturates, Horologium doesn't" is not supported at this scale.

## The real next step: breadth (gated on FP)

A credible study needs more, larger, ILP-diverse workloads. The blocker is the
JSON trace writer: it is integer-focused and emits FP register operands in the
integer `rd`/`rs1`/`rs2` fields, which Mavis rejects (`InvalidRegisterNumber`).
**Every riscv-tests benchmark uses FP** (compute and/or the stats harness), so
only `test.elf`/`rich.elf` ingest cleanly today. Emitting `fs1`/`fs2`/`fd` with
0–31 f-register numbering (and per-operand type) unlocks the benchmark suite and
is the prerequisite for a real calibration study.
