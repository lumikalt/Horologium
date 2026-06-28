# To-Do

## Face

- [ ] Rollback/Back step: single-step backward in the assembler debugger (requires snapshot-based undo or reverse simulation).
- [ ] L2 and L3 caches.
- [x] Assembler to simulate RISC-V in-place.
- [ ] Compile from C to disassembly and simulate that.
- [ ] Cache and virtual addressing visualization.
- [x] Execution visualization: Argos-style pipeline transaction viewer (scrollable waterfall; rows = in-flight
  instructions, columns = cycles, cells = pipeline stage). (inspired by Olympia/Sparta)
- [x] Complete Light Mode implementation — missing dark background on the chart background before execution, on the PEvents graph, on the decoding guide for the Assembly.
- [ ] Work with other ISAs, not just RISC-V.

## CHIP8

- [ ] Screen and keyboard.

## RISC-V

- [x] 64-bit support — RiscV64 project with `Rv64Mechanism`/`Rv64Decoder`/`Rv64Executor`/`Rv64ArchState` extending RiscV32 via inheritance. RV64I W-suffix ops (ADDW/SUBW/SLLW/SRLW/SRAW/ADDIW/SLLIW/SRLIW/SRAIW), new loads/stores (LD/LWU/SD), 6-bit shifts, 64-bit SLT/BLT comparisons, LW sign-extension.
  - [ ] ELF64 loader (`Rv64ElfLoader` / `Rv64ElfWorkload`) for running RV64 binaries.
  - [ ] Sv39 page-table walker for RV64 virtual memory.
  - [ ] RV64 M extension (MUL[H/HU/HSU], DIV[U], REM[U] on 64-bit operands).
  - [ ] RV64 F/D extension: 64-bit FP conversions (fcvt.l.s, fcvt.lu.s, fcvt.s.l, etc.).
- [ ] 128-bit support.
- [ ] Enable or disable specific extensions.

### Extensions

- [ ] Continue extending the V extension: strided/indexed loads-stores (VLSE, VSSE, VLUXEI, VSUXEI), reduction ops (
  vredsum, vredmax, …), widening/narrowing integer ops, integer multiply/divide (vmul, vmulh, vdiv), FP vector ops (
  vfadd, vfmul, vfmacc, …), slide and gather/scatter.
