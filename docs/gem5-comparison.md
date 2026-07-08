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
| Issue queue | 5 × 8 per-class (40 total) | 1 × 40 flat (matched total) |
| LQ / SQ entries | 32 / 32 | 32 / 32 |
| L1 I-cache | 16 KB, 4-way, 10-cycle miss | 16 KB, 8-way, tag+data latency 1+1 |
| L1 D-cache | 16 KB, 4-way, 10-cycle miss | 16 KB, 8-way, tag+data latency 4+4 |
| DRAM | HtifMemory (1-cycle) | SimpleMemory 30 ns |
| Branch predictor | 2-bit saturating BHT + BTB | TournamentBP (local 2048 + global 8192) |
| RAS | 16 entries | 16 entries |
| Load-hit latency | 4 cycles | 4 cycles (via D$ tag+data latency) |
| Bypass (result forwarding) latency | 1 cycle | 0 cycles (gem5 default) |
| DIV/REM latency | 23 cycles | 23 cycles (matched via `--div-lat 23`; DefaultFUPool default is 20) |
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

Horologium (w2): ROB=30, IQ=5×8 per-class, L1 16KB, LoadHit=4, Bypass=1, DivLat=23, **LTage** (kernel-only IPC).
gem5 O3CPU SE: width=2, ROB=30, IQ=40 flat (matched total), L1 16KB, TournamentBP, RAS=16, DivLat=23.
Run with: `bash scripts/gem5-compare.sh`

Predictor evaluated: ITTAGE won on qsort (+7pp) and multiply (+7pp) via indirect-target prediction;
LTage (TAGE with 34-bit history + loop) won on treesum/towers/median via global-history direction
accuracy. LTage chosen as better overall; ITTAGE advantage on qsort is residual.

| workload | gem5 IPC | Horo IPC | H/G ratio | Δinsts |
|----------|----------|----------|-----------|--------|
| median   | 0.682    | 0.570    | 0.836     | +27%   |
| qsort    | 0.818    | 0.696    | 0.851     | +2%    |
| rsort    | 1.292    | 1.208    | 0.935     | +1%    |
| towers   | 0.921    | 0.533    | 0.579     | +41%   |
| vvadd    | 1.108    | 0.808    | 0.729     | +45%   |
| memcpy   | 0.667    | 0.620    | 0.929     | +10%   |
| multiply | 1.786    | 1.497    | 0.838     | +6%    |
| gcd      | 0.323    | 0.278    | 0.862     | +12%   |
| treesum  | 1.705    | 0.709    | 0.416     | +6%    |
| pchase   | 0.442    | 0.566    | 1.278     | +1%    |

H/G ratio > 1 means Horologium has higher IPC than gem5.

## What this shows

**gem5 has higher IPC on all workloads except pchase:**

The IQ structure is the dominant factor: gem5's flat 40-entry IQ schedules any
instruction into any slot, while Horologium's 5 per-class IQs of 8 entries each
constrain scheduling within class boundaries. The per-class design reduces
head-of-line blocking between instruction types but limits cross-class scheduling
flexibility.

- `treesum` (0.42): recursive traversal — both branch mispredictions and IQ
  constraints compound. gem5's TournamentBP (local 2048-entry + global 8192-entry)
  is better than LTage here, and the flat IQ avoids class-local head-of-line blocking.
- `towers` (0.58): recursive Hanoi — same pattern as treesum.
- `vvadd` (0.73): vector-add loop, store-intensive. gem5's flat IQ can overlap loads,
  stores, and ALU instructions freely; Horologium's store-class IQ is a bottleneck.
- `median` (0.84), `qsort` (0.85), `multiply` (0.84): integer-compute workloads.
  gem5's FU pool has 6× IntAlu and 2× IntMultDiv units; the flat IQ keeps them
  saturated more consistently than per-class dispatch.
- `gcd` (0.86): DIV-heavy. Both simulators use 23-cycle IntDiv; gem5 has 2×
  IntMultDiv units vs Horologium's single divider.
- `rsort` (0.94) and `memcpy` (0.93): store-intensive. Previously >1 when gem5
  used IQ=8; with matched IQ=40, gem5 catches up despite the 30 ns DRAM penalty.

**Horologium has higher IPC only on `pchase` (1.28):**

- `pchase` (1.28): pointer-chase through a 64 KB array. gem5's SimpleMemory adds
  30 ns DRAM latency per miss; Horologium's HtifMemory charges only a ~10-cycle
  L1 miss penalty. This DRAM asymmetry (not IQ) explains Horologium's lead here.
  Pass `--mem-lat-ns 10ns` to eliminate it.

## Structural differences

1. **DIV latency**: Both simulators use `--div-lat 23` (matched). gem5's
   `DefaultFUPool` has `IntDiv=20` by default; `o3cpu_riscv.py --div-lat N`
   overrides it. The residual gcd gap comes from FU count (gem5 has 2×
   IntMultDiv units vs Horologium's single shared divider).

2. **Branch predictor quality**: TournamentBP > LTage on recursive workloads
   (treesum, towers). The treesum gap is ~50%, larger than branch prediction
   alone can explain — dispatch stalls (~33% of cycles) are also a factor.

3. **DRAM latency asymmetry**: gem5 SimpleMemory 30 ns vs Horologium HtifMemory
   (~10-cycle miss penalty). Pass `--mem-lat-ns 10ns` to eliminate this
   variable and isolate the pchase/rsort/memcpy ratios from DRAM effects.

4. **Bypass latency**: Horologium uses `bypass_latency=1` (matching Olympia);
   gem5 O3CPU uses 0-cycle forwarding. Pass `--bypass-lat 0` to run Horologium
   with matched forwarding latency.

5. **IQ structure**: The comparison script now passes `gem5-IQ = 5 × per-class IQ`
   (default: 40) to match Horologium's total slot count across 5 classes. gem5
   uses a single flat IQ; Horologium uses per-class queues (5 × 8 = 40 slots).

## Reproduce

```bash
# Build Linux-ABI benchmark ELFs (one-time)
make -C gem5-bmarks benchmarks

# Run the comparison (gem5 + Horologium for all 10 benchmarks)
bash scripts/gem5-compare.sh

# Knobs for structural matching experiments
bash scripts/gem5-compare.sh --width 4 --rob 64        # wider pipeline
bash scripts/gem5-compare.sh --mem-lat-ns 10ns          # match Horologium HtifMemory latency
bash scripts/gem5-compare.sh --bypass-lat 0             # match gem5's 0-cycle forwarding
bash scripts/gem5-compare.sh --div-lat 20               # revert gem5 IntDiv to DefaultFUPool default
```
