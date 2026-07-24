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