- [x] Zba (address generation): sh1add, sh2add, sh3add. 3 instructions.
- [x] Zbb (basic bit manipulation): andn/orn/xnor, clz/ctz/cpop, min/minu/max/maxu, rol/ror/rori, sext.b/sext.h/zext.h, orc.b/rev8. 18 instructions.
- [x] Zbs (single-bit ops): bclr/bext/binv/bset (register) and bclri/bexti/binvi/bseti (immediate). 8 instructions.
- [x] Zicond (integer conditional ops): czero.eqz, czero.nez. 2 instructions.
- [x] Zbc (carry-less multiplication): clmul, clmulh, clmulr. 3 instructions.
- [ ] D extension (RV32D): double-precision FP registers and arithmetic (fadd.d, fsub.d, fmul.d, fdiv.d, fsqrt.d, fmadd.d, …, fcvt.d.w, fcvt.d.wu, fcvt.w.d, fcvt.wu.d, fmv.x.d, fmv.d.x — 26 instructions).
- [ ] Zfh / Zfhmin: half-precision FP (fadd.h, fmul.h, fcvt.h.s, …). Zfhmin is the minimal convert-only subset.
- [ ] Zfinx / Zdinx / Zhinx: FP operations in integer register file (no separate F/D register file). Simplifies embedded implementations.
- [x] Zicbom / Zicboz / Zicbop: cache management operations. Zicbom: cbo.inval/clean/flush (opcode=0x0F, funct3=2, bits[24:20]=0/1/2) — NOP in simulation. Zicboz: cbo.zero (bits[24:20]=4) — zeros 64 bytes at cache-line-aligned address. Zicbop: prefetch.i/r/w already work as NOPs via existing ORI path (opcode=0x13, funct3=6, rd=0).
- [x] Zawrs: wrs.nto (imm=0x00D) and wrs.sto (imm=0x01D) — NOP in single-core simulation (SYSTEM space, funct3=0).
- [x] Zimop: mop.r.N and mop.rr.N — always return 0 in rd (SYSTEM opcode, funct3=4; detected by CSR address pattern).
- [ ] Zcmop: compressed may-be-operations (c.mop.N) — same idea for 16-bit encoding space.
- [x] Zicntr: hardware performance counters — cycle/cycleh (0xC00/0xC80), time/timeh (0xC01/0xC81, reads 0 — no CLINT), instret/instreth (0xC02/0xC82), and their M-mode mirrors mcycle/mcycleh/minstret/minstreth. User-level shadows alias M-mode counters. Pipeline trains call IArchState.OnCycle()/OnRetire() hooks; RvArchState increments CsrFile counters with 32-bit carry into high halves.
- [ ] Zihpm: hardware performance monitor — hpmcounterN/hpmcounterNh (N=3–31) and hpmeventN CSRs.
- [ ] Zabha: byte/halfword atomics (amoadd.b, amoswap.h, …) — extends A extension to sub-word granularity.
- [ ] Zacas: compare-and-swap (amocas.w, amocas.d, amocas.q).
- [ ] Scalar crypto: Zknd/Zkne/Zknh (NIST AES encrypt/decrypt, SHA-2), Zksd/Zkse/Zksh (ShangMi SM4/SM3), Zkr (entropy source / GetNoise CSR). Grouped as Zkn (NIST suite) and Zks (ShangMi suite).
- [ ] Vector bit manipulation (Zvbb): vbrev8, vrev8, vandn, vclz, vctz, vcpop, vrol, vror, …
- [ ] Vector carry-less multiply (Zvbc): vclmul, vclmulh.
- [ ] Vector crypto (Zvkn / Zvkg / Zvks): vectorised AES, SHA, SM3/SM4 round instructions.
- [ ] Zvfh / Zvfhmin: vector half-precision FP (vfadd.h, vfmul.h, …). Zvfhmin is convert-only.
- [ ] H extension (hypervisor): VS-mode, VU-mode, hfence instructions, two-stage address translation (G-stage), virtual CSRs (hstatus, hedeleg, hideleg, htval, htinst, hgatp, …). Large; ~40 new CSRs.
- [ ] Svnapot / Svpbmt / Svadu / Svinval: Sv32/Sv39 page-table extensions (naturally-aligned power-of-two superpages, page-based memory types, hardware A/D updates, local/global sfence.inval).
- [ ] Smaia / Ssaia: Advanced Interrupt Architecture (APLIC, IMSIC, direct MSI delivery; replaces PLIC for scalable multi-hart interrupt routing).
- [ ] Smstateen: state-enable CSRs (mstateen0–3, hstateen0–3, sstateen0) — per-privilege gating of extension state access.
- [ ] Smnpm / Ssnpm: pointer masking (M-mode and S/U-mode) — ignore top N bits of pointers for tagged-memory schemes.
- [x] Supervisor and user-privileged execution (trap delegation, data-path Sv32, page faults, interrupt dispatch).
- [x] Instruction fetch translation: wire pipeline fetch stages through Sv32Walker so InstructionPageFault is reachable.
- [x] UVE (Unlimited Vector Extension) — 1D SAXPY subset: `ss.ld.w`, `ss.st.w`, `so.v.dp.w`, `so.a.mul.fp`, `so.a.add.fp`, `so.b.nc`. Encodings in custom-0/custom-1 opcodes. Verified end-to-end via SAXPY integration test through OoO pipeline.
- [x] UVE multi-dimensional streams: `ss.sta.ld.w`, `ss.sta.st.w`, `ss.app`, `ss.end` (custom-0 funct3 2–5) and `so.b.ndc.D` (custom-1 funct3 5, dim in rs2 field). `StreamDescriptor` extended to N-dim `StreamDimension[]`. `StreamingEngine` tracks per-dim consume-side pass-complete flags for `so.b.ndc.D`. Verified end-to-end via 2D strided matrix load integration test. `ss.cfg.vec` (funct3 6) decoded and no-op pending vector streaming. `UveStoreStream` extended to `StreamDimension[]`/`Indices[]`; multi-dim store-stream cursors fully implemented and verified via 2D strided copy test.
- [ ] UVE 2 (ISCA 2024): 474 instructions, 16 architectural predicate registers (p0–p15), scatter/gather (`ss.idx.ld.*`, `ss.idx.st.*`), predicated execution (`ss.pfr.*`, `so.a.*.pr`), widening/narrowing conversions, structured gather patterns. Far more complex than UVE 1; defer until UVE 1 is complete.
- [ ] `ss.cfg.vec` effect: deliver vector-width (sub-element-grouped) elements from a load stream into the pipeline instead of scalar floats; required for vector streaming mode.
- [ ] SUM (Supervisor User Memory): honor `sstatus.SUM` so S-mode can deliberately access user pages (PTE.U=1);
  currently S-mode always faults on user pages.
