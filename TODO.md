# To-Do

## Face

- [ ] L2 and L3 caches.
- [ ] Assembler to simulate RISC-V in-place.
- [ ] Cache and virtual addressing visualization.
- [ ] Execution visualization.

## CHIP8

- [ ] Screen and keyboard.

## RISC-V

### Extensions

- [ ] Continue extending the V extension.
- [ ] Supervisor and user-privileged execution.
- [ ] UVE (Unlimited Vector Extension)

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
- [ ] Cache pre-fetching.

### Out-of-Order Execution

- [ ] Improve definition of generic units with latency at producing outputs.
- [ ] Streaming-Engine to allow for UVE.

### Branch Prediction

- [x] Hashed Perceptron / Path-based Perceptron
- [x] ITTAGE (Indirect Branch Target Predictor)
- [x] BATAGE (Bimodal-Augmented TAGE)
- [ ] LLBP: https://ieeexplore.ieee.org/abstract/document/11408567/
- [ ] VLA-TAGE: https://ieeexplore.ieee.org/document/11417886
- [ ] Branch pre-computation: https://hps.ece.utexas.edu/pub/TEA.pdf

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
