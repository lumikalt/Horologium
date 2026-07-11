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

All profiles below use the current harness config: store buffer = 8 (matches gem5 L1D
`write_buffers=8`), speculative branch history, RAS checkpoint/restore, and direct-branch
targets resolved from decode. See "Progression of the fixes" and the findings sections for
how each affects the numbers.

Default run (`--mem-lat-ns 30ns`, `--bypass-lat 1` matching Olympia calibration).
Both gem5 and Horologium measure kernel-only IPC; Δinsts ≈ 0% confirms aligned windows.

| workload | gem5 IPC | Horo IPC | H/G ratio | Δinsts |
|----------|----------|----------|-----------|--------|
| median   | 0.7872   | 0.7451   | 0.947     | +0.2%  |
| qsort    | 0.6184   | 0.5726   | 0.926     | +0.0%  |
| rsort    | 1.3237   | 1.3478   | **1.018** | +0.0%  |
| towers   | 1.1726   | 0.8211   | 0.700     | +0.6%  |
| vvadd    | 1.4537   | 1.9042   | **1.310** | +0.3%  |
| memcpy   | 0.7268   | 1.5827   | **2.178** | +0.1%  |
| multiply | 1.8560   | 1.6916   | 0.911     | +0.0%  |
| gcd      | 0.2790   | 0.2420   | 0.868     | +0.1%  |
| treesum  | 1.6610   | 0.7707   | 0.464     | +0.1%  |
| pchase   | 0.1313   | 0.2555   | **1.946** | +0.0%  |

