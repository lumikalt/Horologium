# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

- [ ] ~~Suspended-stream data exchange: `so.v.vload`/`so.v.vstor`~~ — **hold**: dissertation gives one sentence
  with no operand semantics; Spike has no instruction files for it. Skip until the spec is clarified.

## Branch Prediction

- [x] Extend CBP2025/CBP-NG integration to OoOE: `OooeTrain` can have many outstanding unresolved predictions,
  which clobbers harcom predictors' per-block register state as above. Needs either per-predictor block-state
  snapshot/restore (not generic — differs per submission) or another reconciliation strategy.

## Analysis

- [x] ChampSim trace import: parse ChampSim's binary trace format (`input_instr`, the input to CBP branch-predictor
  and CRC cache-replacement-championship submissions) and replay it through Horologium's `IBranchPredictor` and
  `IReplacementPolicy` plug-in surfaces, so those implementations can be validated against real trace corpuses
  instead of only Horologium-generated workloads. https://github.com/ChampSim/ChampSim

## Performance

- [x] Struct latches: `IfIdLatch`/`IdExLatch`/`ExMemLatch`/`MemWbLatch` (`PipelineRegisters.cs`) are now
  `readonly record struct`, one fewer heap allocation per stage per instruction. Behaviorally transparent
  (all 3565 tests pass) — not separately measured against the perf playbook in `docs/references.md`-adjacent
  notes; GC pressure was previously ruled out as a bottleneck, so treat this as a tidiness win, not a proven
  speedup, until measured.
- [x] Fewer interface hops: `ExecuteStage`'s forward-then-restore dance through `IRegisterFile` (`Execute.cs`)
  is replaced by a reused `ForwardingOverlay : IRegisterFile` that shadows up to 3 register reads for one
  `IExecutor.Execute` call. The real regfile is never mutated for forwarding bookkeeping — no more
  save/write/execute/restore. Wrapping `IArchState` itself was ruled out: `Rv32Executor` downcasts `IArchState`
  to `Rv32ArchState` (CSR/vector/UVE access), so any decorator around the whole state breaks that cast. Instead
  `IArchState.IntegerRegisters` was widened to a settable property, so `_state` keeps its real identity and only
  its register-file reference is swapped for the duration of the call (restored in a `finally`, closing a
  latent exception-safety gap the old restore-on-the-happy-path code had). All 7 ISA `ArchState`s updated for
  interface compliance; only RV32/64 actually rely on the swap being correct (verified: `VectorTests` pass under
  `FiveStageTrain`), the other 6 ISAs never run through this path.

## Benchmarks

Measured feasibility (Release, single thread): ~1M instr/s functional (single-cycle), ~0.1M cycles/s
detailed (OoO). SPEC-class ref inputs (~10¹² instructions) are therefore only reachable via sampling;
free embedded suites are runnable in full today.

- [ ] SPEC CPU2006/2017 harness (user-supplied install; SPEC is licensed and non-redistributable):
  RV64 + syscall emulation + SimPoint sampling — BBV profiling, clustering, checkpointed 10M-instruction
  intervals with warmup. — Sherwood et al., ASPLOS 2002 (SimPoint)

## Co-simulation

- [x] RTL functional-unit substitution: swap one pipeline FU (e.g., a custom ALU or accelerator) for cycle-accurate
  RTL via Verilator, so the surrounding pipeline drives real hardware instead of the C# functional/latency model
  for that unit — validates a custom-unit design against the rest of the system before tapeout/FPGA.
  - [x] Verilate a first target FU (Chisel RV32M divider, `native/RtlFu`) into a native shim exposing a C ABI,
    following `native/CbpShim`'s layout and build.
  - [x] P/Invoke wrapper mirroring `CbpFfiPredictor`: load the shim, marshal operands in, step the model's clock,
    read back result + cycle count.
  - [x] Hook the wrapper into `IExecutor` (decorator + ISA-side op selector), replacing the C# functional result
    with the RTL model's output.
  - [x] Dynamic latency: per-instruction cycle count from the RTL model (`ExecuteResult.LatencyOverride`)
    overrides the static `FuLatencyConfig` entry in the ooo/cpr pipelines.
  - [x] Co-sim validation harness: same instruction stream through the C# functional model and the RTL-backed
    model, diff results and latencies (same pattern as the CBP FFI predictor tests).
  - [x] CLI wiring: `--rtl-div-lib <path>` in Runner.
