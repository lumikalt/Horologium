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
  attention" by the author. Do not port without a real reference to check against.
- [ ] `knn` (github.com/hpc-ulisboa/UVE2, same benchmarks dir) is **not 1:1 portable today**: its
  `position_x_j`/`_y`/`_z` neighbor-gather streams use a 4-operand `ss.sta.ld.d ud, base, count, stride`
  header — the same pre-revision inline-dimension syntax `syrk` also turned out to have (not itself a
  decoder gap, and not knn's real blocker) — plus a trailing `ss.end ud, zero, zero, zero` with a
  literal zero count: apparently a placeholder inner dimension whose sole purpose is to make its
  attached `ss.app.indl.ofs.add` (dynamic/`.L` indirect modifier, as opposed to the `sgi` form used by
  `spmv_ellpack`) fire on every element. Unlike `syrk`, this count=0-placeholder-dimension idiom is a
  genuine semantic unknown — `RUN_SIMPLE` can't be used to rederive what a zero-count dimension does to
  the fetch/consume odometers, so this needs the author's confirmation before implementing (per
  `SPEC_NOTES.md`'s "author is authority" discipline) — don't guess at it from the kernel source alone.
- [x] Reverted the `so.b.*` branch `d`-field encoding fix after the UVE2 author retracted his own prior
  correction (email 2026-07-24, see `SPEC_NOTES.md`'s "Branch `d` field" entry): the 2026-07-22 email
  that moved the no-suffix EOS-equivalent form (`so.b.[n]c`) to funct3=7 and made dc.1 reachable at
  funct3=0 was itself a mistake — Appendix B's original table (and Spike's matching encoding) was
  correct all along, and it was the dissertation's §2.3.2 prose that was wrong. `so.b.[n]c` refers to
  the *first* (outermost) dimension, not a separate EOS flag — checking it and checking end-of-stream
  are the same event by construction, since the whole stream ends exactly when its outermost dimension
  does. Reverted `Rv32Decoder.Uve.cs`'s `funct3 == 7` check back to `funct3 == 0`, and the `SoBNc`/`SoBc`
  test encoder helpers back to funct3=0; no other call site needed touching, since every existing
  `SoBNdcD`/`SoBdcD` test call uses dim 1..3, never the 0/7 boundary values that actually differ between
  the two tables. Renamed and rewrote `Decoder_SoBBranchTable_MatchesAuthorCorrectedEncoding` →
  `Decoder_SoBBranchTable_MatchesAuthorReconfirmedEncoding` to transcribe the reinstated table; confirmed
  it fails under the (now reverted) 2026-07-22 decoder logic before reverting. Full non-benchmark suite
  unchanged at 3961/1/3962.

## RISC-V

- [x] Scalar crypto (RV32): Zknd/Zkne/Zknh (NIST AES + SHA2), Zksed/Zksh (ShangMi SM4 + SM3), Zkr
  (entropy source CSR). — RISC-V Cryptography Extensions Volume I: Scalar & Entropy Source
  Instructions, v1.0.1 (`~/dl/riscv-crypto-spec-scalar-v1.0.1.pdf`)
- [x] Scalar crypto (RV64): the RV64-only `aes64ds`/`aes64dsm`/`aes64es`/`aes64esm`/`aes64im`/
  `aes64ks1i`/`aes64ks2` (Zknd/Zkne) and the direct (non-split-register) `sha512sig0`/`sha512sig1`/
  `sha512sum0`/`sha512sum1` forms (Zknh). Closed both `Rv64Decoder` gaps noted below: RV32-only
  `aes32*`/`sha512sig*h/l`/`sum*r` now explicitly trap on RV64 instead of silently decoding, and
  `sha256*`/`sm3p0`/`p1` now decode and correctly sign-extend to XLEN (fixed by casting the RV32
  executor's 32-bit results through `(int)` before widening, so RV64 inheritance gets EXTS instead
  of implicit zero-extension for free — same fix applied to `sm4ed`/`sm4ks`).
- [x] Scalar crypto (Zbkb/Zbkx bitmanip subset): `pack`/`packh`/`packw`/`brev8`/`zip`/`unzip`
  (Zbkb) and `xperm4`/`xperm8` (Zbkx), both widths — the bitmanip-for-crypto instructions the two
  items above didn't cover. Zbkc needed no new code: it's fully satisfied by the pre-existing
  `clmul`/`clmulh`. Closed a third `Rv64Decoder` gap in the same family as the two above: `pack
  rd, rs1, x0` was falling through to RV32's `rs2=0` special case (a 16-bit halfword zero-extend)
  instead of RV64's 32-bit-half pack, silently producing the wrong result for that one operand
  combination — fixed with an explicit RV64 interception. All eight encodings re-verified against
  `riscv{32,64}-none-elf-as`+objdump ground truth (not just the spec's own diagrams).
- [x] Vector crypto element-group architecture + full Zvkned extension (AES block cipher: encrypt
  `vaesem.vv/.vs`+`vaesef.vv/.vs`, decrypt `vaesdm.vv/.vs`+`vaesdf.vv/.vs`, round-zero `vaesz.vs`,
  key schedule `vaeskf1.vi`/`vaeskf2.vi`). — RISC-V Cryptography Extensions Volume II: Vector
  Instructions, v1.0.0 (`~/dl/riscv-crypto-spec-vector.pdf`). Added the general element-group
  infrastructure (`ITooth.HasRuntimeSizedVectorDestination`, `ReadElementGroup`/`WriteElementGroup`,
  LMUL*VLEN>=EGW / SEW / vl,vstart-multiple-of-EGS constraint checking, plus a register-group-range
  reserved-encoding check for the `.vs` forms) that any future Zvk* instruction can reuse. Vector-
  crypto instructions use a dedicated major opcode (`0x77`), not the standard OP-V opcode (`0x57`)
  despite an otherwise identical OPMVV-shaped field layout — the spec-text extraction assumed 0x57;
  a `riscv64-none-elf-as`/objdump round-trip caught the discrepancy and the authoritative
  `riscv-opcodes` project (`extensions/rv_zvkned`) confirmed 0x77. Validated against the FIPS-197
  Appendix A.1 key-expansion and Appendix B cipher example traces (not just an in-repo reference),
  and mutation-tested. `vaesdm.vv`'s round-key XOR lands *before* InvMixColumns (spec pseudocode,
  matching FIPS-197's Equivalent Inverse Cipher §5.3.5) — different from every other round op,
  where the XOR is the final step; caught by chaining a real decrypt against the KAT trace, not
  just the per-instruction from-scratch reference. `OooTrain` needs no changes, since
  head-serialization of `ToothClass.Vector` already covers it; `FiveStageTrain` widens its vector
  RAW hazard check with a runtime-LMUL-derived register span for these instructions (see below).
- [x] Zvksed extension (SM4 block cipher): round function `vsm4r.vv/.vs` and key expansion
  `vsm4k.vi`, reusing the Zvkned element-group infrastructure unchanged (same EGW=128/EGS=4/SEW=32
  shape, same opcode space). Validated against GB/T 32907-2016 Example 1 (key==plaintext,
  transcribed via `draft-ribose-cfrg-sm4`) — key expansion, and both encrypt and decrypt directions
  of the round function (identical operation, reverse round-key order). SM4's own "reverse
  transformation R" (final word-order swap) is not part of `vsm4r`/`vsm4k` themselves and is
  applied in the test, not production, matching the spec. Found and fixed a real bug surfaced by
  this KAT: `ElementGroupGetWord`/`ElementGroupSetWord` (shared word-packing helpers) originally
  packed bytes LSB-first, which is transparent to AES's key schedule (only ever rotates by a whole
  byte) but silently wrong for SM4's non-byte-aligned rotations (2/10/13/18/23 bits) — fixed to
  natural big-endian packing, with AES's `vaeskf1.vi`/`vaeskf2.vi` updated to match (rotate
  direction flipped, `AesRcon` shifted into the high byte at the point of use). Root-caused via a
  throwaway `dotnet fsi` script implementing the cipher independently (no python/node in the nix
  devshell), rather than iterating on the hypothesis via the full test suite.
