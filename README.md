# Horologium

A discrete-event CPU pipeline simulator written in C# targeting .NET 11. The simulation engine is ISA-agnostic; concrete ISAs are plugged in as separate assemblies without modifying the engine. The primary goal is comparing hardware configurations (branch predictors, caches, pipelines) and generating measurement data for analysis.

## Projects

| Project | Purpose |
|---|---|
| **Orrery** | The simulation engine. Knows nothing about instructions or ISAs. |
| **Mechanism** | Interfaces only. Defines the ISA-plugin contract. |
| **Pipeline** | ISA-agnostic pipeline trains (`SingleCycleTrain`, `FiveStageTrain`, `SuperscalarTrain`, `OooeTrain`), pipeline registers, `HazardUnit`, and stage implementations. No dependency on any ISA. |
| **RiscV32** | RV32IMAFCV implementation of the Mechanism contract. Includes Zba/Zbb/Zbc/Zbs/Zicond/Zawrs/Zicbom/Zicboz/Zimop/Zicntr and UVE. |
| **RiscV64** | RV64I implementation extending RiscV32 via inheritance. Adds W-suffix ops (ADDW/SUBW/…/ADDIW/…), LD/LWU/SD, and corrects shift/comparison/LW semantics for 64-bit. |
| **Chip8** | A second ISA implementation, demonstrating that the engine is genuinely ISA-agnostic. |
| **Face** | Avalonia desktop UI for running experiments and visualizing results interactively. Includes a workload preset picker for the built-in demo and all benchmark ELFs. A **PEvents tab** shows a scrollable Argos-style pipeline waterfall (rows = instructions, columns = cycles, cells = stage abbreviation F/DC/D/IS/EX/RT/FL). A **SpecPC** gutter column shows the fetch-window start address for each instruction. Flush/misprediction cycles are highlighted red; fetch-stall cycles are dimmed. An **Assembler tab** provides a three-pane RISC-V assembly editor: left pane is a text editor with Assemble/Step/Run/Reset controls; centre pane lists decoded instructions (offset, hex encoding, disassembly with ABI names and pseudo-instructions) and shows a bit-field breakdown panel when a row is selected; right pane displays integer and float register contents with a per-file format selector (hex, decimal, binary, float). |
| **Runner** | Console entry point. Runs ELF binaries under named hardware configurations and emits results as Markdown or CSV. |
| **Tests** | xUnit tests, organized by project (`Tests/Orrery`, `Tests/RiscV32`, `Tests/RiscV64`, `Tests/Chip8`, `Tests/Mechanism`). |

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

### Pipeline trains (Pipeline/)

Four Trains, all ISA-agnostic — they operate on `IArchState` and `ExecuteResult` closures with no dependency on any ISA assembly. When used with RISC-V they pair with `Rv32Mechanism` (RV32IMAFCV) or `Rv64Mechanism` (RV64I):

