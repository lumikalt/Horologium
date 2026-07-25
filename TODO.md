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
  just the per-instruction from-scratch reference. `FiveStageTrain` explicitly rejects
  element-group vector ops (its decode-time hazard list can't express a runtime-LMUL-sized
  register span); `OooTrain` needs no changes, since head-serialization of `ToothClass.Vector`
  already covers it.
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
  Deferred follow-ups: the other Zvk* families (Zvknha/Zvknhb SHA-2, Zvksh SM3, Zvkg GHASH/GMAC,
  Zvbb/Zvbc/Zvkb vector bitmanip) — none implemented yet. EGW=256 (SHA-512/SM3) would need a second
  element-group infrastructure pass (2 physical registers per group instead of 1). `FiveStageTrain`
  gaining runtime-LMUL-aware vector hazard tracking (rather than rejecting) is also deferred.

## Analysis

- [ ] SMARTS: systematic statistical sampling with functional warming between detailed sample windows. — Wunderlich
  et al., ISCA 2003
- [ ] LoopPoint: checkpoint-driven sampling methodology for multithreaded workloads; the multi-hart counterpart to
  SimPoint. — Sabu et al., HPCA 2022

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
