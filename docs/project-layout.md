# Project Layout and Core Engine

How the codebase is organized, and how the ISA-agnostic simulation engine at its center works. See
[naming.md](naming.md) for the horological-metaphor glossary these type names come from, and the README for
how to build, test, and run everything.

## Projects

Projects live under `src/`: the ISA-agnostic core in `src/Core/`, ISA plugins in `src/Isa/`, and applications in
`src/Apps/`.

### Orrery

The simulation engine. Knows nothing about instructions or ISAs.

### Mechanism

Interfaces only. Defines the ISA-plugin contract.

### Pipeline

ISA-agnostic pipeline trains (`SingleCycleTrain`, `FiveStageTrain`, `SuperscalarTrain`, `OooeTrain`, `CprTrain`,
`SmtTrain`, `DaeTrain`), pipeline registers, `HazardUnit`, and stage implementations. No dependency on any ISA. See
[pipeline-trains.md](pipeline-trains.md), [out-of-order-execution.md](out-of-order-execution.md),
and [checkpoint-processing.md](checkpoint-processing.md) for the full feature set of each train.

### RiscV32

RV32IMAFDCV implementation of the Mechanism contract, plus a wide extension set (bit-manipulation, half-precision FP,
scalar and vector cryptography, and UVE — the Unlimited Vector Extension). See
[isa-extensions.md](isa-extensions.md) for the complete coverage list, privilege model, and virtual memory
support.

### RiscV64

RV64IMAFDAC implementation extending RiscV32 via inheritance: W-suffix ops, doubleword atomics, an ELF64 loader, and
an Sv39 page-table walker. Full extension coverage and the RV32/RV64 divergences are in
[isa-extensions.md](isa-extensions.md).

### Chip8

A second ISA implementation, demonstrating that the engine is genuinely ISA-agnostic. Full display (64×32 XOR-sprite
framebuffer) and 16-key keyboard support.

### Subleq

SUBLEQ OISC implementation. One 12-byte instruction, no register file. Validates that the Mechanism contract accepts the
simplest possible ISA.

### Pdp8

PDP-8 (1965) 12-bit accumulator machine. Eight opcodes: AND, TAD, ISZ, DCA, JMS, JMP, IOT, OPR. Full Group 1/2
micro-operations (CLA, CLL, CMA, CML, RAR/RTR, RAL/RTL, BSW, IAC, SMA/SZA/SNL with RSS complement mode). Page-zero and
current-page addressing, indirect access, auto-increment (words 8–15).

### J1

J1 Forth (James Bowman, 2010) 16-bit stack machine. Fixed 16-bit instruction width, four instruction types (Literal,
Jump, CondJump, ALU). 32-entry data stack (T/N) and return stack (R), full ALU encoding (16 T' selectors, T→N, T→R,
N→\[T] store, 2-bit DDelta/RDelta). Implements `DUP`, `DROP`, `SWAP`, `OVER`, `+`, `AND`, `OR`, `XOR`, `INVERT`, `=`,
`<`, `U<`, `@`, `!`, `>R`, `R>`, `R@`, `EXIT`, and countdown loops.

### Move

TTA/MOVE (Transport Triggered Architecture, Corporaal 1995) 16-bit machine. Fixed 32-bit instruction format
`[dst|src|imm]`. Computation is a side effect of transport: writing to a trigger port (`alu.in2`, `mem.load`,
`mem.store`, `br.target`) fires the FU. Register file r0–r7; ALU FU (16 operations:
ADD/SUB/AND/OR/XOR/NOT/SHL/SHR/SRA/EQ/LT/ULT/NEG/INC/DEC/COPY); memory FU (16-bit word loads and stores); branch FU (
conditional redirect). `SingleCycleTrain` only — FU state is not exposed as register hazards.

### F18A

