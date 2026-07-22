# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

- [ ] ~~Suspended-stream data exchange: `so.v.vload`/`so.v.vstor`~~ — **hold**: dissertation gives one sentence
  with no operand semantics; Spike has no instruction files for it. Skip until the spec is clarified.
- [x] Port UVE2 reference benchmark kernels as Horologium regression tests, first kernel (`stream`):
  ported the reference suite's four McCalpin STREAM kernels (Copy/Scale/Add/Triad) from
  github.com/hpc-ulisboa/UVE2 (`UVE-Testing/spike_test/benchmarks/stream`) as
  `Pipeline_Stream_CopyScaleAddTriad_CorrectResult` in `UveTests.cs`. Chaining real streaming kernels
  (rather than hand-written unit tests) surfaced three real bugs no existing test had hit: (1)
  `ExecuteUveSoVMv` (`so.v.mv`) never wrote through to memory when its destination was bound to an
  active store stream, unlike the arithmetic ops' `UveWriteResult` path — confirmed against Spike
  (`so_v_mv.h`/`so_v_mvt.h` both write through the same generic per-register path every writer uses)
  before fixing; added a dedicated store-stream branch plus a `SoVMv_ToStoreStream_WritesMemory`
  regression test. (2) `so.v.mv`'s `Vs1` operand was missing from `RvInstruction.UveStreamSources`,
  so OoOE's UVE issue-gating never stalled it for its load stream's element to be ready, letting it
  read stale (zero) register state. (3) Both `UveStreamSources` consumers in `OooeTrain.cs`
  (issue-gating and the stream-value injection point) called `StreamingEngine`'s 8-slot-limited
  methods unconditionally on every listed uid, crashing on register numbers ≥8 used for plain
  arithmetic (broadcast/temp) operands — a real, previously-unexercised gap, since every existing UVE
  pipeline test happened to stay within u1–u5; fixed by bounding both call sites to
  `uid < StreamingEngine.MaxStreams` before querying. Verified: each fix's necessity confirmed by the
  natural fail-then-pass progression while building the port, full suite 3920/1/3921 (3918 baseline +
  2 new tests).
- [x] Port `spmv_ellpack` (github.com/hpc-ulisboa/UVE2, `UVE-Testing/spike_test/benchmarks/spmv_ellpack`)
  as `Pipeline_SpmvEllpack_CorrectResult` in `UveTests.cs`: an ELLPACK sparse matrix-vector product,
  `out[i] = sum_j nzval[i,j] * vec[cols[i,j]]`. First kernel port to exercise indirect/scatter-gather
  addressing (`ss.sta.ld.w.inds` index stream + `ss.end.sgi.ofs.add` gather stream) combined with a
  two-level nested loop (`so.b.ndc.2` inner, `so.b.nc` outer). Passed first try — no new bugs found.
  Verified against an independent C# oracle mirroring the reference's own `RUN_SIMPLE` fallback; full
  suite 3923/1/3924.
- [x] Ported `memcpy` (`Pipeline_Memcpy_CorrectResult`) and `jacobi-1d` (`Pipeline_Jacobi1D_CorrectResult`,
  a 3-point stencil using three overlapping-offset load streams over the same array): both scalar-mode
  ports, dropping the reference's `.v` vector-mode suffix per the one-representative-width convention.
  No new bugs. Full suite 3928/1/3929 including these two.
- [x] Ported `mvt` (`Pipeline_Mvt_CorrectResult`: matrix-vector-transpose, row-major then column-major
  passes over the same matrix). This one **did** find a real bug: `OooeTrain`'s `UveBranchStreams`
  handling computed stream-done purely from `StreamingEngine.IsActive`/`IsExhausted`, which store
  streams never register with (they bypass the engine entirely, tracked instead via the ISA layer's own
  `UveStoreStream` cursor) — collapsing to "always done" for any store-stream branch operand, so
  `so.b.nc`/`so.b.c` checking a store stream (as `mvt`'s outer loop does, unlike every prior test's
  load-stream check) exited after one iteration. Fixed by adding `IUveScalars.IsStoreStream`/
  `StoreStreamExhausted` (default-bodied, false/true, for non-UVE ISAs) and branching on it in
  `OooeTrain.cs`; added a minimal dedicated regression test
  (`Pipeline_SoBNc_OnStoreStream_LoopsUntilExhausted`) alongside the `mvt` port, both confirmed to fail
  without the fix via revert-and-recheck. Full suite 3929/1/3930.
