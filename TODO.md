# To-Do

## Face

- [ ] Rollback/Back step: single-step backward in the assembler debugger.
- [x] Pipeline stage viewer in Assembler tab.
- [ ] L2 and L3 caches.
- [x] Assembler to simulate RISC-V in-place.
- [ ] Compile from C to disassembly and simulate that.
- [ ] Browser assembly support: pure C# RV32 two-pass assembler so the Assemble command works in FaceWeb without a GAS subprocess.
- [ ] Cache and virtual addressing visualization.
- [x] Execution visualization: Argos-style pipeline waterfall.
- [x] Light mode.
- [ ] Work with other ISAs, not just RISC-V.

## CHIP8

- [ ] Screen and keyboard.

## RISC-V

- [x] 64-bit support.
  - [ ] ELF64 loader for running RV64 binaries.
  - [ ] Sv39 page-table walker for RV64 virtual memory.
  - [ ] RV64 M extension.
  - [ ] RV64 F/D extension.
- [ ] 128-bit support.
- [x] Enable or disable specific extensions.

### Extensions

- [ ] Continue extending the V extension: strided/indexed loads-stores, reduction ops, widening/narrowing integer ops, integer multiply/divide, FP vector ops, slide and gather/scatter.
- [x] Zba, Zbb, Zbs, Zicond, Zbc.
- [ ] D extension (RV32D): double-precision FP registers and arithmetic.
- [ ] Zfh / Zfhmin: half-precision FP.
- [ ] Zfinx / Zdinx / Zhinx: FP operations in integer register file.
- [x] Zicbom / Zicboz / Zicbop: cache management operations.
- [x] Zawrs: wrs.nto and wrs.sto.
- [x] Zimop: mop.r.N and mop.rr.N.
- [ ] Zcmop: compressed may-be-operations.
- [x] Zicntr: hardware performance counters.
- [x] Zihpm: hardware performance monitor CSRs.
- [x] Zabha: byte/halfword atomics.
- [x] Zacas: compare-and-swap word.
- [ ] Zabha+Zacas narrower variants: amocas.b / amocas.h; amocas.d for RV32.
- [ ] Scalar crypto: Zknd/Zkne/Zknh, Zksd/Zkse/Zksh, Zkr.
- [ ] Vector bit manipulation (Zvbb), carry-less multiply (Zvbc), crypto (Zvkn/Zvkg/Zvks).
- [ ] Zvfh / Zvfhmin: vector half-precision FP.
- [ ] H extension (hypervisor): VS-mode, VU-mode, two-stage address translation.
- [ ] Svnapot / Svpbmt / Svadu / Svinval: Sv32/Sv39 page-table extensions.
- [ ] Smaia / Ssaia: Advanced Interrupt Architecture.
- [ ] Smstateen: state-enable CSRs.
- [ ] Smnpm / Ssnpm: pointer masking.
- [x] Supervisor and user-privileged execution (trap delegation, Sv32, page faults, interrupt dispatch).
- [x] Instruction fetch translation through Sv32Walker.
- [x] UVE (Unlimited Vector Extension) — 1D and multi-dimensional streams.
- [ ] UVE 2 (ISCA 2024): predicates, scatter/gather, widening/narrowing.
- [ ] `ss.cfg.vec` effect: vector-width element delivery from load streams.
- [ ] SUM: honor `sstatus.SUM` so S-mode can access user pages.
- [ ] Implement the rest of the extensions.

### Analysis

- [x] Per-instruction lifecycle events (PEvents).
- [ ] Region-of-interest simulation: fast-forward outside named ELF symbol ranges.
- [ ] Simulation state checkpoint/restore.
- [ ] Elastic trace recording + replay.
- [x] Olympia JSON instruction-trace output.
  - [x] Package Olympia in the Nix flake.
  - [x] Cross-model comparison harness and calibration study.
  - [x] Unblock benchmark breadth via raw-opcode trace path.
  - [x] Fix HTIF MMIO caching bug.
  - [x] Phase-2b matched study with working L1.
  - [ ] JSON-format limitations: no PC/opcode, FP register numbering, vector/UVE ops.
- [ ] STF (Simulation Trace Format) binary output.

## Performance

- [x] Guard cache-stat collection calls when no cache is configured.
- [x] Eliminate nullable `ulong?` overhead on hot-path structs.
- [ ] Memoization of instructions, results, and branches.
- [ ] O(1) executor dispatch (jump table or virtual dispatch on op kind).
- [ ] Structural stage-model rework for in-order trains: struct latches, fewer interface hops.

### Parallelism

- [x] Parallelize config sweep in `Experiment.Run`.
- [ ] Parallelize multi-workload sweeps.
- [ ] Multi-hart concurrency (distinct from run-level parallelism).

## Mechanism

- [ ] Generic interfaces for external devices (basic UART/MMIO).
- [ ] Cache pre-fetching: next-line, stride (RPT), and stream prefetchers.
- [ ] Non-blocking cache with MSHR.
  - [x] Load-side MLP: independent misses overlap via per-load latency countdown.
  - [ ] Store-side: model write buffer / bounded store buffer.
  - [ ] One D-cache access port per cycle (store-commit currently multi-ported).
- [ ] Make `ToothClass` a tag instead of an enum?

### Out-of-Order Execution

- [x] Functional-unit classes with configurable count and per-class latency.
- [x] Memory order violation detection and squash.
- [ ] Separate load queue and store queue for speculative memory disambiguation.
- [x] Fix: fetch decode-fault wedging the fetcher.
- [x] Fix: Load/Store/Atomic sharing one FU budget incorrectly.
- [x] Fix: atomic write-half disambiguation and commit.
- [x] Streaming Engine for UVE wired into OooeTrain.

### Branch Prediction

- [x] Hashed Perceptron / Path-based Perceptron.
- [x] ITTAGE.
- [x] BATAGE.
- [ ] LLBP: https://ieeexplore.ieee.org/abstract/document/11408567/
- [ ] VLA-TAGE: https://ieeexplore.ieee.org/document/11417886
- [ ] Branch pre-computation: https://hps.ece.utexas.edu/pub/TEA.pdf
- [ ] CBP-2025 front runner: correlate on register values rather than history.
- [ ] BranchNet: CNN predictor.
- [ ] Multiperspective Perceptron.
- [ ] Bullseye/SDM as H2P helpers.

## Co-simulation

- [x] Spike online lock-step co-simulation.
  - [x] Extend to FiveStageTrain and OooeTrain.
  - [x] HTIF tohost co-sim fixture.
  - [x] First-class HTIF tohost terminator and self-loop halt.
  - [x] Auto-skip when toolchain is absent; `HOROLOGIUM_REQUIRE_COSIM=1` enforcement switch.
  - [x] Broaden to official riscv-tests conformance ELFs (rv32ui/um/ua/uc/uf).
  - [ ] Watchdog on `ReadLine` to fail cleanly on over-run instead of hanging.
  - [ ] CI workflow with `HOROLOGIUM_REQUIRE_COSIM=1`.
- [ ] gem5 timing co-simulation.

## Multicore

- [ ] Multi-hart simulation: multiple OoOE trains sharing a memory hierarchy.
  - [ ] MESI cache coherence.

## Orrery

- [ ] Generic definition for a parser.

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
