# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

- [ ] ~~Suspended-stream data exchange: `so.v.vload`/`so.v.vstor`~~ — **hold**: dissertation gives one sentence
  with no operand semantics; Spike has no instruction files for it. Skip until the spec is clarified.

## Branch Prediction

- [ ] Extend CBP2025/CBP-NG integration to OoOE: `OooeTrain` can have many outstanding unresolved predictions,
  which clobbers harcom predictors' per-block register state as above. Needs either per-predictor block-state
  snapshot/restore (not generic — differs per submission) or another reconciliation strategy.

## Out-of-Order Execution

- [x] Checkpoint processing and recovery (CPR) + continual flow pipelines (CFP): ROB-free large-window paradigm with
  checkpoint-based recovery and slice-out of miss-dependent instructions. — Akkary et al., MICRO 2003; Srinivasan et
  al., ASPLOS 2004
- [x] SMT fetch policies: ICOUNT and round-robin variants for per-hart fetch/issue arbitration in the SMT train. —
  Tullsen et al., ISCA 1996
- [x] Decoupled access-execute (DAE): the classic access/execute processor split — the architectural ancestor of
  UVE-style streaming. — Smith, ISCA 1982
- [ ] DAE precise exceptions: `DaeTrain` currently halts the simulation on a trapping Access/Execute-lane
  instruction (e.g., a misaligned load) rather than recovering, since the other lane may already be ahead in
  program order with no rollback path. Needs a checkpoint/rollback scheme (or a narrower one restricted to the
  trapping lane) before faulting workloads can run to completion under DAE.

## Performance

- [ ] Memoization of instructions and decodings?
- [ ] Structural stage-model rework for in-order trains: struct latches, fewer interface hops.

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
