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

### Measurement asymmetry

Horologium measures **kernel-only** IPC: `Experiment.RunOne` attaches a
`SetStatsObserver` that snapshots the pipeline `DialBoard` at the first
instruction of `setStats(1)` and `setStats(0)`, then subtracts to isolate the
kernel interval. The `PREALLOCATE` warmup region runs before `setStats(1)` and
warms the caches; the counters only cover the ROI.

gem5 measures the **full run** IPC, because the Linux binary's `setStats` is a
no-op — gem5 has no equivalent ROI instrumentation. Its committed-instruction
count includes startup + kernel + any teardown.

As a result, `Δinsts` is large and negative for all workloads: it measures
`(Horo_kernel_retired − gem5_full_committed) / gem5_full_committed`. This is
expected and reflects the startup/exit overhead in gem5's count, not a kernel
divergence. The IPC column compares actual throughput of each simulator's
measured window — kernel-only for Horologium, full-run for gem5.

## Results

Horologium (w2): ROB=30, IQ=5×8 per-class, L1 16KB, LoadHit=4, Bypass=1, DivLat=23, **LTage** (kernel-only IPC).
gem5 O3CPU SE: width=2, ROB=30, IQ=40 flat (matched total), L1 16KB, TournamentBP, RAS=16, DivLat=23.
Run with: `bash scripts/gem5-compare.sh`

Default run (`--mem-lat-ns 30ns`, `--bypass-lat 1` matching Olympia calibration):

| workload | gem5 IPC | Horo IPC | H/G ratio | Δinsts |
|----------|----------|----------|-----------|--------|
| median   | 0.682    | 0.527    | 0.773     | -59.6% |
| qsort    | 0.818    | 0.576    | 0.705     | -45.5% |
| rsort    | 1.292    | 1.264    | 0.978     | -53.1% |
| towers   | 0.921    | 0.751    | 0.816     | -52.2% |
| vvadd    | 1.108    | 1.072    | 0.968     | -62.0% |
| memcpy   | 0.667    | 0.321    | 0.481     | -64.5% |
| multiply | 1.786    | 1.689    | 0.946     | -50.5% |
| gcd      | 0.323    | 0.229    | 0.711     | -60.3% |
| treesum  | 1.705    | 0.865    | 0.507     | -78.3% |
| pchase   | 0.442    | 0.255    | 0.577     | -88.9% |

