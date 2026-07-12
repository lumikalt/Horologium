# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

1D and multi-dimensional streams.

- [x] 1D load/store stream setup: `ss.sta.ld.w` + `ss.end` / `ss.sta.st.w` + `ss.end`
- [x] Multi-dim stream config sequence: `ss.sta.{ld,st}.w` → `ss.app` → `ss.end`
- [x] `ss.cfg.vec` instruction decode: `ss.sta.ld.*_v` variants (funct2=0, rs3 bit[3]=1). rs3 bits[2:0]=7
  → innermost dim (VecCfgDim=-1); bits[2:0]=0..6 → explicit dim. rs3 bit[4]=1 → masked variant (decoded
  but mask ignored; requires SO_P predicate registers). `IsVectorMode`+`VecCfgDim` propagate through
  PendingStreamConfig → StreamDescriptor → StreamingEngine.StreamState.
- [x] `ss.cfg.vec` effect: vector-width element delivery from load streams — bulk deliver VL elements per
  `Consume()` cycle (currently always 1); engine fills up to VL elements in vector mode, stopping at the
  vecCfgDim boundary.
- [x] Scalar broadcast to u-reg: `so.v.dp.w`
- [x] FP arithmetic on stream elements: add/sub/mul/div/mac (`so.a.fp.*`)
- [x] Stream loop-control branches: `sb.nc` / `sb.ndc.(dim)`
- [x] Branch-if-complete variants: `sb.c` / `sb.dc.(dim)` — opposite polarity of the existing nc/ndc branches
- [x] Integer (USG/SG) arithmetic variants of add/sub/mul/div/mac on stream elements
- [x] Additional arithmetic ops: `abs`, element-wise `min`/`max`, `inc`/`dec`; `sqrt` (FP)
- [x] Logic ops: `and`, `or`, `xor`, `not`, `nand`, `nor`; vector-vector shifts `sll`/`srl`/`sra` and scalar-register
  forms `ssll`/`ssrl`/`ssra`
- [x] Reduction ops (per-element into ud): `adde`/`adde.acc` (overwrite/accumulate), `mine`/`maxe` (running min/max)
- [x] Scalar-write reductions: `sadde`/`fsadde` (→ integer/FP scalar reg)
- [x] Non-word element widths: byte (`.b`), halfword (`.h`), doubleword (`.d`) for `ss.ld` and `ss.st`
- [x] `mvvs`/`mvsv.(width)` (vector↔scalar): `so.v.mvvs` writes first element of ud register into integer rd;
  `so.v.mvsv.(b/h/w/d)` writes integer rs1 bits (width-masked) into ud register as scalar; `so.v.dp.(b/h/d)` width
  variants of the existing scalar-broadcast op
- [x] Vector register manipulation: `mv`/`mvt` (move/transpose) — `so.v.mv` / `so.v.mvt` gated by predicate register
- [x] SO_P predicate register file: 16 registers (VLEN=128 → 16 bytes each); register 0 all-ones; `so.p.{zero,one,vr,not,mv,mvt}` simple ops with governing predicate + zeroing mode; `so.p.{ge,eq,lt}.{us,fp,sg}` element-wise comparisons (merging on inactive); `_z` comparison variants tag output register with Zeroing mode (Spike invariant: tag only, no element-level difference)
- [ ] ~~Explicit vector load/store: `ld.(width)` / `ld.(width).s` and `st` / `st.s` (non-stream bulk memory ops)~~
  — dropped: UVE2 removes non-streaming vector memory ops (replicable with linear streams)
- [x] Static dimension modifiers: `ss.app.mod` — attach a `{Target, Behavior, Displacement, Size}` modifier to a
  descriptor so the inner loop count/stride updates automatically each outer-loop iteration (enables triangular patterns
  without per-row reconfiguration)
- [x] Static modifier E-field (Size) enforcement: `StreamModifier.MaxApplications` (E=0 means unlimited); per-modifier
  application counts tracked separately on fetch and consume sides in `StreamingEngine`. Encoding: funct2=3,
  funct3=dimIndex (0–7), rs1=E register (x0 → unlimited), rs2=behavior<<2|spikeTarget, rs3=displacement register.
  Spike comments out E enforcement entirely; this encoding is Horologium-specific.
