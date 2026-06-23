# Horologium

A discrete-event CPU pipeline simulator written in C# targeting .NET 11. The simulation engine is ISA-agnostic; concrete ISAs are plugged in as separate assemblies without modifying the engine. The primary goal is comparing hardware configurations (branch predictors, caches, pipelines) and generating measurement data for analysis.

## Projects

| Project | Purpose |
|---|---|
| **Orrery** | The simulation engine. Knows nothing about instructions or ISAs. |
| **Mechanism** | Interfaces only. Defines the ISA-plugin contract. |
| **RiscV** | RV32IMAFCV implementation of the Mechanism contract, plus two pipeline topologies. |
| **Chip8** | A second ISA implementation, demonstrating that the engine is genuinely ISA-agnostic. |
| **Runner** | Console entry point. Runs ELF binaries under named hardware configurations and emits results as Markdown or CSV. |
| **Tests** | xUnit tests, organized by project (`Tests/Orrery`, `Tests/RiscV`, `Tests/Chip8`). |

## Commands

```bash
dotnet build                                                    # build the whole solution
dotnet test                                                     # run all tests
dotnet test --filter "FullyQualifiedName~DecoderTests"          # one test class
dotnet test --filter "Name=SpecificTestMethod"                  # one test method
dotnet run --project Runner                                     # run the console entry point
dotnet run --project Runner -- --help                           # CLI usage
```

The development environment is provided by a Nix flake (`flake.nix`, `direnv`). It supplies the .NET 11 SDK, a `riscv32-embedded` GCC/binutils cross-toolchain for producing bare-metal test binaries, and native libraries required to launch Rider via the `rider` command.

## Naming convention

The codebase uses a horological metaphor for domain types throughout. Match it when adding code.

| Thing | Name | Rationale |
|---|---|---|
| Project | Horologium | The instrument that models time and mechanical motion |
| Core | Orrery | The clockwork engine at the center |
| Unit / Resource | Gear | A discrete mechanical part that meshes with others |
| Port | Arbor | The shaft that transmits motion between gears |
| Scheduler | Escapement | The mechanism that releases energy in discrete, controlled steps |
| Pipeline Topology | Train | An arranged sequence of gears transmitting motion |
| ISA Plugin | Mechanism | The specific mechanical logic governing a Train |
| Instruction Token | Tooth | What one gear passes to the next; the discrete unit of transfer |
| µop | Impulse | The single discrete push the Escapement delivers per tick |
| Statistics | Dial | The readable face of the instrument |
| Configuration Parameter | Setting | The instrument's configuration before it runs |
| One Simulation Run | Revolution | One full turning of the mechanism |

## Architecture

### Simulation engine (Orrery)

All simulation activity is driven by the `Escapement` (`Orrery/Scheduling/Escapement.cs`), a single-threaded priority-queue discrete-event scheduler keyed by `(tick, phase)`. Nothing in the simulation happens except by the Escapement scheduling it.

Within a single tick, events execute in a fixed phase order. Pipeline stages depend on this ordering as a contract:

```
Fetch(0) → Dispatch(1) → Issue(2) → Execute(3) → ArborUpdate(4) → Complete(5) → Writeback(6) → Commit(7) → Flush(8) → Collection(9)
```

In-order pipelines use Fetch, Execute, ArborUpdate, Writeback, Commit, Flush, and Collection. The Dispatch, Issue, and Complete phases are reserved for out-of-order execution and are never scheduled by in-order code.

**Gears** (`Orrery/Gears/Gear.cs`) are the simulated components. They register Arbors (ports) and Settings during construction and `Initialize()`, then schedule work via the Escapement. Gears communicate only through typed `Arbor` channels. `OutArbor<T>.Send()` schedules delivery to a bound `InArbor<T>` at `currentTick + latency` at phase `ArborUpdate`. Latency must be at least 1; zero-latency connections would collapse sender and receiver within the same tick and break phase-ordering guarantees.

**Lifecycle** is a strict one-way state machine enforced by the `SimNode` tree (`Orrery/Tree/SimNode.cs`); the entire tree moves together:

```
Building → Finalizing (bind Arbors here) → Running → Finished
```

The **Train** (`Orrery/Train/Train.cs`) owns the Gears and the Escapement and drives `Build()` → `Run(maxTicks)` → `Reset()`. `Build()` calls `Initialize()` on all Gears, transitions to Finalizing, calls `Seal()` (where Arbors are bound), then locks Settings. `Run()` returns a `RevolutionResult` containing a snapshot of every Gear's `DialBoard`, and optionally periodic `TimeSeries` snapshots for tracking how metrics evolve during execution. The Train knows nothing about ISAs or instruction semantics.

### ISA plugins (Mechanism)

`IMechanism` is the factory and registry for one ISA. It produces an `IArchState` and exposes the `Decoder`, `Executor`, optional `ImpulseCracker`, and `TrapController`. A Train is constructed from a single `IMechanism`; swapping the Mechanism swaps the entire ISA without touching any Train code.

### RISC-V pipelines (RiscV/Trains)

Two Trains, both using `RvMechanism` (RV32IMAFCV):

- **`SingleCycleTrain`** — one Gear, one instruction per tick (fetch → decode → execute → writeback, all inline). Used to validate the Mechanism independently of pipeline complexity.
- **`FiveStageTrain`** — classic IF/ID/EX/MEM/WB pipeline. Each stage is its own Gear wired in sequence via Arbors. A `HazardUnit` handles RAW stall detection and register forwarding (controlled by a `forwardingEnabled` flag). Branch handling uses a pluggable `IBranchPredictor`; built-in implementations are `AlwaysNotTakenPredictor`, `AlwaysTakenPredictor`, `OneBitPredictor`, and `TwoBitPredictor`, plus a `ReturnAddressStack` wrapper for call/return prediction. Both instruction and data memory support optional set-associative caches and TLBs. A `StoreBuffer` provides deferred writes with store-to-load forwarding.

The five-stage pipeline timing: an instruction is fetched at cycle T, decoded at T+1, executed at T+2, accesses memory at T+3, and writes back at T+4. Writeback is scheduled at `Phase.Writeback` (6) before Decode runs at `Phase.Commit` (7), so a register written this cycle is visible to a dependent instruction reading the register file in the same cycle.

**ISA coverage:** I/M/A/F (standard), C (compressed 16-bit instructions), and V (vector, VLEN=128, V1.0 subset). The V subset covers `vsetvli`/`vsetivli`/`vsetvl`, unit-stride loads/stores (`VLE8/16/32`, `VSE8/16/32`, `VLM`, `VSM`), integer ALU (`vadd`, `vsub`, `vand`, `vor`, `vxor`, `vsll`, `vsrl`, `vsra`) in VV/VX/VI variants, and mask comparisons (`vmseq`, `vmsne`, `vmsltu`, `vmslt`, `vmsgtu`, `vmsgt`) with `vm`-bit masking.

### Hardware comparison (RiscV/Analysis)

`Experiment.Run(workload, configs, mechanism)` runs the same workload under multiple `NamedConfig` entries (each a named `TrainConfig` describing forwarding, predictor, cache, TLB, and store-buffer parameters), returns an `ExperimentResult`, and supports warmup ticks and periodic time-series snapshots. Results can be formatted as a Markdown table, summary CSV, or time-series CSV for graphing. `NamedConfig` sweep files are plain JSON arrays, readable by the Runner's `--sweep` flag.