GreenArrays GA144 F18A (2010) 18-bit stack computer. 29 opcodes packed four-per-word (5+5+5+3 bits) using the canonical
GA144 encoding (0x00–0x1F). Includes `-if` (MinusIf 0x07: branch when T≥0) and `+*` (MulStep 0x10: shift-and-add
multiply step). Data stack (T/S/8-deep) and return stack (8-deep); A and B address registers; 9-bit word-addressed PC (
P). Canonical `if` semantics: branch when T==0 (false). Per-node memory: 64-word RAM, 64-word ROM, 256-word port space.
Inter-node communication via synchronous `RendezvousArbor` channels (transfer completes only when both sides participate
in the same tick). `F18AGrid` coordinates a rows×cols array of nodes; each node runs `SingleCycleTrain`;
`F18AGrid.Step()` pre-checks `WillBlock` before driving decode→execute→commit. First multi-core ISA in the engine.

### Face

Avalonia desktop UI, RISC-V exclusive (CHIP-8 has its own app, **Chip8Face**, below). A workload/pipeline picker with
single-hart and multi-hart modes, a Waveform tab, a PEvents pipeline-waterfall tab, an Assembler tab (RISC-V ASM or C,
plus a UVE-kernel sub-tab), a Vector-register-file tab, and a Configurator tab (a gem5-style `.csx` architecture
builder with hot reload). See [face-ui.md](face-ui.md) for the full tour of every tab.

### Chip8Face

Avalonia desktop UI for CHIP-8, split out of Face so Face could go RISC-V exclusive. A single window renders the 64×32
pixel framebuffer at 10× scale with a 60 fps game loop, keyboard input (QWERTY layout mapped to the CHIP-8 hex keypad),
and ROM load/start/pause/reset controls. Shares the `Chip8` ISA plugin with the rest of the engine but nothing else
with Face — no launcher, no RISC-V references, its own minimal `App.axaml` (`FluentTheme` + the `MonoFont` resource
only, none of Face's DataGrid/AvaloniaEdit/ScottPlot styling).

### Runner

Console entry point. Runs ELF binaries under named hardware configurations and emits results as Markdown or CSV. Accepts
`--script <file.csx>` to evaluate a C# script that returns a `MachineSpec` and run the workload against it. See
[runner-examples.md](runner-examples.md) for worked CLI examples.

### Script

C# and F# scripting host. `ScriptHost.EvaluateFileAsync(path)` compiles and runs a `.csx` (Roslyn) or `.fsx` (F#
Interactive) file returning a `MachineSpec`, with all Spec/Cache/RiscV32 namespaces pre-imported — no `#r` or
`using`/`open` needed in the script. `ConfiguratorEngine` (UI-framework-free) wraps evaluate→build→snapshot for Face's
Configurator tab. See [scripting-and-checkpointing.md](scripting-and-checkpointing.md).

### Tests