- [x] Ported `jacobi-2d` (`Pipeline_Jacobi2D_CorrectResult`: 5-point 2D stencil, five overlapping-offset
  2D load streams feeding a running sum). No new bugs — `so.b.nc` here checks a load stream's whole-2D
  completion in both passes, the already-proven pattern (unlike `mvt`'s store-stream check). Full suite
  3930/1/3931.
- [x] Ported `spmv_ellpack_delimiters` (`Pipeline_SpmvEllpackDelimiters_CorrectResult`): ELLPACK SpMV
  with per-row nonzero counts from a `rowDelimiters` array (vs. `spmv_ellpack`'s fixed `L`). First
  *engine-level* (not just descriptor-level) exercise of the indirect `ss.app.ind.siz.set` Size
  modifier — resizes each row stream's inner dimension from an IndSource on every wrap, composed with
  the already-proven `sgi` gather on the same stream. No new bugs; `tdim` was chosen to target engine
  index 0 (not copied from the kernel's literal `.2` suffix, whose Spike-internal numbering isn't
  replicated here) — verified correct against the independent oracle on the first attempt. Full suite
  3931/1/3932.
- [x] Ported `3mm` (`Pipeline_3mm_CorrectResult`: a single instance of the generic matmul core all
  three of the kernel's chained calls share — `C[i,j] = sum_k A[i,k]*B[k,j]`). New 3D-stream shape: A
  repeats each row across `j` (D2 stride=0) while B repeats each column across `i` (D1 stride=0), two
  independent stride-0 broadcast dims in different positions — not exercised by mvt/spmv_ellpack's 2D
  streams. No new bugs. Full suite 3932/1/3933.
- [ ] `syrk` (github.com/hpc-ulisboa/UVE2, same benchmarks dir) is **not 1:1 portable today**: like
  `knn`, its `C` stream header (`ss.sta.st.d u1, %[C], %[N], %[N]`) is the same 4-operand form
  Horologium's `ss.sta.*` decoder doesn't implement (single `rs1`=base only) — a decoder gap, not
  test-porting work. Also still contains a pre-revision `ss.cfg.vec` line to drop if/when the header gap
  is closed (see `SPEC_NOTES.md`'s "Removed-instruction reminders").
- [x] Ported `trmm` (`Pipeline_Trmm_CorrectResult`: `B[i,j] += sum_{k=i+1}^{M-1} A[k,i]`, a triangular
  access using a static `ss.app.mod.siz.dec` modifier that shrinks the innermost (k) dimension's size by
  1 each outer (i) wrap). Structurally forces a genuine degenerate case at the last row (k-range empty,
  dimension count hits 0) — passed first try including that edge case, no new bugs. Confirms the
  fetch/consume-side size-queue mechanism (`_indModSizeQueues`) correctly gates delivery even when the
  fetch side speculatively buffers past a nominally-zero-count dimension. Full suite 3933/1/3934.
- [ ] `convolution` (github.com/hpc-ulisboa/UVE2, same benchmarks dir) is **not portable within
  Horologium's current `StreamingEngine.MaxStreams=8` limit**: its 3x3-tap stencil configures 9
  simultaneous overlapping-offset load streams (one per filter tap, `u1`-`u9`) plus a store stream — 10
  concurrent real memory streams, exceeding the engine's 8-slot capacity (the 9 filter *coefficients*
  are broadcast scalars at `u10`-`u18` and fine, since ids ≥8 already bypass `StreamingEngine` for
  arithmetic-only temps per the `stream` port fix — the blocker is the 9 concurrent *load* streams, which
  do need real engine slots). A capacity increase is an `Orrery` change, not a test-porting one; out of
  scope here.
- [x] Ported `gemver`'s first sub-kernel (`Pipeline_Gemver_OuterProductUpdate_CorrectResult`:
  `A[i,j] += u1[i]*v1[j] + u2[i]*v2[j]`) — the structurally distinctive one, with two *simultaneous
  independent* vary/repeat broadcast pairings across four load streams feeding two multiply-adds (beyond
  3mm's single broadcast-vs-vary pairing). The other three chained sub-kernels (transposed matvec,
  vector add, matvec) recombine patterns already exercised by mvt/3mm/jacobi-1d — not ported, low
  expected marginal value. No new bugs. Full suite 3934/1/3935.
- [x] Ported `covariance` (`Pipeline_Covariance_CorrectResult`) in full: per-column mean
  (reduction+divide), broadcast-subtract centering, then an upper-triangular `cov[i,j]=cov[j,i]`
  symmetric update with mirrored writes. Confirmed portable (only single-operand `ss.sta` headers,
  unlike `knn`/`syrk`) but found a real bug: the triangular stage's two `cov` store streams each carry
  simultaneous Offset-Inc + Size-Dec static modifiers (the first use of the `Offset` modifier target by
  any port — every prior one only ever resized `Size`), and `BuildAndActivatePendingStream`'s store
  branch built `UveStoreStream` purely from the flat dimension list, **never passing `StreamModifier[]`
  through at all** — `ss.app.mod` was a complete no-op on any store stream. Fixed by adding
  `Modifiers`/apply/reset logic to `UveStoreStream` itself (mirroring `StreamState`'s static-modifier
  path in `StreamingEngine.cs`; indirect/`SourceStreamId` modifiers on a store stream remain
  unimplemented, not needed here) and wiring `mods` through in the store branch. Also had to rework
  `UveStoreStream.IsExhausted` from a precomputed dimension-product (`_totalCount`) to an
  outermost-dim-wrap flag (`_done`), since a Size-modified stream's real element count isn't knowable
  upfront — full suite re-run confirms this is behavior-preserving for every unmodified store stream.
  Verified: confirmed via revert-and-recheck that `Pipeline_Covariance_CorrectResult` fails identically
  without the fix (wrong values landing at wrong `cov` indices). Full suite 3935/1/3936. Re-verified twice
  more after an advisor push-back that a deleted, unpinned minimal test had undersold the risk: (1) scaled
  `covariance` itself up to M=3/N=4 with asymmetric data (`Pipeline_Covariance_Scaled3x4_CorrectResult`) —
  passes; (2) traced a dedicated minimal reproduction of an apparent "2-instruction write+branch loop on a
  modifier-bearing store stream" failure (`Pipeline_SoBNc_OnModifierBearingStoreStream_LoopsUntilExhausted`)
  down to its actual root cause with `Console.Error` instrumentation at the write site (temporary, removed
  after use) — every write landed at the exact correct address with the correct value; the "failure" was
  the test's OWN verification loop wrongly asserting against consecutive flat addresses instead of the
  stream's real sparse row-major layout (row `r` only fills `r+1` of its `rows` slots; the rest are
  legitimately untouched). No pipeline bug: fixed the test's assertion (now checks the full row-major grid
  incl. untouched sentinel cells) and it passes unconditionally, no `Skip`. The mechanism is confirmed
  correct even in the tightest possible write-immediately-followed-by-branch shape, so there is no
  remaining open item here.