- **`SingleCycleTrain`** — one Gear, one instruction per tick (fetch → decode → execute → writeback, all inline). Used to validate the Mechanism independently of pipeline complexity.
- **`FiveStageTrain`** — classic IF/ID/EX/MEM/WB pipeline. Each stage is its own Gear wired in sequence via Arbors. A `HazardUnit` handles RAW stall detection and register forwarding (controlled by a `forwardingEnabled` flag). Branch handling uses a pluggable `IBranchPredictor`; built-in implementations include static predictors (`AlwaysNotTaken`, `AlwaysTaken`, `AlwaysBackwardNotForwards`), 1-bit and 2-bit saturating counter predictors, correlated (m,n), Gselect, Gshare, and L-TAGE (TAGE with a loop predictor overlay), plus a `ReturnAddressStack` wrapper for call/return prediction. Both instruction and data memory support optional set-associative caches and TLBs. A `StoreBuffer` provides deferred writes with store-to-load forwarding.
- **`OooeTrain`** — superscalar out-of-order pipeline using Tomasulo's algorithm. Physical register renaming, ROB-based in-order commit, and a unified issue queue. Functional units are configurable per class (`FuLatencyConfig`): each class (integer ALU, multiplier/divider, pipelined FP, FP divide/sqrt, load-store, branch, system) has an independent issue-port count and execution latency; multi-cycle results flow through a countdown-based in-flight buffer before CDB broadcast. Default latencies: integer ALU 1 cycle, integer mul/div 3, pipelined FP 4 (add/sub/mul/fma/compare/convert), FP div/sqrt 16, load-store 1. Loads issue speculatively without waiting for older stores; store-to-load forwarding supplies the correct value when the store has already executed, and a memory-order violation squash (flush + re-execute from the load's PC) recovers when the store resolved after the load. A `mem_order_violations` counter tracks re-executions. The `OooeTrain` public API is a thin wrapper; `OoOPipelineCore` is the single-Gear implementation. A `StreamingEngine` (`Orrery/Streaming/StreamingEngine.cs`) is embedded in every `OoOPipelineCore`: it manages up to 8 independently configured affine memory streams (`StreamDescriptor` in `Mechanism/`: base address, element width in bytes, element count, byte stride), each backed by a prefetch buffer of configurable depth. The engine's `Step()` is called unconditionally every pipeline cycle so streams prefetch ahead of consumption; streams are architectural state and survive pipeline flushes. UVE (Unlimited Vector Extension) instructions consume streams in the OoO pipeline: `ToothClass.Uve` ops are head-serialized (like `Vector`); the pipeline injects load-stream elements into `IUveScalars` before calling the executor; Issue stalls when a required load stream has no buffered element; `ExecuteResult.StreamConfig` carries `ss.ld.w` descriptors for `StreamingEngine.Configure`.

The five-stage pipeline timing: an instruction is fetched at cycle T, decoded at T+1, executed at T+2, accesses memory at T+3, and writes back at T+4. Writeback is scheduled at `Phase.Writeback` (6) before Decode runs at `Phase.Commit` (7), so a register written this cycle is visible to a dependent instruction reading the register file in the same cycle.

ISA mutations (register writes, vector state, CSRs, trap returns) are delivered to the Train through a `SideEffect Action<IArchState>` closure on `ExecuteResult`, keeping the trains free of any ISA-specific fields.

**ISA coverage:** I/M/A/F (standard), C (compressed 16-bit instructions), Zba (address generation: sh1add/sh2add/sh3add), Zbb (basic bit manipulation: andn/orn/xnor, clz/ctz/cpop, min/minu/max/maxu, rol/ror/rori, sext.b/sext.h/zext.h, orc.b/rev8), Zbc (carry-less multiply: clmul/clmulh/clmulr), Zbs (single-bit: bclr/bext/binv/bset and immediate forms), Zicond (czero.eqz/czero.nez), Zawrs (wrs.nto/wrs.sto — NOP in single-core), Zicbom (cbo.inval/clean/flush — NOP), Zicboz (cbo.zero — zeros 64-byte cache-line-aligned block), Zicbop (prefetch.i/r/w — NOP via ORI path), Zimop (mop.r.N/mop.rr.N — return 0), Zicntr (cycle/cycleh/time/timeh/instret/instreth user-level counter shadows; mcycle/mcycleh/minstret/minstreth M-mode counters; pipeline trains drive IArchState.OnCycle()/OnRetire() hooks), V (vector, VLEN=128, V1.0 subset), and UVE (Unlimited Vector Extension, scalar subset). The V subset covers `vsetvli`/`vsetivli`/`vsetvl`, unit-stride loads/stores (`VLE8/16/32`, `VSE8/16/32`, `VLM`, `VSM`), integer ALU (`vadd`, `vsub`, `vand`, `vor`, `vxor`, `vsll`, `vsrl`, `vsra`) in VV/VX/VI variants, and mask comparisons (`vmseq`, `vmsne`, `vmsltu`, `vmslt`, `vmsgtu`, `vmsgt`) with `vm`-bit masking. The UVE scalar subset (encoded in RISC-V custom-0/custom-1 opcode space) covers: 1D stream setup (`ss.ld.w`, `ss.st.w`); multi-dimensional stream setup (`ss.sta.ld.w`, `ss.sta.st.w`, `ss.app`, `ss.end`, `ss.cfg.vec`); scalar broadcast (`so.v.dp.w`); element-wise FP arithmetic (`so.a.mul.fp`, `so.a.add.fp`, `so.a.sub.fp`, `so.a.mac.fp`); stream-loop branches (`so.b.nc`, `so.b.ndc.D`). `StreamDescriptor` holds N-dim `StreamDimension[]`; `StreamingEngine` tracks per-dim consume-side pass-complete flags for `so.b.ndc.D` loop control. Sufficient for SAXPY and strided 2D matrix kernels, including multi-dim store streams. `UveStoreStream` supports N-dimensional layouts with per-dim index carry, matching `StreamState` for load streams. Vector-width delivery (`ss.cfg.vec` effect) is not yet implemented; full GEMM requires vector streaming.

