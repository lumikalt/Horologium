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

Key finding: `--bypass-lat 0` (matching gem5's 0-cycle forwarding) is the dominant factor. With matched
bypass, qsort/rsort flip to Horologium leading; median/memcpy close to <1%. Treesum gap is unchanged —
it is entirely branch-prediction-driven (TournamentBP per-PC local history vs LTage global history).
`--flat-iq` has zero effect on any workload: IQ structure is not a factor at issueWidth=2.

Default run (`--mem-lat-ns 30ns`, `--bypass-lat 1` matching Olympia calibration):

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

With `--bypass-lat 0` (matching gem5's 0-cycle result forwarding):

| workload | gem5 IPC | Horo IPC | H/G ratio | Δ from bypass=1 |
|----------|----------|----------|-----------|-----------------|
| median   | 0.682    | 0.675    | 0.989     | +18%            |
| qsort    | 0.818    | 0.862    | **1.054** | +24%            |
| rsort    | 1.292    | 1.389    | **1.075** | +15%            |
| towers   | 0.921    | 0.583    | 0.633     | +9%             |
| vvadd    | 1.108    | 0.885    | 0.799     | +9%             |
| memcpy   | 0.667    | 0.658    | 0.987     | +6%             |
| multiply | 1.786    | 1.568    | 0.878     | +5%             |
| gcd      | 0.323    | 0.295    | 0.914     | +6%             |
| treesum  | 1.705    | 0.711    | 0.417     | +0%             |
| pchase   | 0.442    | 0.654    | **1.478** | +16%            |

With `--mem-lat-ns 10ns` (eliminating DRAM asymmetry, bypass=1):

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

**IQ structure (per-class vs flat) explains zero IPC difference:**

Running Horologium with `--flat-iq` (a single unified 40-entry IQ matching gem5's
scheduling model) produces identical IPC on every workload. The per-class IQ design
is not the source of the gap. With `issueWidth=2` and in-order dispatch, both IQ
modes issue the same two instructions per cycle — the 2-wide issue width saturates
before IQ class boundaries can matter.

**Bypass latency (1 cycle) is the dominant factor for most workloads:**

Running `--bypass-lat 0` to match gem5's 0-cycle result forwarding reveals:

- **median, memcpy**: gap closes to ~1% (0.989, 0.987). Bypass latency was the entire cause.
- **qsort, rsort**: Horologium *leads* gem5 (1.054, 1.075). With matched bypass, LTage's
  global-history prediction is better than TournamentBP on these sort kernels.
- **pchase**: lead grows from 1.278 to 1.478 — deeper DRAM latency tolerance revealed.
- **treesum**: bypass=0 has zero effect (0.416 → 0.417). The gap is purely
  branch-prediction-driven — TournamentBP's per-PC local history tracks the
  alternating null-checks in recursive tree traversal far better than LTage's
  global history.
- **towers**: partial improvement (0.579 → 0.633) — recursive Hanoi, same branch
  pattern as treesum plus stack-depth effects.
- **vvadd, multiply, gcd**: moderate improvement; residual gap from FU count
  differences (gem5 has 2× IntMultDiv vs Horologium's single divider for gcd;
  vvadd residual gap at 0.799 is under investigation).

## Structural differences

1. **DIV latency**: Both simulators use `--div-lat 23` (matched). gem5's
   `DefaultFUPool` has `IntDiv=20` by default; `o3cpu_riscv.py --div-lat N`
   overrides it. The residual gcd gap comes from FU count (gem5 has 2×
   IntMultDiv units vs Horologium's single shared divider).

2. **Branch predictor quality**: TournamentBP > LTage on recursive workloads
   (treesum, towers). The treesum gap (~2.4×) is primarily branch-prediction-driven.

3. **DRAM latency asymmetry**: gem5 SimpleMemory 30 ns vs Horologium HtifMemory
   (~10-cycle miss penalty). Pass `--mem-lat-ns 10ns` to eliminate this
   variable and isolate the pchase/rsort/memcpy ratios from DRAM effects.

4. **Bypass latency**: Horologium uses `bypass_latency=1` (matching Olympia);
   gem5 O3CPU uses 0-cycle forwarding. Pass `--bypass-lat 0` to run Horologium
   with matched forwarding latency. **This is the dominant factor**: with bypass=0,
   median and memcpy close to <1% gap, and qsort/rsort flip to Horologium leading.

5. **IQ structure**: gem5 uses a flat 40-entry IQ; Horologium uses per-class
   queues (5 × 8 = 40 slots). Experiment confirms **zero IPC impact**: switching
   Horologium to a flat unified IQ (`--flat-iq`) produces identical results on
   all 10 workloads. With `issueWidth=2`, the global issue width saturates before
   IQ class boundaries can affect scheduling.

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
bash scripts/gem5-compare.sh --flat-iq                  # Horologium unified IQ: 1×40 flat, matching gem5
```