With `--bypass-lat 0` (matching gem5's 0-cycle result forwarding):

| workload | gem5 IPC | Horo IPC | H/G ratio | Δ from bypass=1 |
|----------|----------|----------|-----------|-----------------|
| median   | 0.7872   | 0.8303   | **1.055** | +11%            |
| qsort    | 0.6184   | 0.6815   | **1.102** | +19%            |
| rsort    | 1.3237   | 1.6543   | **1.250** | +23%            |
| towers   | 1.1726   | 0.8784   | 0.749     | +7%             |
| vvadd    | 1.4537   | 1.9330   | **1.330** | +2%             |
| memcpy   | 0.7268   | 1.5573   | **2.143** | -2%             |
| multiply | 1.8560   | 1.7463   | 0.941     | +3%             |
| gcd      | 0.2790   | 0.2528   | 0.906     | +4%             |
| treesum  | 1.6610   | 0.7644   | 0.460     | -1%             |
| pchase   | 0.1313   | 0.3017   | **2.298** | +18%            |

With `--mem-lat-ns 10ns` (eliminating DRAM asymmetry, bypass=1):

| workload | gem5 IPC | Horo IPC | H/G ratio |
|----------|----------|----------|-----------|
| median   | 0.7901   | 0.7451   | 0.943     |
| qsort    | 0.6185   | 0.5726   | 0.926     |
| rsort    | 1.3418   | 1.3478   | **1.004** |
| towers   | 1.1807   | 0.8211   | 0.695     |
| vvadd    | 1.4705   | 1.9042   | **1.295** |
| memcpy   | 1.0864   | 1.5827   | **1.457** |
| multiply | 1.8584   | 1.6916   | 0.910     |
| gcd      | 0.2792   | 0.2420   | 0.867     |
| treesum  | 1.6658   | 0.7707   | 0.463     |
| pchase   | 0.2181   | 0.2555   | **1.171** |

With `--structurally-matched` (`--bypass-lat 0 --mem-lat-ns 10ns` combined) — closest
structural match to gem5 O3CPU. Store buffer = 8 (matches gem5 L1D write_buffers=8);
fetch resolves direct unconditional jumps without the direction predictor; the OoO RAS
is checkpointed and restored on flush; and the branch predictor keeps speculative global
history (updated at fetch, recovered on flush) instead of commit-only history:

| workload | gem5 IPC | Horo IPC | H/G ratio | Δinsts |
|----------|----------|----------|-----------|--------|
| median   | 0.7901   | 0.8303   | **1.051** | +0.2%  |
| qsort    | 0.6185   | 0.6815   | **1.102** | +0.0%  |
| rsort    | 1.3418   | 1.6543   | **1.233** | +0.0%  |
| towers   | 1.1807   | 0.8784   | 0.744     | +0.6%  |
| vvadd    | 1.4705   | 1.9330   | **1.315** | +0.3%  |
| memcpy   | 1.0864   | 1.5573   | **1.433** | +0.1%  |
| multiply | 1.8584   | 1.7463   | 0.940     | +0.0%  |
| gcd      | 0.2792   | 0.2529   | 0.906     | +0.1%  |
| treesum  | 1.6658   | 0.7644   | 0.459     | +0.1%  |
| pchase   | 0.2181   | 0.3017   | **1.383** | +0.0%  |

Progression of the fixes (structurally-matched):

- **Store buffer 2 → 8** lifted memcpy 0.302 → 1.433 and vvadd 0.740 → 1.315 (both now
  above gem5, for the same memory-latency reason pchase is); rsort 1.139 → 1.233.
- **Speculative branch history** lifted median 0.797 → 1.051, gcd 0.865 → 0.906, towers
  0.721 → 0.744; other workloads flat. It cut branch mispredicts everywhere (treesum
  495 → 322, towers 41 → 21 — below gem5's 30).
- **treesum** slipped 0.478 → 0.459 despite the mispredict drop: better branch prediction
  drives deeper correct-path speculation, so more loads race unresolved stores and
  memory-order violations rise (26 → 245). The opt-in store-set predictor
  (`enable_store_sets`) removes them (violations → 0, IPC 0.76 → 0.84). The residual
  treesum gap is now predictor *structure*, not timing — see "treesum: speculative branch
  history" below.

H/G ratio > 1 means Horologium has higher IPC than gem5.

Note: `--mem-lat-ns` does not change Horologium IPC (HtifMemory is always ~10-cycle
regardless of the flag); only gem5's IPC changes. The `--structurally-matched` preset
eliminates both the bypass-latency and DRAM-latency asymmetries. Remaining gaps (towers,
vvadd, memcpy, treesum) reflect structural simulation differences: FU count, mispredict
penalty, and cache-thrashing behaviour under different associativity.

## What this shows

**Bypass latency (1 cycle) is the dominant factor for many workloads:**

Running `--bypass-lat 0` to match gem5's 0-cycle result forwarding:

- **median, qsort, rsort**: bypass=0 gains +11–23% (median +11%, qsort +19%, rsort +23%).
  All three lead Horologium once forwarding is matched (H/G 1.055, 1.102, 1.250). LTage's
  global-history prediction, now with speculative history, outperforms TournamentBP on these
  kernels.
- **vvadd, multiply, gcd**: small gains (+2–4%). vvadd already leads gem5 at either bypass
  setting (H/G 1.31–1.33) because the store-buffer fix removed its throttle; the residual
  multiply/gcd gaps are FU count (gem5 has 2× IntMultDiv vs Horologium's single divider).
- **treesum**: bypass=0 is ≈ flat now (−1%, 0.771 → 0.764; H/G 0.464 → 0.460). Its gap is not
  bypass latency — see "treesum: speculative branch history" below.
- **towers**: bypass=0 gains +7% (0.821 → 0.878, H/G 0.700 → 0.749). Recursive Hanoi is
  compute-bound, so bypass latency matters; the residual gap is memory-order violations that
  store sets remove.
- **pchase**: bypass=0 gains +18% (0.255 → 0.302, H/G 1.946 → 2.298). The kernel is
  cold-cache (the `PREALLOCATE` phase warms a *different* permutation than the one the kernel
  chases), so the workload is heavily DRAM-miss-bound regardless of bypass latency. With ROI
  instrumentation, gem5 kernel-only IPC (0.131) is now far lower than Horologium's (0.255)
  because Horologium's HtifMemory has ~10-cycle miss latency while gem5's SimpleMemory is
  30 ns ≈ 30 cycles; pchase is entirely latency-bound. The `--mem-lat-ns 10ns` variant
  reduces the gap (gem5 0.218, H/G 1.171).
- **memcpy**: bypass=0 has near-zero effect (−2%, 1.583 → 1.557). memcpy is store-throughput
  bound, not compute; with the store buffer sized to gem5's (8) it runs well above gem5 (H/G
  2.18 at 30 ns DRAM). See "memcpy/vvadd: store-buffer depth" below.

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

**memcpy: cold cache + high miss rate (context for the store-buffer finding below):**

The kernel runs cold with a very high miss rate — but the *IPC* is set by store-buffer
depth, not the miss rate (see "memcpy/vvadd: store-buffer depth"). Two facts about why the
copy misses so much:

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
it also fully thrashes with a similar miss rate. The high miss rate is real in both
simulators — but it does not explain the old 0.321 IPC. With the store buffer sized to
match gem5 (8), the same cold, fully-thrashing copy runs at 1.56 in Horologium: the misses
overlap through the write buffer instead of stalling commit. See the next subsection.

**memcpy/vvadd: store-buffer depth:**

The earlier store_buffer=2 profile put memcpy at H/G 0.302 and vvadd at 0.740, and the
prior write-up blamed cache associativity and DRAM latency. That was wrong. Holding
everything else fixed and sweeping only `store_buffer_capacity` (D-cache `ways` has zero
effect) on memcpy: 0.32 (sb2) → 0.50 (sb4) → 1.03 (sb6) → 1.58 (sb8); vvadd saturates at
~1.90 by sb3; rsort at 1.35 by sb4. A depth-2 store buffer forces committed store-misses
to stall commit (`wb_absorbed_stalls` rises from 12 460 to 39 880 as depth goes 2→8, i.e.
stores get absorbed instead of stalling; `cache_miss_stalls` collapses from 27 430 to 10).
gem5 sustains eight outstanding stores (`config.ini`: L1D `write_buffers=8`, `mshrs=4`),
so the fair value is `store_buffer_capacity=8`. With it, these kernels are bounded only by
Horologium's faster memory and land above gem5 (memcpy 1.43, vvadd 1.32) — the same
latency asymmetry as pchase, not a defect.

**treesum: speculative branch history:**

With ROI instrumentation both simulators commit ~11 530 kernel-only instructions (Δinsts
+0.1%). gem5 commits **70** branch mispredicts (70 conditional, **0 return**, 2 call) over
~6 924 cycles; Horologium commits **~495** → ~521 flushes → 13 342 cycles. The ~450 extra
flushes are the entire 2× gap.

An earlier version of this section claimed gem5 also records ~499 treesum mispredicts and
concluded predictor differences don't explain the gap. **That was a measurement error** —
gem5's actual committed count is 70 (`branchPred.committed`/`commit.branchMispredicts`),
and it is what led the prior analysis to dismiss the control-flow angle.

Three control-flow modeling gaps were addressed:

- *Fixed — direct unconditional jumps bypass the direction predictor.* `FetchHint` gained
  `IsUnconditional`; fetch now resolves `jal`/`j` straight to their known target instead of
  asking a direction predictor that may say not-taken (which is why `true_oracle` and
  `always_not_taken` used to mispredict every call, 1537 on treesum).
- *Fixed — RAS checkpoint/restore.* The OoO core keeps an architectural RAS shadow, updated
  only when a call/return retires, and restores the speculative RAS from it on every flush.
  gem5 checkpoints its RAS the same way (0 committed return mispredicts). These first two
  together removed only ~4 of the 499 — the excess was **conditional**, not return.
- *Fixed — speculative global history.* `LTageBranchPrediction` previously updated the
  global history register `Ghr` only in `Update()` (at commit), so within the 30-entry ROB
  and deep recursion every TAGE lookup indexed stale history. The predictor now advances a
  speculative `Ghr` at fetch (`SpeculativeHistoryUpdate`) against an architectural
  `_committedGhr` shadow that trains the tables and restores `Ghr` on flush
  (`RecoverSpeculativeHistory`). This is bit-identical for in-order pipelines (a latched
  `_speculative` flag) and covers the whole TAGE family, which all index off the shared
  `Ghr`. treesum mispredicts fell 495 → 323; median IPC rose 0.797 → 1.051.

**Residual treesum gap is predictor choice, not a Horologium limit.** With speculative
history LTage still mispredicts treesum's null-check 323 times vs gem5's 70, because LTage is
global-history TAGE and `tree_sum`'s null-check is *locally* predictable. Horologium's own
Tournament predictor — which has a per-PC local history, now also speculative — gets treesum
down to **37 mispredicts, below gem5's 70**, confirming the gap is the *predictor* the
comparison happens to default to (l_tage), not a modelling limitation. The mispredict win
still does not fully convert to IPC because deeper correct-path speculation raises
memory-order violations (LTage 26 → 245); store sets remove them (→ 0) and recover IPC.

Predictor sweep (Horologium kernel-only, bypass=1, store buffer = 8):

| predictor        | IPC   | mispredicts | mem-order violations |
|------------------|-------|-------------|----------------------|
| LTage            | 0.771 | 323         | 245                  |
| TAGE-SC-L        | 0.771 | 323         | 245                  |
| Tournament       | 0.998 | 37          | 188                  |
| always_not_taken | 0.891 | 511         | 1                    |
| always_taken     | 0.672 | 513         | 256                  |

Key findings:

- **TAGE-SC-L ≡ LTage for treesum** (both 323 mispredicts, IPC 0.771): the statistical
  corrector adds nothing. The null-check `beqz a0` in tree_sum has no learnable
  *global*-history or statistical pattern.

- **Tournament is the best treesum predictor** (37 mispredicts, IPC 0.998 — below gem5's
  70). Its per-PC *local* history, now speculative, captures the null-check sequence that
  global-history TAGE misses. This is the direct evidence that the l_tage H/G gap is
  predictor choice, not a modelling limit.

- **IPC here is violation-bound, not mispredict-bound.** `always_not_taken` has the *most*
  mispredicts (511) yet a high IPC (0.891), because predicting not-taken keeps speculation
  shallow — only 1 memory-order violation. LTage predicts far better (323) but its deeper
  speculation triggers 245 violations, dropping IPC to 0.771. Tournament wins on both axes
  (37 mispredicts, 188 violations). Store sets decouple prediction quality from the
  violation penalty.

- **The RAS-checkpoint and direct-unconditional-jump fixes removed `always_not_taken`'s old
  1537-mispredict pathology.** Previously a `jal` predicted not-taken sent wrong-path fetch
  into the `ret` epilogue, whose speculative pop corrupted the uncheckpointed RAS and cost
  ~512 spurious return mispredicts on top of ~512 jal + ~511 conditional. With calls resolved
  from decode and the RAS restored on flush, it now sits at 511 — the true conditional count.

## Structural differences

1. **Bypass latency**: Horologium uses `bypass_latency=1` (matching Olympia);
   gem5 O3CPU uses 0-cycle forwarding. Pass `--bypass-lat 0` to run Horologium
   with matched forwarding latency. **This is the dominant factor** for most
   compute-bound workloads.

2. **Branch-history update timing** (fixed): The OoO branch predictor now keeps
   speculative global history — advanced at fetch, restored on flush — instead of
   commit-only history, so TAGE lookups no longer index stale history across the ROB
   window. This cut mispredicts broadly (treesum 495 → 323, towers 41 → 21) and lifted
   median 0.797 → 1.051. Together with the direct-unconditional-jump and RAS-checkpoint
   fixes it is the control-flow trio. The residual treesum gap under the default l_tage
   (323 vs gem5's 70) is predictor *choice*, not a limit: Horologium's own Tournament (local
   history) gets treesum to 37, below gem5 — plus a violation coupling that store sets
   resolve. See "treesum: speculative branch history".

3. **Store-buffer depth**: `store_buffer_capacity=8` matches gem5's L1D `write_buffers=8`.
   The earlier value 2 throttled store-streaming kernels (memcpy, vvadd) to a fraction of
   gem5's IPC; it was a harness calibration artifact, not a Horologium defect. See
   "memcpy/vvadd: store-buffer depth".

4. **DIV latency**: Both simulators use `--div-lat 23` (matched). The residual gcd
   gap comes from FU count (gem5 has 2× IntMultDiv units vs Horologium's single
   shared divider).

5. **DRAM latency asymmetry**: gem5 SimpleMemory 30 ns vs Horologium HtifMemory
   (~10-cycle, fixed). Pass `--mem-lat-ns 10ns` to eliminate this variable for
   gem5; Horologium IPC is unaffected by this flag. This is why the store-streaming
   and pointer-chasing kernels (memcpy, vvadd, pchase) land above gem5 once other
   throttles are removed — Horologium's memory is simply faster.

6. **IQ structure**: gem5 uses a flat 40-entry IQ; Horologium uses per-class
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
bash scripts/gem5-compare.sh --structurally-matched \
  --predictor-json '{"type":"tournament","LocalHistoryBits":11,"LocalTableSize":2048,"GlobalHistoryBits":13}'
                                                       # gem5-matched TournamentBP table sizes
bash scripts/gem5-compare.sh --width 4 --rob 64        # wider pipeline
bash scripts/gem5-compare.sh --mem-lat-ns 10ns          # reduce gem5 DRAM latency (Horo unaffected)
bash scripts/gem5-compare.sh --bypass-lat 0             # match gem5's 0-cycle forwarding
bash scripts/gem5-compare.sh --div-lat 20               # revert gem5 IntDiv to DefaultFUPool default
bash scripts/gem5-compare.sh --flat-iq                  # Horologium unified IQ: 1×40 flat, matching gem5
```
