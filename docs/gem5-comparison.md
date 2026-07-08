# Horologium vs gem5 O3CPU comparison

A cross-simulator IPC comparison between Horologium's `OooeTrain` and gem5's
`RiscvO3CPU` SE-mode pipeline. Unlike the Olympia calibration (which uses a
trace-replay model), gem5 O3CPU is a full timing simulation — it runs the
actual dynamic instruction stream with branch mispredictions, cache misses, and
register renaming, so the comparison is more direct.

## Setup

### Simulator configurations

| parameter | Horologium | gem5 O3CPU |
|-----------|------------|------------|
| fetch/decode/rename/issue/commit width | 2 | 2 |
| ROB entries | 30 | 30 |
| Issue queue | 5 × 8 (per-class) | 1 × 8 (flat) |
| LQ / SQ entries | 32 / 32 | 32 / 32 |
| L1 I-cache | 16 KB, 4-way, 10-cycle miss | 16 KB, 8-way, tag+data latency 1+1 |
| L1 D-cache | 16 KB, 4-way, 10-cycle miss | 16 KB, 8-way, tag+data latency 4+4 |
| DRAM | HtifMemory (1-cycle) | SimpleMemory 30 ns |
| Branch predictor | 2-bit saturating BHT + BTB | TournamentBP (local 2048 + global 8192) |
| RAS | 16 entries | 16 entries |
| Load-hit latency | 4 cycles | 4 cycles (via D$ tag+data latency) |
| Bypass (result forwarding) latency | 1 cycle | 0 cycles (gem5 default) |
| DIV/REM latency | 23 cycles | 8 cycles (gem5 default IntMultDiv) |
| Physical registers | ROB + 32 extra | 64 int, 64 FP |

### Benchmark binaries

Two separate ELFs are used because gem5 SE mode requires Linux ABI (exit via
`ecall` 93), while Horologium requires HTIF bare-metal (exit via `tohost`
write at 0x80000002a0).

**Horologium** runs the HTIF variants from `TestBinaries/benchmarks/*.elf`.  
**gem5** runs the Linux variants from `gem5-bmarks/*.elf`.

Both compile the same benchmark kernel C sources (`riscv-tests/benchmarks/`),
but with different startup and exit code:

| aspect | HTIF binary | Linux binary |
|--------|-------------|--------------|
| entry point | 0x80000000 | 0x10000 |
| `setStats()` | reads `mcycle`/`minstret` CSRs | no-op |
| exit | write to `tohost` | `ecall a7=93` |
| teardown | `sprintf` + `printstr` (via HTIF) | none (setStats is no-op, so counters are 0) |

The `Δinsts` column shows `(Horo_retired − gem5_committed) / gem5_committed`.
Horologium now reports **kernel-only** stats: `Experiment.RunOne` attaches a
`SetStatsObserver` that snapshots the pipeline DialBoard at the first instruction
of `setStats(1)` and `setStats(0)`, then subtracts to isolate the kernel interval.
This eliminates the `sprintf` teardown overhead that previously inflated the HTIF
binary's instruction count vs. the Linux binary's no-op `setStats`. Small deltas
(±5%) are expected from startup overhead between the two call sites; large deltas
now indicate genuine instruction-count divergence.

## Results

Horologium `+Matched` (w2): ROB=30, IQ=5×8, L1 16KB, LoadHit=4, Bypass=1, DivLat=23, **LTage** (kernel-only IPC).
gem5 O3CPU SE: width=2, ROB=30, IQ=8, L1 16KB, TournamentBP, RAS=16.
Run with: `bash scripts/gem5-compare.sh`

Predictor evaluated: ITTAGE won on qsort (+7pp) and multiply (+7pp) via indirect-target prediction;
LTage (TAGE with 34-bit history + loop) won on treesum/towers/median via global-history direction
accuracy. LTage chosen as better overall; ITTAGE advantage on qsort (0.940 vs 0.971) is residual.

