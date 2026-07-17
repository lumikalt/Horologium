# RtlFu — RTL unit substitution

Swaps one pipeline unit for cycle-accurate RTL via Verilator: the surrounding Horologium
pipeline drives the real hardware model instead of the C# model for that unit. Two unit
kinds so far:

- **Functional units** — `RtlBackedExecutor` (in `src/Core/Mechanism/RtlFu/`) substitutes
  the RTL result for the register write and reports the model's observed cycle count as
  the instruction's FU latency (`ExecuteResult.LatencyOverride`), which the `ooo` and
  `cpr` pipelines use in place of the static `FuLatencyConfig` entry.
- **Branch predictors** — two shim ABIs, auto-detected by `RtlBranchPredictorLoader` so one
  flag/config serves both. `rtl_bp_shim.cpp` wraps a plain predictor (combinational predict,
  one-edge commit update; e.g. the gshare) behind `RtlFfiBranchPredictor`. `rtl_hbp_shim.cpp`
  wraps a predictor that manages its own speculative global history (e.g. the L-TAGE) behind
  `RtlFfiHistoryBranchPredictor`, carrying the full `IBranchPredictor` contract across the
  FFI: fetch-time history folds, flush recovery, and per-branch checkpoints for partial
  squashes (the checkpoint is the model's working-history value — TAGE folds derive from it,
  so no checkpoint RAM). Selectable per sweep config (`{"type": "rtl_bp_plugin"}`) or
  globally via `--rtl-bp-lib`.