- [ ] `sgd` (371 lines) is **not portable within `StreamingEngine.MaxStreams=8`**, confirmed by reading
  its `RUN_UVE` asm in full: all three of its per-epoch reduction sub-kernels configure their streams
  *once*, before the epoch loop begins, using a stride-0 outer "epochs" dimension rather than
  reconfiguring per epoch — meaning `u1`-`u9` (sgd_model load, x load, y_err store, y load, y_err load
  (kernel 2), x load (kernel 3), y_err load (kernel 3), sgd_model load (kernel 3), sgd_model store
  (kernel 3)) are all real, simultaneously-active memory streams: 9 concurrent, one over the engine's
  8-slot capacity. Same underlying blocker as `convolution` (an `Orrery` capacity change, not a
  test-porting one) — not a reduced/simplified version, since dropping a stream would test something
  other than what the kernel actually is.
- [ ] `vec_cv` (661 lines, `so.v.cv` conversions) has an **empty `RUN_SIMPLE`** (`void core(DataType
  src[SIZE]){}`) — there is no independent oracle to verify against at all. Matches the already-recorded
  `SPEC_NOTES.md` finding that `so.v.cv` correctness was "genuinely underspecified, never given
  attention" by the author. Do not port without a real reference to check against.
- [x] Confirmed `test` and `test_dyn` are not portable benchmark kernels: `test`'s `RUN_SIMPLE` is empty
  (`void core(){}`) and its `RUN_UVE` body is just `so.p.cv`/predicate-conversion `printf` dumps with no
  assertions (not a kernel with a checkable result); `test_dyn/kernel.c` is a genuinely empty (0-byte)
  file in the reference repo. Nothing to port from either.