- [x] RTL branch-predictor substitution: verilated Chisel predictor behind `IBranchPredictor`
  (combinational predict, clocked commit-time update); first unit is a gshare mirroring the C#
  `GsharePredictor` bit-for-bit for differential validation. Sweep-config type `rtl_bp_plugin`,
  `--rtl-bp-lib` flag, ChampSim-trace replay support.
- [x] RTL replacement-policy substitution: verilated Chisel policy behind `IReplacementPolicy`
  (combinational victim + clocked aging/hit/install; elaboration-time geometry validated per cache
  level); first unit is an SRRIP mirroring the C# `SrripPolicy` bit-for-bit. Sweep-config field
  `rtl_cache_policy_lib`, `--rtl-rp-lib` flag, ChampSim-trace replay support.
- [x] RTL prefetcher substitution: verilated Chisel prefetcher behind `IPrefetcher` (combinational
  prefetch decision, clocked table update); first unit is an RPT stride prefetcher mirroring the C#
  `StridePrefetcher` bit-for-bit. Sweep-config field `rtl_prefetcher_lib`, `--rtl-pf-lib` flag.
- [x] RTL script-host wiring: `.csx`/`.fsx` architecture scripts can attach all four RTL unit kinds —
  `CacheLevelSpec` gained per-level `PolicyFactory`/`PrefetcherFactory`, and both script hosts pre-import
  the RTL namespaces (`scripts/example-rtl.csx` shows a fully RTL-substituted machine).
- [x] RTL TAGE-class predictor: Chisel L-TAGE (bimodal + 4 tagged tables with folded geometric histories +
  loop predictor) mirroring the C# `LTagePredictor` bit-for-bit, behind a new speculative-history shim ABI
  (`rtl_hbp_shim.cpp`: spec-update/recover/capture/restore ports; the checkpoint is the working-GHR value).
  `RtlBranchPredictorLoader` auto-detects the shim ABI so `--rtl-bp-lib`/`rtl_bp_plugin` serve both kinds.
- [x] RTL set-dueling DRRIP: Chisel DRRIP (SDM leader sets, 10-bit PSEL, 1/32 bimodal BRRIP inserts)
  mirroring the C# `DrripPolicy` bit-for-bit through the unchanged `rtl_rp_shim` ABI — global cross-set
  state (PSEL duel, shared bimodal counter) inside the model.
- [x] RTL multi-degree stream prefetcher: Chisel Jouppi stream buffers (4 streams × depth 8, LRU
  allocation, burst issue) mirroring the C# `StreamPrefetcher` bit-for-bit; new drain-queue port
  contract (`rtl_mpf_shim.cpp`) carries multiple targets per access over the unchanged `rtl_pf_*` C ABI.
- [x] RTL pipelined multiplier: Chisel 3-stage fully pipelined 33×33 RV32M multiplier
  (MUL/MULH/MULHSU/MULHU) under the unchanged FU port contract (`req.ready` constantly high); constant
  3-cycle latency matches the `MulDivLatency` default, so RTL-mul runs are cycle-identical to the static
  model. `--rtl-mul-lib` composes with `--rtl-div-lib` for the whole M extension.
- [ ] RTL FP div/sqrt unit: Chisel iterative FDIV/FSQRT behind `RtlBackedExecutor` — needs IEEE 754
  rounding/flags parity with the C# soft-float model to pass a differential sweep.

## Face

- [ ] Browser assembly support: pure C# RV32 two-pass assembler, so the Assemble command works in FaceWeb without a GAS
  subprocess.
  - Also a C compiler…
- [ ] Improve the cache and virtual addressing visualization. Make it more like Ripes.
- [ ] Vector operation visualization.
  - Need to think of how this should be done.
- [ ] gem5-style architecture configurator: UI surface for the scripting host and pipeline builder — edit `.csx` scripts
  in-app and hot-reload the resulting pipeline, cache hierarchy, branch predictor, and FU configuration without
  restarting. (Phase 5 UI of the architecture builder: AvaloniaEdit code editor, hot-reload on file change via
  `FileSystemWatcher`, workload selector, and live cache/TLB stat display.)