- [x] Static modifier Offset and Stride targets: implement `Target=Offset` and `Target=Stride` mutations in
  `StreamingEngine.ApplyModifiers`; currently only `Target=Size` is handled and the other two are decoded but no-op
- [x] UVE encoding alignment: decoder, executor, and all tests now match AnaBSF/riscv-isa-sim (uve branch, commit
  a048271) — `ss.sta.{ld,st}.w` funct3, `ss.app`/`ss.end` funct2, `ss.app.mod` Spike target encoding, `so.v.dp.w`
  funct7/funct3, `so.a.fp.*` (funct7>>3, funct3) table, UVE B-type branch immediate layout
- [x] Offset register (rs1) in `ss.app`/`ss.end`: rs1*ew added to stream base address; accumulated across all ss.app
  instructions in a config sequence and applied at ss.end time
- [x] Indirect dimension modifiers: `ss.app.ind` / `ss.end.ind` — attach a `{Target, Behavior, StreamPointer}` modifier
  so a live stream drives the offset of another (gather/indirect access; confirmed UVE1 in ISCA 2021 paper)
- [x] Stream suspend/resume/stop: `ss.suspend`, `ss.resume`, `ss.stop` — explicit stream lifecycle control for context
  switching and early termination
- [x] Vector-length control: `ss.getvl` / `ss.setvl` — read and configure the active vector length for narrower-VL
  emulation and VL-aligned dimension padding
- [x] Dimension configuration order alignment with Spike: `ss.app`/`ss.end` build descriptors outermost-first
  (`ss.end` adds the innermost dimension), matching Spike's deque order; `ss.app.mod` funct3, `ss.app.ind`
  rs3, and explicit `ss.cfg.vec` dim indices are all outermost-first and remapped to the engine's
  innermost-first order at `ss.end`.
- [ ] ~~Cache-level stream routing: `so.cfg.memx`~~ — superseded: UVE2 folds this into the stream header
  `mem` field (`ss.sta.mem[l]`, bits [23:22])
- [ ] ~~FP register source for scalar broadcast: `so.v.dup.fp.w ud, fs1`~~ — dropped: no such instruction in UVE2

### UVE2 (target spec: Fernandes, "A functional validation framework for the UVE", MSc dissertation, U. Coimbra 2025)

The AnaBSF/riscv-isa-sim uve branch is the UVE2 reference implementation; Horologium's existing encodings
(config order, so.b.ndc, SO_P including pm/_z bits, so.a.* layout, ss.sta header inds/vec/vdim bits) already match.
Remaining delta, in rough dependency order:

- [x] `ss.app.mod` re-encoding and semantics: UVE2 encodes static modifiers as tc=APP + funct3=MOD with literal
  b/ta fields and an explicit 3-bit `tdim` target dimension (bits [17:15]); the trigger dimension is positional (the
  most recently appended dimension), decoupled from the target; target fields reset to configured values when the
  trigger dimension itself wraps; Offset displacements and indirect offset values are element-scaled. Replaces the
  Horologium-specific funct2=3 encoding; the E/size field is removed in UVE2 (never used in Spike).
- [x] `ss.ld.*` / `ss.st.*` 1D shorthand: never existed as a separate decode path (README naming only, now
  corrected); tc=11 rejects as an illegal instruction, matching UVE2's reserved encoding
- [x] Stream header `pm` (bit 31, merging-predication flag — was mis-documented as "masked variant") and
  `mem` (bits [23:22], cache-level) field decode; recorded on the header instruction but not yet consumed
  (pm → vector-width predication model; mem → cache routing)
- [x] Vector-width execution model: u-registers hold VLEN-wide element vectors (element width from stream config);
  per-register scalar/vector mode (scalar default, `vec` header flag, mode-transition rules per instruction class);
  valid-element counts; implicit predication on lanes beyond the valid count — zeroing (default) or merging (pm)
  per stream register. This is the core semantic chunk of UVE2 and consumes the existing PredZeroing tags.