xUnit tests. Engine tests under `Tests/Orrery`, `Tests/Pipeline`, `Tests/Mechanism`; small-ISA and hand-crafted-buffer
RV64 tests under `Tests/Isa` (`Tests/Isa/RiscV64`); RV32 tests under
`Tests/RiscV32/{Isa,Extensions,Pipelines,MultiHart,System,CoSim,Analysis}`; RV64 ISA-conformance tests (real compiled
`rv64*-p-*` ELFs, all three pipeline trains) under `Tests/RiscV64/Isa`; RV64 `Tests/RiscV64/{System,MultiHart,CoSim}` (
CLINT/HTIF/UART/raw-binary-workload, multi-hart pipeline/atomics/TSO fence, Spike co-sim golden-path + full riscv-tests
conformance loop, torture co-sim, OpenSBI boot, Linux NOMMU boot) mirroring the RV32 System/MultiHart/CoSim suites;
`Tests/Face` covers `ConfiguratorEngine` (UI-framework-free, so it's testable without pulling in Avalonia).

## Core engine

### Simulation engine (Orrery)

All simulation activity is driven by the `Escapement` (`src/Core/Orrery/Scheduling/Escapement.cs`), a single-threaded
priority-queue discrete-event scheduler keyed by `(tick, phase)`. Nothing in the simulation happens except by the
Escapement scheduling it.

Within a single tick, events execute in a fixed phase order. Pipeline stages depend on this ordering as a contract:

```
Fetch(0) → Dispatch(1) → Issue(2) → Execute(3) → ArborUpdate(4) → Complete(5) → Writeback(6) → Commit(7) → Flush(8) → Collection(9)
```

In-order pipelines use Fetch, Execute, ArborUpdate, Writeback, Commit, Flush, and Collection. The Dispatch, Issue, and
Complete phases are reserved for out-of-order execution and are never scheduled by in-order code.

**Gears** (`src/Core/Orrery/Gears/Gear.cs`) are the simulated components. They register Arbors (ports) and Settings
during construction and `Initialize()`, then schedule work via the Escapement. Gears communicate only through typed
`Arbor` channels. `OutArbor<T>.Send()` schedules delivery to a bound `InArbor<T>` at `currentTick + latency` at phase
`ArborUpdate`. Latency must be at least 1; zero-latency connections would collapse sender and receiver within the same
tick and break phase-ordering guarantees.

**Lifecycle** is a strict one-way state machine enforced by the `SimNode` tree (`src/Core/Orrery/Tree/SimNode.cs`); the
entire tree moves together:

```
Building → Finalizing (bind Arbors here) → Running → Finished
```

The **Train** (`src/Core/Orrery/Train/Train.cs`) owns the Gears and the Escapement and drives `Build()` →
`Run(maxTicks)` → `Reset()`. `Build()` calls `Initialize()` on all Gears, transitions to Finalizing, calls `Seal()` (
where Arbors are bound), then locks Settings. `Run()` returns a `RevolutionResult` containing a snapshot of every Gear's
`DialBoard`, and optionally periodic `TimeSeries` snapshots for tracking how metrics evolve during execution;
`SignalExtractor` (`src/Core/Orrery/Observation/SignalExtractor.cs`) turns those cumulative snapshots into named
plottable signals — per-window counter deltas, derived windowed ratios (`ipc`, `*_hit_rate`), and cumulative dial
readings — used by the Face Waveform tab. The Train knows nothing about ISAs or instruction semantics.

### ISA plugins (Mechanism)

`IMechanism` is the factory and registry for one ISA. It produces an `IArchState` and exposes the `Decoder`, `Executor`,
optional `ImpulseCracker`, and `TrapController`. A Train is constructed from a single `IMechanism`; swapping the
Mechanism swaps the entire ISA without touching any Train code.


## Further documentation

Every other subsystem's deep dive lives alongside this file under `docs/`:

- [multi-hart.md](multi-hart.md) — `MultiHartKernel`, `MultiHartPipeline`, `SmtTrain`
- [cache-fundamentals.md](cache-fundamentals.md) — cache timing model and replacement policies
- [prefetching.md](prefetching.md) — instruction and data-cache prefetchers
- [cache-security-and-compression.md](cache-security-and-compression.md) — BΔI compression, CEASER/ScatterCache
- [cache-coherence.md](cache-coherence.md) — MOESIF multi-hart coherence
- [syscall-emulation.md](syscall-emulation.md) — program termination and Linux syscall emulation
- [full-system-boot.md](full-system-boot.md) — OpenSBI/Linux NOMMU boot
- [observability.md](observability.md) — per-instruction lifecycle events (`PEventLog`)
- [performance-analysis.md](performance-analysis.md) — Top-Down analysis and CPI stacks
- [sampling-methodologies.md](sampling-methodologies.md) — SimPoint and SMARTS
- [loop-point.md](loop-point.md) — LoopPoint multi-hart sampling and `RequestBlock`
- [tracing.md](tracing.md) — Olympia/HELF/STF/ChampSim trace formats
- [rtl-substitution.md](rtl-substitution.md) — swapping pipeline units for Verilator-driven RTL
- [gem5-comparison.md](gem5-comparison.md) — validating this model's timing against gem5
- [runner-examples.md](runner-examples.md) — worked Runner CLI examples
