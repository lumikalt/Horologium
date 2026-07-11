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
| `setStats(1)` | reads `mcycle`/`minstret` CSRs | `m5_reset_stats(0,0)` — gem5 pseudo-op (opcode `0x7b`, funct7=`0x40`), zeroes all counters |
| `setStats(0)` | reads `mcycle`/`minstret` CSRs | `m5_exit(0)` — gem5 pseudo-op (funct7=`0x21`), terminates simulation; stats file covers ROI only |
| exit | write to `tohost` | unreachable after `m5_exit` |
| teardown | `sprintf` + `printstr` (via HTIF) | none |

### Measurement window

Both simulators measure **kernel-only** IPC via the same `setStats` ROI boundary.

Horologium: `Experiment.RunOne` attaches a `SetStatsObserver` that snapshots the
pipeline `DialBoard` at the first instruction of `setStats(1)` and `setStats(0)`,
then subtracts to isolate the kernel interval.

gem5: `setStats(1)` in `syscalls_linux.c` emits `m5_reset_stats(0,0)` (gem5
RISC-V pseudo-op, opcode `0x7b`, funct7=`0x40`, a0=a1=0), zeroing all statistics
counters. `setStats(0)` emits `m5_exit(0)` (funct7=`0x21`), terminating the
simulation immediately; the stats file therefore contains only the kernel-interval
counts. gem5 reads its arguments from the ABI registers a0/a1 (not from the
instruction's register-field encoding, which is always x0).

The `PREALLOCATE` warmup region runs before `setStats(1)` in both cases and warms
the caches; neither simulator's counters cover it.

`Δinsts` ≈ 0% for all workloads confirms the windows are aligned.

## Results

Horologium (w2): ROB=30, IQ=5×8 per-class, L1 16KB, LoadHit=4, Bypass=1, DivLat=23, **LTage** (kernel-only IPC).
gem5 O3CPU SE: width=2, ROB=30, IQ=40 flat (matched total), L1 16KB, TournamentBP, RAS=16, DivLat=23.
Run with: `bash scripts/gem5-compare.sh`

Default run (`--mem-lat-ns 30ns`, `--bypass-lat 1` matching Olympia calibration).
Both gem5 and Horologium measure kernel-only IPC; Δinsts ≈ 0% confirms aligned windows.

| workload | gem5 IPC | Horo IPC | H/G ratio | Δinsts |
|----------|----------|----------|-----------|--------|
| median   | 0.7872   | 0.5270   | 0.669     | +0.2%  |
| qsort    | 0.6184   | 0.5762   | 0.932     | +0.0%  |
| rsort    | 1.3237   | 1.2637   | 0.955     | +0.0%  |
| towers   | 1.1726   | 0.7510   | 0.640     | +0.6%  |
| vvadd    | 1.4537   | 1.0721   | 0.737     | +0.3%  |
| memcpy   | 0.7268   | 0.3206   | 0.441     | +0.1%  |
| multiply | 1.8560   | 1.6889   | 0.910     | +0.0%  |
| gcd      | 0.2790   | 0.2294   | 0.822     | +0.1%  |
| treesum  | 1.6610   | 0.8647   | 0.521     | +0.1%  |
| pchase   | 0.1313   | 0.2554   | **1.945** | +0.0%  |

With `--bypass-lat 0` (matching gem5's 0-cycle result forwarding):

| workload | gem5 IPC | Horo IPC | H/G ratio | Δ from bypass=1 |
|----------|----------|----------|-----------|-----------------|
| median   | 0.7872   | 0.6263   | 0.796     | +19%            |
| qsort    | 0.6184   | 0.6871   | **1.111** | +19%            |
| rsort    | 1.3237   | 1.5279   | **1.154** | +21%            |
| towers   | 1.1726   | 0.8369   | 0.714     | +11%            |
| vvadd    | 1.4537   | 1.0880   | 0.748     | +1%             |
| memcpy   | 0.7268   | 0.3276   | 0.451     | +2%             |
| multiply | 1.8560   | 1.7432   | 0.939     | +3%             |
| gcd      | 0.2790   | 0.2396   | 0.859     | +0.1%           |
| treesum  | 1.6610   | 0.7962   | 0.479     | -8%             |
| pchase   | 0.1313   | 0.3016   | **2.297** | +18%            |

With `--mem-lat-ns 10ns` (eliminating DRAM asymmetry, bypass=1):

| workload | gem5 IPC | Horo IPC | H/G ratio |
|----------|----------|----------|-----------|
| median   | 0.7901   | 0.5270   | 0.667     |
| qsort    | 0.6185   | 0.5762   | 0.932     |
| rsort    | 1.3418   | 1.2637   | 0.942     |
| towers   | 1.1807   | 0.7510   | 0.636     |
| vvadd    | 1.4705   | 1.0721   | 0.729     |
| memcpy   | 1.0864   | 0.3206   | 0.295     |
| multiply | 1.8584   | 1.6889   | 0.909     |
| gcd      | 0.2792   | 0.2294   | 0.822     |
| treesum  | 1.6658   | 0.8647   | 0.519     |
| pchase   | 0.2181   | 0.2554   | **1.171** |

With `--structurally-matched` (`--bypass-lat 0 --mem-lat-ns 10ns` combined) — closest
structural match to gem5 O3CPU:

| workload | gem5 IPC | Horo IPC | H/G ratio | Δinsts |
|----------|----------|----------|-----------|--------|
| median   | 0.7901   | 0.6263   | 0.793     | +0.2%  |
| qsort    | 0.6185   | 0.6871   | **1.111** | +0.0%  |
| rsort    | 1.3418   | 1.5279   | **1.139** | +0.0%  |
| towers   | 1.1807   | 0.8369   | 0.709     | +0.6%  |
| vvadd    | 1.4705   | 1.0880   | 0.740     | +0.3%  |
| memcpy   | 1.0864   | 0.3276   | 0.302     | +0.1%  |
| multiply | 1.8584   | 1.7432   | 0.938     | +0.0%  |
| gcd      | 0.2792   | 0.2396   | 0.858     | +0.1%  |
| treesum  | 1.6658   | 0.7962   | 0.478     | +0.1%  |
| pchase   | 0.2181   | 0.3016   | **1.383** | +0.0%  |

H/G ratio > 1 means Horologium has higher IPC than gem5.

Note: `--mem-lat-ns` does not change Horologium IPC (HtifMemory is always ~10-cycle
regardless of the flag); only gem5's IPC changes. The `--structurally-matched` preset
eliminates both the bypass-latency and DRAM-latency asymmetries. Remaining gaps (towers,
vvadd, memcpy, treesum) reflect structural simulation differences: FU count, mispredict
penalty, and cache-thrashing behaviour under different associativity.

## What this shows

**Bypass latency (1 cycle) is the dominant factor for many workloads:**

Running `--bypass-lat 0` to match gem5's 0-cycle result forwarding:

- **median, qsort, rsort**: bypass=0 gains +19–21%. qsort and rsort flip to Horologium leading
  (H/G 1.111 and 1.154 respectively). LTage's global-history prediction outperforms
  TournamentBP on sort kernels once bypass latency is matched.
- **vvadd, multiply, gcd**: small gains (1–4%). Residual gaps are from FU count differences
  (gem5 has 2× IntMultDiv vs Horologium's single shared divider for gcd) and potential
  write-allocate cache miss patterns.
- **treesum**: bypass=0 *reduces* Horologium IPC by 8% (0.865 → 0.796, H/G 0.521 → 0.479)
  — not zero effect. Branch mispredicts are unchanged (499 → 500), but D-cache misses
  increase by 138 and dispatch stall cycles by 1 479, consistent with wrong-path execution
  reaching deeper into the cache under faster forwarding. With ROI instrumentation both
  simulators now commit ~11 530 kernel-only instructions (Δinsts +0.1%). The remaining
  H/G gap is genuine simulation divergence: see "treesum: predictor sweep" below.
- **towers**: bypass=0 gains +11% (0.751 → 0.837). Recursive Hanoi is more compute-bound
  than treesum, making bypass latency a larger factor. A predictor sweep finds the same
  result as treesum: TournamentBP reduces Horologium IPC (0.708 vs LTage 0.751) due to
  extra memory-order violations.
- **pchase**: bypass=0 gains +18% (0.255 → 0.302, H/G 1.945 → 2.297). The kernel is
  cold-cache (the `PREALLOCATE` phase warms a *different* permutation than the one the kernel
  chases), so the workload is heavily DRAM-miss-bound regardless of bypass latency. With ROI
  instrumentation, gem5 kernel-only IPC (0.131) is now far lower than Horologium's (0.255)
  because Horologium's HtifMemory has ~10-cycle miss latency while gem5's SimpleMemory is
  30 ns ≈ 30 cycles; pchase is entirely latency-bound. The `--mem-lat-ns 10ns` variant
  reduces the gap (gem5 0.218, H/G 1.171).
- **memcpy**: bypass=0 has near-zero effect (+2%, 0.481 → 0.491). The gap is not
  bypass-driven; see "memcpy: PREALLOCATE elimination + total cache thrashing" below.

**IQ structure (per-class vs flat) explains zero IPC difference:**

Confirmed by `--flat-iq` experiment (not shown above): switching Horologium to a flat
unified 40-entry IQ produces identical IPC on every workload. With `issueWidth=2` and
in-order dispatch, the global issue width saturates before IQ class boundaries can matter.

**pchase: kernel window is cold-cache:**

The pchase `PREALLOCATE` warms a first permutation, then `init_permutation()` re-shuffles
before the kernel. The kernel chases a second permutation the cache never saw. Both
simulators now measure this cold-cache kernel only. gem5's low kernel IPC (0.131 vs
Horologium 0.255) is primarily DRAM latency: gem5 SimpleMemory 30 ns vs Horologium
HtifMemory ~10 cycles. The `--mem-lat-ns 10ns` variant closes most of the gap
(gem5 0.218, H/G 1.171).

**memcpy: PREALLOCATE elimination + total cache thrashing:**

Two compounding factors explain the low kernel IPC (0.321) and the near-zero bypass gain.

*1. GCC -O2 eliminates the PREALLOCATE warmup.* `memcpy_main.c` has:
```c
#if PREALLOCATE
  memcpy(results_data, input_data, sizeof(int) * DATA_SIZE);  // eliminated
#endif
setStats(1);
memcpy(results_data, input_data, sizeof(int) * DATA_SIZE);  // kernel
```
The PREALLOCATE copy and the kernel copy have identical source and destination; the output
of the preallocate is unconditionally overwritten before any read. GCC -O2 recognises this
as dead code and removes it entirely. Disassembly of the HTIF binary confirms: `main` calls
`setStats(1)` at offset +0x38 with no load/store loop preceding it, so the kernel starts
with a completely cold D-cache.

*2. Total set-associative thrashing.* DATA_SIZE=4000 ints = 16 000 bytes = 250 cache lines
(64 B blocks). The 16 KB 4-way D-cache has 64 sets. `input_data` sits in the `.data`
section (0x80001CC8); `results_data` lives on the stack (0x8001D170 at run time). Both
arrays span all 64 sets at roughly 4 lines/set each: combined, every set holds 7–8
competing lines against only 4 ways. The result is total thrashing from the first access:
Horologium counters show 4238 D-misses / 8021 accesses = **52.8% miss rate**,
27 430 / 34 399 cycles = **79.7% stall fraction** in the kernel window.

gem5's L1 D-cache is 8-way (32 sets), which changes the set mapping but not the outcome:
with 250 lines/array the per-set occupancy rises to ~16 lines vs 8 ways, so gem5 also
fully thrashes. gem5 kernel-only stats show a similar miss rate. With ROI instrumentation
both simulators measure the cold, fully-thrashing streaming copy; the IPC gap (0.321 vs
0.727) reflects gem5's DRAM latency (30 ns) and wider cache (8-way thrash pattern differs),
not startup or teardown overhead.

**treesum: predictor sweep:**

With ROI instrumentation both simulators commit ~11 530 kernel-only instructions (Δinsts
+0.1%). The H/G gap (gem5 1.661 vs Horologium 0.865, ratio 0.521) is genuine simulation
divergence. gem5 records ~499 conditional mispredictions over ~6 924 kernel cycles with
TournamentBP; the gap reflects structural differences between gem5's O3CPU (0-cycle
forwarding by default, full out-of-order windows, TournamentBP local history) and
Horologium (1-cycle bypass, 30-entry ROB, LTage).

Predictor sweep (Horologium kernel-only, bypass=1):

| predictor        | IPC   | branch mispredicts         |
|------------------|-------|----------------------------|
| LTage            | 0.865 | 499                        |
| TAGE-SC-L        | 0.865 | 499                        |
| TournamentBP     | 0.742 | slightly fewer than LTage  |
| always_not_taken | 0.431 | 1537                       |

Key findings:

- **TAGE-SC-L ≡ LTage for treesum**: the statistical corrector adds nothing. The
  null-check `beqz a0` in tree_sum follows a tree-topology-driven sequence with no
  learnable global-history pattern; both predictors make ~499 mispredictions.

- **TournamentBP reduces IPC** (0.742 vs LTage 0.865) despite slightly fewer branch
  mispredicts. TournamentBP's per-PC local history generates a different speculative
  execution pattern, producing +155 extra memory-order violations (wrong-path loads
  conflicting with correct-path stores). The additional pipeline flushes more than
  offset the branch-prediction gain. The same effect appears in towers (0.708 vs 0.751).

- **always_not_taken yields 1537 mispredictions** — ~3× the expected ~511 conditional
  branches. The excess comes from a RAS interaction: when the recursive `jal` at
  0x80000384 is predicted not-taken, wrong-path fetch from the fall-through address
  reaches the `ret` epilogue at 0x800003b4 (11 instructions away, within ~6 fetch cycles)
  before the mispredict is detected. That wrong-path ret pops the entry the jal just pushed,
  leaving the RAS short by one for the actual return. This fires for all 512 recursive
  calls, producing ~512 spurious ret mispredictions on top of the 512 jal mispredictions
  and ~511 conditional mispredictions (256 taken beqz + 255 taken bnez ≈ 1537 total).

- **bypass=0 hurts treesum** (0.865 → 0.796, −8%). Branch mispredicts are unchanged
  (499 → 500), but D-cache misses increase by 138 and dispatch stall cycles by 1 479.
  The extra cache pressure and stalls are consistent with wrong-path execution reaching
  deeper into the memory hierarchy under faster forwarding.

## Structural differences

1. **Bypass latency**: Horologium uses `bypass_latency=1` (matching Olympia);
   gem5 O3CPU uses 0-cycle forwarding. Pass `--bypass-lat 0` to run Horologium
   with matched forwarding latency. **This is the dominant factor** for most
   compute-bound workloads.

2. **Branch predictor quality**: Predictor differences do not explain the treesum H/G gap.
   Using TournamentBP inside Horologium *reduces* IPC for both treesum (0.742 vs LTage 0.865)
   and towers (0.708 vs 0.751), driven by additional memory-order violations from
   TournamentBP's different speculative execution pattern. The null-check `beqz` in tree_sum
   is nearly unpredictable: LTage (499 mispredicts) saves only ~16 mispredictions vs
   always_taken (515), so predictor quality is not a lever here. The residual H/G gap for
   treesum is genuine simulation divergence (0-cycle vs 1-cycle forwarding, ROB/IQ differences).

3. **DIV latency**: Both simulators use `--div-lat 23` (matched). The residual gcd
   gap comes from FU count (gem5 has 2× IntMultDiv units vs Horologium's single
   shared divider).

4. **DRAM latency asymmetry**: gem5 SimpleMemory 30 ns vs Horologium HtifMemory
   (~10-cycle, fixed). Pass `--mem-lat-ns 10ns` to eliminate this variable for
   gem5; Horologium IPC is unaffected by this flag.

5. **IQ structure**: gem5 uses a flat 40-entry IQ; Horologium uses per-class
   queues (5 × 8 = 40 slots). Experiment confirms **zero IPC impact**: switching
   Horologium to a flat unified IQ (`--flat-iq`) produces identical results on
   all 10 workloads.

## Reproduce

```bash
# Build Linux-ABI benchmark ELFs (one-time)
make -C gem5-bmarks benchmarks

# Run the comparison (gem5 + Horologium for all 10 benchmarks)
bash scripts/gem5-compare.sh

# Knobs for structural matching experiments
bash scripts/gem5-compare.sh --structurally-matched     # bypass=0 + mem=10ns: closest structural match
bash scripts/gem5-compare.sh --width 4 --rob 64        # wider pipeline
bash scripts/gem5-compare.sh --mem-lat-ns 10ns          # reduce gem5 DRAM latency (Horo unaffected)
bash scripts/gem5-compare.sh --bypass-lat 0             # match gem5's 0-cycle forwarding
bash scripts/gem5-compare.sh --div-lat 20               # revert gem5 IntDiv to DefaultFUPool default
bash scripts/gem5-compare.sh --flat-iq                  # Horologium unified IQ: 1×40 flat, matching gem5
```