**Privilege model:** Three privilege levels (User=0, Supervisor=1, Machine=3). Trap delegation: when an exception's `medeleg` bit is set and the hart is below Machine privilege, `RaiseTrap` enters S-mode (writes `sepc`/`scause`/`stval`, updates `sstatus` SPP/SPIE/SIE, sets privilege to Supervisor, returns `stvec` base); otherwise the existing M-mode path applies. Interrupt causes (bit 31 set in mcause/scause) are delegated via `mideleg` rather than `medeleg`. `MRET` is guarded to Machine mode; `SRET` requires at least Supervisor. `ECALL` emits the correct cause code for the current privilege level (8=U, 9=S, 11=M). CSR accesses from an insufficient privilege level or writes to read-only CSRs raise `IllegalInstruction`.

**Virtual memory (Sv32):** The `satp` CSR (0x180, Supervisor-mode) controls address translation. When `satp.MODE=1`, both instruction fetch and data loads/stores go through a two-level Sv32 page table walk (`Sv32Walker`). `MODE=0` (bare) uses virtual address = physical address and preserves the existing behaviour for all benchmark workloads. A/D bits are enforced using a fault-on-access model: `A=0` or (`D=0` on a store) raises the corresponding page fault (`LoadPageFault`/`StorePageFault`). Supervisor User Memory (SUM) is not implemented; S-mode always faults on user pages (PTE.U=1). Instruction fetch translation crosses the ISA isolation boundary via the `IFetchTranslator` interface in Mechanism/: `RvFetchTranslator` (in RiscV/) calls `Sv32Walker` with `isExec=true`, returning `(physAddr, 0)` on success or `(0, 12)` for `InstructionPageFault`; M-mode fetches bypass the walk entirely (RISC-V priv spec §3.1.6). All four pipeline trains wire up the translator and propagate the pre-baked `TrapInfo` through the pipeline latch chain to Writeback, where it is raised via the normal trap path.

**Interrupt dispatch:** `ITrapController.PeekInterrupt(IArchState)` returns the highest-priority pending interrupt (per the RISC-V §3.1.9 priority order: MEI > MSI > MTI > SEI > SSI > STI) when `mip & mie` has a pending bit and the global interrupt enable for the current privilege and delegation state permits delivery. All four pipeline trains call `PeekInterrupt` at each retire/commit boundary and invoke `RaiseTrap` when a non-null result is returned. `mideleg` routes delegated interrupts to S-mode.

### Benchmark workloads (TestBinaries/benchmarks)