With `--bypass-lat 0` (matching gem5's 0-cycle result forwarding):

| workload | gem5 IPC | Horo IPC | H/G ratio | Δ from bypass=1 |
|----------|----------|----------|-----------|-----------------|
| median   | 0.682    | 0.626    | 0.918     | +19%            |
| qsort    | 0.818    | 0.687    | 0.840     | +19%            |
| rsort    | 1.292    | 1.528    | **1.183** | +21%            |
| towers   | 0.921    | 0.837    | 0.909     | +11%            |
| vvadd    | 1.108    | 1.088    | 0.982     | +1%             |
| memcpy   | 0.667    | 0.328    | 0.491     | +2%             |
| multiply | 1.786    | 1.743    | 0.976     | +3%             |
| gcd      | 0.323    | 0.240    | 0.743     | +4%             |
| treesum  | 1.705    | 0.796    | 0.467     | -5%             |
| pchase   | 0.442    | 0.302    | 0.682     | +18%            |

With `--mem-lat-ns 10ns` (eliminating DRAM asymmetry, bypass=1):

| workload | gem5 IPC | Horo IPC | H/G ratio |
|----------|----------|----------|-----------|
| median   | 0.736    | 0.527    | 0.716     |
| qsort    | 0.835    | 0.576    | 0.690     |
| rsort    | 1.347    | 1.264    | 0.938     |
| towers   | 1.010    | 0.751    | 0.744     |
| vvadd    | 1.399    | 1.072    | 0.766     |
| memcpy   | 0.988    | 0.321    | 0.325     |
| multiply | 1.815    | 1.689    | 0.931     |
| gcd      | 0.323    | 0.229    | 0.710     |
| treesum  | 1.716    | 0.865    | 0.504     |
| pchase   | 0.655    | 0.255    | 0.390     |

H/G ratio > 1 means Horologium has higher IPC than gem5.

Note: the 10ns DRAM variant does not change Horologium IPC (HtifMemory is always ~10-cycle
regardless of `--mem-lat-ns`); only gem5's IPC changes. So this variant isolates the effect
of gem5's DRAM latency on its full-run IPC.

## What this shows

**Bypass latency (1 cycle) is the dominant factor for many workloads:**

Running `--bypass-lat 0` to match gem5's 0-cycle result forwarding:

- **median, qsort, rsort**: bypass=0 gains +19–21%. rsort flips to Horologium leading (1.183).
  LTage's global-history prediction outperforms TournamentBP on sort kernels once bypass
  latency is matched.
- **vvadd, multiply, gcd**: small gains (1–4%). Residual gaps are from FU count differences
  (gem5 has 2× IntMultDiv vs Horologium's single shared divider for gcd) and potential
  write-allocate cache miss patterns.
- **treesum**: bypass=0 *reduces* Horologium IPC by 8% (0.865 → 0.796, H/G 0.507 → 0.467)
  — not zero effect. Branch mispredicts are unchanged (499 → 500), but D-cache misses
  increase by 138 and dispatch stall cycles by 1 479, consistent with wrong-path execution
  reaching deeper into the cache under faster forwarding. The ~2× H/G gap is primarily
  **measurement asymmetry**: gem5 full-run commits 53 272 instructions (make_tree, startup,
  PREALLOCATE traversal, kernel, teardown) while Horologium measures only the kernel interval
  (11 539 instructions). See "treesum: predictor sweep" below.
- **towers**: bypass=0 gains +11% (0.816 → 0.909). Recursive Hanoi is more compute-bound
  than treesum, making bypass latency a larger factor. A predictor sweep finds the same
  result as treesum: TournamentBP reduces Horologium IPC (0.708 vs LTage 0.751) due to
  extra memory-order violations.
- **pchase**: bypass=0 gains +18% (0.577 → 0.682). The kernel is cold-cache (the
  `PREALLOCATE` phase warms a *different* permutation than the one the kernel chases),
  so the workload is heavily DRAM-miss-bound regardless of bypass latency.
- **memcpy**: bypass=0 has near-zero effect (+2%, 0.481 → 0.491). The gap is not
  bypass-driven; see "memcpy: PREALLOCATE elimination + total cache thrashing" below.

**IQ structure (per-class vs flat) explains zero IPC difference:**

Confirmed by `--flat-iq` experiment (not shown above): switching Horologium to a flat
unified 40-entry IQ produces identical IPC on every workload. With `issueWidth=2` and
in-order dispatch, the global issue width saturates before IQ class boundaries can matter.

**pchase: kernel window is cold-cache:**

The pchase `PREALLOCATE` warms a first permutation, then `init_permutation()` re-shuffles
before the kernel. The kernel chases a second permutation that the I-cache never saw.
This explains the low kernel IPC (0.255) and why reducing DRAM latency helps gem5 (its
full-run includes the warm PREALLOCATE phase) but not Horologium's kernel-only measurement.

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
fully thrashes. gem5 stats confirm: 7495 misses / 16 022 accesses = **46.8% miss rate**
(full-run; the denominator includes startup accesses that partially warm the cache before
`setStats` would fire). The IPC gap (0.321 vs 0.667) is therefore primarily a measurement
asymmetry: gem5's full-run number averages in startup and verification phases that access
a much smaller working set and run at much higher IPC; Horologium's kernel-only number
isolates the cold, fully-thrashing streaming copy.

**treesum: predictor sweep and measurement asymmetry:**

The ~2× H/G gap (gem5 1.705 vs Horologium 0.865) is primarily **measurement asymmetry**.
gem5 full-run commits 53 272 instructions spanning make_tree, startup, a full PREALLOCATE
tree traversal, the kernel traversal, and teardown; Horologium measures only the kernel
traversal (11 539 instructions). gem5 records 209 conditional mispredictions over 31 047
cycles — the low mispredict count reflects that startup and PREALLOCATE phases dominate
its cycle budget at high IPC, diluting branch overhead. A like-for-like comparison would
require gem5 ROI instrumentation.

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
   The ~2× gap is primarily measurement asymmetry (gem5 full-run vs Horologium kernel-only;
   see "treesum: predictor sweep"). Using TournamentBP inside Horologium *reduces* IPC for
   both treesum (0.742 vs LTage 0.865) and towers (0.708 vs 0.751), driven by additional
   memory-order violations from TournamentBP's different speculative execution pattern. The
   null-check `beqz` in tree_sum is nearly unpredictable: LTage (499 mispredicts) saves only
   ~16 mispredictions vs always_taken (515), so predictor quality is not a lever here.

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
bash scripts/gem5-compare.sh --width 4 --rob 64        # wider pipeline
bash scripts/gem5-compare.sh --mem-lat-ns 10ns          # reduce gem5 DRAM latency (Horo unaffected)
bash scripts/gem5-compare.sh --bypass-lat 0             # match gem5's 0-cycle forwarding
bash scripts/gem5-compare.sh --div-lat 20               # revert gem5 IntDiv to DefaultFUPool default
bash scripts/gem5-compare.sh --flat-iq                  # Horologium unified IQ: 1×40 flat, matching gem5
```
