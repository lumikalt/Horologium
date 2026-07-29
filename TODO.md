# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

- [ ] ~~Suspended-stream data exchange: `so.v.vload`/`so.v.vstor`~~ — **hold**: dissertation gives one sentence
  with no operand semantics; Spike has no instruction files for it. Skip until the spec is clarified.
- [ ] `vec_cv` (661 lines, `so.v.cv` conversions) has an **empty `RUN_SIMPLE`** (`void core(DataType
  src[SIZE]){}`) — there is no independent oracle to verify against at all. Matches the already-recorded
  `SPEC_NOTES.md` finding that `so.v.cv` correctness was "genuinely underspecified, never given
  attention" by the author, who also confirmed (2026-07-27) there's no forward plan for it — UVE compute
  instructions are being phased out in favor of RISC-V V's. He's fine with implementing it anyway "to
  validate", but that isn't a reference to check against, so the no-oracle hold stands.

## µops

- [ ] µop cache on `FiveStageTrain`, exploiting its real Fetch/Decode stage split (`Stages/Fetch.cs`
  → `Stages/Decode.cs`, a cycle apart) to skip Decode's stage-cycle on a hit — the one train where
  this could be a genuine, paper-faithful timing win (mirrors the paper's own Figure 1: stream-mode
  has fewer pipe stages than build-mode) rather than the ~0-cycle-impact story everywhere else.
  **Blocked for now**: user wants to confirm first whether the actually-intended follow-up is
  something bigger that also touches `OooTrain`, before scoping this.
- [ ] Loop stream detector: detect short loops and replay µops from a small buffer, bypassing fetch
  and decode.
- [ ] µop decomposition for complex instructions: atomics, vector ops, and CSR accesses emit
  multi-µop sequences through `ITooth`.

## Front-End

- [ ] Boomerang / Shotgun: metadata-free front-end prefetching that unifies BTB prefill and I-cache
  prefetch under the branch predictor. — Kumar et al., HPCA 2017 / ASPLOS 2018
- [ ] EIP (entangling instruction prefetcher): links the instruction that gives timely coverage
  ("entangler") to the miss it hides. — Ros & Jimborean, ISCA 2021

## Cache Prefetching

- [ ] ISB (irregular stream buffer): linearizes PC-localized correlated irregular streams into a
  structural address space for temporal prefetching. — Jain & Lin, MICRO 2013
- [ ] Temporal memory streaming: record long miss sequences in off-chip metadata and replay them on
  a matching miss (STMS; Domino). — Wenisch et al., ISCA 2005 / HPCA 2009; Bakhshalipour et al.,
  HPCA 2018
- [ ] Hermes: off-chip load prediction — a perceptron predicts which loads will miss the entire
  hierarchy and starts the DRAM access early, in parallel with cache lookup. — Bera et al., MICRO
  2022

## Memory System

- [ ] MMU translation research: page-walk caches / translation caching ("skip, don't walk") and TLB
  prefetching; builds on the existing Sv32 walker. — Barr, Cox & Rixner, ISCA 2010; Kandiraju &
  Sivasubramaniam, ISCA 2002

## Analysis

- [ ] Cache-Aware Roofline Model (CARM) output: compute per-cache-level bandwidth and
  arithmetic-intensity ceilings from simulation statistics and render a roofline plot. — Ilic,
  Pratas & Sousa, IEEE CAL 2013; Williams, Waterman & Patterson, CACM 2009 (base Roofline)
- [ ] Mansard Roofline extension: split each cache-level roof into a read roof and a write roof for
  more accurate mixed-access characterization — builds on the CARM item above. — Marques, Ilic &
  Sousa, ACM TOMPECS 2021

## Benchmarks

Measured feasibility (Release, single thread): ~1M instr/s functional (single-cycle), ~0.1M cycles/s
detailed (OoO). SPEC-class ref inputs (~10¹² instructions) are therefore only reachable via sampling;
free embedded suites are runnable in full today.

- [ ] SPEC CPU2006/2017 harness (user-supplied install; SPEC is licensed and non-redistributable):
  RV64 + syscall emulation + SimPoint sampling — BBV profiling, clustering, checkpointed 10M-instruction
  intervals with warmup. — Sherwood et al., ASPLOS 2002 (SimPoint). All supporting infrastructure is
  built and validated against real, genuinely compiled RV64 binaries: RV64 syscall wiring, a psABI
  initial-stack builder, `LinuxSyscallEmulator` (real file I/O/mmap/clock_gettime/getrandom/writev),
  checkpoint/SimPoint sampling glue (reused once per `--sweep` rather than per config, and covering full
  syscall-emulator state — brk/mmap cursors, fd table, stdin position — so a syscall inside a SimPoint
  interval's startup-/shutdown-edge phases measures correctly too), `--bench-config` batch mode, and
  `--simpoint-argv` CLI wiring for real ELFs. **Blocked on the SPEC license itself, not on remaining
  code**: missing a license currently.