- [x] Zvknha/Zvknhb extension (SHA-2 compression + message schedule): `vsha2ch.vv`/`vsha2cl.vv`
  (two rounds of compression) and `vsha2ms.vv` (four rounds of message-schedule expansion),
  implementing the Zvknhb superset unconditionally (SEW=32 SHA-256 and SEW=64 SHA-512 both
  accepted, rather than gating SEW=64 behind a separate hart-extension flag). Unlike every earlier
  Zvk* op, EGW=4*SEW is itself runtime-dependent (128 for SHA-256, 256 for SHA-512 — the latter
  needing 2 physical registers per element group, confirming the existing `ReadElementGroup`/
  `WriteElementGroup` helpers already generalized correctly since they took `egwBits` as a runtime
  parameter from the start), and `vs1` is a genuine third vector source register rather than a
  sub-op selector or immediate. The element-index-to-named-variable mapping ({a,b,e,f} etc.) was
  confirmed against the RISC-V Sail reference model (github.com/riscv/sail-riscv,
  `model/extensions/vector_crypto/zvknhab_insts.sail`) rather than derived from the spec's prose
  concatenation notation alone, which is genuinely ambiguous without seeing how `get_velem`/
  `read_vreg` actually index elements. Validated end-to-end (multi-block, chained
  `vsha2ms`+`vsha2ch`/`vsha2cl` through a full SHA-256/SHA-512 hash) against
  `System.Security.Cryptography.SHA256`/`SHA512` — a real, independently-implemented oracle —
  rather than a hand-transcribed round trace, since FIPS 180-4's on-disk text has no worked
  example with intermediate values (same situation as FIPS-197's Appendix C). Uncovered and
  documented (not just used) the vsha2c[hl] register ping-pong identity: only vd is written each
  call (with the new {a,b,e,f}), and the untouched vs2 register's stale content is exactly the
  {c,d,g,h} the next call needs, since after 2 real SHA-2 rounds new-c/d/g/h == old-a/b/e/f.
- [x] Zvksh extension (SM3 secure hash): `vsm3c.vi` (two rounds of compression) and `vsm3me.vv`
  (eight rounds of message-schedule expansion), EGW=256/EGS=8/SEW=32 fixed (a new EGS, distinct
  from every earlier Zvk* op). Validated against the full GB/T 32905-2016 Example 1 and Example 2
  round-by-round traces (via IETF draft-sca-cfrg-sm3, since no built-in .NET SM3 oracle exists) —
  both single-instruction traces (isolating the round math/element ordering) and full multi-block
  end-to-end digests (Example 2 crosses a block boundary, exercising the feed-forward XOR
  finalization `V_(i+1) = CF(V_i, B_i) xor V_i`, distinct from SHA-2's modular addition). Confirmed
  element ordering against the RISC-V Sail reference model (github.com/riscv/sail-riscv,
  `model/extensions/vector_crypto/zvksh_insts.sail` + `model/extensions/V/vext_utils_insts.sail`'s
  `get_velem_oct_vec`/`write_velem_oct_vec`/`vrev8`) rather than the spec's own prose tables, which
  use the opposite left-to-right listing convention from every other instruction in the same
  document. `vsm3c.vi`'s round function is implemented in the "obvious" plain (A,B,C,D,E,F,G,H)
  order rather than porting the Sail source's own shuffled return-vector shape verbatim — both are
  output-equivalent (verified byte-exact against the traces), the plain form just doesn't require
  resolving a Sail vector-literal indexing detail this port doesn't otherwise need.
- [x] Zvkg extension (vector GCM/GMAC): `vghsh.vv` (one GHASH add-multiply iteration,
  Yi+1 = (Yi ^ Xi) * H over GF(2^128)) and `vgmul.vv` (one GHASH multiply, Y * H — sharing
  `vaesem.vv`/`vsm4r.vv`'s funct6, disambiguated by a hardcoded vs1=0x11). EGW=128/EGS=4/SEW=32,
  the same shape as Zvkned/Zvksed, but the first Zvk* op where the whole 128-bit element group is
  one GF(2^128) polynomial with no sub-word decomposition — and the first with no register-overlap
  reserved encoding at all (confirmed against the Sail encdec guard, which calls no
  `zvk_valid_reg_overlap` for either op, unlike every earlier Zvk* instruction). Validated against
  the McGrew-Viega GCM specification's Test Case 4, which — unlike NIST SP 800-38D — publishes the
  raw intermediate `GHASH(H, A, C)` value directly, letting `vghsh.vv` be chained across real
  nonzero AAD/ciphertext/length blocks and checked byte-exact without needing a full AES-CTR
  encryption harness. `vgmul.vv` cross-checked against `vghsh.vv` called with an all-zero vs1.
- [x] Zvbb/Zvbc/Zvkb extensions (vector basic bit-manipulation / carryless multiply): `vandn`,
  `vrol`/`vror` (`.vv`/`.vx`, plus `.vi` for `vror`), `vwsll` (`.vv`/`.vx`/`.vi`), the VXUNARY0 unary
  group `vbrev8.v`/`vrev8.v`/`vbrev.v`/`vclz.v`/`vctz.v`/`vcpop.v`, and `vclmul`/`vclmulh`
  (`.vv`/`.vx`). Architecturally distinct from every earlier Zvk* family: these live on the
  standard OP-V opcode (0x57), operating per-element (EEW=SEW) like the base V-extension integer
  ALU/multiply ops, not the dedicated crypto opcode (0x77) with element-group (EGW/EGS/
  `get_velem`) semantics — so they extend the existing `VIntOp`/`VWideOp` op-family shapes rather
  than the crypto element-group infrastructure. Zvkb is a proper subset of Zvbb (`vandn`,
  `vbrev8`, `vrev8`, `vrol`, `vror`) with no encodings of its own, confirmed directly from the
  spec text, so implementing Zvbb covered it with zero additional code. `vror.vi`'s encoding
  steals bit 26 (normally the funct6 LSB) as immediate bit 5 — handled by intercepting the raw
  bit pattern before the generic funct6-based dispatch runs, since the generically-computed
  funct6 otherwise folds to the same value as `vrol.vv`/`vx`'s real funct6. Uncovered and fixed
  two latent bugs while adding SEW=64 support (required by `vrol`/`vror`/`vclz`/etc. per spec):
  `ApplyVIntOp`'s bit-mask computation silently produced 0 at SEW=64 (C#'s ulong-shift-count-
  mod-64 rule turns `1UL << 64` into `1UL << 0`), invisible until now since every pre-existing
  `VIntOp` member is carry-safe/low-bit-independent; and `ReadVElement`'s ewBytes==4 path can
  sign-extend a high-bit-set byte through an `int` cast, invisible to arithmetic ops but corrupting
  the new bit-magnitude-sensitive ops (`vclz`/`vctz`/`vcpop`/`vbrev*`), fixed with a defensive
  re-mask at the call site rather than touching the shared read helper. `vclmul`/`vclmulh`
  validated against hand-derived GF(2)[x] polynomial identities (e.g. `(x²+1)(x+1)=0b1111`,
  `(x⁶³+1)² = x¹²⁶+1` landing exactly on the 64-bit half boundary) rather than the implementation's
  own loop. This closes out the entire RISC-V Vector Cryptography Extensions Volume II instruction
  set.
- [x] `FiveStageTrain` runtime-LMUL-aware vector hazard tracking, replacing its blanket rejection
  of element-group vector-crypto ops. `ITooth.RuntimeVectorRegisterSpan(baseRegister, state)` gives
  an in-flight producer's precise LMUL-derived register span (its own LMUL is always already
  resolved by hazard-check time, since any `vsetvli` that set it is strictly older and has already
  reached Execute); `ITooth.MaxRuntimeVectorRegisterSpan(baseRegister)` gives a state-independent
  conservative maximum (architectural max LMUL, 8) for the not-yet-decoded consumer side, whose own
  LMUL could still change from a `vsetvli` sitting in a pipeline latch this very cycle — using the
  live (and, for the consumer, not-yet-applicable) vtype on both sides would under-count that
  consumer's span and miss the hazard. Both exempt vs2 in the three ".vs" scalar-key forms
  (`vaesem.vs`-style, `vaesz.vs`, `vsm4r.vs`), which is a single fixed register regardless of LMUL.
  `VectorRawHazard` became a register-range overlap check (mirroring the codebase's own
  `VGroupOverlap` reserved-encoding idiom) instead of single-register equality.
- [x] Investigated `SuperscalarTrain`/`SmtTrain`/`DaeTrain` not referencing
  `ITooth.VectorDestinationRegister`/`VectorSourceRegisters` at all (flagged while auditing which
  in-order trains needed the `FiveStageTrain` fix above) — **no correctness bug in any of the
  three**, so no fix needed:
  - `SmtTrain`: each hart executes one instruction fully to completion (decode → execute →
    `SideEffect` → PC update) before that hart is eligible to issue again, even within the same
    cycle — hazard-free by construction, same as `SingleCycleTrain`.
  - `DaeTrain`: `IsBarrierClass` is a deny-list (`not IntegerAlu/IntegerMulDiv/Load/Store`), so
    `ToothClass.Vector` is a synchronizing barrier by construction, not by an enumeration that
    could go stale — a vector instruction only executes once both lanes are fully drained,
    directly against live architectural state.
  - `SuperscalarTrain`: issue is strictly in-order and instructions execute functionally at issue
    (`SideEffect` applied synchronously before the next instruction's `Execute` call), so a stale
    vector-register read is structurally impossible. The real gap is narrower than a correctness
    bug: its scoreboard (`_regReadyCycle`) only tracks the scalar `DestinationRegister`, so a
    vector RAW dependency chain issues with no stall cycles charged, and `FuLatencyConfig` has no
    dedicated Vector case (falls to the System default, count=1/latency=1) — an IPC-fidelity gap,
    not a wrong-answer risk, and nothing in the test suite exercises vector code on this train
    today. Not fixed: modeling it meaningfully needs a real per-vector-op latency source (LMUL/EGW/
    op-dependent), which doesn't exist anywhere in the codebase yet — inventing one (e.g. reusing
    `FloatingPoint`'s latency as a stand-in) would produce authoritative-looking IPC numbers
    resting on a fabricated constant, worse than the current honest "vector timing unmodeled."
    Revisit if `SuperscalarTrain` ever gains a real vector timing model to hang the stall on.

## Analysis

- [x] SMARTS: systematic statistical sampling with functional warming between detailed sample
  windows — live-switches architectural state between a functional fast-forward train
  (`SingleCycleTrain`, sharing cache/TLB/branch-predictor instances with the detailed train
  rather than re-warming from scratch) and a detailed warm-then-measure window per sampling
  unit, instead of a full-checkpoint-per-unit design (too expensive at the paper's n≈10,000
  scale). `SmartsDriver`/`SmartsStatistics` (`src/Core/Pipeline/`) are ISA-agnostic; `FiveStage`
  and `Ooo` detailed-train factories both supported (`Drain()`-before-handoff for OoO's
  in-flight ROB/IQ/LQ/SQ state). Wired into the Runner CLI (`--smarts <U> <W> <K>`,
  `--smarts-n`, `--smarts-offset`) via `Experiment.RunSmarts`, printing mean CPI, coefficient of
  variation, 95%/99.7% confidence intervals, and the paper's two-step `n_tuned` recommendation.
  argv/Linux-ABI workload support (`--smarts-argv`, mirroring `--simpoint-argv`'s psABI
  initial-stack + `LinuxSyscallEmulator` pattern) lets real compiled binaries, not just bare-metal
  HTIF ELFs, be sampled — needs no per-pass syscall-state serialization the way `--simpoint-argv`
  does, since `SmartsDriver.Run` reuses one mechanism (and whatever `ISyscallHandler` it carries)
  for the whole run instead of recreating it at each checkpoint/measure boundary. Known gaps: RAS
  state isn't warmed by the functional pass (it lives outside `IBranchPredictor`); timing-dependent
  CSRs (e.g. `mcycle`) can't be sampled faithfully, since functional fast-forward doesn't advance
  cycle count the way detailed windows do — inherent to the sampling approach, not a gap to close.
  — Wunderlich et al., ISCA 2003

## Multi-threaded Simulation

LoopPoint (checkpoint-driven sampling for multi-threaded workloads, the multi-hart counterpart to SimPoint)
requires real pthread/OpenMP-capable execution Horologium doesn't have today, so it's broken into ordered,
independently actionable stages rather than one item. Horologium is natively execution-driven, so the paper's
own PinPlay pinball/constrained-vs-unconstrained-replay apparatus is simply not needed here — that's not a gap,
just infrastructure this design doesn't require.

- [x] Thread pointer (`tp`, x4) + PT_TLS support: real AT_PHDR/AT_PHENT/AT_PHNUM auxv values
  (`IElfWorkload.PhdrAddress`/`PhEntrySize`/`PhNum`) now reach the initial stack, so musl's own
  `_start`/`__init_tls` sets `tp` correctly itself — no host-side PT_TLS parsing needed. Validated
  against a real compiled `__thread`-using binary (`TestBinaries/tls_probe.c`).
- [x] `clone()` thread creation + dynamic hart activation, for `MultiHartKernel` (the functional/bare-metal
  driver LoopPoint's profiling pass needs) — `LinuxSyscallEmulator` gained a `clone()` case (`IHartSpawner`),
  `MultiHartKernel` gained pre-allocated dormant hart slots. `MultiHartPipeline` (the detailed-timing-pipeline
  driver LoopPoint's warm+measure phase will need) doesn't have the same support yet — still open.
- [x] `futex()` FUTEX_WAIT/FUTEX_WAKE for `MultiHartKernel` (the functional/bare-metal driver LoopPoint's
  profiling pass needs) — poll-based, not queue-based: `FUTEX_WAIT` returns `-EAGAIN` immediately if the word
  already differs from the expected value, otherwise `ExecuteResult.RequestBlock` makes the driver re-run the
  same `ecall` next tick without advancing PC until it changes; `FUTEX_WAKE` is a no-op returning 0. Sound
  without any waiter-identity/wait-queue bookkeeping because real futex callers (musl's mutex/cond/barrier
  code) always re-validate the guarded condition themselves rather than branching on the wait's return value.
  `MultiHartPipeline` dynamic hart activation + threading block-semantics through the detailed pipeline
  trains' commit stages is deferred to the multi-hart checkpoint item below, since injecting a spawned/restored
  hart's state into a live pipeline train is the same problem checkpoint-restore already has to solve.
- [x] Per-hart `gettid`/thread-exit semantics: `ISyscallHandler.Handle` gained a `hartId` parameter
  (`Rv32Executor.HartId`, the same field already used for LR/SC routing); `gettid`/`set_tid_address` now
  return `hartId + 1` (never 0) instead of a hardcoded 1, and `SYS_exit`/`SYS_exit_group` are distinguished
  via a new `ExecuteResult.RequestHaltAll` flag (`exit` halts only the calling hart, `exit_group` halts
  every hart of a `MultiHartKernel` run). `clone()`'s returned tid uses the same `+1` convention as its
  `SpawnHart` slot index — this only agrees with a later `gettid()` call from the spawned hart if the
  caller constructs that slot's mechanism with a matching `hartId`, which neither this class nor
  `MultiHartKernel` enforces (documented in both places, and exercised by a dedicated consistency test).
- [x] OpenMP/pthreads-capable RISC-V toolchain + test fixture: `flake.nix` needed **no change** —
  `pkgsCross.riscv64-musl`'s GCC already ships `libgomp` and links `-pthread`/`-fopenmp` static
  binaries cleanly (confirmed by compiling and linking real probes with it). Added
  `TestBinaries/pthread_probe.c` (real `pthread_create`+`pthread_join`) and a decisive integration
  test (`Tests/RiscV64/System/PthreadProbeTests.cs`) booting it through `MultiHartKernel`. Tracing the
  compiled binary's own disassembly surfaced a real gap the earlier `clone()`/`futex()` items missed:
  real musl passes `&__thread_list_lock` (a global lock, not the exiting thread's own tid word) as
  `clone()`'s `ctid`, relying on the kernel's `CLONE_CHILD_CLEARTID` as a backstop to release that
  lock on exit — without it, a second thread hangs forever polling that lock once a prior one exits.
  Implemented (`LinuxSyscallEmulator` records `ctid` per hart at `clone()` time, writes 0 to it on
  that hart's own `SYS_exit`/`SYS_exit_group` — no explicit wake needed beyond the write, since the
  poll-based `futex()` waiter notices the value changed on its own). An initial hypothesis that
  `gettid`'s `hartId + 1` tid values were *also* load-bearing (colliding with a sentinel range in
  musl's join loop) did not survive an isolating re-test and was dropped — tids stayed at `hartId + 1`.
- [x] Loop-header region-boundary detection: `LoopHeaderTracker` (`src/Core/Pipeline/LoopPointAnalysis.cs`),
  a standalone `ICommitObserver` sibling to `BbvProfiler` (not yet wired into its interval slicing — that's
  the per-thread-BBV item below), identifies a loop header as the target of a backward, direct, non-call
  transfer (`IDecoder.GetFetchHint`'s `BranchTarget.HasValue && !IsCall` — excludes indirect JALR
  returns/virtual calls and direct backward calls alike, both confirmed necessary by a discriminating test),
  producing the paper's `(PC, count)` markers. `count` is backward-taken re-entries, not total iterations
  (the header's first, fall-through entry isn't a discontinuity and so isn't observable in a single pass) —
  a deliberate, documented streaming-design tradeoff, not a bug. `rangeStart`/`rangeEnd` are address-range
  scoping only (a basic sanity bound); separating user code from statically-linked library code sharing the
  same segment is the spin-loop-filtering item below, not this one.
- [x] Spin-loop filtering: `IElfWorkload.EnumerateSymbols()` exposes every named, non-zero-size `.symtab`
  entry; `SyncLibrarySymbols.ExcludedRanges` classifies them into `[Start, End)` ranges by name prefix
  (`__tl_`/`__vm_`/`__wait`/`__lock`/`pthread_`/`sem_`/`gomp_`/etc., verified against
  `pthread_probe.elf`'s real musl symbol table); `LoopHeaderTracker` gained an `excludedRanges` constructor
  parameter so a header whose PC falls in one is never counted, mirroring the paper's exclusion of
  busy-waiting from loop-based work counting while still executing that code normally. Beyond the
  synthetic-fixture unit tests, this is now proven end-to-end under real multi-hart contention:
  `SpinLoopFilteringEndToEndTests` runs `pthread_probe.elf` through `MultiHartKernel` (3 harts, real
  `clone()`/`futex()`/thread-list-lock contention from two concurrent `pthread_create`/`pthread_join`
  pairs) and shows real markers land inside `__tl_lock`'s etc. ranges without exclusion (89/61/56 markers
  per hart, 22/3/6 of them inside excluded ranges) and disappear exactly (and only) those markers with
  exclusion wired in. `hello64_musl.elf` (single-threaded) could never show this — an uncontended lock's
  retry loop is never taken backward — which is why the real proof needed the multi-hart fixture below.
- [x] Flow-control profiling scheduler: **no new code** — the paper's "flow-control" (Section III-B)
  is a Pintool that restricts thread forward progress specifically to correct skew from a real, non-
  deterministic host OS scheduler running Pin instrumentation during profiling ("thread imbalance...
  caused by external events on the host processor and... unrelated to the analysis environment").
  `MultiHartKernel.Step()` and `MultiHartPipeline.Run()` already advance every non-halted, non-dormant
  hart by exactly one instruction/cycle per call, deterministically — no running hart can ever retire
  more instructions than another over the same tick window, so the specific artifact flow-control
  exists to correct cannot arise here; building a balancing policy on top would be correcting a problem
  the architecture doesn't have. `MultiHartPipeline.RunConcurrent` (`Parallel.For` real host threads,
  the only mode with actual host parallelism) still holds the invariant because each tick is a barrier —
  `Parallel.For` blocks until every hart's `StepCycle()` for that tick completes before the next tick
  starts, so host scheduling can only reorder work *within* a tick, never let one hart get ahead by a
  whole cycle. Verified (not just argued) with a decisive test reading through independent
  architectural state rather than the scheduler's own tick counter: two harts each running an infinite
  counting loop (`addi x1,x1,1; jal x0,-4`), asserting their `x1` values stay equal after every
  `Step()`/after `RunConcurrent` completes (`MultiHartKernelTests.TwoHarts_InfiniteCountingLoops_StayInLockstep`,
  `MultiHartPipelineTests.RunConcurrent_TwoIndependentInfiniteCountingLoops_StayInLockstep`).
- [x] Per-thread loop-iteration BBV + multi-thread region clustering: `MultiHartKernel` gained per-hart
  commit-observer wiring (`SetObserver`) — it had none before, despite being LoopPoint's own profiling-pass
  driver. `MultiHartLoopPointProfiler` (`src/Core/Pipeline/LoopPointAnalysis.cs`) coordinates one
  `LoopHeaderTracker` + one `BbvProfiler` per hart: a region closes at the next loop-header hit (on any
  hart) after a shared global instruction counter reaches `targetGlobalInstructions` — the paper's
  "approximately N x 100M global (all-threads) instructions", incremented only for non-excluded commits,
  matching spin-loop filtering's "not work done". At closure every hart's accumulated interval is cut,
  per-thread-normalized to a fixed integer total, namespaced into a disjoint key range
  (`(hartId << 48) | pc`) to avoid same-PC collisions across identical-binary threads, and concatenated
  into one region vector — reusing `SimPointAnalysis.Analyze` completely unchanged. `BbvProfiler` gained a
  public `CutInterval()` (refactored out of its existing fixed-instruction-count auto-cut logic) so an
  externally-triggered region boundary correctly splits an in-progress basic block and continues tracking
  its remainder into the next region, instead of `Complete()`'s one-shot-flush semantics silently losing
  block continuity — caught by a focused test before it could corrupt every subsequent region's BBV.
  `CompositeCommitObserver` (mentioned in this item's original wording) turned out unneeded: the
  coordinator's own per-hart adapter already does the fan-out internally, so it was deleted as dead code
  rather than kept for a design the actual implementation didn't end up needing.
  Proven against real input, not just synthetic fixtures: `MultiHartLoopPointProfilerRealElfTests` runs
  the full coordinator on `pthread_probe.elf` (with `SyncLibrarySymbols.ExcludedRanges` wired in), spanning
  regions with genuinely varying active-hart-counts (single-threaded startup/teardown vs. 3-hart steady
  state), and feeds the resulting `RegionBbvs` straight into `SimPointAnalysis.Analyze` — this is the test
  that finally exercises spin-loop filtering's exclusion of BBV weight and the global work-target counter
  under real contention (`SpinLoopFilteringEndToEndTests`, above, proved header-marker suppression only).
- [x] Multi-hart checkpoint capture/measure + warmup: `MultiHartCheckpoint`
  (`src/Core/Mechanism/MultiHartCheckpoint.cs`) captures N harts' architectural state (PC, privilege,
  registers, ISA blob) plus **one** shared-memory blob and **one** optional shared syscall-handler blob —
  not N of either, matching every established multi-hart config (threads share memory/fd-table/brk by
  `CLONE_VM|CLONE_FILES`, and duplicating either N-fold would let per-hart copies drift independently on
  restore, which real shared state never does). `MultiHartWarmupMeasureDriver` extends
  `WarmupMeasureDriver`'s pattern to N trains, bounding warmup/measure by a *global* (summed-across-harts)
  instruction count, matching `MultiHartLoopPointProfiler`'s own accounting. `LinuxSyscallEmulator.WriteState`/
  `ReadState` gained `_childCleartid` serialization — previously excluded with a comment flagging exactly
  this item as the reason; without it a restored hart's later exit would clear the wrong (or no) `ctid`
  address, reproducing the `__thread_list_lock` hang class `pthread_probe.elf` originally surfaced. Proven
  with a cold-baseline comparison (not just warm-equals-restored, which would pass even with a broken
  `RestoreInto` — see `feedback_checkpoint_roundtrip_theater`): captured mid-run on the functional
  `MultiHartKernel`, restored into fresh `FiveStageTrain`s driven by `MultiHartPipeline`, and compared
  against a `FiveStageTrain`-only cold run of the same program to completion — two harts with different
  iteration counts to different addresses, so a swapped-hart restore bug would produce a visibly wrong
  final value rather than coincidentally matching. A second test drives a real, stateful
  `LinuxSyscallEmulator` (brk moved, `_childCleartid` set via `clone()`) through the checkpoint's shared-
  handler-blob path specifically, closing the gap the hart-state-only tests left uncovered.
  **What this does not prove**, split into its own item below rather than silently folded in: measuring a
  genuinely *blocking* multi-threaded region (a real futex wait) under detailed pipeline timing — that
  needs `ExecuteResult.RequestBlock` support in the detailed trains, confirmed to require real
  squash-and-refetch logic (see below), not covered by anything here. `MultiHartPipeline` dynamic hart
  activation (a measured region that itself calls `clone()`) is likewise out of scope — a checkpoint taken
  at a LoopPoint region boundary has all harts already spawned, so it isn't needed for the common case.
- [x] `RequestBlock` support in `FiveStageTrain`: squash-and-refetch, the same shape as branch-
  misprediction recovery, not a simple stall — a still-blocked syscall (`ExecuteResult.RequestBlock`)
  is squashed at the EX→MEM and MEM→WB boundaries (mirroring the existing halt/trap/return-from-trap
  double-squash) and, once it reaches WB, redirects Fetch back to its own Pc instead of retiring,
  mirroring `MultiHartKernel`'s functional retry-in-place (`StepHart` returns without advancing
  `state.Pc` when blocked). `MemWbLatch` gained a `RequestBlock` field; `WritebackStage` gained
  `BlockRedirect`, checked alongside `TrapRedirect` in `FiveStageTrain.RunCycle`'s third squash round.
  Proven two ways, not just single-hart retry-in-place: the original two tests use a self-clearing
  stub handler (proves squash/refetch/resume), and a third, `BlockedEcall_ResumesWhenAnotherHartClearsTheSharedFutexWord`
  (`Tests/Pipeline/FiveStageRequestBlockGuardTests.cs`), runs two `FiveStageTrain`s under
  `MultiHartPipeline` sharing one `FlatMemory` — hart 0 blocks re-reading a shared word each retry,
  hart 1 (a plain, unrelated mechanism) writes it and halts — proving the actual cross-hart futex
  hand-off this feature exists for, not just a mechanism proven in single-hart isolation. Confirmed
  discriminating by temporarily making the handler cache its first read instead of re-reading memory
  each retry: the test failed as expected.
- [x] `RequestBlock` support in `OooTrain`: unlike `FiveStageTrain`'s trap-shaped fix, this follows the
  *load-violation* precedent instead — the blocked entry is left at the ROB head (never `_rob.Retire()`'d),
  so `StepFlush`'s existing `InOrder().Reverse()` walk-back un-renames it for free, with no
  `_pendingRollback*` bookkeeping needed. `RobEntry`/`ExecResult` gained a `RequestBlock` field (threaded
  through `ExecuteOne`'s return and `StepComplete`'s CDB-broadcast copy, alongside `RequestHalt`); a new
  `StepCommit` case does no `CommitRegisters`/retired-count-increment/`State.OnRetire()` (it never
  broadcast a register value onto the CDB, so it contributed no committed work) and calls
  `SetFlush(head.Pc)`. `RobEntry.Clear()` resets the new field. Proven the same three ways as
  `FiveStageTrain`: two single-hart tests with a self-clearing stub handler, plus a cross-hart
  `MultiHartPipeline` composition test (hart 1's plain `sw` clears hart 0's blocked ecall through shared
  `FlatMemory`) — all three confirmed to fail with the `StepCommit` case removed, then pass restored.
- [x] `RequestBlock` support in `CprTrain`: `CprTrain` has no ROB at all (checkpoint-epoch tracking
  instead), but the fix is structurally the same *load-violation* shape as `OooTrain`'s, made exact by a
  fact already true of this train — syscalls are always `IsSerialized` (`ToothClass.System`), which
  forces a fresh checkpoint both before and after one, so a still-blocked `ecall` is always the sole
  entry of its own checkpoint. `StepComplete` gained a `RequestBlock` branch (checked before marking the
  entry complete) that calls the existing `ScheduleRecovery(entry.InstrId, entry.CheckpointSeq, 0, false)`
  — the same helper a memory-order violation uses — which squashes back to that checkpoint (i.e. exactly
  this instruction) and refetches its own Pc, with no new field needed on `CheckpointEntry` (the check
  happens and discards inline, never persisted). `ExecResult` gained a `RequestBlock` field (threaded
  through `ExecuteOne`'s return, alongside `RequestHalt`). `Tests/Pipeline/CprRequestBlockGuardTests.cs`
  mirrors the `OooTrain` tests (2 single-hart stub-handler + 1 cross-hart `MultiHartPipeline` composition
  test using dial-board `recoveries`/`retired` counters in place of a commit observer, since `CprTrain`'s
  constructors don't accept one) — all 3 confirmed to fail with the `StepComplete` branch removed, then
  pass restored.
  **Found and fixed along the way: `CprTrain`'s PRF was never seeded from `ArchState.IntegerRegisters` at
  construction** — see the dedicated item immediately below.
- [x] `CprTrain` PRF wasn't seeded from `ArchState.IntegerRegisters` at construction — the same bug class
  already fixed for `OooTrain` in commit `832f1ab`, never ported to `CprTrain`'s own separate
  `PhysicalRegisterFile`/`RenameMap`. A pre-`Run()` register write (the same pattern a checkpoint restore
  performs) was invisible to any instruction sourcing that register until a full flush happened to occur
  first (the seeding logic already existed verbatim inside `ApplyFullFlush`'s post-trap resync, but
  nothing called it at `Wind()`/construction time). Confirmed with an isolated repro (an addi-synthesized
  address register worked; the identical program with the register pre-set via
  `ArchState.IntegerRegisters.Write()` silently sourced 0), fixed by mirroring `OooTrain.Wind()`'s exact
  shape (`CprTrain.Wind()` now seeds the PRF from `State.IntegerRegisters` before scheduling the first
  tick), and proven with a new regression test (`CprTrainTests.PreRunRegisterWrite_IsVisibleToExecution`)
  confirmed to fail without the fix and pass with it. This was blocking `CprTrain` from being a valid
  LoopPoint (or any other checkpoint-restore) measurement target.
- [x] `RequestBlock` support in `DaeTrain`: the only one of the four so far needing **no rollback at
  all**. `ecall` is barrier-class (`IsBarrierClass`), and `ExecuteBarrier` only ever runs once both the
  Access and Execute lanes are fully drained (`_accessQueue.Count == 0 && _executeQueue.Count == 0`) —
  the front end never dispatches while a barrier is staged, so nothing younger can ever be in flight when
  one executes, and its own undo-log entries were just cleared immediately before. A still-blocked
  barrier is therefore a pure retry-in-place: a new `RequestBlock` check right after `Execute()` skips
  retiring/`SideEffect`/register-write/Pc-advance and sets both `State.Pc` and `_fetchPc` back to the
  barrier's own Pc, so it's re-decoded and re-dispatched as a fresh instruction next cycle.
  `Tests/Pipeline/DaeRequestBlockGuardTests.cs` mirrors the other trains' tests (2 single-hart
  stub-handler + 1 cross-hart `MultiHartPipeline` composition test, the latter needing no PRF-seeding
  workaround since `DaeTrain` reads/writes `ArchState.IntegerRegisters` directly with no separate
  physical register file) — all 3 confirmed to fail with the fix removed, then pass restored.
- [ ] `RequestBlock` support in the remaining detailed pipeline trains (`SuperscalarTrain`/`SmtTrain`):
  same squash-and-refetch shape as the four trains above, but each needs its own translation.
  `SuperscalarTrain` is in-order functional-execute (like `DaeTrain`'s barriers) and is expected to be a
  near-pure retry-in-place case; `SmtTrain` is multi-hart *within a single train* (interleaved threads
  sharing one core), so its cross-hart futex-wake test can't reuse the `MultiHartPipeline`-of-two-trains
  shape the other four tests do — it needs one `SmtTrain` instance with two internal harts and per-hart
  syscall-handler wiring instead. Survey each on its own terms rather than assuming either ports directly
  from what's already done.
- [ ] `MultiHartWarmupMeasureDriver`'s global-instruction-bounded measure loop has no way to detect a
  hart that's permanently spinning on `RequestBlock` (its `StepCycle()` keeps returning `true` forever,
  since it's retrying, not halted) — if the waking hart halts first with no further global-instruction
  progress from the blocked one, the loop never terminates. Not reachable today (the `--looppoint` CLI
  path is single-hart only), but it's the first thing a real multi-hart measure pass will hit once
  `RequestBlock` support lands in the trains `MeasureLoopPointCheckpoints` actually uses.
- [x] Weighted-multiplier runtime extrapolation + Runner CLI wiring: `LoopPointRuntimeExtrapolation`
  (`src/Core/Pipeline/LoopPointRuntimeExtrapolation.cs`) implements Eq. 1/2 — per-representative multiplier
  from filtered-instruction-count ratios (not `SimulationPoint.Weight`, which assumes fixed-length intervals,
  false for LoopPoint's data-dependent regions), then a weighted sum of representative runtimes.
  `MultiHartLoopPointExperiment` (`src/Isa/RiscV32/Analysis/`) orchestrates capture → cluster → checkpoint →
  measure → extrapolate: every region boundary's `MultiHartCheckpoint` is captured opportunistically during
  the single profiling pass (`MultiHartLoopPointProfiler`'s new `onRegionBoundary` hook), since which regions
  turn out representative isn't known until clustering runs afterward. `--looppoint`/`--looppoint-warmup`/
  `--looppoint-argv` wired into Runner, mirroring `--simpoint`'s flags; single-hart only for now (real
  multi-hart pthread measurement needs the `RequestBlock` item above, since `pthread_join` blocks). Validated
  against `simpoint_kernel.elf`: the extrapolated total (4,736,068 ticks) matched a full cold `FiveStageTrain`
  run to completion (4,738,700 ticks) within 0.06%. Found and fixed a real livelock along the way: detailed
  pipeline trains (`FiveStageTrain`, likely `OooTrain` too) track fetch PC separately from `IArchState.Pc`,
  seeded only once at construction — a checkpoint restore that doesn't pass the restored PC as the train's
  own constructor `entryPoint` leaves fetch stuck at the wrong address forever, silently producing zero
  progress rather than an error; `MultiHartCheckpoint.PcOf(hartId)` now exposes what
  `Experiment.MeasureSimPointCheckpoints` already threaded through for the single-hart case.

## µops

- [ ] µop cache (decoded instruction cache / loop buffer): cache decoded µop bundles so the front-end skips re-decode on
  repeated loops.
- [ ] Macro-fusion: fuse compare+branch pairs into a single issue-slot µop (as in Intel Sandy Bridge onward).

## Cache Prefetching

- [ ] Real prefetch-eviction feedback: `SetAssociativeCache` has no notion of `IPrefetcher` today and no per-line
  "resident via prefetch, never demand-touched" bit, so nothing can tell a prefetcher when one of its lines got
  evicted unused. `PpfPrefetcher` needs exactly this signal (the paper's third training trigger) and currently
  approximates it via its own 1024-entry Prefetch Table's slot-overwrite — table pressure standing in for real
  cache-capacity pressure, documented as a fidelity limit in the class docs. A lighter-weight real version: an
  optional `Action<ulong>?` eviction callback on `SetAssociativeCache` (default null, near-zero cost when unset),
  wired up in `MemoryLayers.Build` only for configs that actually attach a prefetcher wanting it, rather than
  threading `IPrefetcher` through the (already long) cache constructor. Revisit if the table-pressure proxy is ever
  shown to mispredict in a case that matters — `SetAssociativeCache` is shared by every ISA/cache level/RTL policy,
  and PPF would be the only one of ten prefetchers consuming it, so it's not worth the blast radius speculatively.
- [ ] MLOP (multi-lookahead offset prefetcher): BOP generalized to score offsets at multiple lookahead depths; DPC-3
  winner. — Shakerinava et al., DPC-3 2019

## Memory System

- [ ] Cache compression: base-delta-immediate (BΔI) compressed caches with variable effective capacity. — Pekhimenko
  et al., PACT 2012

## Security

- [ ] Transient-execution defense modeling: invisible speculative loads (InvisiSpec) and speculative taint tracking
  (STT); measure the IPC cost of each defense on the OoO train. — Yan et al., MICRO 2018; Yu et al., MICRO 2019

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