- [x] Explicit predicate operand in compute ops: ps3 field (bits [27:25]) on so.a.* — decoded from funct7&7,
  stored in Ps3 field of all so.a.* records; UveWriteResult gates per-lane writes with predicate register;
  inactive lanes always merge (keep existing dest value) per Spike semantics; zeroing flag still governs lanes
  beyond vLen; sadde/fsadde skip inactive elements from the accumulation sum
- [x] Scatter-gather dynamic modifiers (`ss.app.sgi` / `ss.end.sgi`): applied per element rather
  than per dimension wrap, offset target only — enables vectorial gather (SpMV-2 pattern)
- [x] Predicate width conversion `so.p.cv.<dw>.<sw>` (dual width fields) and vector element conversion
  `so.v.cv.<fps>.<wth>` (narrowing/widening; lost-lane behaviour still open in the spec)
- [ ] Suspended-stream data exchange: `so.v.vload` / `so.v.vstor` (load/store vector data to/from suspended streams) — **hold**: the dissertation gives one sentence with no operand semantics; Spike has only MATCH/MASK entries in encoding.h and no instruction files; currently decoded as illegal. Skip until the spec is clarified.
- [x] ISA-level stride operands: switch from byte counts to element counts to match Spike and the RTL (`ss.app` and
  `ss.end` now multiply the register value by element width; stride modifier displacements for Stride target scaled
  likewise) — binary portability with Spike/RTL restored; all pipeline tests updated

## RISC-V

- [x] RV64 completion:
  - [x] ELF64 loader for running RV64 binaries.
  - [x] Sv39 page-table walker for RV64 virtual memory.
  - [x] RV64 M extension.
  - [x] RV64 F/D extension.
- [ ] riscv64-embedded cross-toolchain in the devshell (`flake.nix` only exposes `riscv32-embedded`) — RV64
  ELF loader and end-to-end tests currently use hand-crafted byte buffers rather than real compiled binaries.
- [x] RV64A: AMO*.D / LR.D / SC.D (opcode 0x2F, funct3=0x3) — the base RV32 AMO decoder only handles
  funct3=0x2 (word) and the Zabha .b/.h forms; doubleword atomics are entirely undecoded on RV64.
- [x] RV64C: quadrant reassignments — C.LD/C.SD replace C.FLW/C.FSW (quadrant 0, funct3 3/7),
  C.ADDIW replaces C.JAL (quadrant 1, funct3 1), C.LDSP/C.SDSP replace C.FLWSP/C.FSWSP
  (quadrant 2, funct3 3/7).
- [x] RV64 Zbb/Zbs immediate-form ops unreachable: CLZ/CTZ/CPOP/SEXT.B/SEXT.H/BSETI/BCLRI/BINVI/RORI/
  REV8/ORC.B/BEXTI all decode through OP-IMM funct3=1/5, which Rv64Decoder's 6-bit-shamt override
  (added for SLLI/SRLI/SRAI) intercepts and rejects with IllegalInstructionException for any encoding
  that isn't a plain shift.
- [x] RV64-only Zba ops: ADD.UW, SH1ADD.UW/SH2ADD.UW/SH3ADD.UW, SLLI.UW (OP-32/OP-IMM-32 with a
  zero-extended-word left operand) — not implemented; these have no RV32 counterpart to inherit from.
- [x] RV64 Zbb/Zbs register-form ops unreachable/incorrect: ROL/ROR/BSET/BCLR/BINV/BEXT (R-type,
  register-indexed shift/bit-manipulation) have the same 32-bit-truncation bug as the immediate forms
  above but were out of scope for that fix — no Rv64Decoder/Rv64Executor overrides exist for them.
- [ ] Zfh / Zfhmin: half-precision FP.
- [x] Zcmop: compressed may-be-operations (c.mop.N, N odd 1–15) — already implemented (decoder, executor,
  disassembler) and verified commit-for-commit against Spike on all three trains; the TODO entry was stale.
- [ ] Zabha+Zacas narrower variants: amocas.b / amocas.h; amocas.d for RV32.

## Analysis

- [x] gem5 ROI instrumentation for treesum: wire `setStats(1)`/`setStats(0)` markers into the gem5 SE
  simulation so gem5 measures the same kernel interval as Horologium's `SetStatsObserver` and the IPC
  comparison is apples-to-apples.