- **Cache replacement policies** — `RtlFfiReplacementPolicy` (in `src/Core/Orrery/Cache/`)
  implements `IReplacementPolicy` on top of a verilated policy; geometry is fixed at Chisel
  elaboration and exposed via `io_cfgSets`/`io_cfgWays`, and the policy attaches only to
  cache levels whose sets×ways match (others keep the configured C# policy). Selectable per
  sweep config (`"rtl_cache_policy_lib"`) or globally via `--rtl-rp-lib`.
- **Cache prefetchers** — `RtlFfiPrefetcher` (in `src/Core/Orrery/Cache/`) implements
  `IPrefetcher` on top of a verilated prefetcher. Two shim shapes share the same
  `rtl_pf_*` C ABI, so the C# side is identical for both: `rtl_pf_shim.cpp` for
  single-target models (decision read combinationally, table update on one edge — the
  stride prefetcher), and `rtl_mpf_shim.cpp` for multi-degree models (the access edge
  loads an internal drain queue; the shim pops one address per clock — the stream
  prefetcher's allocation burst). Selectable per sweep config (`"rtl_prefetcher_lib"`)
  or globally via `--rtl-pf-lib`.

## Files

| File | Role |
|---|---|
| `DivUnit.scala` | Chisel source: RV32M DIV/DIVU/REM/REMU, sequential restoring divider with early termination (latency = significant-bits(dividend) + 1; RISC-V special cases resolve in 1 cycle) |
| `MulUnit.scala` | Chisel source: RV32M MUL/MULH/MULHSU/MULHU, fully pipelined 3-stage 33×33 multiplier (`io.req.ready` constantly high; constant 3-cycle latency = the `MulDivLatency` default) |
| `FDivSqrtUnit.scala` | Chisel source: RV32F FDIV.S/FSQRT.S — iterative IEEE binary32 with full subnormal support, RNE, canonical NaNs, and exception flags mirroring the C# soft-float model's quirks (OF without NX; UF only for inexact nonzero subnormals) |
| `GshareBp.scala` | Chisel source: gshare predictor (8-bit GHR, 256×2-bit PHT + BTB) mirroring the C# `GsharePredictor` bit-for-bit so differential tests can demand identical predictions |
| `LTageBp.scala` | Chisel source: L-TAGE (4096-entry bimodal + 4×512 tagged tables with 8/13/21/34-bit folded histories + 32-entry loop predictor) mirroring the C# `LTagePredictor`; manages its own speculative GHR with capture/restore ports |
| `SrripRp.scala` | Chisel source: SRRIP replacement policy (2-bit RRPVs, combinational victim + unrolled aging) mirroring the C# `SrripPolicy`; default geometry 64 sets × 4 ways |
| `DrripRp.scala` | Chisel source: DRRIP (SRRIP base + Set Dueling: SDM leader sets, 10-bit PSEL, 1/32 bimodal BRRIP inserts) mirroring the C# `DrripPolicy`; carries global cross-set state through the same shim ABI |
| `StridePf.scala` | Chisel source: RPT stride prefetcher (64 PC-indexed entries, 64-bit datapath, 2-bit confidence) mirroring the C# `StridePrefetcher` |
| `StreamPf.scala` | Chisel source: Jouppi stream-buffer prefetcher (4 streams × depth 8, LRU allocation, burst issue through a drain queue) mirroring the C# `StreamPrefetcher` |
| `generated/*.sv` | Committed firtool output — consumers never need a JVM |
| `generate.sh` | Chisel → SystemVerilog (`nix-shell -p scala-cli circt`); rerun after editing the Chisel |
| `rtl_fu_shim.cpp` | Verilator harness for functional units; model-agnostic via `-DRTL_MODEL` |
| `rtl_fpu_shim.cpp` | Verilator harness for flag-reporting FP units (`rtl_execute_flags`: result + fflags + cycles) |
| `rtl_bp_shim.cpp` | Verilator harness for plain branch predictors (`rtl_bp_predict`/`rtl_bp_update`) |
| `rtl_hbp_shim.cpp` | Verilator harness for speculative-history branch predictors (`rtl_hbp_*`: predict/update/spec_update/recover/history/restore) |
| `rtl_rp_shim.cpp` | Verilator harness for replacement policies (`rtl_rp_choose_victim`/`rtl_rp_record_hit`/`rtl_rp_record_install` + geometry query) |
| `rtl_pf_shim.cpp` | Verilator harness for single-target prefetchers (`rtl_pf_access`) |
| `rtl_mpf_shim.cpp` | Verilator harness for multi-degree prefetchers (same `rtl_pf_*` ABI; drains the model's prefetch queue one address per clock) |
| `build.sh <sv> <top> <out.so> [shim]` | Verilates + links the shared library (`nix-shell -p verilator python3` fallback); shim defaults to `rtl_fu_shim.cpp` |

## Functional-unit C ABI

```c
void* rtl_create();                 // construct + reset the verilated model
void  rtl_destroy(void*);
int   rtl_execute(void*, unsigned op, unsigned a, unsigned b,
                  unsigned* result, int* cycles);  // 0 = ok, -1 = model hung
// Flag-reporting FP units (rtl_fpu_shim) export this instead; RtlFfiFunctionalUnit
// detects which export is present:
int   rtl_execute_flags(void*, unsigned op, unsigned a, unsigned b,
                        unsigned* result, unsigned* flags, int* cycles);
```

`cycles` counts clock edges from request acceptance until `io_resp_valid` — the FU
latency the pipeline charges for the instruction.

## Branch-predictor C ABI

```c
void* rtl_bp_create();
void  rtl_bp_destroy(void*);
void  rtl_bp_predict(void*, unsigned long long pc, int* taken, unsigned long long* target);
void  rtl_bp_update(void*, unsigned long long pc, int taken, unsigned long long target);
```

`rtl_bp_predict` is a pure combinational read (no clock edge) — a fetch-stage table
lookup; `rtl_bp_update` drives the update ports for one clock edge — the commit-time
training write.

## Port contracts

A functional unit must expose (a Chisel module with `io.req = Flipped(Decoupled(...))`
and `io.resp = Valid(UInt)` produces exactly this):

```
clock, reset
io_req_ready, io_req_valid, io_req_bits_op, io_req_bits_a, io_req_bits_b
io_resp_valid, io_resp_bits
```

A flag-reporting FP unit replaces the response with a result/flags bundle:

```
io_resp_valid, io_resp_bits_result, io_resp_bits_flags
```

A plain branch predictor must expose:

```
clock, reset
io_predPc, io_predTaken, io_predTarget            (combinational lookup)
io_updValid, io_updPc, io_updTaken, io_updTarget  (applied on one clock edge)
```

A speculative-history branch predictor must expose (direction only — targets live in
the C# wrapper's BTB):

```
clock, reset
io_predPc, io_predTaken                  (combinational lookup)
io_updValid, io_updPc, io_updTaken       (commit-time training, one clock edge)
io_specValid, io_specTaken               (fold predicted direction at fetch, one edge)
io_recoverValid                          (flush: restore committed history, one edge)
io_histOut                               (combinational working-history read = capture)
io_restValid, io_restHist, io_restTaken  (partial squash: restore checkpoint + resolved
                                          direction, one edge)
```

A replacement policy must expose:

```
clock, reset
io_cfgSets, io_cfgWays                            (elaboration-time geometry, constant)
io_hitValid, io_hitSet, io_hitWay                 (hit promotion, one clock edge)
io_instValid, io_instSet, io_instWay              (fill insertion, one clock edge)
io_victimValid, io_victimSet, io_victimWay        (victim combinational; state update on edge)
io_metaSet, io_metaWay, io_metaRrpv               (combinational metadata read)
```

A single-target prefetcher must expose:

```
clock, reset
io_cfgTableSize                                   (elaboration-time size, constant)
io_accValid, io_accPc, io_accAddr, io_accHit      (one demand access)
io_prefValid, io_prefAddr                         (combinational prefetch decision;
                                                   table update commits on the edge)
```

A multi-degree prefetcher must expose:

```
clock, reset
io_cfgTableSize                                   (elaboration-time size, constant)
io_accValid, io_accPc, io_accAddr, io_accHit      (one demand access, one clock edge;
                                                   loads the drain queue)
io_drainValid, io_drainAddr, io_drainPop          (queue head; drainPop pops one entry
                                                   per clock edge)
```

## Usage

From the CLI (flags below) or from a `.csx`/`.fsx` architecture script — the script hosts
pre-import `Mechanism.RtlFu`, `Orrery.Cache`, and `RiscV32.Execute`, so scripts can attach
RTL units directly: `BranchPredictorFactory` on a pipeline spec, `RtlBackedExecutor` around
the mechanism's executor, and `PolicyFactory`/`PrefetcherFactory` on a `CacheLevelSpec`.
See `scripts/example-rtl.csx` for a machine with all four surfaces substituted.

```bash
native/RtlFu/build.sh native/RtlFu/generated/DivUnit.sv DivUnit /tmp/rtl_div.so
native/RtlFu/build.sh native/RtlFu/generated/MulUnit.sv MulUnit /tmp/rtl_mul.so
native/RtlFu/build.sh native/RtlFu/generated/FDivSqrtUnit.sv FDivSqrtUnit /tmp/rtl_fdiv.so rtl_fpu_shim.cpp
native/RtlFu/build.sh native/RtlFu/generated/GshareBp.sv GshareBp /tmp/rtl_gshare.so rtl_bp_shim.cpp
native/RtlFu/build.sh native/RtlFu/generated/LTageBp.sv LTageBp /tmp/rtl_ltage.so rtl_hbp_shim.cpp
native/RtlFu/build.sh native/RtlFu/generated/SrripRp.sv SrripRp /tmp/rtl_srrip.so rtl_rp_shim.cpp
native/RtlFu/build.sh native/RtlFu/generated/DrripRp.sv DrripRp /tmp/rtl_drrip.so rtl_rp_shim.cpp
native/RtlFu/build.sh native/RtlFu/generated/StridePf.sv StridePf /tmp/rtl_stride.so rtl_pf_shim.cpp
native/RtlFu/build.sh native/RtlFu/generated/StreamPf.sv StreamPf /tmp/rtl_stream.so rtl_mpf_shim.cpp
dotnet run --project src/Apps/Runner -- prog.elf \
    --rtl-div-lib /tmp/rtl_div.so --rtl-bp-lib /tmp/rtl_gshare.so \
    --rtl-rp-lib /tmp/rtl_srrip.so --rtl-pf-lib /tmp/rtl_stride.so
```

Thread-safety: one verilated model per `RtlFfiFunctionalUnit`, one unit per
pipeline/thread — parallel config sweeps construct one mechanism (and thus one model)
per worker thread.
