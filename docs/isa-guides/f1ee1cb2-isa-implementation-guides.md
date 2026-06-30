# ISA Implementation Guides

Created: 2026-06-26
Tags: #index

---

Guides for adding new ISAs to the Horologium engine. Each guide covers the six
types you need to implement, the mapping from ISA concepts to engine abstractions,
and which pipeline train to reach for first.

## Guides

1. [[2fede37b-implementing-subleq|SUBLEQ]] — OISC, no opcode field (trivial)
2. [[bd95dba6-implementing-pdp-8|PDP-8]] — Accumulator, 12-bit (easy)
3. [[7b2f2489-implementing-j1-forth|J1 Forth]] — Stack machine (easy)
4. [[a3c51f9e-implementing-move-tta|TTA/MOVE]] — Triggered side-effect (medium)
5. GA144 F18A — Async multi-core (medium)

## What you always implement

Every ISA requires exactly six types, all in their own project (e.g. `Subleq/`):

1. `ITooth` — the decoded instruction token the pipeline holds
2. `IDecoder` — byte sequence → ITooth
3. `IExecutor` — ITooth + state + memory → ExecuteResult
4. `IArchState` — mutable architectural state (PC, registers)
5. `ITrapController` — trap entry and return
6. `IMechanism` — factory wiring the five above together

The pipeline only ever sees these interfaces. No pipeline code changes when you
add a new ISA.

## Invariants to keep in mind

- `DestinationRegister` and `SourceRegisters` on `ITooth` are used exclusively
  for register hazard detection. If your ISA has no registers, return `-1` / `[]`
  and use `SingleCycleTrain` — pipeline hazard units won't interfere.
- `SideEffect` on `ExecuteResult` is for writes to `IArchState` (registers, CSRs)
  that must be applied at commit time. Memory writes go directly to the `IMemory`
  parameter in `IExecutor.Execute()`.
- `FetchHint.IsBranch = true` on every instruction that could redirect the PC —
  even unconditional jumps.