- [x] treesum bypass=0 D-cache regression: re-measured on current trunk (l_tage, ROB=30, IQ=5×8,
  L1 16KB, store buffer=8) — switching bypass=1→0 now *decreases* both dispatch-stall cycles
  (1099→369) and D-cache misses (3→0), with `icache_hits` also falling (27938→25518), consistent
  with faster forwarding resolving branches earlier and fetching less wrong-path work. The
  originally observed regression doesn't reproduce; it predates the store-sets and execute-time
  branch-resolution fixes since landed. No further action.
- [x] JSON-format limitations: no PC/opcode, FP register numbering, vector/UVE ops. Resolved —
  `OlympiaJsonTraceWriter` has emitted the raw `opcode` (not mnemonic+register fields) since commit
  7005e60, which subsumes both the opcode and FP-register-numbering concerns; this TODO entry
  predated that fix and was never updated. The writer emits raw encoding bits unconditionally, so
  there's no Horologium-side vector/UVE gap left to close — whether a UVE opcode decodes is a
  property of Olympia's Mavis, not this writer, and no calibration benchmark exercises UVE anyway
  (the Olympia calibration research is closed; see docs/olympia-calibration.md).

## Co-simulation

- [x] Watchdog on `ReadLine` to fail cleanly on over-run instead of hanging.
- [x] CI workflow with `HOROLOGIUM_REQUIRE_COSIM=1`.

## Out-of-Order Execution

- [x] Store sets for memory dependence prediction: predict which loads depend on which stores to avoid unnecessary
  stalls. — Chrysos & Emer, ISCA 1998
- [x] Store-set false-dependency mitigation: the periodic SSIT/LFST clear already existed but its 250k-load
  period never fired within a single benchmark run, so a false dependency (PC-indexed, no address hashing) was
  effectively permanent. Swept the clear period against the full gem5-compare suite and found a cliff at ~4096
  loads: below it, towers loses its store-set benefit; at/above it, rsort/qsort recover most of their regression
  while towers/treesum keep their gains. New default: 4096 (was 250 000). rsort H/G 0.631→0.973, qsort
  0.893→0.916, towers unchanged at 0.924, treesum 0.678→0.650 (small giveback). See
  `docs/gem5-comparison.md` "Store sets false-dependency mitigation".

## Branch Prediction

- [x] Extend speculative global history to the non-TAGE GHR predictors (Gshare, Gselect, Perceptron,
  HashedPerceptron, ITTAGE, Tournament) via the shared `SpeculativeGlobalHistory` helper.
- [x] Extend speculative history to the per-PC *local* history registers (Tournament BHT, Correlated) via the
  shared `SpeculativeLocalHistory` helper, and to the IMLI loop counter. Validated in OoO for Tournament
  (treesum mispredicts 180 → 37); Correlated and IMLI now run in OoO too (cold-BTB fetch crash fixed).
- [x] Fix the cold-BTB fetch crash: direct branches now take their taken target from the statically-known
  `FetchHint.BranchTarget` instead of the predictor's (possibly cold/aliased) BTB, and a cold indirect target
  (0) falls through instead of fetching a null address. Applied to both the OoO and five-stage fetch.
- [x] Speculative history for the LLBP/VLA-TAGE context registers. LLBP's Rolling Context Register is now
  speculative (working RCR advanced at fetch, committed shadow restored on flush, training keyed off the
  committed context); measurably improves OoO predictions (llbp median mispredicts 217 → 196). VLA-TAGE's
  Vector Loop Table is execute-driven (operand values arrive at execute, not fetch) and is intentionally not
  fetch-speculative — the inherited speculative `Ghr` is all its fetch-time history. Residual OoO imprecision:
  which pattern *within* an LLBP context is trained still uses predict-time fields (`LlbpHistIdx`/`LastProvider`)
  that a younger in-flight branch can clobber — a training-quality-only limitation, never a correctness issue,
  and not worth the full commit-time lookup recompute for a near-inert exotic predictor.
- [x] Local (per-PC) history predictor component to close the residual treesum gap. Resolved by measurement:
  Horologium's Tournament (per-PC local history, now speculative) already gets treesum to 37 mispredicts, below
  gem5 TournamentBP's 70 — so l_tage's 322 is a predictor-choice gap, not a modelling limit. See
  `docs/gem5-comparison.md` "treesum with Tournament + store sets".
