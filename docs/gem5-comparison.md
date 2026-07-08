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

Default run (`--mem-lat-ns 30ns`, gem5 SimpleMemory default):

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

With `--mem-lat-ns 10ns` (eliminating DRAM asymmetry):

| workload | gem5 IPC | Horo IPC | H/G ratio |
|----------|----------|----------|-----------|
| median   | 0.736    | 0.570    | 0.775     |
| qsort    | 0.835    | 0.696    | 0.833     |
| rsort    | 1.347    | 1.208    | 0.896     |
| towers   | 1.010    | 0.533    | 0.528     |
| vvadd    | 1.399    | 0.808    | 0.578     |
| memcpy   | 0.988    | 0.620    | 0.627     |
| multiply | 1.815    | 1.497    | 0.825     |
| gcd      | 0.323    | 0.278    | 0.860     |
| treesum  | 1.716    | 0.709    | 0.413     |
| pchase   | 0.655    | 0.566    | 0.864     |

H/G ratio > 1 means Horologium has higher IPC than gem5.

## What this shows

**gem5 leads on every workload with matched DRAM:**

The pchase Horologium advantage (1.278 at 30 ns) collapses to 0.864 at 10 ns DRAM,
confirming it was entirely DRAM-latency-driven. With equalized memory, gem5 leads
everywhere.

**Per-class IQ structure is the dominant remaining gap:**

gem5's flat 40-entry IQ schedules any instruction into any slot; Horologium's 5
per-class IQs of 8 entries each constrain scheduling within class boundaries.
The per-class design eliminates head-of-line blocking between instruction *types*
but limits cross-class scheduling freedom.

- `treesum` (0.42/0.41): recursive traversal — branch mispredictions and IQ
  constraints compound. gem5's TournamentBP (local 2048-entry + global 8192-entry)
  is better than LTage for this pattern, and the flat IQ fills execution slots
  more aggressively between mispredictions.
- `towers` (0.58/0.53): recursive Hanoi — same pattern as treesum.
- `vvadd` (0.73/0.58): store-intensive loop. gem5's flat IQ overlaps loads,
  stores, and ALU freely; Horologium's store-class IQ saturates first and
  stalls issue of other classes waiting on addresses.
- `memcpy` (0.93/0.63): store-intensive; the DRAM difference was masking a
  significant scheduling gap — at 10 ns this gap is fully exposed.
- `median` (0.84/0.78), `qsort` (0.85/0.83), `multiply` (0.84/0.83): integer
  compute; gem5's 6× IntAlu + 2× IntMultDiv FU pool with flat IQ keeps FUs
  saturated more consistently than per-class dispatch.
- `gcd` (0.86): DIV-heavy; both simulators use 23-cycle IntDiv but gem5 has
  2× IntMultDiv units vs Horologium's single divider.

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
