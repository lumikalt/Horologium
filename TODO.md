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
- [x] Micro-fusion: fuse load+ALU or store-address+store-data into a single dispatch slot.
  Analyzed and scoped honestly before building: load+ALU fusion (`RvMacroFuser`'s second
  pattern, `RvFusedLoadAlu`) on `OooTrain`/`CprTrain`/`SuperscalarTrain` collapses the
  load-to-use scheduling round-trip for the `lw t0,...; alu t0,t0,...` read-modify idiom —
  occupancy/critical-path relief like macro-fusion, not a throughput win, and structurally
  weaker than macro-fusion's own effect since it can't legitimately collapse execute latency
  the way compare+branch does. Store-address+store-data turned out to be a dead end as
  literal fusion — stores are already one indivisible unit, so fusing a split back to one
  slot just recreates today's model with zero effect. The real, separately-scoped feature
  actually built is a store address/data *split*: `SqEntry.DataKnown` alongside
  `AddressKnown`, plus `StepEarlyStoreAddressResolution`/`IExecutor.TryComputeStoreAddress`
  letting a store's address release address-only dependents (`HasUnresolvedPrecedingStore`/
  `ConservativeLoads`, Store Sets) as soon as it's known, independent of a slower data
  operand — a genuine, measured cycle-count win on `OooTrain` when a store's address is
  ready before its data (`enableEarlyStoreAddress`, default off). Porting to `CprTrain`
  caught a real bug: its Store Sets release check used `AddressKnown` as a "store fully
  resolved" proxy, which the split invalidates — fixed to check `DataKnown` instead. A
  post-implementation review caught the identical bug already latent on `OooTrain`'s own
  `StoreSetStallLoad` (same `AddressKnown`-as-"resolved" proxy) — confirmed live with a
  same-address load under `enableStoreSets` + `enableEarlyStoreAddress` (a guaranteed
  memory-order violation on every loop iteration instead of a clean stall) and fixed the
  same way.

## Cache Prefetching

- [x] Bingo: spatial prefetcher associating footprints with multiple event signatures in a single table. —
  Bakhshalipour et al., HPCA 2019. `BingoPrefetcher` reuses SMS's Active Generation
  Table (32-entry filter / 64-entry accumulation FIFO, same 2 KB region and footprint
  bitmask) unchanged, replacing SMS's single-event Pattern History Table with a
  TAGE-like dual-event history table: one physical 16K-entry/16-way table, indexed
  once by the short event (PC+Offset), looked up twice — first a precise match
  against the long event (PC+exact trigger address), falling back on a miss to an
  aggregate of every entry matching just the short event, combined per the paper's
  literal ≥20%-of-matching-entries threshold. Modeling simplification: entries store
  `Pc`/`RegionBase`/`Offset` as literal fields rather than a bit-packed tag+index
  split — behaviorally identical (the index is still computed from `(Pc, Offset)`
  alone) but avoids a hardware bit-decomposition trick irrelevant to a functional
  model. Like SMS/STeMS (and unlike PPF), relies on AGT capacity pressure rather
  than a real per-line eviction callback to terminate a page generation — a
  deliberate consistency choice, since Bingo's own contribution is the history-table
  lookup scheme, not the AGT/termination model it inherits from SMS.

## Security

- [x] Randomized cache side-channel defense: CEASER encrypted-address remapping and CEASER-S
  P-way partitioning. — Qureshi, MICRO 2018 / ISCA 2019. New `CeaserCache : IMemory` (L2/L3 only,
  same restriction as BΔI), a from-scratch class following `BdiCache`'s "structurally different
  cache variant" precedent rather than a `SetAssociativeCache` fork — `SetAssociativeCache`'s
  `(tag, set) → address` concatenation reconstruction, used at 8+ call sites, is incompatible with
  a keyed/randomized index. Modeling simplification: stores the plaintext line address directly as
  the tag instead of the paper's encrypted-line-address (ELA) tag — behaviorally equivalent (same
  hit/miss/eviction/remap behavior; nothing about tag storage format is observable) and eliminates
  the need for the paper's invertible 4-stage Feistel cipher entirely, since nothing ever needs to
  be decrypted. The index function is accordingly a keyed avalanche hash (murmur3's `fmix64`
  finalizer over `address XOR key`) rather than a literal S-box/P-box block cipher, and no per-line
  EpochID bit is needed (full-address tag comparison is never ambiguous). CEASER-S is the same
  class with a `Partitions` parameter (P=1 is plain CEASER, matching the paper's own "CEASER-S1 ==
  CEASER"), each partition with independent keys/SPtr/ACtr and its own way-range; a miss installs
  into a uniformly-random partition. This does not model or claim cryptographic hardness against a
  real attacker — only the mapping-randomization/periodic-remap behavior that affects miss rate,
  latency, and data placement.
- [ ] ScatterCache: way-separate skewed-associative randomized indexing (an Index Derivation
  Function maps address+key to a distinct index per way-array) with SDID-based security-domain
  isolation. — Werner et al., USENIX Security 2019. Deferred from the CEASER/CEASER-S pass above:
  structurally distinct from CEASER's shared-index-per-set model (needs `IMemory.SetRequestPc`-style
  SDID plumbing, no per-set replacement policy in the usual sense), large enough to warrant its own
  pass. Purnal & Verbauwhede's follow-up eviction-set-profiling attack (arXiv 2019) is not a
  mechanism to implement — note it as a documentation caveat when ScatterCache lands (their
  profiling technique is faster than the original paper's own threat model assumed).

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