- [x] Execute-time branch misprediction resolution in `OooeTrain`: partial squash that redirects fetch when a
  branch resolves mispredicted at Execute (as gem5 O3CPU's `iew` does) rather than deferring a full flush to the
  ROB head. Closes the treesum resolution-timing gap (H/G 0.921 → 0.962); see `docs/gem5-comparison.md`.

## Cache Model Realism

- [x] Write-back buffer (eviction buffer): dirty victims drain to the next level asynchronously from a small
  (4–8 entry) buffer instead of charging the full miss latency synchronously at eviction; stall only when the buffer
  is full or a demand miss targets a line still queued in it. Write-through counterpart: a coalescing write buffer so
  stores don't pay backing latency individually.
- [ ] Cache-level MSHRs with hit-under-miss: move outstanding-miss tracking from the pipeline into each cache level;
  a secondary miss to a line already in flight merges into the existing MSHR entry instead of paying a second full
  miss, and the cache continues serving hits while misses are outstanding. Makes L2/L3 non-blocking too. — Kroft,
  ISCA 1981
- [ ] Sequential tag/data access mode: gem5's third timing knob alongside tag/data latency — hit latency
  = tag + data (probe tags first, then read only the matching way) instead of max(tag, data); typical for large
  lower-level caches.
- [ ] Inclusion policy per level pair: inclusive (Intel-style — lower-level eviction back-invalidates the line in
  upper levels), exclusive (AMD-style — lower levels act as victim caches for the level above), or NINE
  (non-inclusive non-exclusive, the current behavior).
- [ ] Critical-word-first / early restart: a miss fill returns the demanded word first so the load resumes after the
  leading edge while the rest of the line streams in; matters when block size is large relative to miss latency.
- [ ] Banked caches and port limits: N banks with conflict stalls on same-bank concurrent accesses; configurable
  read/write port counts (the OoO train currently has unlimited D-cache bandwidth).
- [ ] Per-sector dirty/valid bits: sectored lines so writebacks transfer only dirty sectors and fills can be partial;
  bandwidth refinement over whole-line granularity.
- [ ] Zicbom write-back semantics: wire cbo.clean/cbo.flush/cbo.inval into dirty-line state now that write-back
  caches track it (clean = writeback and keep, flush = writeback and invalidate, inval = discard without writeback).
- [ ] Victim cache: small fully-associative buffer to absorb conflict misses. — Jouppi, ISCA 1990

## Performance

- [ ] Memoization of instructions, results, and branches.
- [ ] Structural stage-model rework for in-order trains: struct latches, fewer interface hops.

### Parallelism

- [ ] Deterministic parallel multi-hart tick: BSP-style barrier synchronization deferring every cross-hart-visible
  coherence action (snoop state transitions included) into per-hart queues drained in fixed hart order — a
  run-to-run-reproducible replacement for the racy two-phase concurrent mode.
- [ ] Parallelize the benchmark test suite across per-binary test collections; each run is fully independent.
- [ ] Background checkpoint and report serialization: write checkpoint files and result tables on a worker thread
  after a synchronous state copy.

## Face

- [ ] Browser assembly support: pure C# RV32 two-pass assembler so the Assemble command works in FaceWeb without a GAS
  subprocess.
  - Also a C compiler…
- [ ] L2 and L3 caches.
- [ ] Cache and virtual addressing visualization.
- [ ] Waveform/signal viewer: plot pipeline signals (IPC, cache hit rate, branch mispredictions) over simulation time.
- [ ] Vector operation visualization.
  - Gotta think of how this should be done.
- [ ] Cache management policy selection.
  - More cache settings like Ripes.
- [ ] gem5-style architecture configurator: UI surface for the scripting host and pipeline builder — edit `.csx` scripts
  in-app and hot-reload the resulting pipeline, cache hierarchy, branch predictor, and FU configuration without
  restarting. (Phase 5 UI of the architecture builder: AvaloniaEdit code editor, hot-reload on file change via
  `FileSystemWatcher`, workload selector, and live cache/TLB stat display.)
