# rtl-machine — bring-your-own-RTL example

A self-contained user project showing how to attach **your own** Chisel units to a
Horologium pipeline: a custom ALU and a custom branch predictor, wired into a 2-wide
out-of-order train by an F# architecture script. Nothing in the C# codebase knows
about these units — the instruction→opcode mapping for the ALU lives in the script.

| File | Role |
|---|---|
| `MyAlu.scala` | Fully pipelined 1-stage RV32 ALU (ADD/SUB/AND/OR/XOR, own opcode space) under the standard FU port contract |
| `MyBp.scala` | 512-entry bimodal predictor + direct-mapped BTB under the plain branch-predictor port contract |
| `generated/*.sv` | Committed firtool output — running the example never needs a JVM |
| `generate.sh` | Chisel → SystemVerilog (rerun after editing the Chisel) |
| `build.sh` | Verilates both units into `/tmp/rtl_example_{alu,bp}.so`, reusing the repo's `native/RtlFu` shims |
| `machine.fsx` | The machine: OoO train + RTL predictor via `BranchPredictorFactory` + RTL ALU via `RtlBackedExecutor` with an inline F# selector |

## Run

```bash
examples/rtl-machine/build.sh
dotnet run --project src/Apps/Runner -- prog.elf --script examples/rtl-machine/machine.fsx
```

## How the pieces connect

- The **ALU** follows the `rtl_fu_shim.cpp` port contract (`Decoupled` op/a/b request,
  `Valid` response; see `native/RtlFu/README.md`). Being fully pipelined, it holds
  `req.ready` high and delivers each result one edge after acceptance — the shim
  reports 1 cycle, matching the C# model's ALU latency, so substituting it is
  timing-neutral. The F# selector in `machine.fsx` pattern-matches decoded
  `RvInstruction` payloads (`RvAdd`, `RvSub`, …) and reads operand values through
  `state.IntegerRegisters` at call time, which sees pipeline forwarding and the OoO
  train's speculative register values — exactly like the built-in C# selectors.
  Unclaimed instructions fall through to the C# executor.
- The **predictor** follows the `rtl_bp_shim.cpp` contract (combinational predict,
  one-edge commit update) and plugs into the pipeline spec's
  `BranchPredictorFactory`. Speculative-history hooks stay at their interface
  defaults; see `native/RtlFu/LTageBp.scala` for the full-contract shape.
- Each machine build constructs fresh `RtlFfi*` wrappers — verilated models are not
  thread-safe, so never share one across machines.

For the sweep-JSON way to attach RTL units to ELF runs (no script needed), see
`native/RtlFu/README.md`; for a C# script with all four substitution surfaces, see
`scripts/example-rtl.csx`.