- [ ] `knn` (github.com/hpc-ulisboa/UVE2, same benchmarks dir) is **not 1:1 portable today**: its
  `position_x_j`/`_y`/`_z` neighbor-gather streams use a 4-operand `ss.sta.ld.d ud, base, count, stride`
  header that configures a dimension inline (Horologium's `ss.sta.ld.*` header only takes `rs1`=base;
  all dimensions come from separate `ss.app`/`ss.end`), plus a trailing `ss.end ud, zero, zero, zero`
  with a literal zero count — apparently a placeholder inner dimension whose sole purpose is to make its
  attached `ss.app.indl.ofs.add` (dynamic/`.L` indirect modifier, as opposed to the `sgi` form used by
  `spmv_ellpack`) fire on every element. Both are decoder/semantics gaps, not test-porting work; the
  count=0-placeholder-dimension idiom's exact semantics need the author's confirmation before
  implementing (per `SPEC_NOTES.md`'s "author is authority" discipline) — don't guess at it from the
  kernel source alone.
- [x] Closed the `ss.app.ind` (dynamic indirect modifier, distinct from `ss.app.sgi`) test-coverage gap
  found while investigating `knn` above — it had zero tests despite being fully implemented.
  `Decoder_SsAppInd_Roundtrip` + `SsAppInd_DotL_AttachesSourceStreamModifier_TargetsLastConfiguredDimension`
  (mirroring the existing `ss.app.mod`/`.L` pair) confirm it attaches a general `StreamModifier` with
  `SourceStreamId` set and that `.L` resolves to the last configured dimension here too. Descriptor-level
  only (construction, not engine stepping) — deliberately scoped below the count=0-placeholder-dimension
  question above. Full suite 3925/1/3926.
- [x] Fixed `.L` modifier target-dimension resolution: the UVE2 author confirmed (2026-07-22) that
  `.L` targets the *last configured dimension of the stream*, not "the dimension configured right
  after the trigger" as `ExecuteUveSsAppMod`/`ExecuteUveSsAppInd` (`Rv32Executor.Uve.cs`) previously
  resolved it via `targetDimRaw == 7 ? spikeTrigger + 1 : targetDimRaw`. That resolution only happened
  to be correct when the modifier triggered on the second-to-last configured dimension; a modifier
  appended earlier, with two or more further dimensions configured afterward, got the wrong target
  dimension and wrong addresses. Fixed by no longer resolving `.L` eagerly at
  `ss.app.mod`/`ss.app.ind` time — the raw tdim=7 sentinel is now carried unresolved through
  `PendingConfig.Modifiers` and resolved in `BuildAndActivatePendingStream` (ss.end time, when `ndim`
  is finally known) directly to engine index 0 (innermost — since ss.end always appends the innermost
  dimension last, "the last configured dimension" is unconditionally engine index 0, independent of
  `ndim`). Spike could not be the oracle (its own `.L` handling is a confirmed Spike bug — hardcoded
  `targetDim = 7`, throws on <8-dimension streams); verified instead with a direct executor-level test
  (`SsAppMod_DotL_TargetsLastConfiguredDimension_NotTriggerPlusOne`) using a 3-dimension stream where
  the modifier triggers on the outermost dimension with two more configured afterward — the old and
  new resolutions genuinely diverge there (confirmed the test fails under the old resolution before
  fixing). Full suite 3921/1/3922 (3920 baseline + 1 new test).
- [x] Fixed `so.b.*` branch `d`-field encoding to match the UVE2 author's authoritative correction
  (2026-07-22, see `SPEC_NOTES.md`'s "Branch `d` field" entry) — the author is the absolute authority
  and overrules Spike here. Corrected table: `SO.B.NC.1`=000 .. `SO.B.NC.7`=110, `SO.B.NC`=111 (dc.1 is
  now reachable; the no-suffix EOS-equivalent form moved from funct3=0 to funct3=7; dc.8 no longer
  exists). Checked against Spike ground truth (`riscv/encoding.h`'s `MATCH_SO_B_*`, AnaBSF/riscv-isa-sim
  @ a048271) beforehand: Spike ships the *old* encoding Horologium previously implemented, not the
  author's table — a confirmed Spike divergence, recorded but overruled per the author's authority (see
  CLAUDE.md's updated UVE2 policy), not a reason to keep matching Spike. The fix turned out to be a
  one-line decoder change (`funct3 == 0` → `funct3 == 7`, `Rv32Decoder.Uve.cs`) plus updating the
  `SoBNc`/`SoBc` test-encoder helpers to match: the dimensioned branch's `dim = funct3` mapping and the
  outermost-first indexing convention (the "numeric tdim and branch-D direction" question) turned out
  to be entirely unaffected — that convention only concerns which physical dimension a *given* funct3
  value addresses, not which funct3 value selects the EOS-equivalent vs. dimensioned instruction family,
  so no separate fix was needed there after all (see `SPEC_NOTES.md`'s "Numeric tdim and branch-D
  direction" entry). Since round-trip tests alone can't validate an encoding change (encoder and decoder
  would flip together and still agree with each other), verified with a dedicated test
  (`Decoder_SoBBranchTable_MatchesAuthorCorrectedEncoding`) that hand-encodes raw words transcribing the
  author's table directly, bypassing the `SoBNc`/`SoBNdcD` helpers entirely — confirmed it fails under
  the old decoder logic before fixing. Full suite 3922/1/3923 (3921 baseline + 1 new test).

## Cache Prefetching

- [x] STeMS: spatio-temporal memory streaming extending SMS with temporal miss-sequence recording. —
  Somogyi et al., ISCA 2009

## Analysis

- [x] Simulation state checkpoint/restore, Option B: warm microarchitectural checkpoint (caches, TLBs,
  branch predictor, RAS) layered on `ArchitecturalCheckpoint`, valid only at a drained pipeline boundary
  (`OooeTrain.Drain`) — mirroring gem5's `drain()`-before-`serialize()` precedent. `OooeTrain` only.
- [x] Extend the Option B microarchitectural checkpoint to `StoreSetPredictor`/`SmbPredictor`/
  `RdipPrefetcher`/`TokenPassingCriticalityPredictor` (one `ICriticalityPredictor` implementation)/`LvpVp`
  (one `IValuePredictor` implementation). `FdipPrefetcher` deliberately excluded — no trained table, only
  a cheap-to-rebuild lookahead FTQ. Key design rule discovered along the way: serialize only PC/address/
  content-keyed tables; skip anything keyed by or compared against a monotonic per-train counter
  (InstrId/SeqNo/commit count) — those reset to 0 on a freshly restored train, so a carried-over counter
  value can never match again (`StoreSetPredictor`'s LFST, `TokenPassingCriticalityPredictor`'s token/
  slot/commit-count state).
- [x] Extend further: the remaining `IValuePredictor` implementations (`VtageVp`, `StrideVp`, `HybridVp`,
  `DynamicClassificationVp`) and seven more `IReplacementPolicy` implementations (`FifoPolicy`,
  `MruPolicy`, `ClockPolicy`, `PlruPolicy`, `SrripPolicy`/`BrripPolicy`/`DrripPolicy`, `ShipPolicy`,
  `HawkeyePolicy`) — `RandomPolicy`/`RtlFfiReplacementPolicy` deliberately excluded (RNG-only state,
  FFI-owned native state). Found one genuine trained-state case that looked transient at first glance
  but wasn't: `DynamicClassificationVp`'s `_armed`/`_missStreak` are never cleared by `Update` (only by
  eviction/reclassification/squash), unlike a real in-flight counter — serialized, not skipped. Verified
  by direct instrumentation (not just doc-comment assertion) that `StrideVp`'s `_inFlight` genuinely is
  zero at its equivalence test's drain point, same method used for `StoreSetPredictor`'s LFST.
- [x] Wire the Option B microarchitectural checkpoint into the Runner CLI: `--checkpoint-save-micro`/
  `--checkpoint-load-micro`, mirroring the existing architectural-only `--checkpoint-save`/
  `--checkpoint-load`. Requires `--script` and an OoOE pipeline train (graceful warning + fall back to
  architectural-only restore otherwise); save drains at the simplest trigger, the end of the run — the
  CLI has no way to name a mid-run boundary. Incidentally fixed a pre-existing, unrelated bug found while
  smoke-testing this: `ScriptHost`/`FSharpScriptHost` pre-imported the stale namespace
  `Mechanism.BranchPredictModels` (renamed to `Mechanism.BranchPred` at some point), which broke every
  `--script` invocation.
- [x] The BP zoo, representative-per-family scope (user chose this over exhaustive or stopping): `LTageBp`
  (TAGE lineage — also the base of `TageScLBp`/`BullseyeBp`/`MultiperspectiveBp`/`BatageBp` via
  inheritance, so they inherit the base TAGE/loop/history round trip; their own additional layered
  tables still cold-start), `HashedPerceptronBp` (perceptron family), `TournamentBp` (local/global/chooser
  hybrid family). `SpeculativeGlobalHistory`/`SpeculativeLocalHistory` (the shared speculative/committed
  history-shadow helpers used by most predictors in this file) gained `WriteState`/`ReadState` once,
  benefiting every predictor built on them, not just these three. Only `LTageBp` got a full pipeline
  equivalence test (an alternating-parity branch pattern a bimodal counter can't learn but a
  history-indexed predictor can) — the one case that actually exercises history serialization through a
  live pipeline, since every prior equivalence test used history-free `NBitBp`/`AlwaysNotTaken`;
  `HashedPerceptronBp`/`TournamentBp` got history-populated unit-level round trips only, per the
  deliberately capped scope.
- [x] Extend BP zoo coverage to the rest of the zoo (user follow-up: "do them now since it's relevant" —
  moved from representative-per-family to exhaustive). Every `TageScLBp`-lineage subclass's own layered
  tables: `TageScLBp` (Statistical Corrector), `BatageBp` (bias table), `BullseyeBp` (HIT/H2P
  perceptrons), `MultiperspectivePerceptronBp` (five hashed-history feature tables), `LlbpBp`/`LlbpXBp`
  (RCR + context-keyed pattern storage + CTT), `TeaBp`/`LvcpBp`/`RunltsBp` (correlation tables + register/
  load-value tracking), `VlaTageBp` (Vector Loop Table). Every remaining standalone predictor:
  `PerceptronBp`, `CorrelatedBp`/`GselectPredictor`/`GshareBp`, `IttagePredictor` (ITTAGE — confirmed
  wireable as a standalone `IBranchPredictor` before including it), `ImliPredictor` (genuine
  speculative/committed counter semantics — got its own pipeline equivalence test, reusing the
  alternating-parity program, since it's the one mechanism `LTageBp`'s equivalence test doesn't
  structurally cover), `BranchNetBp` (composes a `TageScLBp` field rather than extending it — its own
  offline-trained per-branch CNNs are frozen shape, not state, same category as `TeaBp`'s `_chains`),
  `HypreBp` (hyperdimensional/SDM — a genuinely different HD-vector data structure; its round-trip test
  needed far more training reps than every other predictor to move its Hamming-distance threshold, a real
  anti-theater case caught by running the test rather than assumed). `OracleBp` (trace-driven)/`StaticBp`
  (stateless)/`CbpFfiBp`/`CbpNgFfiBp`/`CbpNgCommitDrivenBp` (FFI-owned native state) remain excluded on
  principled grounds. The BP zoo — and with it, Option B's predictor/policy coverage — is now complete.

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

## Face

- [ ] Browser assembly support: pure C# RV32 two-pass assembler, so the Assemble command works in FaceWeb without a GAS
  subprocess.
  - Also a C compiler…
- [ ] Improve the cache and virtual addressing visualization. Make it more like Ripes.
- [x] Vector operation visualization.
- [x] gem5-style architecture configurator: new desktop-only "Configurator" tab (`ConfiguratorView`/
  `ConfiguratorViewModel`) — an AvaloniaEdit pane edits a `.csx` script, hot-reloads on both a
  600ms debounced in-app edit and an external save via `FileSystemWatcher` (both converge on the
  same `ConfiguratorEngine.BuildAsync`), a workload selector reuses `MainWindowViewModel`'s
  benchmark preset list, and a live stat panel shows cache/TLB counters and dial snapshots while
  running. Coexists with the pre-existing `ConfigViewModel`/`TrainConfig` GUI path rather than
  replacing or bridging it — the two talk to unrelated data models (`MachineSpec` vs
  `TrainConfig`) and there was no reason to unify them for this. **Desktop-only, by constraint**:
  the tab depends on `Script.csproj` (Roslyn `CSharpScript`), which can't dynamic-codegen under
  browser-wasm, so `Face.csproj` excludes `ConfiguratorView`/`ConfiguratorViewModel` from the
  `net11.0-browser` TFM (verified: the published browser DLL has zero references to either type)
  and gates the tab's registration in `MainWindowViewModel`/`MainPanel.axaml.cs` behind a
  `BROWSER` compile constant. No code-completion/diagnostics in the in-app editor (that's a
  separate mini-IDE-scale feature); a "Save script…" button writes the buffer to a file and
  launches the OS default handler so real tooling (Rider, vim, …) can be used externally, with
  the `FileSystemWatcher` picking the edits back up.
- [ ] More intuitive ways to visualize prefetching, cache policies, branch prediction, etc.
- [x] Unify Face's two disconnected configuration models, phase 1: `TrainConfig`/`Experiment` used
  to bypass `PipelineSpec` entirely and hand-roll pipeline construction inline, duplicated across
  four independent sites (`Experiment.RunOne`, `Experiment.Trace`, Runner's `--simpoint-warmup`
  measurement switch, and `PipelineSpec` itself for the `.csx`/`MachineSpec` scripting path) —
  already a source of real drift (`OutOfOrderSpec` was missing `EnableStoreSets`/
  `FdipFtqCapacity`/`Rdip`, which `Experiment.RunOne` set). Added `CprSpec`/`DaeSpec` to
  `PipelineSpec` and closed the remaining knob gaps on `OutOfOrderSpec`/`FiveStageSpec`/
  `SuperscalarSpec`, then added `TrainConfig.ToPipelineSpec()` — the single place the
  "which `PipelineSpec` subtype for which `Pipeline` string" mapping lives — and rewired all four
  sites through it. `TrainConfig` remains the JSON-serialisable GUI/sweep-file format; only the
  construction step is now shared with the scripting path. Verified strictly behavior-preserving:
  a before/after sweep-run diff (`git stash`) confirms byte-identical output for every pipeline kind
  the old inline switches handled (`five_stage`/`superscalar`/`ooo`/`cpr`/`dae`), full solution build
  clean, and the entire non-benchmark test suite unchanged (3910 passed, 1 skipped, same as
  baseline). `ToPipelineSpec` deliberately leaves `"single_cycle"` falling through to `FiveStageSpec`
  (matching `Experiment`'s old behavior exactly, bug and all — see the next item); Runner's
  `--simpoint-warmup` path, whose `single_cycle` handling was already correct before this change,
  special-cases it inline instead of routing through `ToPipelineSpec`, so that behavior didn't move
  either.
- [ ] Fix `Experiment`'s `"single_cycle"` pipeline handling: `TrainConfig.Pipeline == "single_cycle"`
  (a real, user-selectable option — `ConfigViewModel.PipelineOptions`) has never had a case in
  `Experiment`'s pipeline-construction switch (nor, now, in `TrainConfig.ToPipelineSpec()`, see the
  item above), so selecting "Single Cycle" in Face's GUI silently builds a `FiveStageTrain` instead.
  The fix itself is a one-line addition to `ToPipelineSpec` (`"single_cycle" => new
  SingleCycleSpec(commitObserver)`) — the work here is verifying it in Face itself before flipping
  it, since `SingleCycleTrain`'s dial schema (`core.*` counters) differs from the `pipeline.*`
  schema every other pipeline kind emits, and the Face comparison grid/CSV output has not been
  checked against that schema switch.
- [x] Unify Face's two configuration models, phase 2a (backend-only subset — done while away from a
  machine that could run Face; the three GUI-facing items below stay deferred until that can be
  visually verified): added `"RiscV32.Config"` to `ScriptHost`/`FSharpScriptHost`'s pre-imports so
  `.csx`/`.fsx` scripts can reference `BranchPredictorConfig`'s ~30-entry catalog (e.g.
  `BranchPredictorConfig.LTage()`) unqualified — verified end-to-end via a real `ScriptHost` eval, not
  just the import list. Documented (not fixed) a real limitation this surfaced: `OracleConfig`/
  `BranchNetConfig`/`TeaConfig` need `Build(IMechanism, IWorkload)`, but every `PipelineSpec`'s
  `BranchPredictorFactory` is a bare `Func<IBranchPredictor>` with no way to supply those — they only
  exist after a script has already returned its `MachineSpec`, so those three predictors remain
  unusable from scripts (`TrainConfig.ToPipelineSpec` avoids this only because it runs after
  mechanism/workload are known). Also extracted the duplicated 12-case `PrefetcherKind` switch
  (`MemoryConfig.cs`'s two `MemoryLayers.Build` overloads had it verbatim, twice) into one
  `MemoryLayers.MakePrefetcher` helper — deliberately *not* full delegation of one overload to the
  other: `ConfigViewModel`'s `ICacheEnabled`/`L2CacheEnabled` toggles are independent and ungated, so
  sparse hierarchies (L2 configured without L1) are genuinely reachable, and the flat builder's
  positional stat-field mapping (`Cache=l1, L2Cache=l2, L3Cache=l3`, null if a slot is absent) diverges
  from the `CachePathSpec` builder's innermost-surviving-cache mapping for exactly that case — a full
  merge would need to reproduce the flat builder's sparse-null behavior exactly, real risk for a
  duplication-only cleanup. Verified via a dedicated sweep (write-back + non-zero tag/data
  latency/wb-capacity on L1+L2, plus a real D-prefetcher engaging through an OoO run against
  `embench-matmult-int.elf` — confirmed non-zero `dcache_prefetches`, not just a config that never
  exercises the switch) diffed byte-identical before/after via `git stash`, plus the full
  `Tests/Orrery` suite (589/589) and the full non-benchmark suite (3910/1/3911, unchanged).
- [x] Unify Face's two configuration models, phase 2b — FDIP semantics on `MachineSpec`'s split-I/D
  branch: fixed and tested; the unified branch has a separate, pre-existing structural gap, not fixed
  (own item below). `PipelineSpec.Build` (both overloads) gained an additive `fdipBackingMemory`
  parameter (defaults preserve every existing caller's behavior exactly — `TrainConfig`/`Experiment`
  pass nothing and are unaffected, confirmed via sweep diffs), threaded through `FiveStageTrain`'s and
  `OooeTrain`'s constructors down to `FdipPrefetcher`'s own decode-ahead reads specifically — not to
  `fetchTranslatorMemory`/`CreateFetchTranslator` (the page-table-walker path), which must stay
  untouched (conflating the two would have silently moved real instruction fetch onto raw backing,
  not just FDIP's lookahead). `MachineSpec.Build`'s split-I/D branch now threads real raw backing
  through; FDIP now correctly bypasses the I-cache it's warming there, matching `FdipPrefetcher`'s own
  doc comment ("reads bypass the cache") and the `TrainConfig` path's existing behavior. Verified with
  `Tests/Pipeline/MachineSpecFdipTests.cs`: confirmed the new assertions actually fail without the
  `MachineSpec.cs` half of the fix (9597 vs. 45 I-cache accesses — FDIP's own reads were inflating the
  counters), not just that they pass with it. Full suite 3912/1/3913 (3910 baseline + 2 new tests), full
  solution build clean, sweep diffs byte-identical on the unaffected `TrainConfig` path.
- [x] Unify Face's two configuration models, phase 2b-2 — FDIP now engages via
  `CacheHierarchySpec.Unified` too (previously it never did, before or after phase 2b's fix — a
  separate, deeper structural gap than bypass-vs-through-cache semantics). Root cause: the unified
  branch stayed on the *flat* `Build` overload specifically because `CprSpec`/`DaeSpec` only
  implemented that one; the flat overload always rebuilds its own internal `MemoryLayers` with
  `MemoryConfig.None`, leaving `iLayers.Cache` null and `FdipFtqCapacity > 0`'s guard always failing,
  regardless of `fdipBackingMemory`. Fixed by giving `CprTrain`/`DaeTrain` an internal pre-built-
  `MemoryLayers` constructor (mirroring `FiveStageTrain`/`OooeTrain`/`SuperscalarTrain`'s existing
  pattern — their Gear gear classes, `CprPipelineCore`/`DaeCore`, already took `MemoryLayers` directly,
  so this was a thin wrapper) and a matching `CprSpec`/`DaeSpec` `Build(MemoryLayers, ...)` override;
  `SmtSpec` had the same gap (no `MemoryLayers` override at all) and got one too, for the same reason.
  With every `PipelineSpec` subtype now supporting both overloads, `MachineSpec.Build`'s unified branch
  switched from the flat overload (`layers.Accessor` as raw backing, rebuilding an empty wrapper) to
  the `MemoryLayers` overload (passing the real externally-built `layers` directly as both I and D) —
  which also incidentally fixes the pre-existing "train's internal ICache/DCache stat fields are null"
  limitation the old `MachineSpec` doc comment called out, since `iLayers`/`dLayers` are no longer a
  rebuilt wrapper. Verified: confirmed the two new unified-branch FDIP tests
  (`Tests/Pipeline/MachineSpecFdipTests.cs`) fail without the `MachineSpec.cs` branch change
  specifically (isolated via a temporary one-line revert, not a full-stack `git stash`, since none of
  this work is committed yet); added `CprSpec`/`DaeSpec`-via-`MachineSpec`-with-cache tests to
  `Tests/Pipeline/MachineSpecTests.cs` (new capability, didn't exist before). Full suite 3918/1/3919
  (3912 baseline + 6 new tests), full solution build clean, three separate sweep diffs
  (default/dedup+ELF/cpr+dae) byte-identical, confirming zero change on the `TrainConfig`/`Experiment`
  path this entire pass never touches.
- [ ] Unify Face's two configuration models, phase 2c (GUI-facing, deferred until Face can be
  visually verified): RV64 support for `TrainConfig`/`ConfigViewModel` (the scripting path already
  supports RV64; `TrainConfig` is RV32-only), multi-hart GUI support (`MulticoreSpec`/`HartSpec` exist
  on the scripting side only), and exposing `CacheLevelSpec`'s richer knobs (bank count, read/write
  ports, sector size, victim cache, inclusion policy) in `ConfigViewModel`.