| workload | gem5 IPC | Horo IPC | H/G ratio | Δinsts |
|----------|----------|----------|-----------|--------|
| median   | 0.608    | 0.570    | 0.937     | +27%   |
| qsort    | 0.741    | 0.696    | 0.940     | +2%    |
| rsort    | 1.088    | 1.208    | 1.110     | +1%    |
| towers   | 0.781    | 0.533    | 0.683     | +41%   |
| vvadd    | 0.864    | 0.808    | 0.935     | +45%   |
| memcpy   | 0.559    | 0.620    | 1.110     | +10%   |
| multiply | 1.735    | 1.497    | 0.863     | +6%    |
| gcd      | 0.317    | 0.278    | 0.878     | +12%   |
| treesum  | 1.330    | 0.709    | 0.533     | +6%    |
| pchase   | 0.420    | 0.566    | 1.348     | +1%    |

H/G ratio > 1 means Horologium has higher IPC than gem5.

## What this shows

**gem5 has higher IPC on branch-heavy / call-heavy workloads (ratio < 0.9):**

- `treesum` (0.51): two-call recursive traversal with a 9-deep call stack. The
  tree_sum function calls itself at two distinct sites; gem5's TournamentBP
  (local 2048-entry + global 8192-entry) is substantially better than
  Horologium's 2-bit saturating BHT for this pattern. Both have a 16-entry
  RAS, but the conditional branch prediction matters here.
- `towers` (0.66): recursive Hanoi — high branch misprediction rate. 
  TournamentBP's global history component helps here vs Horologium's local 2-bit.
- `multiply` (0.80): integer multiply chains — likely gem5's default MultDiv
  latency is lower than Horologium's 3-cycle MulLatency for some paths, or
  gem5's O3CPU's FU pipeline has more throughput.
- `gcd` (0.86): DIV-heavy (Euclidean). gem5's default IntMultDiv latency is 8
  cycles vs Horologium's DivLatency=23. This explains why gem5 shows higher IPC
  for gcd: gem5's DIV is 3× faster.

**Horologium has higher IPC on memory-bound workloads (ratio > 1.0):**

- `pchase` (1.35): pointer-chase through a 64 KB array. gem5's SimpleMemory
  adds 30 ns DRAM latency per miss; Horologium's HtifMemory charges 10-cycle
  L1 miss penalty, producing shorter miss penalties for long-latency loads.
- `rsort` (1.11) and `memcpy` (1.11): store-intensive. Horologium's write
  buffer (2 slots at w2) absorbs store-commit stalls; gem5's cache model
  serializes more store traffic through the SimpleMemory 30 ns path.

**Workloads within ±15% (ratio 0.85–1.15): median, qsort, vvadd, gcd**

These are close enough to suggest structural model alignment for integer
computation, branch prediction, and basic cache behavior.

## Structural differences (not yet matched)

1. **DIV latency gap**: gem5 uses IntMultDiv default (~8 cycles) vs Horologium
   DivLatency=23. This makes gem5 faster on gcd. Set gem5's `IntMultDiv` FU
   latency to 23 to match, or use `--div-lat 23` once that flag is added.

2. **Branch predictor quality**: TournamentBP > 2-bit BHT on recursive
   workloads (treesum, towers). Switching Horologium to ITTAGE (already
   implemented) would reduce this gap. In the Olympia calibration, ITTAGE
   closed ~6–10% of the qsort gap; the treesum gap here is much larger (−50%),
   suggesting additional misprediction cost.

3. **DRAM latency asymmetry**: gem5 SimpleMemory 30 ns vs Horologium HtifMemory
   (essentially instant beyond L1 miss penalty of 10 cycles). Setting gem5
   `SimpleMemory.latency = "10ns"` would eliminate this variable.

4. **Bypass latency**: Horologium sets `BypassLatency=1` (matching Olympia);
   gem5 O3CPU has 0-cycle forwarding by default. Adding `--bypass-lat 0` to
   the Horologium config (or configuring gem5's forwarding latency) would
   isolate this.

5. **IQ structure**: Horologium uses 5 × 8 per-class IQs; gem5 uses 1 × 8
   flat IQ here. Increasing gem5's IQ to 40 or using multiple `instQueues`
   would match total capacity.

## Reproduce

```bash
# Build Linux-ABI benchmark ELFs (one-time)
make -C gem5-bmarks benchmarks

# Run the comparison (gem5 + Horologium for all 10 benchmarks)
bash scripts/gem5-compare.sh

# Optional: compare at different widths
bash scripts/gem5-compare.sh --width 4 --rob 64
```
