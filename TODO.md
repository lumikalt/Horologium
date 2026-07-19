# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

- [ ] ~~Suspended-stream data exchange: `so.v.vload`/`so.v.vstor`~~ — **hold**: dissertation gives one sentence
  with no operand semantics; Spike has no instruction files for it. Skip until the spec is clarified.

## Performance

- [x] Value prediction: predict ALU/load results to break dependence chains; commit only if the prediction is correct. —
  Lipasti & Shen, MICRO 1996 (LVPT); Perais & Seznec, HPCA 2014 (VTAGE + FPC confidence)
- [x] EOLE: late in-order ALU execution atop value prediction, to shrink the OoO issue width without losing
  performance. — Perais & Seznec, ISCA 2014 (EOLE)
- [x] Early Execution: the front-end half of EOLE — single-cycle ALU ops with immediate/predicted operands
  execute in-order, in parallel with Rename, bypassing the OoO scheduler entirely. — Perais & Seznec, ISCA 2014
  (EOLE)
- [ ] TMA/CPI-stack accounting for EOLE Early/Late Execution: instructions retired via either are dispatched
  but never issued, so the `_td*` slot counters and CPI-stack classification (which only observe
  `StepIssue`/`StepDispatch`) misreport Retiring/backend slots when `enableEoleLateExec`/`enableEoleEarlyExec`
  and TMA/CPI accounting are both enabled. Currently unmodeled — no test exercises the combination.
- [ ] Widen value-prediction eligibility beyond scalar ALU/load (FP, MulDiv, CSR-writing ops) and add a
  computational (stride-family) predictor component to hybridize with VTAGE.

## Cache Prefetching

- [x] Best-Offset Prefetcher (BOP): offset-selection tournament with timeliness scoring; the DPC-2 winner. — Michaud,
  HPCA 2016
- [x] Signature Path Prefetcher (SPP): compressed access-pattern signatures with path-confidence lookahead. — Kim et
  al., MICRO 2016
- [x] Perceptron Prefetch Filter (PPF): a learned filter on top of SPP that suppresses low-value prefetches by
  perceptron vote over signature/confidence/delta features. — Bhatia et al., ISCA 2019

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
  - Need to think of how this should be done.
- [ ] gem5-style architecture configurator: UI surface for the scripting host and pipeline builder — edit `.csx` scripts
  in-app and hot-reload the resulting pipeline, cache hierarchy, branch predictor, and FU configuration without
  restarting. (Phase 5 UI of the architecture builder: AvaloniaEdit code editor, hot-reload on file change via
  `FileSystemWatcher`, workload selector, and live cache/TLB stat display.)