Seven bare-metal RISC-V benchmarks compiled from the riscv-tests suite: `median`, `memcpy`, `multiply`, `qsort`, `rsort`, `towers`, and `vvadd`. They are built against a minimal `crt0.s` + `bmarks.ld` (code at `0x80000000`, Spike's `DRAM_BASE`, 4 MB RAM) and exit via the HTIF `tohost` symbol. All are available as preset workloads in the Face UI and can be passed to the Runner as ELF arguments. Note: benchmark tests are slow — run them selectively with `--filter`.

ISA conformance tests (`TestBinaries/isa/`) are also linked at `0x80000000`. `FlatMemory` accepts an optional `baseAddress` constructor parameter so the backing byte array starts at the first PT_LOAD segment (e.g. `0x80000000`) rather than at address 0, avoiding a 2 GB allocation. `Rv32ElfWorkload.BaseAddress` exposes this value; `IWorkload.BaseAddress` defaults to 0 for zero-based images.

### Per-instruction lifecycle events (Orrery/Observation)

`PEventLog` captures structured per-instruction lifecycle events — Fetch, Decode, Dispatch, Issue, Execute, Retire, Flush — tagged with an instruction ID, PC, and cycle number. A cycle-level `FetchStall` sentinel (instrId=0) marks cycles where the OoO fetch unit is blocked (faulted PC). Pass a `PEventLog` instance to `FiveStageTrain` or `OooeTrain` to enable recording (null = zero overhead). Query methods include `ForInstruction(id)`, `OfKind(kind)`, and `InCycleRange(from, to)` for post-hoc filtering and phase analysis. Every instruction is assigned a monotonically increasing `InstrId` at fetch time, unique across the full simulation run, so lifecycle phases can be correlated even for wrong-path instructions that are later flushed.

FiveStage records Fetch/Decode/Execute/Retire/Flush. OoO records the full lifecycle: Fetch → Decode → Dispatch → Issue → Execute → Retire/Flush. Flush events appear as an additional terminal event for wrong-path or squashed instructions.

### Spike lock-step co-simulation (RiscV32/CoSim)

`SpikeCoSimReference` implements `ICommitObserver` and launches Spike as a live child process with `--log-commits`. For each instruction that Horologium commits, it reads the next line from Spike's stderr stream (blocking until Spike produces it), then immediately compares PC, raw encoding, and any integer register write — divergence is reported at the exact failing instruction. Boot-ROM commits (PC below `baseAddress`) are skipped. Attach it via the optional `commitObserver` parameter to `SingleCycleTrain`, `FiveStageTrain`, or `OooeTrain` — the in-order pipeline fires `OnCommit` from `WritebackStage` on normal retire, and the out-of-order pipeline fires once per ROB-head commit in program order, so the check covers the hazard/forwarding and speculative-memory datapaths too. Wrap in `using` to kill Spike on completion. `SpikeCoSimTests` runs three fixtures across all three trains: `test.elf` (simple RV32I golden path), `rich.elf` (RV32IM — multiply/divide, an insertion sort, and heavy data-dependent branching, to exercise the multi-cycle functional units, store-to-load forwarding, and flush paths), and `htif.elf` (the same RV32IM workload but terminating through the HTIF `tohost` register instead of EBREAK, so standalone Spike exits cleanly). Spike logs the post-exit spin-loop an indeterminate number of times; each train commits up to the exit, then halts, so its stream is a clean prefix of Spike's. `dtc` must be on PATH (the Nix dev-shell provides it); the tests can be excluded from CI without Spike with `--filter "FullyQualifiedName!~SpikeCoSim"`.

### Program termination

Two halt mechanisms, both stopping all three trains at the terminator instead of spinning to `maxTicks`:

- **HTIF tohost exit (first-class).** When the mechanism is given the `tohost` address (`new Rv32Mechanism(htifTohost)`), a word store of an odd exit code to that register is flagged by the executor with `ExecuteResult.RequestHalt`. The trains carry that flag through commit (the in-order pipeline via `MemWbLatch`, the out-of-order core via the ROB) and halt *after* the store commits — the engine terminates at the exit write itself, not the spin that follows. The flag is ISA-agnostic: the trains act on it without knowing about HTIF. The address is surfaced generically as `IWorkload.HtifTohostAddress` (the ELF's `tohost` symbol), so `Experiment.Run`/`Trace`, the Runner, and the Face all wire it automatically for HTIF ELFs.
- **Unconditional jump-to-self (backstop).** For non-HTIF programs, a `jal`/`jalr` whose resolved target is its own PC (the conventional bare-metal `j .` terminator) halts the run. Gated on `ToothClass.Branch` so a conditional spin-wait — which may be waiting on an interrupt — is not mistaken for a halt.

Because the five-stage and out-of-order trains previously spun HTIF binaries to `maxTicks`, adding these halts also makes the HTIF benchmark suite finish in seconds. `HtifExitTests` covers both paths across all three trains without requiring Spike.

### Hardware comparison (RiscV/Analysis)

`Experiment.Run(workload, configs, mechanism)` runs the same workload under multiple `NamedConfig` entries (each a named `TrainConfig` describing forwarding, predictor, cache, TLB, and store-buffer parameters), returns an `ExperimentResult`, and supports warmup ticks and periodic time-series snapshots. Results can be formatted as a Markdown table, summary CSV, or time-series CSV for graphing. `NamedConfig` sweep files are plain JSON arrays, readable by the Runner's `--sweep` flag.