- [ ] Implement the rest of the extensions.

### Analysis

- [x] Per-instruction lifecycle events (PEvents): structured FETCH/DISPATCH/ISSUE/EXECUTE/RETIRE/FLUSH records with
  instruction ID and cycle number, enabling post-hoc filtering, phase analysis, and RTL correlation. (inspired by
  Olympia/Sparta)
- [ ] Region-of-interest simulation: run full timing model only between named ELF symbols or address ranges;
  fast-forward the rest with the single-cycle train.
- [ ] Simulation state checkpoint/restore: serialize registers, memory, and cache mid-run; resume the same saved state
  against a different hardware configuration.
- [ ] Elastic trace recording + replay: capture a RAW-dependency-annotated instruction trace from an OoOE run and replay
  it against alternate memory hierarchies without re-simulating the core. (inspired by gem5 TraceCPU)
- [ ] STF (Simulation Trace Format) output for trace interop with external RISC-V tools (spike, dromajo).

## Performance

- [x] Guard `CollectMemoryStalls` / `ConsumeAllStalls` / `UpdateCacheStat` — skip the entire call when no cache layers
  are configured. Add `bool _anyCache` field to `PipelineCore`, set in constructor. Profiler showed ~681ms combined cost
  called unconditionally every cycle.
- [x] Eliminate nullable `ulong?` property overhead on hot-path structs: `FetchHint.BranchTarget`,
  `WritebackStage.TrapRedirect`, `RobEntry.ResolvedNextPc`, `ExecuteResult.RegisterResult`. Converted to
  `(ulong Value, bool HasValue)` pairs. Profiler showed ~833ms combined across the four setters.
- [ ] Memoization of instructions, results, and branches.
- [ ] Replace the executor's ~150-arm type-pattern `switch` over `RvOp` records with O(1) dispatch (an
  `RvOpKind` enum/int discriminator switched as a jump table, or virtual dispatch on the op). Roslyn compiles
  type-pattern switches to a linear `isinst` chain; a Timeline profile (rsort) showed `CastHelpers.IsInstanceOfClass`
  at ~3.4% of real main-thread work. Same shape in `RvInstruction`'s remaining type-pattern properties.
- [ ] Structural stage-model rework for the in-order trains: the per-cycle pipeline allocates a latch record
  (`IfIdLatch`/`IdExLatch`/`ExMemLatch`/`MemWbLatch`) per stage per cycle and dispatches operands through
  `IRegisterFile`/`IMemory` interfaces with a save/inject/restore dance in `ExecuteStage`. Converting latches to
  structs and cutting the per-cycle interface hops is the lever for a large (vs. incremental) speedup; the Timeline
  profile showed no single hotspot — cost is spread across the stage `Cycle()` methods.

## Mechanism

- [ ] Generic interfaces for external devices.
  - [ ] Basic UART/MIMO?
- [ ] Cache pre-fetching: next-line, stride (RPT), and stream prefetchers as pluggable `IPrefetcher` implementations on
  `SetAssociativeCache`.
- [ ] Non-blocking cache with MSHR (Miss Status Holding Registers) to allow hits-under-misses and reduce cache-miss
  stall depth. (inspired by gem5)
- [ ] Make the ToothClass a tag instead of just using enum members?

### Out-of-Order Execution

- [x] Functional-unit classes with configurable count and per-class latency (integer ALU, multiplier/divider, FP
  pipelined, FP div/sqrt, load-store); result latency drives IQ wakeup via countdown-based in-flight buffer.
- [x] Memory order violation detection and squash. Detect when a speculative load read stale data because an older store
  to the same address resolved after it. Flush and reexecute from the violating load; store-to-load forwarding at
  execute time avoids squash when the store has already resolved. (inspired by gem5 O3)
- [ ] Separate load queue and store queue for speculative memory disambiguation.
- [x] Streaming-Engine to allow for UVE: `StreamingEngine` in `Orrery/Streaming/`; 8 streams, affine (base/stride/count/width) with configurable prefetch depth; wired into `OooeTrain` (steps every cycle, survives flushes).
- [x] Wire stream consumption into `OooeTrain`: `UveStreamSources`/`UveBranchStreams` on `ITooth`; pipeline injects load-stream elements into `IUveScalars` and syncs exhaustion before calling executor; Issue stalls when a required load stream has no buffered element; `StreamConfig` field on `ExecuteResult` carries `ss.ld.w` descriptor to pipeline for `StreamingEngine.Configure` call.

