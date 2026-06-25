# To-Do

## Face

- [ ] L2 and L3 caches.
- [ ] Assembler to simulate RISC-V in-place.
- [ ] Cache and virtual addressing visualization.
- [ ] Execution visualization: Argos-style pipeline transaction viewer (scrollable waterfall; rows = in-flight
  instructions, columns = cycles, cells = pipeline stage). (inspired by Olympia/Sparta)

## CHIP8

- [ ] Screen and keyboard.

## RISC-V

- [ ] 64-bit support.

### Extensions

- [ ] Continue extending the V extension: strided/indexed loads-stores (VLSE, VSSE, VLUXEI, VSUXEI), reduction ops (
  vredsum, vredmax, …), widening/narrowing integer ops, integer multiply/divide (vmul, vmulh, vdiv), FP vector ops (
  vfadd, vfmul, vfmacc, …), slide and gather/scatter.
- [ ] B extension: Zba (address generation), Zbb (basic bit manipulation), Zbc (carry-less multiplication), Zbs (
  single-bit ops).
- [ ] Zicond (conditional zero/nonzero move).
- [x] Supervisor and user-privileged execution (trap delegation, data-path Sv32, page faults, interrupt dispatch).
- [ ] Instruction fetch translation: wire pipeline fetch stages through Sv32Walker so InstructionPageFault is reachable.
- [ ] UVE (Unlimited Vector Extension).

### Analysis

- [ ] Per-instruction lifecycle events (PEvents): structured FETCH/DISPATCH/ISSUE/EXECUTE/RETIRE/FLUSH records with
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
- [ ] Memoization of instructions, results and branches.

## Mechanism

- [ ] Generic interfaces for external devices.
    - [ ] Basic UART/MIMO?
- [ ] Cache pre-fetching: next-line, stride (RPT), and stream prefetchers as pluggable `IPrefetcher` implementations on
  `SetAssociativeCache`.
- [ ] Non-blocking cache with MSHR (Miss Status Holding Registers) to allow hits-under-misses and reduce cache-miss
  stall depth. (inspired by gem5)

### Out-of-Order Execution

- [x] Functional-unit classes with configurable count and per-class latency (integer ALU, multiplier/divider, FP
  pipelined, FP div/sqrt, load-store); result latency drives IQ wakeup via countdown-based in-flight buffer.
- [x] Memory order violation detection and squash: detect when a speculative load read stale data because an older store
  to the same address resolved after it; flush and re-execute from the violating load; store-to-load forwarding at
  execute time avoids squash when the store has already resolved. (inspired by gem5 O3)
- [ ] Separate load queue and store queue for speculative memory disambiguation.
- [ ] Streaming-Engine to allow for UVE.

### Branch Prediction

- [x] Hashed Perceptron / Path-based Perceptron
- [x] ITTAGE (Indirect Branch Target Predictor)
- [x] BATAGE (Bimodal-Augmented TAGE)
- [ ] LLBP: https://ieeexplore.ieee.org/abstract/document/11408567/
- [ ] VLA-TAGE: https://ieeexplore.ieee.org/document/11417886
- [ ] Branch pre-computation: https://hps.ece.utexas.edu/pub/TEA.pdf

## Multi-core

- [ ] Multi-hart simulation: multiple OoOE trains sharing a memory hierarchy.
    - [ ] MESI cache coherence protocol between harts.

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
