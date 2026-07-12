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
| median   | 0.7872   | 0.7810   | 0.992     | +0.2%  |
| qsort    | 0.6184   | 0.5837   | 0.944     | +0.0%  |
| rsort    | 1.3237   | 1.3495   | **1.019** | +0.0%  |
| towers   | 1.1726   | 0.8351   | 0.712     | +0.6%  |
| vvadd    | 1.4537   | 1.9501   | **1.341** | +0.3%  |
| memcpy   | 0.7268   | 1.5864   | **2.183** | +0.1%  |
| multiply | 1.8560   | 1.8017   | 0.971     | +0.0%  |
| gcd      | 0.2790   | 0.2421   | 0.868     | +0.1%  |
| treesum  | 1.6610   | 0.7857   | 0.473     | +0.1%  |
| pchase   | 0.1313   | 0.2555   | **1.946** | +0.0%  |

With `--bypass-lat 0` (matching gem5's 0-cycle result forwarding):

| workload | gem5 IPC | Horo IPC | H/G ratio | Δ from bypass=1 |
|----------|----------|----------|-----------|-----------------|
| median   | 0.7872   | 0.8781   | **1.115** | +12%            |
| qsort    | 0.6184   | 0.6998   | **1.132** | +20%            |
| rsort    | 1.3237   | 1.6559   | **1.251** | +23%            |
| towers   | 1.1726   | 0.8955   | 0.764     | +7%             |
| vvadd    | 1.4537   | 1.9533   | **1.344** | +0%             |
| memcpy   | 0.7268   | 1.5570   | **2.142** | -2%             |
| multiply | 1.8560   | 1.8637   | **1.004** | +3%             |
| gcd      | 0.2790   | 0.2529   | 0.906     | +4%             |
| treesum  | 1.6610   | 0.8576   | 0.516     | +9%             |
| pchase   | 0.1313   | 0.3017   | **2.298** | +18%            |

With `--mem-lat-ns 10ns` (eliminating DRAM asymmetry, bypass=1):

| workload | gem5 IPC | Horo IPC | H/G ratio |
|----------|----------|----------|-----------|
| median   | 0.7901   | 0.7810   | 0.989     |
| qsort    | 0.6185   | 0.5837   | 0.944     |
| rsort    | 1.3418   | 1.3495   | **1.006** |
| towers   | 1.1807   | 0.8351   | 0.707     |
| vvadd    | 1.4705   | 1.9501   | **1.326** |
| memcpy   | 1.0864   | 1.5864   | **1.460** |
| multiply | 1.8584   | 1.8017   | 0.970     |
| gcd      | 0.2792   | 0.2421   | 0.867     |
| treesum  | 1.6658   | 0.7857   | 0.472     |
| pchase   | 0.2181   | 0.2555   | **1.171** |

With `--structurally-matched` (`--bypass-lat 0 --mem-lat-ns 10ns` combined) — closest
structural match to gem5 O3CPU. Store buffer = 8 (matches gem5 L1D write_buffers=8);
fetch resolves direct unconditional jumps without the direction predictor; the OoO RAS
is checkpointed and restored on flush; and the branch predictor keeps speculative global
history (updated at fetch, recovered on flush) instead of commit-only history:

| workload | gem5 IPC | Horo IPC | H/G ratio | Δinsts |
|----------|----------|----------|-----------|--------|
| median   | 0.7901   | 0.8781   | **1.111** | +0.2%  |
| qsort    | 0.6185   | 0.6998   | **1.132** | +0.0%  |
| rsort    | 1.3418   | 1.6559   | **1.234** | +0.0%  |
| towers   | 1.1807   | 0.8955   | 0.758     | +0.6%  |
| vvadd    | 1.4705   | 1.9533   | **1.328** | +0.3%  |
| memcpy   | 1.0864   | 1.5570   | **1.433** | +0.1%  |
| multiply | 1.8584   | 1.8637   | **1.003** | +0.0%  |
| gcd      | 0.2792   | 0.2529   | 0.906     | +0.1%  |
| treesum  | 1.6658   | 0.8576   | 0.515     | +0.1%  |
| pchase   | 0.2181   | 0.3017   | **1.383** | +0.0%  |

Progression of the fixes (structurally-matched). *These bullets give each earlier fix's isolated
contribution as measured when it landed, before execute-time branch resolution was added on top;
the current absolute IPCs are in the tables above (execute-time resolution lifts them a further
1–7%).*

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

**Store sets is a net loss across the full suite — it should stay opt-in.** Running
`--enable-store-sets` against all 10 benchmarks (default bypass=1, l_tage): treesum
(0.473 → 0.678) and towers (0.712 → 0.924) improve substantially, matching the
per-workload story above, but **rsort regresses severely** (H/G 1.019 → 0.631; IPC
1.3495 → 0.8347, cycles 126 843 → 205 079) and qsort dips slightly (0.944 → 0.893).
The remaining six workloads are unaffected (no store/load conflicts to predict). For
rsort, `mem_order_violations` drops 82 → 1 as intended, but `stalls` balloons
125 299 → 204 899 — roughly 79 000 stall cycles paid to avoid ~81 violations, which
individually cost far less than that to squash-and-replay. The likely cause: the SSIT
is PC-indexed only (no address hashing), so a single genuine conflict at a load/store
PC pair permanently merges *every* future dynamic instance of that pair into the same
store set — recursive/generic functions (rsort's partition step reuses one swap PC for
many independent array indices) pay for one real dependency by serializing all the
unrelated ones forever. See the TODO for a possible mitigation (periodic SSIT/LFST
clearing or address-aware set assignment).

H/G ratio > 1 means Horologium has higher IPC than gem5.

Note: `--mem-lat-ns` does not change Horologium IPC (HtifMemory is always ~10-cycle
regardless of the flag); only gem5's IPC changes. The `--structurally-matched` preset
eliminates both the bypass-latency and DRAM-latency asymmetries. Remaining gaps (towers,
vvadd, memcpy, treesum) reflect structural simulation differences: FU count, mispredict
penalty, and cache-thrashing behaviour under different associativity.

## What this shows

**Bypass latency (1 cycle) is the dominant factor for many workloads:**

Running `--bypass-lat 0` to match gem5's 0-cycle result forwarding:

- **median, qsort, rsort**: bypass=0 gains +12–23% (median +12%, qsort +20%, rsort +23%).
  All three lead Horologium once forwarding is matched (H/G 1.115, 1.132, 1.234). LTage's
  global-history prediction, now with speculative history, outperforms TournamentBP on these
  kernels.
- **vvadd, multiply, gcd**: small gains (+2–4%). vvadd already leads gem5 at either bypass
  setting (H/G 1.31–1.33) because the store-buffer fix removed its throttle; the residual
  multiply/gcd gaps are FU count (gem5 has 2× IntMultDiv vs Horologium's single divider).
- **treesum**: bypass=0 gains +9% (0.786 → 0.858; H/G 0.473 → 0.516). Its residual gap is not
  bypass latency but predictor structure and violation coupling — see "treesum: speculative branch
  history" below.
- **towers**: bypass=0 gains +7% (0.835 → 0.895, H/G 0.712 → 0.764). Recursive Hanoi is
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
+0.1%). gem5 commits **72** branch mispredicts (`branchPred.mispredicted_0`: 70 `DirectCond`,
**0 `Return`**, 2 `CallDirect`) over ~6 924 cycles; Horologium (default l_tage) commits
**~495** → ~521 flushes → 13 342 cycles. The ~450 extra flushes are the entire 2× gap.

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

Predictor sweep (Horologium kernel-only, bypass=1, store buffer = 8; execute-time resolution).
The *squashes* column is the execute-time branch-squash count, which includes transient
wrong-path branches (see the metric note above) — so it exceeds the committed-mispredict count in
the violation-heavy configs here; the clean committed anchor is the 0-violation tournament +
store-sets result (37, above):

| predictor        | IPC   | squashes | mem-order violations |
|------------------|-------|----------|----------------------|
| LTage            | 0.786 | 416      | 245                  |
| TAGE-SC-L        | 0.786 | 416      | 245                  |
| Tournament       | 1.017 | 38       | 188                  |
| always_not_taken | 0.969 | 513      | 1                    |
| always_taken     | 0.693 | 512      | 256                  |

Key findings:

- **TAGE-SC-L ≡ LTage for treesum** (both 416 squashes, IPC 0.786): the statistical
  corrector adds nothing. The null-check `beqz a0` in tree_sum has no learnable
  *global*-history or statistical pattern.

- **Tournament is the best treesum predictor** (38 squashes here, a clean 37 committed with store
  sets — below gem5's 70; IPC 1.017). Its per-PC *local* history, now speculative, captures the
  null-check sequence that global-history TAGE misses. This is the direct evidence that the
  l_tage H/G gap is predictor choice, not a modelling limit.

- **IPC here is violation-bound, not squash-bound.** `always_not_taken` has the *most* squashes
  (513) yet a high IPC (0.969), because predicting not-taken keeps speculation shallow — only 1
  memory-order violation. LTage speculates far deeper (245 violations), dropping IPC to 0.786
  despite fewer squashes. Tournament wins on both axes (38 squashes, 188 violations). Store sets
  decouple prediction quality from the violation penalty.

- **The RAS-checkpoint and direct-unconditional-jump fixes removed `always_not_taken`'s old
  1537-mispredict pathology.** Previously a `jal` predicted not-taken sent wrong-path fetch
  into the `ret` epilogue, whose speculative pop corrupted the uncheckpointed RAS and cost
  ~512 spurious return mispredicts on top of ~512 jal + ~511 conditional. With calls resolved
  from decode and the RAS restored on flush, it now sits at 511 — the true conditional count.

**treesum with Tournament + store sets — how close it gets, and the residual gap.**
Stacking the two treesum levers (Tournament for prediction, store sets to break the
violation coupling) closes most of the l_tage gap. Kernel-only treesum, ROB=30, IQ=8, store
buffer = 8:

| config                                              | bypass | IPC   | H/G ratio | committed cond. mispredicts | violations | cycles |
|-----------------------------------------------------|--------|-------|-----------|-----------------------------|------------|--------|
| l_tage (default)                                    | 1      | 0.786 | 0.473     | 323                         | 245        | 14 681 |
| tournament                                          | 1      | 1.017 | 0.612     | 37                          | 188        | 11 346 |
| tournament + store sets (default)                   | 1      | 1.457 | 0.875     | 37                          | 2          | 7 918  |
| tournament + store sets, commit-time resolution     | 0      | 1.535 | 0.921     | 37                          | 0          | 7 515  |
| **tournament + store sets, execute-time resolution**| 0      | **1.603** | **0.962** | 37                      | 0          | **7 200** |
| gem5 O3CPU (TournamentBP, execute-time resolution)  | 0      | 1.666 | 1.000     | 70                          | 0          | 6 923  |

The gem5 anchor is apples-to-apples: gem5's 70 is `branchPred.mispredicted_0::DirectCond`
(conditional-direction mispredicts), the same category Horologium's 37 counts; gem5's other two
mispredicts are `CallDirect`, which Horologium resolves from decode and never mis-speculates. So
the config lands at **H/G 0.962** — within ~4% of gem5, while committing **half** as many
conditional mispredicts (37 vs 70). The remaining ~4% is *not* a prediction-quality or
memory-ordering deficit; both are already at or below gem5's level.

**Execute-time resolution, and how it closes the gap.** gem5's O3CPU detects a mispredicted branch
in the **execute** stage (`iew`) and redirects fetch on the same cycle, squashing younger in-flight
instructions immediately. Horologium's `OooeTrain` now does the same: a branch that resolves
mispredicted before reaching the ROB head triggers a **partial squash** — the branch and every
older in-flight instruction stay live and commit normally, while everything younger is discarded
and fetch is redirected. Previously the flush waited until the branch reached the ROB head at
commit, so the wrong path stayed alive for the whole commit-drain window.

**The intervention hit the predicted ceiling, with no second-order losses.** Before building it, ROB
instrumentation (a `CompleteTick` per entry) predicted a **315-cycle** recoverable ceiling — the
summed gap, over the 37 flushed branches, between each branch resolving (direction known at CDB
broadcast) and reaching the ROB head. The implemented feature landed treesum at **7 200 cycles**,
i.e. `7 515 − 315` to the cycle. Because the instrument measured `commitTick − CompleteTick` and the
feature redirects at exactly `CompleteTick`, recovering the full sum is partly by construction — so
what hitting the *upper bound* independently establishes is that second-order effects (resource
contention on the freed slots, refill misalignment) are negligible here, not merely that the
attribution was right. IPC rose 1.535 → 1.603, H/G 0.921 → 0.962.

The recovery is bounded because `tree_sum`'s null-check is load-dependent (`a0` = node pointer), so
the branch resolves only ~8.5 cycles before it would commit anyway. The wrong-path fetch count
corroborates: Horologium's wrong-path fetch fell (icache_hits 12 749 → 12 199), the ~630-instruction
drop the 315 slack-cycles at width 2 predict.

**Residual ~277 cycles (7 200 − 6 923): still gem5-favouring, cause not yet confirmed.** The
leading hypothesis is Horologium's longer fetch-to-execute refill window (more pipeline stages
between redirect and the first correct-path execute); FU/scheduling differences are a secondary
candidate. This is an attribution, not a measurement — unlike the 315-cycle component it has not
been independently instrumented.

*Metric note.* `branch_misses` now counts execute-time squashes, which include transient wrong-path
branches that resolve mispredicted before an older mispredict flushes them — the same events gem5
counts as squashes. It is therefore ≥ the committed-mispredict count in general (e.g. l_tage default
323 committed → 416 raw squashes). In a 0-violation config like tournament + store sets the two
coincide (37 = 37, verified against the commit-time model), which is why the 37-vs-70 comparison
above stays a clean committed-vs-committed anchor.

An oracle-floor check (perfect prediction to isolate the cycle floor) was also attempted with
`true_oracle` but is **confounded** in the OoO core: `TrueOraclePredictor` consumes its trace
positionally and drifts on wrong-path speculation, so it mispredicts more (511) than Tournament
(37) rather than less. It does not cleanly establish the floor; the ROB slack measurement and the
confirming 7 200-cycle result above are the load-bearing evidence instead.

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

7. **Misprediction-resolution timing** (resolved): both simulators now resolve branch
   mispredicts at **execute** and redirect fetch the same cycle. gem5 O3CPU does this in `iew`;
   Horologium's `OooeTrain` does it via a partial squash that keeps the redirecting branch and
   every older in-flight instruction live (previously it deferred the flush to the ROB head at
   commit). On treesum with matched prediction and store sets this recovered a **measured**
   315 cycles — exactly the pre-build ROB-slack prediction — lifting H/G 0.921 → 0.962. The
   remaining ~4% (277 cycles) is hypothesised to be Horologium's longer fetch-to-execute refill
   window, not yet independently confirmed. See "treesum with Tournament + store sets" above.

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
bash scripts/gem5-compare.sh --structurally-matched \
  --predictor-json '{"type":"tournament"}' --enable-store-sets
                                                       # treesum's best config: H/G 0.962, 37 mispredicts, 0 violations
```