### Branch Prediction

- [x] Hashed Perceptron / Path-based Perceptron
- [x] ITTAGE (Indirect Branch Target Predictor)
- [x] BATAGE (Bimodal-Augmented TAGE)
- [ ] LLBP: https://ieeexplore.ieee.org/abstract/document/11408567/
- [ ] VLA-TAGE: https://ieeexplore.ieee.org/document/11417886
- [ ] Branch pre-computation: https://hps.ece.utexas.edu/pub/TEA.pdf
- [ ] Check other interesting algorithms, specifically non-TAGE ones.
- [ ] CBP-2025 front runner: correlate on register values rather than history.
- [ ] BranchNet: CNN predictor.
- [ ] Multiperspective Perceptron.
- [ ] Bullseye/SDM as H2P helpers.

## Co-simulation

- [x] Spike online lock-step co-simulation via streaming `--log-commits`: `ICommitObserver` in `Mechanism/`, wired into `SingleCycleTrain`; `SpikeCoSimReference` launches Spike as a live child process, reads its commit log line-by-line in real time (one `ReadLine()` per `OnCommit`), and compares PC + encoding + integer register writes immediately — divergence is reported at the exact failing instruction. `SpikeCoSimTests` verifies `test.elf` end-to-end in ~50 ms. Covers standard ISA; UVE is invisible to Spike.
  - [x] Extend `ICommitObserver` to `FiveStageTrain` and `OooeTrain` so the co-sim check also covers the pipeline hazard and forwarding paths. `WritebackStage` fires `OnCommit` on normal retire (after the committed-PC update, before the interrupt peek); `OooeTrain` fires once per ROB-head commit (after `CommitRegisters`, covering both the normal and branch-mispredict retire paths). Verified via `FiveStage_TestElf_MatchesSpike` / `Oooe_TestElf_MatchesSpike`.
  - [x] HTIF tohost co-sim fixture (`htif.elf`, RV32IM): terminates through the HTIF `tohost` register instead of EBREAK so standalone Spike exits cleanly. Required jump-to-self (`NextPc == Pc`) halt detection in `FiveStageTrain` (`WritebackStage`) and `OooeTrain` — previously only `SingleCycleTrain` had it, so the pipelined/OoO trains spun the HTIF exit loop to `maxTicks`. Side benefit: the HTIF benchmark suite now halts at the exit and finishes in seconds instead of minutes. Verified via `*_HtifElf_MatchesSpike` across all three trains.
  - [ ] Auto-skip `SpikeCoSimTests` in CI without Spike (requires `Xunit.SkippableFact` or xunit 3.x upgrade).
- [ ] gem5 timing co-simulation: compare pipeline event timing (fetch cycle, issue cycle, retire cycle) against gem5's O3CPU. Verifies IPC and stall counts, not ISA correctness. Requires gem5 Python integration and a structural mapping between PEvents and gem5's stats. Substantially more complex than Spike ISA co-sim.

## Multicore

- [ ] Multi-hart simulation: multiple OoOE trains sharing a memory hierarchy.
  - [ ] MESI cache coherence protocol between harts.

## Orrery

- [ ] Generic definition for a parser, plus whatever might be necessary for Face.

## Other ISAs

| Priority | Architecture     | Paradigm to test                       | Difficulty   |
|----------|------------------|----------------------------------------|--------------|
| 1        | SUBLEQ           | OISC, no opcode field                  | 1 (Trivial)  |
| 2        | PDP-8            | Accumulator, 12b, minimal opcodes      | 2 (Easy)     |
| 3        | J1 Forth         | Stack machine, packed opcodes          | 2 (Easy)     |
| 4        | TTA/NOVE         | Triggered side-effect execution        | 3 (Medium)   |
| 5        | GA144 F18A       | Async, multi-core, packed 5-op words   | 3 (Medium)   |
| 6        | MIL-STD-1750A    | Committee designed, spec driven        | 3 (Medium)   |
| 7        | Burroughs B5000  | Tagged stack-machine, segmented memory | 4 (Hard)     |
| 8        | Symbolica/CADR   | Full tagged LISP machine               | 4 (Hard)     |
| 9        | IA-64/Itanium    | VLIW with templates and predication    | 4 (Hard)     |
| 10       | Mill Belt        | Belt-machine, no register file         | 5 (Research) |
| 11       | TRIPS/WaveScalar | True dataflow, no PC                   | 5 (Research) |
