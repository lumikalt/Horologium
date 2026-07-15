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

## Face

- [ ] Browser assembly support: pure C# RV32 two-pass assembler, so the Assemble command works in FaceWeb without a GAS
  subprocess.
  - Also a C compiler…
- [ ] Improve the cache and virtual addressing visualization. Make it more like Ripes.
- [ ] Vector operation visualization.
  - I've got to think of how this should be done.
- [ ] gem5-style architecture configurator: UI surface for the scripting host and pipeline builder — edit `.csx` scripts
  in-app and hot-reload the resulting pipeline, cache hierarchy, branch predictor, and FU configuration without
  restarting. (Phase 5 UI of the architecture builder: AvaloniaEdit code editor, hot-reload on file change via
  `FileSystemWatcher`, workload selector, and live cache/TLB stat display.)
