# Horologium

[![CI](https://github.com/lumikalt/Horologium/actions/workflows/ci.yml/badge.svg)](https://github.com/lumikalt/Horologium/actions/workflows/ci.yml)

[▶ Face demo](docs/face.mp4)

A discrete-event CPU pipeline simulator written in C# targeting .NET 11. The simulation engine is ISA-agnostic; concrete
ISAs are plugged in as separate assemblies without modifying the engine. The primary goal is comparing hardware
configurations (branch predictors, caches, pipelines) and generating measurement data for analysis.

## Projects

| Project       | Purpose                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
|---------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Orrery**    | The simulation engine. Knows nothing about instructions or ISAs.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| **Mechanism** | Interfaces only. Defines the ISA-plugin contract.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| **Pipeline**  | ISA-agnostic pipeline trains (`SingleCycleTrain`, `FiveStageTrain`, `SuperscalarTrain`, `OooeTrain`, `CprTrain`, `SmtTrain`, `DaeTrain`), pipeline registers, `HazardUnit`, and stage implementations. No dependency on any ISA.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| **RiscV32**   | RV32IMAFDCV implementation of the Mechanism contract. Includes Zba/Zbb/Zbc/Zbs/Zfh/Zicond/Zawrs/Zicbom/Zicboz/Zimop/Zcmop/Zicntr/Zabha/Zacas/Zknd/Zkne/Zknh/Zksed/Zksh/Zkr/Zbkb/Zbkc/Zbkx/Zvkned (full AES block-cipher extension)/Zvksed (full SM4 block-cipher extension)/Zvknha+Zvknhb (full SHA-2 compression + message-schedule extension)/Zvksh (full SM3 secure-hash extension)/Zvkg (full GCM/GMAC extension)/Zvbb+Zvbc+Zvkb (vector basic bit-manipulation + carryless multiply — completes the Vector Cryptography Extensions Volume II instruction set) and UVE. Zacas adds `amocas.w`; combined with Zabha it also adds narrow `amocas.b`/`amocas.h` compare-and-swap. RV32 also implements the Zacas register-pair form of `amocas.d` (rd/rd+1 and rs2/rs2+1 forming a 64-bit compare/swap value): the low half commits through the normal destination-register path, the high half through `ITooth.SecondaryDestinationRegister` + `ExecuteResult.SideEffect`, an ISA-agnostic second-destination hook available to any Mechanism plugin that needs one.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| **RiscV64**   | RV64IMAFDAC implementation extending RiscV32 via inheritance. Adds W-suffix ops (ADDW/SUBW/…/ADDIW/…), LD/LWU/SD, LR.D/SC.D/AMO*.D doubleword atomics, `amocas.d` (native single-register 64-bit compare-and-swap — RV32 uses the register-pair form instead, see above), C.LD/C.SD/C.ADDIW/C.LDSP/C.SDSP compressed quadrant reassignments, RV64-only Zba (ADD.UW/SH1-3ADD.UW/SLLI.UW) and 64-bit-width overrides of the inherited Zbb/Zbs immediate and register-form ops (6-bit shift-amount mask included), corrects shift/comparison/LW semantics for 64-bit, and adds the RV64-only Zknd/Zkne AES instructions (aes64ds/dsm/es/esm/im/ks1i/ks2) and Zknh direct SHA2-512 forms (sha512sig0/1, sha512sum0/1), disjoint from RiscV32's RV32-only forms, plus RV64-only Zbkb `packw` and 64-bit-width overrides of `pack`/`brev8`/`xperm4`/`xperm8`. Includes an ELF64 loader and an Sv39 page-table walker (satp held in a dedicated 64-bit-wide RV64 CSR rather than RiscV32's 32-bit `CsrFile`, to hold the Sv39 MODE field). The inherited V extension and UVE work unmodified at XLEN=64 (register file and CSRs are width-agnostic); the XLEN-sensitive paths — vlse/vsse strided load-store and vlsseg/vssseg segment strided load-store — read their stride through an overridable hook so RV64 uses the full 64-bit signed register value instead of RV32's 32-bit sign-extension.                                                                                                                                                                                                                                                     |
| **Chip8**     | A second ISA implementation, demonstrating that the engine is genuinely ISA-agnostic. Full display (64×32 XOR-sprite framebuffer) and 16-key keyboard support.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| **Subleq**    | SUBLEQ OISC implementation. One 12-byte instruction, no register file. Validates that the Mechanism contract accepts the simplest possible ISA.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| **Pdp8**      | PDP-8 (1965) 12-bit accumulator machine. Eight opcodes: AND, TAD, ISZ, DCA, JMS, JMP, IOT, OPR. Full Group 1/2 micro-operations (CLA, CLL, CMA, CML, RAR/RTR, RAL/RTL, BSW, IAC, SMA/SZA/SNL with RSS complement mode). Page-zero and current-page addressing, indirect access, auto-increment (words 8–15).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| **J1**        | J1 Forth (James Bowman, 2010) 16-bit stack machine. Fixed 16-bit instruction width, four instruction types (Literal, Jump, CondJump, ALU). 32-entry data stack (T/N) and return stack (R), full ALU encoding (16 T' selectors, T→N, T→R, N→[T] store, 2-bit DDelta/RDelta). Implements `DUP`, `DROP`, `SWAP`, `OVER`, `+`, `AND`, `OR`, `XOR`, `INVERT`, `=`, `<`, `U<`, `@`, `!`, `>R`, `R>`, `R@`, `EXIT`, and countdown loops.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| **Move**      | TTA/MOVE (Transport Triggered Architecture, Corporaal 1995) 16-bit machine. Fixed 32-bit instruction format `[dst                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |src|imm]`. Computation is a side effect of transport: writing to a trigger port (`alu.in2`, `mem.load`, `mem.store`, `br.target`) fires the FU. Register file r0–r7; ALU FU (16 operations: ADD/SUB/AND/OR/XOR/NOT/SHL/SHR/SRA/EQ/LT/ULT/NEG/INC/DEC/COPY); memory FU (16-bit word loads and stores); branch FU (conditional redirect). `SingleCycleTrain` only — FU state is not exposed as register hazards. |
| **F18A**      | GreenArrays GA144 F18A (2010) 18-bit stack computer. 29 opcodes packed four-per-word (5+5+5+3 bits) using the canonical GA144 encoding (0x00–0x1F). Includes `-if` (MinusIf 0x07: branch when T≥0) and `+*` (MulStep 0x10: shift-and-add multiply step). Data stack (T/S/8-deep) and return stack (8-deep); A and B address registers; 9-bit word-addressed PC (P). Canonical `if` semantics: branch when T==0 (false). Per-node memory: 64-word RAM, 64-word ROM, 256-word port space. Inter-node communication via synchronous `RendezvousArbor` channels (transfer completes only when both sides participate in the same tick). `F18AGrid` coordinates a rows×cols array of nodes; each node runs `SingleCycleTrain`; `F18AGrid.Step()` pre-checks `WillBlock` before driving decode→execute→commit. First multi-core ISA in the engine.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| **Face**      | Avalonia desktop UI. Opens with an **ISA launcher** so the user picks RISC-V or CHIP-8 before entering the appropriate view. The RISC-V side includes an RV32/RV64 selector and a workload preset picker (RV64 offers only the built-in demo and a custom ELF path, since no RV64 benchmark ELFs are bundled), a **multi-hart mode** toggle that swaps the single-config sweep for N harts (each with its own pipeline/predictor config, an optional private cache, and a memory pool id), running a fixed, hand-verified LR/SC atomic-increment demo program — the only workload safe to share across harts, since real ELFs assume a single, non-shared stack — with harts sharing a pool id sharing one coherent memory/bus/shared-LLC domain and harts in different pools fully isolated from each other, with results rendering as one row per hart in the same Chart/Table view, a **Waveform tab** that plots selected signals over simulation time from the run's periodic snapshots (per-window counter deltas plus derived windowed IPC and cache hit rates, one line per config × signal), a **PEvents tab** with a scrollable Argos-style pipeline waterfall (rows = instructions, columns = cycles, cells = stage abbreviation F/DC/D/IS/EX/RT/FL), a **SpecPC** gutter column showing the fetch-window start address, flush/misprediction cycles highlighted red, fetch-stall cycles dimmed, and an **Assembler tab** with a three-pane RISC-V assembly editor (editor + decoded listing + register file), and a **Vector tab** showing the v0-v31 vector register file (VLEN=128) as a grid with a selectable e8/e16/e32/e64 element-width view and a live vtype/vl readout. The Assembler tab has a sidebar **language toggle (RISC-V ASM / C)**: in C mode the source is compiled with `riscv32-none-elf-gcc` (selectable `-O` level) against a tiny `_start` stub, the resulting `.text` is disassembled into the listing, and single-cycle stepping highlights the current C source line via `objdump -dl` line info. A **Configurator tab** is a gem5-style architecture builder: an AvaloniaEdit pane edits a `.csx` script (the same `Script.ScriptHost`/`Pipeline.Spec.MachineSpec` API `Runner --script` uses) and hot-reloads it on both in-app edits (debounced) and external saves (`FileSystemWatcher`), against a workload preset picker, with a live cache/TLB/dial stat panel while running — a separate, coexisting path from the `ConfigViewModel`/`TrainConfig` GUI knobs the other tabs use. The CHIP-8 side renders the 64×32 pixel framebuffer at 10× scale with a 60 fps game loop, keyboard input (QWERTY layout mapped to the CHIP-8 hex keypad), and ROM load/start/pause/reset controls. |
| **Runner**    | Console entry point. Runs ELF binaries under named hardware configurations and emits results as Markdown or CSV. Accepts `--script <file.csx>` to evaluate a C# script that returns a `MachineSpec` and run the workload against it.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| **Script**    | C# and F# scripting host. `ScriptHost.EvaluateFileAsync(path)` compiles and runs a `.csx` (Roslyn) or `.fsx` (F# Interactive) file returning a `MachineSpec`, with all Spec/Cache/RiscV32 namespaces pre-imported and assemblies pre-referenced — no `#r` or `using`/`open` needed in the script. `ConfiguratorEngine` (UI-framework-free) wraps evaluate→build→snapshot for Face's Configurator tab.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| **Tests**     | xUnit tests. Engine tests under `Tests/Orrery`, `Tests/Pipeline`, `Tests/Mechanism`; small-ISA and hand-crafted-buffer RV64 tests under `Tests/Isa` (`Tests/Isa/RiscV64`); RV32 tests under `Tests/RiscV32/{Isa,Extensions,Pipelines,MultiHart,System,CoSim,Analysis}`; RV64 ISA-conformance tests (real compiled `rv64*-p-*` ELFs, all three pipeline trains) under `Tests/RiscV64/Isa`; RV64 `Tests/RiscV64/{System,MultiHart,CoSim}` (CLINT/HTIF/UART/raw-binary-workload, multi-hart pipeline/atomics/TSO fence, Spike co-sim golden-path + full riscv-tests conformance loop, torture co-sim, OpenSBI boot, Linux NOMMU boot) mirroring the RV32 System/MultiHart/CoSim suites; `Tests/Face` covers `ConfiguratorEngine` (UI-framework-free, so it's testable without pulling in Avalonia).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |

Projects live under `src/`: the ISA-agnostic core in `src/Core/`, ISA plugins in `src/Isa/`, and applications in
`src/Apps/`.

## Commands

```bash
dotnet build                                                    # build the whole solution
dotnet test                                                     # run all tests
dotnet test --filter "FullyQualifiedName~DecoderTests"          # one test class
dotnet test --filter "Name=SpecificTestMethod"                  # one test method
dotnet run --project src/Apps/Runner                                     # run the console entry point
dotnet run --project src/Apps/Runner -- --help                           # CLI usage
```

The development environment is provided by a Nix flake (`flake.nix`, `direnv`). It supplies the .NET 11 SDK,
`riscv{32,64}-embedded` GCC/binutils cross-toolchains for producing bare-metal test binaries, a
`riscv64-unknown-linux-musl-gcc` real-libc userspace toolchain (statically links cleanly, unlike the
`riscv64-unknown-linux-gnu` glibc cross toolchain already used in-tree for OpenSBI/the Linux kernel — that one needs
extra static-libc plumbing not wired up here) for building real linked binaries to validate the batch-mode harness
against, and native libraries required to launch Rider via the `rider` command.

## Naming convention

The codebase uses a horological metaphor for domain types throughout. Match it when adding code.

| Thing                   | Name       | Rationale                                                        |
|-------------------------|------------|------------------------------------------------------------------|
| Project                 | Horologium | The instrument that models time and mechanical motion            |
| Core                    | Orrery     | The clockwork engine at the center                               |
| Unit / Resource         | Gear       | A discrete mechanical part that meshes with others               |
| Port                    | Arbor      | The shaft that transmits motion between gears                    |
| Scheduler               | Escapement | The mechanism that releases energy in discrete, controlled steps |
| Pipeline Topology       | Train      | An arranged sequence of gears transmitting motion                |
| ISA Plugin              | Mechanism  | The specific mechanical logic governing a Train                  |
| Instruction Token       | Tooth      | What one gear passes to the next; the discrete unit of transfer  |
| µop                     | Impulse    | The single discrete push the Escapement delivers per tick        |
| Statistics              | Dial       | The readable face of the instrument                              |
| Configuration Parameter | Setting    | The instrument's configuration before it runs                    |
| One Simulation Run      | Revolution | One full turning of the mechanism                                |

## Architecture

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

### Pipeline trains (src/Core/Pipeline/)

Four Trains, all ISA-agnostic — they operate on `IArchState` and `ExecuteResult` closures with no dependency on any ISA
assembly. When used with RISC-V they pair with `Rv32Mechanism` (RV32IMAFCV) or `Rv64Mechanism` (RV64IMAFDAC):

- **`SingleCycleTrain`** — one Gear, one instruction per tick (fetch → decode → execute → writeback, all inline). Used
  to validate the Mechanism independently of pipeline complexity.
- **`FiveStageTrain`** — classic IF/ID/EX/MEM/WB pipeline. Each stage is its own Gear wired in sequence via Arbors. A
  `HazardUnit` handles RAW stall detection and register forwarding (controlled by a `forwardingEnabled` flag). Branch
  handling uses a pluggable `IBranchPredictor`; built-in implementations include static predictors (`AlwaysNotTaken`,
  `AlwaysTaken`, `AlwaysBackwardNotForwards`), 1-bit and 2-bit saturating counter predictors, correlated (m,n), Gselect,
  Gshare, L-TAGE (TAGE with a loop predictor overlay), ITTAGE (Indirect Target TAGE — tagged geometric-history
  tables store predicted *target addresses* instead of counters, each entry with a confidence counter for
  update hysteresis and a usefulness bit for allocation, so the same indirect-branch PC can resolve to different
  targets depending on execution history, e.g. virtual dispatch; Seznec, CBP-3/JWAC-2, 2007), IMLI (Inter-Mediated Loop Iteration — single shared
  loop-iteration counter indexes the PHT so body-branch predictions are iteration-specific; Jiménez, IEEE CAL 2018),
  LLBP (Last-Level Branch Predictor — context-addressed backing store over TAGE-SC-L; Rolling Context Register hashes
  recent taken-branch PCs into a context ID, patterns indexed by TAGE's PC×GHR tags; Schall et al., MICRO 2024),
  LLBP-X (LLBP Revisited — adds a Context Tracking Table that promotes high-contention contexts from shallow W=2 to deep
  W=64 history depth, splitting storage into short/long history ranges; Schall et al., HPCA 2026), VLA-TAGE (
  Vector-Loop-Aware TAGE — extends TAGE-SC-L with a power-gating mechanism; a Vector Loop Table tracks backward branches
  and a Loop Monitor estimates remaining iterations from comparison-register operand values at execute time; when the
  innermost loop is confirmed vector-intensive with ≥32 estimated remaining iterations the PEN signal bypasses tagged
  history tables T1–T3 and the SC predictor, relying only on bimodal T0 and the loop predictor; PEN is deasserted 5
  iterations early to give the full predictor time to re-engage before loop exit; `GatedPredictions` counter enables
  power-reduction modeling; Zhang et al., IEEE CAL 2026), RUNLTS-sR (register-value-correlated branch prediction layered
  on TAGE-SC-L: per-bank freshness-tracked register digests select a most-useful currently-valid register whose value
  indexes a direction-counter table, summed across banks into a final-override score; Koizumi, Maekawa, Mizuno, Kuroki,
  Tsumura &amp; Shioya, CBP 2025), LVCP (Load Value Correlated Predictor — an H2P Branch Table classifies
  hard-to-predict branches, a Load Tracking Queue records recent load (PC, value) pairs via
  `IValueAwareBranchPredictor.NotifyRegisterResult`, and a direct-mapped, permanently-retiring correlation table keyed
  on (branch PC, load PC, load value) overrides TAGE-SC-L when confident; Man, Gou, Liu, Chen &amp; Bao, CBP 2025),
  BranchNet (a per-branch CNN over folded global history, trained offline in a `BranchProfiler` functional pre-pass
  against H2P branches and loaded via `FromProfile`; Zangeneh et al., MICRO 2020), and TEA (Timely, Efficient, and
  Accurate branch precomputation — an offline `TeaProfiler` pre-pass runs a Backward Dataflow Walk from each H2P
  branch's compare operands through retired instructions to discover its producer dependence chain, which is then
  correlated online, in place of the paper's literal slice re-execution (not expressible in the ISA-agnostic `Mechanism`
  layer), via a permanently-retiring correlation table keyed on chain-producer PC/value pairs; Deshmukh, Cai &amp; Patt,
  MICRO 2024), `CbpFfiPredictor` (loads a CBP-3/CBP-5, 2016-era `class PREDICTOR` submission compiled to a native
  shared library via `native/CbpShim/build.sh`, driven through `GetPrediction`/`UpdatePredictor` over a P/Invoke ABI
  — desktop-only, since `NativeLibrary` loading requires `dlopen`/`LoadLibrary`), `CbpNgFfiPredictor` (loads a
  CBP2025/CBP-NG, AmpereComputing/cbp-ng submission — a templated harcom struct, not a fixed `class PREDICTOR` —
  compiled to a native shared library via `native/CbpNgShim/build.sh`; drives `predict1`/`predict2`/`update_condbr`/
  `update_cycle` through the harcom clocked-register hardware-timing-modeling DSL one prediction block at a time;
  desktop-only, and safe only for `SingleCycleTrain`, since harcom predictors keep per-block state in shared registers
  that a second outstanding prediction — anything with unresolved fetch overlapping commit — would clobber) and
  `CbpNgCommitDrivenPredictor` (wraps `CbpNgFfiPredictor` so the same harcom submissions run safely on `FiveStageTrain`
  and `OooeTrain`: fetch-time steering comes from an ordinary reentrant-safe C# predictor, `GsharePredictor` by
  default, while the native predictor is only ever touched inside `Update`, which the `IBranchPredictor` contract
  guarantees fires at commit in program order — so harcom's own `predict1`/`predict2`/`update_condbr`/`update_cycle`
  run back to back for one already-resolved branch at a time, exactly matching how CBP itself replays a trace; not
  suitable for `CprTrain`, which trains predictors out of program order at execute), Bullseye (H2P-branch subsystem
  layered on TAGE-SC-L
  via a HIT — H2P Identification Table — that admits branches past adaptive execution/misprediction thresholds, then
  arbitrates between TAGE-SC-L and a dual local/global perceptron pair trained with Seznec's O-GEHL dynamic-threshold
  rule, filtering TAGE's own update after sustained perceptron-only wins; Behrendt, Pun &amp; Nair, "Taming Wild
  Branches: Overcoming Hard-to-Predict Branches using the Bullseye Predictor", CBP 2025), and HYPRE (a
  hyperdimensional-computing / sparse-distributed-memory predictor: per-history-length HyperVector accumulators
  (Taken/Not-Taken) keyed on a deterministic hash of PC and folded history, longest-match-wins by Hamming-distance
  threshold against an HD-bimodal fallback, trained one-shot-learning style — reinforced only when not already
  confidently correct; Vougioukas, Sandberg &amp; Nikoleris, "Branch Predicting with Sparse Distributed Memories",
  arXiv:2110.09166, 2021), and MPP (Multiperspective Perceptron — five hashed "perspective" feature
  tables (BlurryPath, RecencyPos, GhistModPath, and backward/forward IMLI taken-streak counters,
  matching gem5's `MultiperspectivePerceptronTAGE8KB` reference configuration) whose weighted sum is
  folded additively into TAGE-SC-L's own statistical-corrector total before the existing threshold
  decision, trained whenever that combined total disagrees with or is under-confident about the
  outcome; Jiménez, "Multiperspective Perceptron Predictor", CBP 2025), plus a `ReturnAddressStack`
  wrapper for call/return prediction, and a `TrueOraclePredictor` that runs a
  `SingleCycleTrain` functional pre-pass to collect the complete branch trace and replay it with zero mispredictions (
  useful as an IPC upper bound). Both instruction and data memory support optional set-associative caches and TLBs. A
  `StoreBuffer` provides deferred writes with store-to-load forwarding.
- **`OooeTrain`** — superscalar out-of-order pipeline using Tomasulo's algorithm. Physical register renaming, ROB-based
  in-order commit, and a unified issue queue. Functional units are configurable per class (`FuLatencyConfig`): each
  class (integer ALU, multiplier/divider, pipelined FP, FP divide/sqrt, load-store, branch, system) has an independent
  issue-port count and execution latency; multi-cycle results flow through a countdown-based in-flight buffer before CDB
  broadcast. Default latencies: integer ALU 1 cycle, integer mul/div 3, pipelined FP 4 (
  add/sub/mul/fma/compare/convert), FP div/sqrt 16, load-store 1. Dedicated `LoadQueue` and `StoreQueue` circular
  buffers track in-flight speculative loads and stores independently of the ROB. Loads and stores share a monotonic
  sequence number at dispatch so program order can be determined across queues without ROB-index wrap. Loads issue
  speculatively without waiting for older stores; store-to-load forwarding supplies the correct value when the store has
  already executed, and a memory-order violation squash (flush + re-execute from the load's PC) recovers when the store
  resolved after the load. A `mem_order_violations` counter tracks re-executions. A **store-set predictor** (
  `StoreSetPredictor`, enable with `enableStoreSets: true`) avoids unnecessary squashes by predicting, at dispatch,
  which stores a load depends on: a Store-Set Identifier Table (SSIT, 1024 entries, PC-indexed) maps instructions to
  store-set IDs; a Last Fetched Store Table (LFST, 1024 entries) tracks the most-recently-dispatched store per set;
  loads stall at issue until their predicted store's address is known; violations train the predictor via four
  SSID-merge rules (Chrysos &amp; Emer, ISCA 1998). Because the SSIT carries no address information, one genuine
  conflict permanently merges every future dynamic instance of that load/store PC pair — costly for recursive/generic
  functions that reuse one PC pair across many independent addresses. Both tables are cleared every 4096 load
  dispatches (`clearPeriod`) to bound that cost; the value was chosen by sweeping the full gem5-compare benchmark
  suite (see `docs/gem5-comparison.md`). A **speculative memory bypassing predictor** (NoSQ, `SmbPredictor`,
  enable with `enableSmbBypass: true`) lets a load short-circuit straight to an early result instead of waiting
  for its own execution: a PC-indexed, confidence-gated table predicts the SSN distance (shared LQ/SQ dispatch
  sequence number) back to the producing store, and if a store with that exact distance is live in the SQ at
  dispatch with a static access width matching the load's, the load is marked `Bypassed` and gets an early PRF
  write + CDB broadcast the moment that store's value resolves — without needing anyone's address. The load's
  own shadow execution still runs the ordinary pipeline afterward and is the sole thing that gates its commit;
  a mismatch marks it `BypassMispredicted`, triggering the same flush + retrain recovery as an ordinary
  memory-order violation. Ordinary (non-bypassed) loads that forward from a live store also train the
  predictor, so the very first prediction for a PC doesn't have to wait for a successful bypass to seed it.
  v1 scope reductions (Sha, Martin &amp; Roth, MICRO 2006; Tyson &amp; Austin, MICRO 1997): the predictor is
  path-insensitive (PC-indexed only, no history register); bypass is full-word/zero-offset only (the predicted
  producer's static width must equal the load's, so no address is ever needed to trust a prediction); and the
  `LoadQueue` is retained unconditionally as the verification backstop rather than eliminated, exactly as the
  papers themselves treat LQ elimination as an optional, performance-neutral extension. A **value predictor** (pass
  an `IValuePredictor` via the `valuePredictor` constructor parameter; `null` disables the feature entirely) predicts
  the destination register value of eligible instructions (`IntegerAlu`, `IntegerMulDiv`, `Load`, `FloatingPoint`,
  `FloatDivSqrt`, `System` — i.e. any single-register-destination result that lands in the PRF; excludes `Atomic`,
  whose secondary destination bypasses the PRF, and `Vector`, which isn't renamed) at rename, writes it
  speculatively into the PRF and marks it ready immediately — so dependent instructions issue and execute without
  waiting for the real producer — while the producing instruction still executes for real in the background. Two
  predictors are provided: `LvpVp` (Lipasti &amp; Shen, "Exceeding the Dataflow Limit via Value Prediction",
  MICRO 1996 — the LVPT scheme), a tagless, PC-indexed table of last-seen values; and `VtageVp` (Perais &amp;
  Seznec, "Practical Data Value Speculation for Future High-end Processors", HPCA 2014), which adapts the ITTAGE
  indirect-branch predictor to value prediction — a tagless `LvpVp` base component backed by six tagged
  components indexed by a hash of the PC and a geometrically increasing number of global-branch-history bits (2, 4,
  8, 16, 32, 64), so it can predict back-to-back occurrences of an instruction in a tight loop with no same-cycle
  critical dependency, unlike local-value-history predictors. Both are gated by a `ForwardProbabilisticCounter`
  (Perais &amp; Seznec §5, after Riley &amp; Zilles, HPCA 2006): a 3-bit saturating counter whose forward
  transitions are only taken probabilistically, mimicking a much wider counter at a fraction of the storage, and
  which hard-resets to 0 on any misprediction; a prediction is used only once fully saturated. Recovery mirrors
  `SmbPredictor`'s: no selective reissue, no execution-time repair path — a mismatch discovered when the real
  result completes (`StepComplete`) is squashed with a full re-fetch the moment the mispredicted instruction reaches
  the ROB head (`StepCommit`), the paper's central finding that squash-at-commit performs within noise of an
  idealized selective-reissue implementation once FPC accuracy exceeds ~99.5%. `VtageVp` keeps its own
  speculative/committed global-history shadow (independent of whichever `IBranchPredictor` is configured), advanced
  at fetch and rewound via checkpoint/restore on both a full flush and an execute-time partial squash — so its
  index survives ordinary branch mispredictions exactly, not just approximately. **EOLE Late Execution** (Perais
  &amp; Seznec, "EOLE: Paving the Way for an Effective Implementation of Value Prediction", ISCA 2014; enable with
  `enableEoleLateExec: true`, requires a `valuePredictor`) removes confidently value-predicted single-cycle ALU
  ops from the OoO scheduler entirely: instead of entering the IQ, they are marked complete at Dispatch (their
  predicted value is already the live PRF value) and their real computation is deferred to an in-order
  verify-at-Commit step reusing the exact same `ExecuteOne`/squash-at-commit machinery as ordinary value
  prediction, just relocated from `StepComplete` to `StepCommit` — so it costs no new recovery path, only a
  narrower window for OoO issue-port contention to matter, letting a narrow-issue machine approach a wider one's
  performance on ALU-heavy code (the paper's own headline result). **EOLE Early Execution** (same paper §3.2;
  enable with `enableEoleEarlyExec: true`, no `valuePredictor` required — operand readiness at rename is
  provenance-agnostic) computes a single-cycle `IntegerAlu` instruction immediately in `StepRename`, in-order,
  whenever both its source registers are already ready — an immediate, an already-committed value, or a value
  prediction all count, exactly as the paper specifies ("operands are never read from the PRF" in their hardware;
  Horologium's PRF already stores all three provenances, so reading it is the equivalent simplification). The one
  detail that came directly from the paper rather than Horologium's own structure: the paper found chaining
  Early-Execution results *within the same rename cycle* ("more than a single [ALU] stage") "highly inefficient"
  and settled on a 1-deep design where only the *previous* cycle's Early-Execution results may feed a new one.
  `StepRename`'s `_eeWrittenThisTick` set enforces exactly that cap (cleared every tick), which matters because
  `StepRename` can drain a multi-tick decode-queue backlog in one call — without the cap, a stalled dependency
  chain would collapse implausibly in a single tick the moment the backlog cleared. An Early-Executed
  instruction's result needs no separate commit-time verification (unlike Late Execution): the only way one of
  its operands could be wrong is if it came from a value prediction, and in-order commit guarantees that
  prediction's own squash-at-commit path (if it mispredicts) flushes the Early-Executed consumer before it ever
  reaches the ROB head. Both bypass `StepIssue`/`StepExecute` entirely, so the Top-Down (Yasin, ISPASS 2014)
  Level-2 Core/Memory Bound split — the only TMA/CPI-stack accounting that reads execute-stage traffic rather
  than dispatch-stage slot counts or ROB-head completeness — folds each tick's EOLE-bypassed count in at the
  end of `StepDispatch` so a narrow-issue machine leaning on EOLE isn't misreported as execution-stalled.
  VTAGE's `TryPredict`/`Update` calls take an explicit per-instruction `ValueHistoryCheckpoint`, captured once at
  that instruction's own Fetch and threaded through Rename/Commit, rather than each method separately reading
  whichever of the predictor's live-speculative or committed-shadow history register it used to read — the
  latter let heavy squash/refetch churn drift the two apart, aliasing a confidently-wrong prediction onto a
  slot training could never reach to correct (a permanent livelock, not just a missed opportunity).
  **`StrideVp`** is a computational value predictor (Sazeides & Smith's taxonomy, as summarized in
  Perais & Seznec, HPCA 2014 §2) complementary to LVP/VTAGE's value-repetition approach: it tracks a static
  instruction's last value and the constant stride between successive occurrences, predicting `lastValue +
  stride`, so a monotonically incrementing register (which never repeats a value, and so never lets
  LVP/VTAGE's confidence saturate) still predicts trivially. Confidence is a 4-state FSM (`Init`/`Transient`/
  `Steady`/`NoPred`) requiring two consecutive matching strides to reach `Steady` before predicting -- a
  "2-delta"-style confidence gate, not a reproduction of any specific historical stride predictor's exact
  mechanism. It also tracks an in-flight speculative depth per PC -- how many renamed-but-uncommitted
  occurrences of that PC are still unresolved, confident prediction or not -- so a tight loop with several
  genuinely overlapping iterations (renamed well ahead of commit under a competent branch predictor) predicts
  `lastCommittedValue + stride * (depth + 1)` rather than a single un-scaled stride step; measured directly in
  Horologium's own OoOE pipeline, the latter mispredicted roughly 44% of the time once overlap was allowed to
  develop. Counting every `TryPredict` call toward the depth (not just confident ones) closes a residual
  undercount in the warmup and post-squash windows, where earlier, still-in-flight occurrences of the same PC
  hadn't yet reached `Steady` when renamed but still owed a matching commit -- the 84 residual mispredicts left
  by the depth-scaling fix above dropped to 0 on the same loop once this was fixed. The depth counter is reset
  on any squash (a discarded occurrence has no commit to decrement it, so it would otherwise leak upward).
  **`HybridVp`** composes any context-based and computational `IValuePredictor` (e.g.
  `VtageVp` + `StrideVp`) per the paper's own §7.1.2 combination rule: a lone confident
  component's prediction is used as-is; two confident components that agree are used; two that disagree
  suppress the prediction entirely; both are trained at every retire regardless of which one predicted. Not
  modeled: the paper's further optimization of feeding one component's speculative prediction to the other to
  resolve back-to-back same-PC occurrences within a single cycle -- Horologium's pipeline only calls
  `TryPredict`/`Update` once per instruction, at Rename/Commit, so there's no equivalent intra-cycle chaining
  to hook into. **`DynamicClassificationVp`** (Rychlik et al., CMuART-1998-01, §3.2.3 "Efficient
  Dynamic Scheme") is the alternative to always-query-both: each PC is assigned, after a 3-value learning
  window, to *at most one* of the two components -- equal consecutive deltas (including zero) route to the
  computational component, anything else routes to the context component, folding the paper's 3-predictor
  split (Popular Last Value / Stride+ / FCM) onto Horologium's 2-component hybrid since VTAGE's own tagless
  LVP base already subsumes Popular Last Value. A classified PC whose component stops predicting confidently
  after having predicted at least once is evicted -- permanently to Don't Predict if it was on the context
  (FCM-role) component, or back to Unclassified to relearn if it was on the computational one -- after
  `evictThreshold` (default 2) consecutive non-confident `TryPredict` calls, resetting on the next confident
  one. That threshold is still an adapted proxy for the paper's confidence-reaches-zero trigger, since
  `IValuePredictor` exposes no raw confidence value to distinguish "low but nonzero" from "zero" -- but it no
  longer drops a context-classified PC (whose whole premise is ~95%, not 100%, accuracy) to permanent Don't
  Predict on its first ordinary miss. A **critical-path
  predictor** (`TokenPassingCriticalityPredictor`,
  enable with `enableCriticalityPrediction: true`) biases `StepIssue` to prefer predicted-critical
  instructions when several ready instructions compete for the same functional-unit/port slot. Each
  instruction is modeled as a 3-node dependence graph (dispatch/execute/commit); the pipeline resolves,
  at commit, which of seven edge types (ROB-stall, branch-redirect, last-arriving-operand producer, etc.)
  fed each node, and a token-passing predictor plants a token at a seed instruction's execute node,
  propagates it forward along those edges, and trains a 16K-entry PC-indexed hysteresis table on whether
  the token survives `500 + robCapacity` commits (Fields, Rubin &amp; Bodík, "Focusing Processor Policies
  via Critical-Path Prediction", ISCA 2001). Purely a scheduling-priority hint — disabled by default and,
  when enabled, never changes committed architectural results, only issue order among already-ready
  instructions. **Runahead execution** (enable with `enableRunahead: true`, budget via `runaheadBudget`,
  default 200) pre-executes past a full-window stall to generate prefetches (Mutlu et al., "Runahead
  Execution: An Alternative to Very Large Instruction Windows for Out-of-order Processors", HPCA 2003;
  Naithani, Roelandts &amp; Eeckhout, "Precise Runahead Execution", HPCA 2020). A literal port of either
  paper's release-and-refetch or elastic-ROB-release mechanism doesn't fit: `ReorderBuffer` retires
  strictly from the head with no out-of-order release, and dispatch is already unconditionally stalled
  the instant the ROB is full — there is no free ROB/IQ capacity to run inside during the stall the way
  PRE's target microarchitecture has. What Horologium builds instead is a self-contained shadow execution
  lane, entered only when dispatch is stalled behind a full ROB whose head is an incomplete load. Because
  real commit and real dispatch are already frozen for the whole stall, the shadow lane can safely draw
  fresh physical registers from the same live `RenameMap` free list with zero collision risk (nothing else
  is renaming during the stall) and undo everything on exit by restoring a `RenameMapSnapshot` of just the
  RAT — no ROB/IQ/LQ/SQ involvement needed. Since `SetAssociativeCache.Read()` installs data functionally
  and immediately on every call (hit or miss; miss cost is a separately-accounted stall-cycle count, not
  an async fill), no runahead cache or INV-bit array is needed either: the only genuinely unavailable value
  during an episode is the blocking load's own not-yet-written physical register and anything that
  transitively reads it, tracked by a small tainted-physical-register set seeded at entry from every
  not-ready live RAT mapping. A tainted source blocks a shadow load/store's real memory access entirely —
  broadened from the papers' narrower "tainted address" framing because identifying which specific source
  is the address is not exposed generically by `ITooth` and would require ISA-specific operand-ordering
  knowledge, which the pipeline/ISA isolation boundary forbids; the broadened rule is strictly more
  conservative (it may occasionally forgo a safe prefetch, never a correctness risk, since nothing shadow-
  computed is ever committed). Shadow stores write only into a scratch dictionary keyed by exact
  `(address, bytes)`; shadow loads check that dictionary first, then fall through to the real
  `DLayers.Accessor` — which is what actually warms the real cache for the real pipeline to find hot once
  it resumes. v1 scope reductions: the shadow stream is scalar-only (`IntegerAlu`, `IntegerMulDiv`, `Load`,
  `Store`, `Branch`, `ConditionalBranch` — anything else, including `Vector`/`Uve`, exits the episode
  cleanly rather than modeling side effects); shadow branches call `IBranchPredictor.Predict()` for direction but
  never train predictor history or touch the RAS; runahead is disabled whenever fetch is not effectively
  bare-metal (active Sv32 paging resolves a non-identity physical address for the fetch PC); and shadow
  throughput is capped at `issueWidth` instructions per real cycle, bounded per-episode by
  `runaheadBudget`. The LQ, SQ, and RAS are never touched by any runahead code path. **Vector Runahead**
  (enable with `enableVectorRunahead: true`, lane width via `runaheadVectorWidth`, default 8; requires
  `enableRunahead`) extends the shadow lane to chase long dependent (pointer-chasing) gather/scatter
  chains instead of exiting the instant the real blocking load resolves (Naithani, Ainsworth, Jones &amp;
  Eeckhout, "Vector Runahead", ISCA 2021). A PC-indexed, direct-mapped stride table (`LastAddr`/`Stride`/
  2-bit saturating `Confidence`/learned `Terminator`, sized like `StridePrefetcher`'s RPT) is trained only
  from the real (non-shadow) demand-load stream at the existing prefetcher hook. Once a load's own PC
  reaches saturated confidence, the shadow lane replicates its own instruction stream `runaheadVectorWidth`-
  wide instead of stepping one iteration at a time, and that vectorized state propagates through dependent
  arithmetic and indirect loads exactly the way `_runaheadTainted` already propagates the invalid-bit —
  membership in `_runaheadVectorized` is the paper's vectorize-bit, membership in `_runaheadTainted` is its
  invalid-bit. Per the paper's termination-condition change (innovation #1), the shadow lane keeps running
  past the point a scalar-only episode would exit as long as a chain is actively vectorizing, stopping only
  when the chain loops back to its own origin PC (backfilling the learned terminator) or reaches a
  previously-learned terminator. A chain-origin load whose address operand is itself tainted — typically
  because the front end has renamed several loop iterations ahead of a stalled dispatch, not because it
  truly depends on the stalled load's value — can still vectorize directly off the trained `LastAddr`/
  `Stride` sequence rather than requiring the live operand; this RPT-driven bypass is what lets Vector
  Runahead chase a simple strided loop-induction address in practice. As with the scalar feature, no real
  state is ever put at risk: the real `RiscV32.VectorRegisterFile` and RVV gather/scatter encodings are
  never touched, since a shadow episode must stay perfectly discardable and there is no physical VRF or
  vector rename table to safely draw scratch registers from the way scalar runahead draws from the live
  integer `RenameMap` free list. Vectorization is instead pure N-wide replication of the existing scalar
  shadow body against new scratch, physical-register-indexed lane state (`_runaheadVectorLanes`,
  `_runaheadVectorized`), discarded on exit exactly like `_runaheadTainted`/`_runaheadStoreBuffer` already
  are. **Vector unrolling** (§III-G of the paper, bounded by `runaheadUnrollLength`, default 8) extends a
  chain past its first termination point: instead of ending the moment the shadow lane loops back to the
  chain's origin PC or reaches the learned terminator, `TerminateOrUnroll` issues another
  `runaheadVectorWidth`-wide round from the same origin (advancing a round base address by
  `runaheadVectorWidth × Stride` each time) until `runaheadUnrollLength` total rounds have run, matching
  the paper's default of U=8 rounds of N=8 lanes (64 scalar-equivalent iterations) before falling back to
  normal shadow stepping. A `_runaheadCappedOrigins` set records which origin PCs have spent their round
  budget so a later revisit of the same PC in the same episode does not silently restart a fresh chain.
  Physical-register pressure from many rounds is handled by immediate free-on-rename reclamation
  (`FreeShadowRename`) rather than the paper's VRAT plus in-order register-deallocation queue: because the
  shadow lane issues strictly one instruction at a time along a single PC (never the paper's overlapped,
  out-of-program-order pipelined issue), any shadow instruction that could still read a register's old value
  has, by construction, already executed before the instruction that redefines it, so a physical register
  can be freed the instant its architectural register is renamed again — an RDQ's ordering guarantee for
  free, with no queue needed. **Vector pipelining** (the paper's P overlapped in-flight rounds, bounded by
  `runaheadPipelineDepth`, default 1) decouples round issuance from the shadow PC's single-PC loop-body
  walk: `PipelineRoundsThisVisit` computes `min(P, U − roundsSoFar)` — how many of the remaining unroll
  rounds to pack into the *next* origin-load vectorization event — so a single visit to the chain origin
  produces an N×rounds-wide lane array instead of a fixed N-wide one, needing only `⌈U/P⌉` total loop-body
  walks to reach `U` total rounds rather than `U` separate ones. No VRAT is needed the way the paper's fixed
  512-bit AVX vector registers require one: because "vectorization" here is already pure scratch lane-array
  replication (see above), a vectorized physical register's lane array is naturally width-generic —
  `VectorizeByReplay` propagates whatever width the origin produced (`srcLanes.Length`, not a fixed field),
  so ALU/indirect-load propagation through the dependent chain automatically carries P× the width with zero
  extra bookkeeping. `runaheadPipelineDepth: 1` is defined as exactly today's pre-pipelining behavior (every
  origin visit packs 1 round, matching `TerminateOrUnroll`'s old per-visit increment byte-for-byte) so the
  knob is purely additive. This is a real timing effect, not a counter relabeling: `SetAssociativeCache` has
  finite MSHR capacity (`MshrCount`, per-cycle countdown via `TickMshr`, capacity-stall charging via
  `ChargeAndAllocateMshr` when every slot is busy — `MshrCapacityStalls`), and shadow-lane reads share the
  same cache instance and `_pendingStalls` accumulator as real loads, so packing many rounds' worth of
  misses into one instant (no `TickMshr` ticks between them, unlike misses spread across separate
  loop-body-walk rounds) measurably contends for a small MSHR table — the *direction* of the paper's own
  §VI-B/Fig. 11 MSHR-count sensitivity (an under-provisioned MSHR table limits how much a deep pipeline
  depth can help) shows up here too, as higher `MshrCapacityStalls` under a tight `CacheMshrCount`, and
  vanishes (contention stays negligible) once the table is sized ≥ N×P. In principle a chain-bound episode
  (one that `UpdateChainTermination` keeps open past the point the originally-blocking real load already
  resolved, purely to keep vectorizing) *should* let a shorter, pipelined walk resume real dispatch/commit
  sooner than a serial one needing `U` separate walks — the episode's real-cycle length is `max` of the
  load's own resolution time and the chain's; only the second term is pipeline-depth-dependent, and it
  shrinks with `P`. In measurement, though, this potential saving was never isolable from two confounds
  that dominate it in every synthetic single-pass-loop configuration tried here: MSHR contention (above)
  and a more severe effect — deep `P` packs far more simultaneous speculative reads through the shared
  cache than a serial round would, and on these small test programs that measurably *raised* real demand-
  side `dcache_misses` (over an order of magnitude in one configuration) rather than lowering it, i.e. the
  extra speculative volume evicted or otherwise disturbed data the real stream still needed sooner than the
  far-future addresses it fetched — cache pollution, not the paper's assumed clean-prefetch benefit.
  Net effect measured across every configuration tried up to that point: pipelining came out
  neutral-to-worse on real `cycles`/`dcache_misses`, never demonstrably better.

  **Follow-up investigation, resolved:** disentangling MSHR contention and cache pollution from the
  in-principle episode-shortening benefit required a program where `U` rounds' reach never overshoots
  real future demand and an MSHR table large enough (`CacheMshrCount ≥ N×P`) to keep contention at
  zero — once both confounds are eliminated by construction, `runaheadPipelineDepth` **does** shrink
  real cycles, monotonically (measured: P=1 2565, P=2 2506, P=4 2492, P=8 2487 cycles on a 200-iteration,
  one-cache-line-stride program). But the deeper finding, found only by adding the `enableRunahead: false`
  baseline that earlier comparisons omitted, is less flattering than "pipelining helps": on this same
  program, turning Vector Runahead **off entirely** measured 1311 cycles — faster than *every*
  runahead-enabled configuration, including the best-case fully-pipelined one. The reason is structural,
  not confound-related: `StepRename` freezes real rename for the entire duration `_runaheadActive` is
  true (a chain-bound episode extends that freeze past the point the real blocking load resolves, for as
  long as the chain keeps unrolling), which blocks further iterations from even entering the ROB window
  during the episode — but this program's ROB (8 entries, 3 instructions/iteration) is already deep
  enough that plain OoO execution extracts most of the available memory-level parallelism for free,
  without any speculative help. Runahead's rename freeze is pure overhead here; deeper `P` only shrinks
  how long that freeze lasts (recovering part of the self-inflicted cost), it never converts Vector
  Runahead into a net win on a pattern the ROB alone can already parallelize. So the TODO's question
  ("why does pipelining measure neutral-to-worse") resolves to: it usually isn't really about pipelining
  at all — it's Vector Runahead itself being net-negative on ROB-parallelizable streaming patterns, with
  `P` only ever modulating the size of that self-inflicted loss. Locked in by
  `VectorRunaheadPipelineTests.PipelineDepthP_RecoversPartOfRunaheadsOwnOverhead_ButNeverBeatsRunaheadOff`.
  Also worth remembering: this comparison is itself sensitive to `runaheadBudget`/`extraPhysRegs` — under
  small values (400/32) sized for shorter chains, the same 200-iteration program's P=1-vs-P=8 comparison
  *inverts* (P=8 measured worse: 3009 vs. P=1's 1808 cycles). Checked, not asserted on a guess: at P=1
  under the small budget, `runahead_episodes` explodes to 645 (vs. 9 in the well-provisioned regime) while
  total `runahead_instructions` stays tiny (226) — nearly every episode aborts on `!_rat.HasFree` almost
  immediately, so P=1 does barely any speculative work and ends up cheap almost by accident (close to the
  1311-cycle runahead-off floor). At P=8 under the same small budget, episodes stay low (13) but
  `runahead_vector_lane_accesses` is high (576 = 9 chains × 64 lanes) — most episodes *do* complete a full
  unrolled chain before something forces a restart, so P=8 pays for several complete, largely redundant
  re-vectorizations of overlapping address ranges across those restarts. A fourth, distinct mechanism from
  MSHR contention, reach-overshoot, and the ROB-MLP finding above: tight budgets change how much redundant
  speculative work survives per episode, with opposite-signed effect for small vs. large P.
  `TryVectorizeShadowStep`'s untainted chain-origin path now tracks `_runaheadRoundBaseAddr` explicitly
  across visits, mirroring `TryVectorizeTaintedLoad`, instead of re-deriving each round's lane addresses
  from `mem.LastReadAddress` (which only advances one real loop iteration's stride per visit and made
  later rounds' lane ranges overlap almost entirely with earlier ones). This closes a real divergence
  between the two sibling paths, but empirically it produced no measurable behavioral difference in this
  model — real scalar demand coverage and shadow-lane stepping already reach these addresses on their own,
  so the overlap it fixes was never the actual bottleneck; kept for correctness and consistency, not a
  measured performance gain.

  v1-v3 scope reductions: no per-lane divergence/masking (an invalid lane is simply
  marked tainted rather than modeled with a real predicate mask); fixed lane width, not tied to the real
  `VLEN=128` architectural setting; and only one live chain is tracked at a time. Control-flow
  speculation follows gem5: direct unconditional jumps (`jal`/`j`,
  flagged by `FetchHint.IsUnconditional`) are resolved straight to their statically known target at fetch instead of
  being routed through the direction predictor; every direct branch (conditional included) takes its taken-target from
  the decode hint (`FetchHint.BranchTarget`) rather than a possibly-cold predictor BTB, so a stale/aliased BTB entry can
  never send speculative fetch to a null address (only indirect branches use the predictor's target, and a cold indirect
  target falls through); the `ReturnAddressStack` is checkpointed against an architectural shadow (advanced only when a
  call/return retires) that restores it on every flush, so wrong-path push/pop corruption does not leak into later
  return predictions; and history-based predictors keep a **speculative global history** advanced at fetch (
  `IBranchPredictor.SpeculativeHistoryUpdate`) and restored on flush (`RecoverSpeculativeHistory`) against an
  architectural committed-history shadow, so TAGE/LTage lookups index up-to-date history across the ROB window rather
  than stale commit-time history (the in-order trains are unaffected — they retain commit-time history bit-for-bit).
  Branch mispredicts are resolved at **execute** rather than commit, matching gem5's `iew`: a branch that resolves off
  its predicted path before reaching the ROB head triggers a **partial squash** that redirects fetch immediately,
  discarding only the younger in-flight instructions while the branch and everything older stay live and commit
  normally (a full flush is reserved for traps, load-order violations, and a mispredict that is already the ROB head).
  Exact recovery of speculative predictor state uses a per-branch history checkpoint captured at fetch (
  `IBranchPredictor.CaptureHistory`/`RestoreHistory`, covering the shared global- and local-history helpers and the
  whole TAGE family) plus a `ReturnAddressStack` rebuilt from the committed shadow and a replay of the surviving
  in-flight calls/returns; a branch younger than an older in-flight halt or trap is left to the commit-time path since
  that older entry will redirect first. Memory-level parallelism is modelled on both sides: each missed load carries the
  miss penalty in its own in-flight countdown so independent misses overlap (load-side MLP); a bounded write buffer (
  `writeBufferCapacity` parameter, default 0) absorbs committed store write-miss penalties asynchronously so the
  pipeline is not frozen while the write bus drains (store-side MLP). The D-cache is write-through / no-write-allocate,
  so writes reach memory the instant they are issued and write-buffer occupancy is a pure bus-latency model with no
  forwarding implications. TSO fences are modeled: a FENCE whose predecessor set contains W and successor set contains
  R (`ITooth.IsStoreLoadFence`, including FENCE.TSO) issues only at the ROB head once the write buffer has fully
  drained, and younger loads may not issue while it is in the ROB — closing the store→load window, the only reordering
  the train performs; all other fence flavours are timing no-ops because TSO already provides their ordering. The
  `OooeTrain` public API is a thin wrapper; `OoOPipelineCore` is the single-Gear implementation. A `StreamingEngine` (
  `src/Core/Orrery/Streaming/StreamingEngine.cs`) is embedded in every `OoOPipelineCore`: it manages up to 8
  independently configured affine memory streams (`StreamDescriptor` in `src/Core/Mechanism/`: base address, element
  width in bytes, element count, byte stride), each backed by a prefetch buffer of configurable depth. The engine's
  `Step()` is called unconditionally every pipeline cycle so streams prefetch ahead of consumption; streams are
  architectural state and survive pipeline flushes. UVE (Unlimited Vector Extension) instructions consume streams in the
  OoO pipeline: `ToothClass.Uve` ops are head-serialized (like `Vector`); the pipeline injects load-stream elements into
  `IUveScalars` before calling the executor; Issue stalls when a required load stream has no buffered element;
  `ExecuteResult.StreamConfig` carries `ss.ld.w` descriptors for `StreamingEngine.Configure`.

- **`CprTrain`** — ROB-free out-of-order pipeline implementing **Checkpoint Processing and Recovery** (Akkary,
  Rajwar & Srinivasan, MICRO 2003) with optional **Continual Flow Pipelines** (Srinivasan, Rajwar, Akkary, Gandhi &
  Upton, ASPLOS 2004) via `enableCfp: true`. Instead of a reorder buffer, a small FIFO of rename-map checkpoints
  (`CheckpointList`, `checkpointCount`, default 8) is created selectively at **low-confidence branches** (JRS
  estimator, `CheckpointConfidencePredictor`: 4-bit counters indexed by PC⊕history, reset to zero on a mispredict,
  high-confidence only at saturation), plus forced checkpoints every `checkpointMaxInstructions` (counter-overflow
  prevention), at serializing instructions (CSR/fence/atomic/halt, which then issue only when architecturally
  oldest), and at the first branch after a recovery (forward-progress rule). A misprediction restores the
  containing checkpoint's RAT snapshot in one shot — no per-instruction walk-back — re-executing the instructions
  between the checkpoint and the branch (COVHD, counted by `covhd_squashed`) with the recorded branch outcome
  **replayed by distance** so the same branch cannot mispredict twice. Instructions retire in **bulk**: a whole
  checkpoint commits at once when its completion counter fills, bounded only by the one-store-per-cycle D-cache
  write port (a completed prefix ending in a trap/halt commits early — same architectural outcome as the paper's
  recover-and-force-checkpoint dance, slightly less re-execution). **Aggressive register reclamation** (after
  Moudgill et al., MICRO 1993) frees a physical register the moment its use counter (renamed readers + holding
  checkpoints) hits zero, its architectural register has been renamed again, and no write can still arrive —
  decoupled from retirement entirely, which is what lets a small PRF sustain a large window. Stores live in a
  two-tier **hierarchical store queue** (`HierarchicalStoreQueue`: the youngest `l1SqCapacity` entries are the fast
  tier, everything older is the slow tier) with a non-tagged direct-mapped **membership test buffer** whose
  per-block counters give a fast "definitely no matching store"; forwarding sourced from the L2 tier — or an MTB
  false positive — costs `l2SqForwardPenalty` cycles. Forwarding byte-merges every older resolved overlapping store
  over the raw memory value, so partial-overlap cases never deadlock inside a bulk-committing checkpoint. Memory
  disambiguation uses the store-set predictor (as the CFP paper's own CPR baseline does); a load that read stale
  data rolls the pipeline back to its containing checkpoint. With **CFP enabled**, a load whose miss penalty
  reaches `cfpMissThresholdCycles` becomes a slice head: its destination is tagged **NAV** (not-a-value), and every
  instruction whose sources are all ready-or-NAV drains out of the scheduler into the **Slice Data Buffer**
  (`SliceDataBuffer`, `sdbCapacity`) carrying its completed source *values* and physical-register *names* — so both
  the completed source registers and the slice destinations are released while the miss is outstanding, and
  miss-independent work keeps flowing (the continual-flow property; nothing is re-executed, unlike runahead). NAV
  propagates through memory via store-set-predicted NAV stores. When the miss returns, the slice re-enters through
  **back-end (physical→physical) renaming**: a slice destination still mapped by the RAT (the rename-filter
  live-out check) keeps its original register so front-end consumers wake normally; everything else acquires a
  fresh register from a small reserve (`cfpReservedRegs`) the front end may not touch, with the front end pausing
  while a slice actively drains. Chained dependent misses simply re-drain. Vector/UVE instructions are rejected at
  rename (v1 is scalar-only); the per-checkpoint entry list with captured result values is simulator bookkeeping
  the hardware wouldn't need — under aggressive reclamation the PRF slot may be legally reused before its
  checkpoint retires, so bulk commit syncs the separate `IArchState` mirror from captured values instead. Dials:
  `checkpoints_created/retired`, `recoveries`, `covhd_squashed`, `cfp_slice_instructions`, `cfp_reinsertions`, plus
  the full TMA (`td_*`) and CPI-stack (`cpi_*`) sets described in the analysis sections. Three liveness rules keep
  bulk commit deadlock-free on real workloads: a checkpoint never holds more loads (stores) than the LQ (HSQ)
  capacity, never accepts appends behind a started commit cursor (checkpoint opening also precedes the
  free-register stall, so a fully-committed lone checkpoint can always gain the successor it needs to retire and
  release its snapshot's register references), and a serializing instruction sits alone in its epoch (a fence
  sharing a checkpoint with a younger load would deadlock: the load's issue gates on the fence committing, which
  requires the load to complete). Store sets should stay enabled: violation recovery re-executes the whole
  checkpoint, so without memory-dependence learning the same load can re-violate forever. Sweeps select the train
  with `"pipeline": "cpr"` (shared OoO knobs: width, IQ, physical registers, predictor, caches, FU latencies).

The five-stage pipeline timing: an instruction is fetched at cycle T, decoded at T+1, executed at T+2, accesses memory
at T+3, and writes back at T+4. Writeback is scheduled at `Phase.Writeback` (6) before Decode runs at `Phase.Commit` (
7), so a register written this cycle is visible to a dependent instruction reading the register file in the same cycle.

ISA mutations (register writes, vector state, CSRs, trap returns) are delivered to the Train through a
`SideEffect Action<IArchState>` closure on `ExecuteResult`, keeping the trains free of any ISA-specific fields.

**ISA coverage:** I/M/A/F/D (standard), C (compressed 16-bit instructions), Zfh (half-precision FP: full
arithmetic/compare/classify/FMA/conversion set, NaN-boxed with the upper 48 bits of the unified register set to 1),
Zba (address generation: sh1add/sh2add/sh3add), Zbb (basic bit manipulation: andn/orn/xnor, clz/ctz/cpop,
min/minu/max/maxu, rol/ror/rori, sext.b/sext.h/zext.h, orc.b/rev8), Zbc (carry-less multiply: clmul/clmulh/clmulr),
Zbs (single-bit: bclr/bext/binv/bset and immediate forms), Zicond (czero.eqz/czero.nez), Zawrs (wrs.nto/wrs.sto — NOP in
single-core), Zicbom (cbo.inval/clean/flush — NOP), Zicboz (cbo.zero — zeros 64-byte cache-line-aligned block), Zicbop (
prefetch.i/r/w — NOP via ORI path), Zifencei (fence.i — NOP; I-cache invalidation on self-modifying code is not
modeled), Zimop (mop.r.N/mop.rr.N — return 0), Zcmop (c.mop.N, N odd 1–15 — compressed NOP hints, reserved C.LUI nzimm=0
encoding space), Zicntr (cycle/cycleh/time/timeh/instret/instreth user-level counter shadows;
mcycle/mcycleh/minstret/minstreth M-mode counters; pipeline trains drive IArchState.OnCycle()/OnRetire() hooks), V (
vector, VLEN=128, V1.0 fully implemented), and UVE (Unlimited Vector Extension, scalar subset; target spec is UVE2 —
Fernandes, U. Coimbra 2025 — with the AnaBSF/riscv-isa-sim uve branch as reference implementation). The UVE scalar
subset (encoded in RISC-V custom-0/custom-1 opcode space) covers: stream setup (`ss.sta.ld.w`, `ss.sta.st.w`, `ss.app`,
`ss.end`, `ss.cfg.vec` — dimensions configured outermost-first, Spike deque order, with `ss.end` adding the innermost;
modifier and vec-dim indices are likewise outermost-first, remapped to the engine's innermost-first order at `ss.end`);
stream modifiers with UVE2 semantics — `ss.app.mod` (static) and `ss.app.ind` (indirect) attach to the most recently
configured dimension as trigger and carry an explicit target dimension (`tdim`); the target's fields reset to configured
values when the trigger dimension itself wraps; Offset displacements and indirect offset values are element-scaled;
scalar broadcast (`so.v.dp.w`); element-wise FP arithmetic (`so.a.mul.fp`, `so.a.add.fp`, `so.a.sub.fp`, `so.a.mac.fp`);
stream-loop branches (`so.b.nc`, `so.b.ndc.D`); SO_P predicate register file (16 registers, VLEN/8=16 bytes each, reg 0
all-ones; `so.p.{zero,one,vr,not,mv,mvt}` simple manipulation ops with governing predicate and zeroing mode;
`so.p.{ge,eq,lt}.{us,fp,sg}` element-wise comparisons and `_z` zeroing-mode variants); `so.v.mv` / `so.v.mvt`
predicate-gated vector register move/transpose. `StreamDescriptor` holds N-dim `StreamDimension[]`; `StreamingEngine`
tracks per-dim consume-side pass-complete flags for `so.b.ndc.D` loop control. Stream-loop dimension branches (
`so.b.ndc.D` / `so.b.dc.D`) count dimensions from the outermost with funct3 = D−1 (Spike EODTable convention); the
pipeline remaps to the engine's innermost-first index. `UveStoreStream` supports N-dimensional layouts with per-dim
index carry, matching `StreamState` for load streams. Configure-once kernels are exercised end-to-end through the OoO
pipeline: 3D-stream GEMM with stride-0 repeat dimensions (`GemmTests`), lower-triangular sum via a single `ss.app.mod`
Size modifier (`TriangularSumModifierTests`), and sparse·dense dot product via `ss.sta.ld.w_inds` + `ss.app.ind`
indirect gather (`SparseDotProductTests`), alongside SAXPY and per-row-reconfiguring triangular/trisolv variants.

Scalar cryptography (RISC-V Cryptography Extensions Volume I: Scalar & Entropy Source Instructions, v1.0.1): Zknd
(NIST AES decryption: RV32 `aes32dsi`/`aes32dsmi`, RV64 `aes64ds`/`aes64dsm`/`aes64im`/`aes64ks1i`/`aes64ks2`), Zkne
(NIST AES encryption: RV32 `aes32esi`/`aes32esmi`, RV64 `aes64es`/`aes64esm`, sharing `aes64ks1i`/`aes64ks2` with
Zknd), Zknh (NIST SHA2: `sha256sig0`/`sha256sig1`/`sha256sum0`/`sha256sum1` common to both widths, plus the RV32
split-register SHA2-512 forms `sha512sig0h`/`sha512sig0l`/`sha512sig1h`/`sha512sig1l`/`sha512sum0r`/`sha512sum1r` and
their RV64 direct-64-bit counterparts `sha512sig0`/`sha512sig1`/`sha512sum0`/`sha512sum1`), Zksed (ShangMi SM4:
`sm4ed`/`sm4ks`, common to both widths), Zksh (ShangMi SM3: `sm3p0`/`sm3p1`, common to both widths), and Zkr (the
`seed` entropy-source CSR at 0x015, modeled as a virtual entropy source per §4.2.3 — every poll succeeds with fresh
deterministic pseudorandomness, default M-mode-only access). RV32 and RV64 use disjoint AES/SHA2-512 encodings by
design (`Rv64Decoder` explicitly rejects the RV32-only forms); the width-shared instructions (SHA2-256, SM3, SM4)
sign-extend their 32-bit result to XLEN on RV64 via a `(int)`-cast in the shared RV32 executor code, which RV64
inherits unmodified.

The same spec volume's bitmanip-for-crypto subset is also implemented: Zbkb (`pack`/`packh`, RV64-only `packw`,
`brev8` per-byte bit reversal, and the RV32-only `zip`/`unzip` bit-interleave pair), Zbkx (`xperm4`/`xperm8`
crossbar nibble/byte permutation), and Zbkc (fully satisfied by the pre-existing Zbc `clmul`/`clmulh` — no new
code needed). `pack`/`brev8`/`xperm4`/`xperm8` are width-dependent (half-width/byte-count/element-count doubles
under RV64's `Rv64Executor` override); `pack rd, rs1, x0` in particular needed an explicit RV64 decoder
interception, since RV32's decoder special-cases `rs2=0` as the narrower Zbb `zext.h`.

Vector cryptography (RISC-V Cryptography Extensions Volume II: Vector Instructions, v1.0.0): the full Zvkned
AES block-cipher extension — encrypt (`vaesem.vv/.vs`, `vaesef.vv/.vs`), decrypt (`vaesdm.vv/.vs`,
`vaesdf.vv/.vs`), round-zero (`vaesz.vs`), and forward key schedule (`vaeskf1.vi` AES-128, `vaeskf2.vi`
AES-256) — operating on 128-bit "element groups" (EGS=4 consecutive 32-bit elements forming one AES block)
rather than single SEW-wide elements. This introduced the general element-group architecture that any future
vector-crypto instruction can build on: `ITooth.HasRuntimeSizedVectorDestination` flags instructions whose
destination register *count* depends on runtime `vtype`/LMUL rather than the encoding. `OooTrain` needs no
changes at all — head-serializing every `ToothClass.Vector` op is already sufficient regardless of how many
registers get written. `FiveStageTrain` widens its vector RAW hazard check with a runtime LMUL-derived
register span instead: `ITooth.RuntimeVectorRegisterSpan(baseRegister, state)` gives an in-flight producer's
precise span (its own LMUL is always already resolved by hazard-check time), while
`ITooth.MaxRuntimeVectorRegisterSpan(baseRegister)` gives a state-independent conservative maximum for the
not-yet-decoded consumer side, whose LMUL could still change from a `vsetvli` sitting in a pipeline latch
this very cycle. The element-group architecture also added constraint checking for the spec's §1.5 rules
(LMUL·VLEN≥EGW, SEW matching, `vl`/`vstart` multiples of EGS), and a register-group-range reserved-encoding
check for the `.vs` forms (vd's LMUL group must not overlap the scalar vs2 register). Vector-crypto
instructions use a dedicated major opcode (`0x77`), not the
standard OP-V opcode (`0x57`) despite an otherwise identical field layout — caught via a
`riscv64-none-elf-as`/objdump round-trip and confirmed against the `riscv-opcodes` project, after the
spec-text extraction assumed 0x57. `vaesdm.vv`'s round-key XOR lands *before* InvMixColumns — unlike every
other round op, where the XOR is the final step — matching the spec pseudocode and FIPS-197's Equivalent
Inverse Cipher (§5.3.5) construction. Validated against the FIPS-197 Appendix A.1 key-expansion and Appendix B
cipher-example traces.

The full Zvksed extension (SM4 block cipher) reuses this same element-group infrastructure unchanged: round
function `vsm4r.vv/.vs` and key expansion `vsm4k.vi`, validated against GB/T 32907-2016 Example 1
(key==plaintext, via `draft-ribose-cfrg-sm4`). SM4's own final word-order-reversing "R transformation" is not
part of `vsm4r`/`vsm4k` themselves — applied by the caller (the test, here), matching the spec. Surfaced a
real bug in the shared `ElementGroupGetWord`/`ElementGroupSetWord` word-packing helpers: they originally
packed bytes LSB-first, which is transparent to AES's key schedule (only ever rotates by a whole byte) but
silently wrong for SM4's non-byte-aligned rotations (2/10/13/18/23 bits); fixed to natural big-endian packing,
with AES's key-schedule instructions updated to match (rotate direction flipped, the shared `AesRcon` table
shifted into the high byte at the point of use, without touching the table itself since it's shared with the
scalar `aes64ks1i` instruction).

The Zvknha/Zvknhb extension (SHA-2 compression + message schedule) implements the Zvknhb superset
unconditionally: `vsha2ch.vv`/`vsha2cl.vv` (two rounds of compression) and `vsha2ms.vv` (four rounds of
message-schedule expansion), accepting both SEW=32 (SHA-256) and SEW=64 (SHA-512) rather than gating the
64-bit path behind a separate hart-extension flag. Unlike every other Zvk* op, EGW=4·SEW is itself
runtime-dependent (128 for SHA-256, 256 for SHA-512 — the latter needing 2 physical registers per element
group, which the existing `ReadElementGroup`/`WriteElementGroup` helpers already handled correctly since they
took `egwBits` as a runtime parameter from the start), and `vs1` is a genuine third vector source register
rather than a sub-op selector or immediate. The element-index-to-named-variable mapping (which element holds
`a`, `b`, `e`, `f`, etc.) was confirmed against the RISC-V Sail reference model
(`github.com/riscv/sail-riscv`, `model/extensions/vector_crypto/zvknhab_insts.sail`) rather than derived from
the spec's prose concatenation notation alone, which is genuinely ambiguous without seeing how `get_velem`
actually indexes elements. Validated end-to-end — multi-block, chaining `vsha2ms` with `vsha2ch`/`vsha2cl`
through a complete SHA-256/SHA-512 hash — against `System.Security.Cryptography.SHA256`/`SHA512`, a real
independently-implemented oracle, since FIPS 180-4's on-disk text has no worked example with intermediate
round values to hand-transcribe (same situation as FIPS-197's Appendix C).

The Zvksh extension (SM3 secure hash) adds `vsm3c.vi` (two rounds of compression) and `vsm3me.vv` (eight
rounds of message-schedule expansion), with a fixed EGW=256/EGS=8/SEW=32 shape — a new element-group size
(8, not 4) distinct from every earlier Zvk* op. Validated against the full GB/T 32905-2016 Example 1 and
Example 2 round-by-round traces (via IETF draft-sca-cfrg-sm3, since no built-in .NET SM3 oracle exists like
SHA-2 had): both single-instruction checks in isolation (localizing the round math and element ordering
independently) and complete multi-block end-to-end digests, the second of which crosses a block boundary to
exercise SM3's feed-forward finalization (`V_(i+1) = CF(V_i, B_i) xor V_i`, an XOR rather than SHA-2's modular
addition). Element ordering was confirmed against the RISC-V Sail reference model rather than the spec's own
prose tables, which use the opposite left-to-right listing convention from every other instruction in the
same document. `vsm3c.vi`'s round function is implemented in the straightforward plain (A,B,C,D,E,F,G,H)
order rather than porting the Sail source's own shuffled return-vector shape verbatim — both are
output-equivalent (confirmed byte-exact against the traces), and the plain form avoids depending on a Sail
vector-literal indexing detail this port doesn't otherwise need to resolve.

The Zvkg extension (vector GCM/GMAC) adds `vghsh.vv` (one GHASH add-multiply iteration,
Yi+1 = (Yi ^ Xi) * H over GF(2^128)) and `vgmul.vv` (one GHASH multiply, Y * H — sharing
`vaesem.vv`/`vsm4r.vv`'s funct6, disambiguated by a hardcoded vs1=0x11 rather than a real register
operand), reusing the EGW=128/EGS=4/SEW=32 shape of Zvkned/Zvksed. Unlike every other Zvk* op, the
whole 128-bit element group is one GF(2^128) polynomial with no sub-word decomposition, and neither
op has a register-overlap reserved encoding (confirmed against the Sail encdec guard, which calls no
`zvk_valid_reg_overlap` for either instruction — the first Zvk* ops without one). Validated against
the McGrew-Viega GCM specification's Test Case 4, which — unlike NIST SP 800-38D — publishes the raw
intermediate `GHASH(H, A, C)` value directly, letting `vghsh.vv` be chained across real nonzero
AAD/ciphertext/length blocks and checked byte-exact without needing a full AES-CTR encryption
harness; `vgmul.vv` cross-checked against `vghsh.vv` called with an all-zero vs1 (X=0).

The Zvbb/Zvbc/Zvkb extensions (vector basic bit-manipulation / carryless multiply) add `vandn`,
`vrol`/`vror` (`.vv`/`.vx`, plus `.vi` for `vror`), `vwsll` (`.vv`/`.vx`/`.vi`), the VXUNARY0 unary
group `vbrev8.v`/`vrev8.v`/`vbrev.v`/`vclz.v`/`vctz.v`/`vcpop.v`, and `vclmul`/`vclmulh`
(`.vv`/`.vx`). Unlike every other Zvk* family, these live on the standard OP-V opcode (0x57) and
operate per-element (EEW=SEW) exactly like the base V-extension integer ALU/multiply ops, rather
than the dedicated crypto opcode (0x77) with element-group (EGW/EGS/`get_velem`) semantics — so
they extend the existing `VIntOp`/`VWideOp` shapes instead of the crypto element-group
infrastructure. Zvkb is a proper subset of Zvbb (`vandn`, `vbrev8`, `vrev8`, `vrol`, `vror`) with
no encodings of its own, per the spec text, so implementing Zvbb covers it with no additional
code. `vror.vi`'s encoding steals bit 26 (normally the funct6 LSB) as immediate bit 5 — the top 5
bits (31:27) select the opcode, and the stolen bit concatenates with the 5-bit rs1 field to form a
6-bit rotate amount (0-63, needed since SEW can be 64) — handled by intercepting the raw bit
pattern before the generic funct6-based dispatch runs, since the generically-computed funct6
would otherwise fold to the same value as `vrol.vv`/`vx`'s real funct6. Adding SEW=64 support
(required by `vrol`/`vror`/`vclz`/etc.) uncovered two latent bugs: `ApplyVIntOp`'s bit-mask
computation silently produced 0 at SEW=64 (C#'s ulong-shift-count-mod-64 rule turns `1UL << 64`
into `1UL << 0`), invisible until now since every pre-existing `VIntOp` member is
carry-safe/low-bit-independent; and `ReadVElement`'s ewBytes==4 path can sign-extend a
high-bit-set byte through an `int` cast, invisible to arithmetic ops but corrupting the new
bit-magnitude-sensitive ops, fixed with a defensive re-mask at the call site rather than touching
the shared read helper. `vclmul`/`vclmulh` were validated against hand-derived GF(2)[x]
polynomial identities rather than the implementation's own loop. This closes out the entire
RISC-V Vector Cryptography Extensions Volume II instruction set.

**Privilege model:** Three privilege levels (User=0, Supervisor=1, Machine=3). Trap delegation: when an exception's
`medeleg` bit is set and the hart is below Machine privilege, `RaiseTrap` enters S-mode (writes `sepc`/`scause`/`stval`,
updates `sstatus` SPP/SPIE/SIE, sets privilege to Supervisor, returns `stvec` base); otherwise the existing M-mode path
applies. Interrupt causes (bit 31 set in mcause/scause) are delegated via `mideleg` rather than `medeleg`. `MRET` is
guarded to Machine mode; `SRET` requires at least Supervisor. `ECALL` emits the correct cause code for the current
privilege level (8=U, 9=S, 11=M). CSR accesses from an insufficient privilege level or writes to read-only CSRs raise
`IllegalInstruction`.

**Virtual memory (Sv32):** The `satp` CSR (0x180, Supervisor-mode) controls address translation. When `satp.MODE=1`,
both instruction fetch and data loads/stores go through a two-level Sv32 page table walk (`Sv32Walker`). `MODE=0` (bare)
uses virtual address = physical address and preserves the existing behaviour for all benchmark workloads. A/D bits are
enforced using a fault-on-access model: `A=0` or (`D=0` on a store) raises the corresponding page fault (
`LoadPageFault`/`StorePageFault`). Supervisor User Memory (SUM) is not implemented; S-mode always faults on user pages (
PTE.U=1). Instruction fetch translation crosses the ISA isolation boundary via the `IFetchTranslator` interface in
src/Core/Mechanism/: `RvFetchTranslator` (in src/Isa/RiscV32/) calls `Sv32Walker` with `isExec=true`, returning
`(physAddr, 0)` on success or `(0, 12)` for `InstructionPageFault`; M-mode fetches bypass the walk entirely (RISC-V priv
spec §3.1.6). All four pipeline trains wire up the translator and propagate the pre-baked `TrapInfo` through the
pipeline latch chain to Writeback, where it is raised via the normal trap path.

**Interrupt dispatch:** `ITrapController.PeekInterrupt(IArchState)` returns the highest-priority pending interrupt (per
the RISC-V §3.1.9 priority order: MEI > MSI > MTI > SEI > SSI > STI) when `mip & mie` has a pending bit and the global
interrupt enable for the current privilege and delegation state permits delivery. All four pipeline trains call
`PeekInterrupt` at each retire/commit boundary and invoke `RaiseTrap` when a non-null result is returned. `mideleg`
routes delegated interrupts to S-mode.

### Benchmark workloads (TestBinaries/benchmarks)

Thirteen bare-metal RISC-V benchmarks compiled from the riscv-tests suite: `dhrystone`, `gcd`, `median`, `memcpy`, `mm`,
`multiply`, `pchase`, `qsort`, `rsort`, `spmv`, `towers`, `treesum`, and `vvadd` (`dhrystone` self-times via `mcycle`;
`mm` and `spmv` use double-precision FP — all three became buildable once those features landed), plus **CoreMark** (
EEMBC, vendored under `TestBinaries/coremark/` with a bare-metal port in `coremark/port/`: mcycle timing, HTIF console
output, and the self-validation verdict surfaced as the exit code so the test gate catches CRC failures). The committed
`coremark.elf` runs 14 iterations (~4.4M retired instructions, ~10× the next-largest benchmark); rebuild with
`make benchmarks/coremark.elf COREMARK_ITERATIONS=n` for heavier runs. Also included are all 19 **Embench-IoT**
benchmarks (Patterson et al., the modern IoT benchmark suite replacing Dhrystone): `aha-mont64`, `crc32`, `depthconv`,
`edn`, `huffbench`, `matmult-int`, `md5sum`, `nettle-aes`, `nettle-sha256`, `nsichneu`, `picojpeg`, `qrduino`,
`sglib-combined`, `slre`, `statemate`, `tarfind`, `ud`, `wikisort`, and `xgboost`. They are built with the same
riscv-tests `crt.S`/`syscalls.c` environment and a bare-metal BSP under `TestBinaries/embench-iot/port/` (no-op
`start_trigger`/`stop_trigger`, inline `memcmp`/`strchr`/`memmove`/`sqrt`, and a bare-metal `ctype.h`). The key
compile-time requirement is `-fno-tree-loop-distribute-patterns`: without it GCC 15 converts the byte-by-byte fallback
in the syscalls.c `memset` into an infinite recursive call on unaligned inputs. They are built against `bmarks.ld` (same
HTIF exit protocol) and all pass under `BenchmarkTests`. All benchmarks are available as preset workloads in the Face UI
and can be passed to the Runner as ELF arguments. `BenchmarkTests` (`Tests/RiscV32/Analysis/BenchmarkTests.cs`) fans
each binary out across `Parallel.ForEach` rather than relying on xUnit's per-class serialization, since every run builds
its own memory image and train with no shared state. Note: benchmark tests are still the heaviest test set — run them
selectively with `--filter`.

ISA conformance tests (`TestBinaries/isa/`) are also linked at `0x80000000`. `FlatMemory` accepts an optional
`baseAddress` constructor parameter so the backing byte array starts at the first PT_LOAD segment (e.g. `0x80000000`)
rather than at address 0, avoiding a 2 GB allocation. `Rv32ElfWorkload.BaseAddress` exposes this value;
`IWorkload.BaseAddress` defaults to 0 for zero-based images.

### Full-system booting (src/Isa/RiscV32/Memory, src/Isa/RiscV64/Memory, src/Core/Orrery/Devices)

Two full-system boot milestones are verified by tests in `Tests/RiscV32/` and their RV64 counterparts in
`Tests/RiscV64/`:

- **OpenSBI v1.8** (`SingleCycle_OpenSBI_PrintsBanner`): `fw_jump.bin` (generic platform) boots on a `SingleCycleTrain`
  and prints its version banner on the ns16550a UART. RV32 built via `nix build .#opensbi-rv32`; RV64 (PIE
  `fw_jump.elf`, `riscv64-unknown-linux-gnu-` toolchain) via `nix build .#opensbi-rv64`.
- **Linux 6.12 NOMMU** (`SingleCycle_Linux_PrintsBanner`): a `nommu_virt_defconfig + M-mode` kernel loads at
  `0x80000000` (PAGE_OFFSET) and prints `Linux version …` via earlycon on the UART. No OpenSBI — the kernel runs
  entirely in M-mode, so it is launched directly. RV32 (`+ 32-bit.config` fragment) built via `nix build .#linux-rv32`;
  RV64 (`nommu_virt_defconfig` defaults to RV64) via `nix build .#linux-rv64`.

The peripheral bus is a `PeripheralBus` routing three devices: a `ClintDevice` (MTIP/MSIP at 0x02000000), a
`PlicDevice` (external interrupt routing at 0x0C000000), and an `Ns16550aUart` (ns16550a console at 0x10000000; TX
writes flush immediately to a `TextWriter`). `VirtDtb.Bytes` (`src/Isa/RiscV32/Memory/virt.dts`) and its RV64
counterpart `Rv64VirtDtb.Bytes` (`src/Isa/RiscV64/Memory/virt64.dts`, 2-cell addressing, `riscv,isa="rv64imafdc"`,
`mmu-type="riscv,sv39"`) are hand-crafted device tree blobs declaring 128 MiB RAM, all three devices, and one
`virtio_mmio` block device slot. The RV32 and RV64 OpenSBI/Linux `nix build` outputs must use distinct `-o` out-link
names (e.g. `result-opensbi-rv64`, `result-linux-rv64`) — otherwise building one clobbers the other's default `result`
symlink.

### Multi-hart kernel (src/Isa/RiscV32/MultiCore)

`MultiHartKernel` drives N RISC-V harts round-robin against a shared physical memory. Each call to `Step()` advances
every non-halted hart by one instruction and returns the number still active; `Run(maxTicks)` loops until all harts halt
or the tick limit is reached. Each hart has its own `IArchState` (created by `Rv32Mechanism.CreateArchState()`). Halt
detection covers EBREAK (`result.IsHalt`), HTIF tohost (`result.RequestHalt`), and the infinite-self-loop idiom (
`PC == pc && class == Branch`). The kernel operates in physical address space (no fetch translation), making it suited
for bare-metal multi-hart workloads.

Two constructors are available: `MultiHartKernel(IMemory sharedMemory, …)` gives every hart the same `IMemory` (simplest
path, used with `ReservationAwareMemory` for LR/SC); `MultiHartKernel(IMemory[] perHartMemory, …)` gives each hart its
own cache (e.g. a `MoesifCache` backed by a shared `MoesifBus`) — both instruction fetch and data access route through
the per-hart memory.

`Rv32Mechanism` now accepts optional `reservationTable` and `hartId` constructor parameters, forwarded to `Rv32Executor`
for LR/SC routing. Typical setup:

```csharp
var table   = new ReservationTable();
var guarded = new ReservationAwareMemory(flat, table);
var kernel  = new MultiHartKernel(guarded,
    new Rv32Mechanism(reservationTable: table, hartId: 0),
    new Rv32Mechanism(reservationTable: table, hartId: 1));
```

`ReservationTable` tracks per-hart LR/SC reservations. Each hart registers a reservation on `LR.W`; any write from any
hart to the same 4-byte-aligned granule cancels all overlapping reservations so a subsequent `SC.W` fails correctly.
`ReservationAwareMemory` is a thin `IMemory` wrapper whose `Write()` calls `table.InvalidateAt()` before the actual
write, ensuring cancellation fires on every store. Single-hart setups leave `ReservationTable` null and use the existing
private `_reservation` field unchanged — no API or behaviour change for existing code.

**`clone()` and dynamic hart activation.** `MultiHartKernel`'s `activeHartCount` constructor parameter pre-allocates every
hart's `IMechanism`/`IArchState` up front but only starts the first `activeHartCount` of them running — the rest sit
*dormant* (skipped by `Step()`) until `SpawnHart` activates one. `MultiHartKernel : IHartSpawner`, and
`LinuxSyscallEmulator.Spawner` (settable post-construction, breaking the construction-order cycle between the syscall
handler and the hart driver that needs it) wires `clone()` (syscall 220) to it: the handler snapshots the parent's
`IArchState` (`IArchState.Snapshot()`), overrides `sp`/`tp`/`a0=0` per the real clone() ABI, and calls `SpawnHart` to
activate the next dormant slot. The raw RISC-V syscall ABI is `a0=flags, a1=newsp, a2=ptid, a3=tls, a4=ctid` (confirmed
by compiling and disassembling real musl 1.2.5 `__clone`); `CLONE_SETTLS`/`CLONE_PARENT_SETTID` are honored, and all
harts sharing one `LinuxSyscallEmulator` instance is required (mirrors real `CLONE_FILES`/`CLONE_VM`). Backward-compatible
constructor overloads default `activeHartCount` to every hart starting active, so pre-existing single-shot multi-hart
setups are unaffected. `MultiHartPipeline`'s equivalent dynamic-activation support is not yet implemented — see
`TODO.md`.

**`futex()` blocking.** `LinuxSyscallEmulator` handles syscall 98 (`FUTEX_WAIT`/`FUTEX_WAKE`) by polling rather than a
wait queue: `FUTEX_WAIT` returns `-EAGAIN` immediately if the word at `uaddr` already differs from the expected value;
otherwise it returns an `ExecuteResult` with the new `RequestBlock` flag set. `MultiHartKernel.StepHart` checks
`RequestBlock` right after `Execute()` and returns without advancing PC, applying `SideEffect`, writing a register
result, or calling `OnRetire()` — so a blocked hart's `ecall` is simply re-decoded and re-executed next tick, unchanged,
until the word changes, contributing zero retired instructions or BBV samples while blocked. `FUTEX_WAKE` is a no-op
returning 0; there is no waiter-identity bookkeeping to report a real wake count from. This is sound without a wait
queue because real futex(2) callers must always re-validate the guarded condition themselves after any wait returns
(spurious wakeups are always possible), and glibc/musl's mutex/cond/barrier primitives never branch on `FUTEX_WAIT`'s
specific return value for correctness — so `-EAGAIN` on any mismatch, whether from a real wake-triggered store or any
other write, is both futex(2)-conformant and sufficient for real pthread code.

**Per-hart `gettid`/thread-exit semantics.** `ISyscallHandler.Handle` takes a `hartId` parameter (`Rv32Executor.HartId`,
the same field LR/SC routing already uses), so `gettid`/`set_tid_address` return a real per-hart value (`hartId + 1`,
never 0 — some futex-based lock implementations reserve 0 as a sentinel) instead of a hardcoded constant; `getpid`
stays constant across every hart, matching real Linux (the whole thread-group shares one pid). `SYS_exit` (this hart
only) and `SYS_exit_group` (every hart) are distinguished via a new `ExecuteResult.RequestHaltAll` flag, checked by
`MultiHartKernel.Step` alongside `RequestHalt`. `clone()`'s returned tid (`newHartId + 1`, from its `SpawnHart` slot
index) uses the same convention as `gettid()` (from `Rv32Executor.HartId`) but the two are computed independently —
they agree only if the caller constructs the mechanism occupying a given dormant slot with a matching `hartId`;
neither `LinuxSyscallEmulator` nor `MultiHartKernel` enforces this invariant, so any driver spawning harts into
pre-allocated slots must keep the two in sync itself.

**`CLONE_CHILD_CLEARTID` and the real pthread integration test.** `clone()` records `ctid` per hart (when the
`CLONE_CHILD_CLEARTID` flag bit is set) and `LinuxSyscallEmulator` writes 0 to that address on that hart's own
`SYS_exit`/`SYS_exit_group` — no explicit wake call is needed beyond the write, since a blocked poll-based `futex()`
waiter (see above) notices the value changed on its own next recheck. `Tests/RiscV64/System/PthreadProbeTests.cs` boots
a real, statically-linked musl `pthread_create`/`pthread_join` binary (`TestBinaries/pthread_probe.c`) through
`MultiHartKernel` end to end — the decisive integration proof for the `clone()`/`futex()`/`gettid` prerequisite chain.
Disassembling that exact binary is what surfaced the need for `CLONE_CHILD_CLEARTID` in the first place: real musl
passes `&__thread_list_lock` (a global lock, not the exiting thread's own tid word) as `ctid`, relying on the kernel as
a backstop to release that lock on exit, since `__pthread_exit` does not reliably call `__tl_unlock` along every exit
path. No `flake.nix` change was needed for the pthread/OpenMP toolchain — `pkgsCross.riscv64-musl`'s GCC already ships
`libgomp` and links `-pthread`/`-fopenmp` static binaries cleanly.

### Cache timing model (src/Core/Orrery/Cache)

`SetAssociativeCache` (write-through, no-write-allocate by default; write-back/write-allocate optional) models several
timing effects beyond a flat hit/miss latency, all configured on `CacheLevelSpec`/`MemoryConfig` and independently
combinable except where noted:

- **MSHRs with hit-under-miss** (`mshrCount`): outstanding misses are tracked per line in a small table instead of
  blocking the cache; a second access to a line already in flight merges into the existing entry and pays only the
  remaining countdown (`MshrMerges`), while unrelated addresses continue to hit normally. A miss that arrives when every
  MSHR slot is occupied pays the minimum remaining countdown plus the full `missLatency` (`MshrCapacityStalls`). Call
  `TickMshr()` once per cycle to advance the countdowns — L2/L3 levels are non-blocking the same way L1 is.
- **Sequential tag/data access** (`accessMode: CacheAccessModeKind.Sequential`): `HitLatency` becomes
  `tagLatency + dataLatency` (probe tags first, read only the matching way) instead of the default `Parallel` mode's
  `max(tagLatency, dataLatency)` — typical of large lower-level caches.
- **Inclusion policy** (`inclusionPolicy`, via `AttachInner`): `Inclusive` (Intel-style — a lower-level eviction
  back-invalidates the line in attached upper levels), `Exclusive` (AMD-style — the lower level acts as a victim cache,
  receiving evicted lines from the level above and reclaiming them on a re-fill so the same line never lives in both),
  or `Nine` (non-inclusive non-exclusive, the default — no cross-level invalidation). No effect until an inner cache is
  attached.
- **Critical-word-first / early restart** (`criticalWordLatency`): a fresh demand miss charges only this latency to the
  requester (the demanded word is assumed to arrive first off the bus) while the MSHR entry keeps counting down the full
  `missLatency` in the background, so a different access hitting the same in-flight line before it fully arrives still
  pays the remaining full-line latency. Requires `mshrCount > 0` and cannot exceed `missLatency`.
- **Banked caches and port limits** (`bankCount`, `readPorts`, `writePorts`): the line address selects one of
  `bankCount` independent banks; `readPorts`/`writePorts` cap concurrent accesses per bank per cycle (0 = unlimited),
  charging a 1-cycle structural-hazard stall to an access that finds its bank already at capacity. Call `TickPorts()`
  once per cycle to reset per-bank usage — this is what bounds the OoO train's D-cache bandwidth when configured.
- **Per-sector dirty/valid bits** (`sectorBytes`): splits each line into `blockSizeBytes / sectorBytes` independently
  valid (and, for write-back, independently dirty) sectors. A fresh miss fetches only the sector covering the triggering
  address; a later access to a different, still-untouched sector on an otherwise-resident line pays `missLatency` again
  for that sector alone; evictions write back only dirty sectors instead of the whole line. Not currently combinable
  with `wbCapacity > 0`.
- **Zicbom cache-maintenance semantics** (`CleanLine`/`FlushLine`/`InvalidateLine` on `IMemory`, wired through `Tlb` and
  `MoesifCache` as well): `cbo.clean` writes back a dirty line and keeps it resident; `cbo.flush` writes back and then
  invalidates; `cbo.inval` discards any dirty data without writing it back, then invalidates. All three reuse the same
  dirty-flush path as ordinary eviction, so they honor sectoring automatically and charge no pipeline stall.
- **Victim cache** (`victimCacheEntries`, `victimCacheHitLatency`; Jouppi, ISCA 1990): a small fully-associative FIFO
  buffer beside the main array that captures a line evicted by set conflict instead of flushing/discarding it — capture
  is free (no stall, no writeback even if dirty). A later access that hits in the buffer performs a full swap (not an "
  extra way"): the line installs into the main array via the normal replacement policy, and whatever it displaces takes
  the vacated buffer slot, so both levels stay populated with genuine working-set data. A hit charges
  `victimCacheHitLatency` instead of the full miss latency, allocates no MSHR, and counts as a `Hit`, not a `Miss` (so
  prefetcher training isn't corrupted). A line that eventually leaves the buffer for good — FIFO overflow while dirty,
  or an explicit Zicbom clean/flush — is billed exactly like an ordinary write-back-mode eviction. Not currently
  combinable with `sectorBytes > 0`. Distinct from the unrelated `InsertVictim`/`VictimInserts` (the pre-existing
  Exclusive-inclusion-policy hand-off) and `IReplacementPolicy.ChooseVictim` (generic eviction-way selection) — a level
  can have both a local Jouppi buffer and be the Exclusive receiver for the level above it.

### Cache replacement policies (src/Core/Orrery/Cache)

`SetAssociativeCache` supports a pluggable replacement policy via `IReplacementPolicy` and the `ReplacementPolicyKind`
enum: **LRU** (default), **MRU** (`Mru`): inverse of LRU — a hit promotes the way to age 0 (next eviction candidate);
new installs are placed at the LRU position so they survive until first use; useful for sequential-scan workloads where
the just-accessed block is unlikely to be reused soon, **CLOCK** (`Clock`): one reference bit per way and a circular
hand per set; on eviction the hand sweeps forward clearing bits=1 (second chance) until it finds a bit=0 victim; on hit
or install the bit is set to 1; O(1) amortized victim search, hardware-cheap LRU approximation common in OS page
replacement, **FIFO** (circular-pointer eviction, ignores hits — ordering baseline), **Random** (uniform random victim,
deterministically seeded), **Tree-PLRU** (`Plru`): binary tree of `ways−1` bits per set; on every access the bits on the
root-to-leaf path are pointed away from the accessed subtree; victim selection follows bits root-to-leaf; exact LRU for
2-way, hardware-friendly approximation for wider associativity (Intel P6 and later), **SRRIP-HP** (scan-resistant;
inserts at RRPV 2^M−2, promotes hits to 0), **BRRIP-HP** (thrash-resistant; inserts at distant RRPV 2^M−1 with
probability 1−ε, long with probability ε=1/32), **DRRIP-HP** (scan- and thrash-resistant; uses Set Dueling — 32-set
SDMs, 10-bit PSEL — to dynamically choose between SRRIP and BRRIP per set) — Jaleel et al., ISCA 2010; and **SHiP-Mem
** (`Ship`) and **SHiP-PC** (`ShipPc`): layer a 16K×3-bit Signature History Counter Table (SHCT) on top of SRRIP-HP —
inserts at RRPV=3 (distant) when SHCT[sig]=0, RRPV=2 (long) when SHCT[sig]>0; increments SHCT on every hit; decrements
on eviction without reuse. SHiP-Mem indexes the SHCT by the upper address bits of the miss address; SHiP-PC indexes by
the load PC via `IMemory.SetRequestPc(pc)`, which all pipeline trains call before every execute. Prefetches always use
address-based signatures since no PC is available at prefetch time — Wu et al., MICRO 2011; and **Hawkeye** (`Hawkeye`):
reconstructs Belady's optimal replacement decisions for the observed access stream using OPTgen (a circular occupancy
vector of length 8×ways per set), trains a PC-indexed 8K×3-bit saturating-counter predictor, and uses the predictor's
label at each install — cache-friendly (counter ≥ 4) inserts at RRPV=0, cache-averse at RRPV=7; demand hits decrement
RRPV toward 0; victim selection is SRRIP-style (scan for RRPV=7, age all lines if none found) — Jain &amp; Lin, ISCA

2016. The `ReplacementPolicy` field on `MemoryConfig` (and `CacheReplacementPolicy` string on `TrainConfig`) selects the
      policy for all cache levels.

### Instruction prefetcher (src/Core/Pipeline)

**RDIP** (RAS-Directed Instruction Prefetching — Kolli, Saidi &amp; Wenisch, MICRO 2013): associates I-cache miss
sequences with call-stack signatures derived from the commit-time RAS (4-entry, per the paper's sensitivity study). On
every call or return at commit the prefetcher (1) computes a signature = XOR of the top RAS entries | direction bit (
0=call, 1=return), (2) flushes the Current Signature Misses buffer (up to 16 miss addresses accumulated since the last
signature change) into the Miss Table under the previous signature, and (3) looks up the new signature's Miss Table
entry and issues prefetches. The Miss Table is 1024 sets × 4 ways (LRU); each entry holds up to 3 trigger records, each
a base address + 8-bit block mask covering an 8-block window; new misses are merged into the nearest existing window or
replace the oldest trigger round-robin. RDIP is wired into `FiveStageTrain` and `OooeTrain` (commit-hook calls
`OnCommit`; fetch-hook calls `OnIcacheMiss`); enable with `rdip: true`.

**FDIP** (Fetch Directed Instruction Prefetching — Reinman, Calder &amp; Austin, MICRO 1999): decouples the branch
predictor from instruction fetch via a Fetch Target Queue (FTQ). Each cycle the predictor steps ahead of the fetch PC
using the raw backing memory (bypassing the cache to avoid charging stall latency to the pipeline), filling the FTQ with
predicted cache-line base addresses. A configurable prefetch window (default: entries 1–10, skipping entry 0 which is
too close to benefit) drives `SetAssociativeCache.Prefetch` directly on the L1 I-cache. The FTQ is drained by position (
one head entry per cache-line boundary crossed by fetch), not by address magnitude, so it stays correctly aligned across
backward branches (loops). Mispredictions reset lookahead state to the flush target. The lookahead does not maintain a
RAS, so call/return targets rely on the BTB; mispredicted targets are recovered by the normal flush mechanism.
`FdipPrefetcher` is wired into `FiveStageTrain` (via `FetchStage`) and `OooeTrain` (directly in `StepFetch`/
`StepFlush`); enable with `fdipFtqCapacity: 32` (0 = disabled, default). FDIP is bare-mode only (no virtual-to-physical
lookahead translation).

### Cache prefetchers (src/Core/Orrery/Cache)

`IPrefetcher.OnAccess(pc, address, wasHit, Span<ulong> targets)` writes zero or more prefetch addresses into the
caller-provided span and returns the count; the `OooeTrain` execute stage drives it once per demand load and calls
`MemoryLayers.TryPrefetch` for each result, subject to MSHR capacity. Ten prefetchers are implemented: **NextLine** —
always prefetches the cache line immediately following the access; bandwidth-greedy but effective for sequential
workloads. **Stride / RPT** — Reference Prediction Table (per-PC stride tracking with a 0–3 saturating confidence
counter); issues a prefetch at `address + stride` once the stride is confirmed (confidence ≥ 2). **Stream** — multi-way
sequential stream buffer (Jouppi, ISCA 1990): maintains up to N independent stream buffers in parallel (default 4,
controlled by `PrefetcherTableSize`); on a cache miss that matches no buffer, the LRU buffer is evicted and restarted at
the missed address, issuing `depth` lines at once (default 8, controlled by `PrefetcherDepth`); on each subsequent
sequential access the frontier is advanced by one line to keep exactly `depth` lines pre-loaded; LRU replacement across
buffers; targets sequential and near-sequential patterns including RVV vector loads and UVE streams. **IPCP** — IP
Classifier-based Spatial Prefetcher (Pakalapati & Panda, ISCA 2020): classifies each load PC into one of three classes
and issues spatially-targeted prefetches; **CS** (Constant Stride) tracks per-PC stride with a 2-bit saturating
confidence counter and issues up to 3 prefetches at the confirmed stride; **CPLX** (Complex Stride) maintains a 7-bit
rolling signature of recent strides (`sig = (sig<<1) XOR stride`) indexing a 128-entry CSPT table, issuing up to 3
prefetches when a pattern repeats (confidence ≥ 1); **GS** (Global Stream) tracks 2 KB regions in an 8-entry LRU Region
Stream Table (RST) with a 64-bit access bitvector, classifying a PC as a global-stream if ≥75% of its region's lines
have been touched (dense), then issuing up to 6 prefetches in the stream direction; a tentative GS prefetch fires when
an IP enters a new region and its previous region was dense; no prefetch crosses a page boundary; a 32-entry
recent-request filter suppresses duplicate prefetch requests. Priority: GS > CS > CPLX. **Berti** — accurate local-delta
L1D prefetcher (Navarro-Torres et al., MICRO 2022): for each load IP maintains an 8-set × 16-way FIFO History Table (HT)
of recent (line address, tick) pairs; on a demand miss it searches the IP's HT set for "timely" entries (entries whose
tick satisfies `entry.tick + latency ≤ current_tick`, i.e., a prefetch issued then would have arrived before the miss)
and accumulates the signed line-count deltas to the current miss address into a 16-entry fully-associative Table of
Deltas (ToD); an epoch counter trips at 16 training events and assigns statuses by coverage fraction: >10/16 → L1DPref,
6–10/16 → L2Pref (or L2PrefRepl if <8/16), ≤5/16 → NoPref; at most 12 deltas may be active (L1DPref+L2Pref+L2PrefRepl
combined); warmup mode issues a delta only when counter ≥ 8 and coverage > 80% of the counter value; latency is
approximated by a configurable tick count (default 10) since no MSHR timestamps are available. **Pythia** — online
reinforcement learning prefetcher (Bera et al., MICRO 2021): formulates prefetching as a SARSA RL problem; the agent
observes two program features per demand — PC+Delta (current load PC XOR'd with the current cacheline delta) and the
last-4-deltas rolling hash — and selects one prefetch offset from a 16-entry pruned action list
{−6,−3,−1,0,+1,+3,+4,+5,+10,+11,+12,+16,+22,+23,+30,+32} (lines); Q-values are stored in a hierarchical Q-Value Store (
QVStore): 2 vaults × 3 tile-coded planes × 128 feature-entries × 16 actions; Q(S,A) = max over vaults of the sum of
plane partial Q-values; rewards: RAT=+20 (accurate+timely), RAL=+12 (accurate+late), RCL=−12 (page-crossing), RIN=−8 (
inaccurate), RNP=−4 (no-prefetch); a 256-entry FIFO Evaluation Queue (EQ) defers SARSA updates (α=0.0065, γ=0.556) until
the evicted entry's reward is known; ε=0.002 greedy exploration; per-access overhead is a 16-way Q-value lookup over 2
vaults × 3 planes. **SMS** — Spatial Memory Streaming (Somogyi et al., ISCA 2006): learns spatial access patterns over
fixed 2 KB address regions and prefetches all blocks predicted to be accessed during a region generation; indexed by the
PC and block offset of the trigger (first) access. The Active Generation Table (AGT) is split into a 32-entry
fully-associative filter table (holds single-access generations; entries are discarded on eviction) and a 64-entry
fully-associative accumulation table (promotes from filter on the second distinct block access; accumulates a 64-bit
spatial pattern bitvector); accumulation entries are retired to the Pattern History Table on AGT capacity pressure,
matching the paper's explicit description of capacity-based generation termination. The PHT (16 K entries, 16-way
set-associative, LRU) stores one pattern bitvector per (trigger PC, block offset) hash key; on a trigger access the PHT
is consulted first and matching predicted blocks (excluding the trigger block itself) are immediately emitted as
prefetch targets. **BOP** — Best-Offset prefetcher (Michaud, HPCA 2016; the DPC-2 winner): a degree-one offset
prefetcher — on each eligible access to line X (demand miss or first demand touch of a prefetched line) it prefetches
X + D, never crossing a page boundary. The offset D is re-selected by a scoring tournament that accounts for prefetch
*timeliness*: a 256-entry direct-mapped Recent Requests (RR) table records the base address of each *completed*
prefetch, and learning tests one candidate offset d per eligible access (round-robin over the paper's 52-entry list —
all offsets 1–256 with prime factors ≤ 5, pruned to the page size in lines); if X − d hits in the RR table, a prefetch
with offset d issued back then would have completed in time, so d scores. A phase ends at SCOREMAX (31) or after
ROUNDMAX (100) rounds; the top scorer becomes D, and a winning score ≤ BADSCORE (1) turns prefetching off (learning
continues against demand fills so it can re-enable). The L2 prefetch bit of the paper is tracked internally, and
completion time is approximated by a configurable tick count (default 10), as in Berti. **SPP** — Signature Path
Prefetcher (Kim et al., MICRO 2016): a PC-free lookahead prefetcher that compresses per-page delta history into a
12-bit signature (`sig = (sig << 3) XOR delta`, sign+magnitude deltas) indexing a 512-entry global Pattern Table of
(delta, confidence) predictions shared across all pages. Prediction recursively walks a *signature path*: the
highest-confidence delta extends the signature speculatively (no confirmation), producing a new signature to predict
from, and the walk continues until path confidence `P_d = α·C_d·P_(d−1)` (`P_0 = C_d`) falls below the prefetch
threshold (25%); α is the measured global accuracy (useful / total prefetches, from a 1024-entry direct-mapped
Prefetch Filter that also drops redundant requests), throttling lookahead depth to the current program phase. A
prediction that would cross the 4KB page boundary is not issued but recorded in an 8-entry Global History Register
(signature, confidence, last offset, delta); the first access to an untracked page searches the GHR for an entry
whose predicted landing offset matches, and if found inherits that signature — so complex patterns continue into a
new physical page with no per-page warmup, SPP's signature contribution beyond plain lookahead prefetching. Beats
BOP/Berti/next-line on workloads with complex, non-strided-but-learnable access patterns (coremark: cuts D$ misses
~39% vs. no-prefetch, where next-line/BOP manage ~8% and Berti is a no-op) but can lag simpler prefetchers when
capacity/conflict misses dominate a tiny cache. **PPF** — Perceptron-based Prefetch Filter (Bhatia, Chacon, Teran,
Gratz &amp; Jiménez, ISCA 2019): reimplements the same Signature/Pattern-Table/GHR core as SPP but discards its
confidence-throttling entirely — the lookahead walk runs until the Pattern Table has no more information for the
current signature (or a 64-depth safety cap), regardless of confidence — and instead routes every delta candidate the
de-throttled walk produces through a hashed-perceptron filter that decides admit/reject per candidate. Nine hashed
features (address, cache line, page, PC⊕depth, a 3-PC path hash, PC⊕delta, confidence, page⊕confidence,
signature⊕delta) each index an independent table of signed 5-bit saturating weights (paper's Table 3: 4×4096-entry +
2×2048-entry + 2×1024-entry + 1×128-entry, cross-checked against its 113,280-bit total); the nine partial weights sum
to a single score thresholded against an admit line. Training: a demand hit on an admitted line (tracked via a
1024-entry Prefetch Table) trains its contributing weights toward "useful"; a demand hit on a *rejected* candidate
(tracked via a matching 1024-entry Reject Table) is a false negative and trains toward "should have admitted." The
paper's third trigger — an L2 eviction of a still-unused prefetched line — has no analogue in `IPrefetcher` (no
eviction callback reaches the prefetcher); it is approximated by training a departing, never-marked-useful Prefetch
Table entry toward "should have rejected" when a slot collision evicts it, a documented fidelity limit (table
pressure standing in for real cache-capacity pressure) rather than the paper's literal mechanism. The paper's L2-vs-
LLC fill-level split (τ_hi/τ_lo) collapses into one admit threshold, matching the same simplification already
documented for SPP's own T_F. On coremark PPF cuts D$ misses to roughly a sixth of plain SPP's (188 vs. 1104 misses
at 32KB/8-way, vs. 1806 with no prefetching) — the paper's central claim that de-throttling plus perceptron filtering
beats a throttled lookahead prefetcher outright, not just a marginal gain. **STeMS** — Spatio-Temporal Memory
Streaming (Somogyi, Wenisch, Ailamaki &amp; Falsafi, ISCA 2009): extends SMS with temporal miss-sequence recording so
prefetching can cross region boundaries, which SMS alone cannot do. A trigger (first miss to a region) is recorded in
a Region Miss Order Buffer (RMOB, 128K-entry circular buffer of `(block address, trigger PC, trigger offset, delta)`)
alongside a block-address → most-recent-RMOB-slot map. SMS's AGT/PHT are kept structurally identical (32-entry
filter/64-entry accumulation AGT, 16K-entry 16-way PST) but store an *ordered sequence* of `(offset, delta)` pairs per
generation instead of a bit vector — each block appears once, in first-access order. Every recorded entry's delta is
the count of *other* misses (from any region) interleaved before it since the previous entry of the same sequence
(trigger stream or a region's own spatial stream); reconstruction re-derives absolute positions from these deltas via
one recurrence, `pos[entry] = pos[previous same-sequence entry] + delta + 1`, merging the trigger stream and every
region's spatial stream into a single ordered prediction. On a trigger miss whose address has a prior RMOB
occurrence, this reconstruction runs synchronously and returns the whole predicted sequence at once (bounded by a
256-entry reconstruction window and the caller's target span — replacing the paper's decoupled stream-queue/SVB
throttling the same way SPP/PPF's lookahead walks replace theirs), with ±2-position collision resolution matching the
paper's own (§4.2). Verified directly against the paper's own worked example (Fig. 3/5): training on the observed
order A, A+4, B, A+2, B+6, A−1, C, D, D+1, D+2 and re-triggering A reconstructs the exact original continuation.
Select with `Prefetcher = PrefetcherKind.{NextLine,Stride,Stream,Ipcp,Berti,Pythia,Sms,Bop,Spp,Ppf,Stems}` on
`MemoryConfig`/`CacheLevelSpec`, or `d_prefetcher:
"next_line"/"stride"/"stream"/"ipcp"/"berti"/"pythia"/"sms"/"bop"/"spp"/"ppf"/"stems"` in `TrainConfig` JSON.

### MOESIF cache coherence (src/Core/Orrery/Cache)

`MoesifCache` is an N-way set-associative write-back cache that participates in a MOESIF coherence protocol with
cache-to-cache supply. Unlike `SetAssociativeCache` (write-through, no-write-allocate), `MoesifCache` is write-back and
write-allocate: writes stay in the cache as Modified lines until eviction or a snoop, not every write goes to backing
memory.

`MoesifBus` coordinates snooping between all registered `MoesifCache` instances sharing a physical address space. Three
bus transactions cover the full protocol:

- **BusRead** (read miss): a peer holding the line in M, O, E, or F supplies the block directly to the requester (
  cache-to-cache) instead of the requester filling from backing. A dirty supplier (M/O) keeps the line as **Owned** — no
  writeback to backing occurs; the owner retains writeback responsibility until eviction or invalidation, and later
  readers install plain S. A clean supplier (E/F) downgrades to S and the requester installs **Forward**: exactly one
  sharer of a clean line holds F and keeps answering later read misses cache-to-cache, so memory stays silent; the F
  role migrates to the most recent requester on each supply (Intel MESIF semantics). If only plain S peers hold the
  line (the forwarder was evicted), the requester fills from backing — guaranteed clean in that case — and becomes the
  new forwarder. With no peers at all it fills from backing as E.
- **BusReadForOwnership** (write miss): all peers transition to I, and an M/O/E/F holder forwards the block to the
  requester along with the invalidation — no writeback; the requester installs the line as M, making its copy
  authoritative. Only if no such holder exists does the requester fill from backing (which is guaranteed current in that
  case).
- **BusReadInvalidate** (S/O/F→M upgrade, block-boundary-crossing writes): all peers transition to I; dirty M/O holders
  write back first. No data transfer — the upgrading requester already holds the bytes.

Silent E→M upgrade (write hit on an Exclusive line) requires no bus transaction — the cache takes M without notifying
peers. While a line is Owned, backing memory is stale; every path that removes the Owned copy (eviction,
snoop-invalidate, `cbo` maintenance, `Flush()`) writes it back. Accesses that straddle a block boundary read backing
directly after a **BusSyncToBacking** transaction forces dirty holders — including the requesting cache itself — to
write back.

```csharp
var backing = new FlatMemory(0x10000);
var bus     = new MoesifBus(backing);
var cache0  = new MoesifCache(bus, capacityBytes: 4096, ways: 2, blockSizeBytes: 64);
var cache1  = new MoesifCache(bus, capacityBytes: 4096, ways: 2, blockSizeBytes: 64);

// cache0 reads 0x00  → Exclusive
// cache1 reads 0x00  → BusRead: cache0 E→S supplies the block, cache1 installs Forward
// cache1 writes 0x00 → BusReadInvalidate (F→M): cache0→I, cache1→M
// cache0 reads 0x00  → BusRead: cache1 M→O supplies cache-to-cache (no writeback),
//                      cache0 installs S and sees cache1's value; backing stays stale
```

`StateOf(address)` returns the current MOESIF state of the line covering an address (for test assertions). `Flush()`
writes all dirty (M/O) lines to backing without evicting them — useful for inspecting backing memory from tests.
`ConsumePendingStalls()` returns accumulated miss-penalty cycles for pipeline integration; a fill supplied
cache-to-cache is charged `PeerSupplyLatency` (constructor parameter, defaults to `MissLatency`) instead of the full
miss penalty, and `PeerSupplies` counts such fills.

`DirectoryBus` is a drop-in `IBus` alternative to the snooping `MoesifBus` for sequential multi-hart simulation: it
keeps a precise per-line directory (designated responder + sharer set, maintained via eviction notifications) so
invalidations snoop only actual holders and forwarder-less shared read misses need no probe at all. Cache-to-cache
supply is directed: the directory contacts the single M/O/E/F responder. It cannot be wrapped by `DeferredBus` (
two-phase concurrent mode), which is hardcoded to `MoesifBus`.

`MoesifCache` and `MoesifBus` are ISA-agnostic (`Orrery.Cache`). Use the `MultiHartKernel(IMemory[] perHartMemory, …)`
overload to give each hart its own cache. Pass the `ReservationTable` to `MoesifBus` so that LR/SC reservations are
cancelled on every `BusReadForOwnership` (write miss) and `BusReadInvalidate` (S/O/F→M upgrade):

```csharp
var flat   = new FlatMemory(0x10000);
var table  = new ReservationTable();
var bus    = new MoesifBus(flat, table: table);
var cache0 = new MoesifCache(bus, capacityBytes: 4096, ways: 2, blockSizeBytes: 64);
var cache1 = new MoesifCache(bus, capacityBytes: 4096, ways: 2, blockSizeBytes: 64);

var kernel = new MultiHartKernel([cache0, cache1],
    new Rv32Mechanism(reservationTable: table, hartId: 0),
    new Rv32Mechanism(reservationTable: table, hartId: 1));
```

Instruction fetch and data access both route through the per-hart cache (unified I/D model). `ReservationAwareMemory` is
not required in this stack. All three write paths cancel reservations via the bus:

| Path                                                | Bus transaction       | Reservation cancellation                      |
|-----------------------------------------------------|-----------------------|-----------------------------------------------|
| Write miss (write-allocate)                         | `BusReadForOwnership` | `table.InvalidateAt` in `BusReadForOwnership` |
| S/O/F→M upgrade (write hit on Shared/Owned/Forward) | `BusReadInvalidate`   | `table.InvalidateAt` in `BusReadInvalidate`   |
| E→M upgrade (write hit on Exclusive)                | none (silent)         | `table.InvalidateAt` in `BusSilentUpgrade`    |

### MultiHartPipeline (src/Core/Pipeline/)

`MultiHartPipeline` coordinates N full pipeline trains (`ISteppableTrain`) in round-robin cycle-interleaved order — the
pipeline-train analogue of `MultiHartKernel`. Each hart owns its own train instance (and typically its own
`MoesifCache`); the coordinator advances every non-halted train by one tick per logical cycle.

`ISteppableTrain` (`src/Core/Orrery/Train/`) is a minimal interface: `BeginStepping()`, `StepCycle() → bool`, `IsIdle`,
`FinishStepping() → RevolutionResult`. All pipeline train types implement it: `SingleCycleTrain`, `FiveStageTrain`,
`SuperscalarTrain`, `OooeTrain`, `CprTrain`, `SmtTrain`, `DaeTrain`.

```csharp
var flat   = new FlatMemory(0x10000);
var bus    = new MoesifBus(flat);
var cache0 = new MoesifCache(bus, 4096, 2, 64);
var cache1 = new MoesifCache(bus, 4096, 2, 64);

var train0 = new SingleCycleTrain(new Rv32Mechanism(), cache0, entryPoint: 0x00);
var train1 = new SingleCycleTrain(new Rv32Mechanism(), cache1, entryPoint: 0x40);

RevolutionResult[] results = new MultiHartPipeline(train0, train1).Run(maxTicks: 100_000);
```

`Run` returns one `RevolutionResult` per hart. Combine with `MoesifBus(flat, table:)` +
`Rv32Mechanism(reservationTable:, hartId:)` for LR/SC atomics between pipeline trains.

**OoO timing note:** `OooeTrain`'s physical register file starts zeroed; `ArchState.IntegerRegisters.Write()` updates
the architectural register file but not the PRF, so register values pre-set before `Run()` are invisible to the
pipeline. For OoO MOESIF coherence tests or any test that requires non-zero initial register values, compute those
values inside the program (e.g. `lui`+`addi` sequences). Also, OoO stores commit to the cache at ROB-head (several
cycles after fetch), so a cross-hart load must be issued late enough to see the committed store — pad H1 with nops in
the decode stream before the load's source-register computation.

### SmtTrain (src/Core/Pipeline/)

`SmtTrain` is a barrel-processor SMT train: N independent hart contexts share a single issue window of width
`issueWidth`. Each tick the coordinator distributes the available slots across active harts via a pluggable
`ISmtFetchPolicy` (src/Core/Mechanism, implementations under `Mechanism.SmtFetchPolicies`): `RoundRobinFetchPolicy`
(default) rotates the starting hart every cycle for long-run fairness; `IcountFetchPolicy` implements Tullsen et al.'s
ICOUNT (ISCA 1996), prioritizing harts with fewer recent cache/TLB stall cycles as a fetch/decode/queue-occupancy proxy
— the barrel core's atomic per-slot fetch+decode+execute has no literal queue depth to count, unlike the multi-stage
front end ICOUNT was designed for. This interleaves hart instructions at issue-slot granularity rather than the
whole-tick round-robin of `MultiHartPipeline`.

Each hart has its own `IArchState` and `MemoryLayers` (typically backed by per-hart `MoesifCache` instances sharing a
`MoesifBus`). All harts share the same `Escapement` and advance in lock-step. A hart that hits a branch, halt, trap, or
MRET is blocked for the rest of the current cycle's issue window; the remaining slots go to other harts. When all harts
have halted the Gear stops scheduling itself.

```csharp
var flat   = new FlatMemory(0x10000);
var bus    = new MoesifBus(flat);
var cache0 = new MoesifCache(bus, 4096, 2, 64);
var cache1 = new MoesifCache(bus, 4096, 2, 64);

var smt = new SmtTrain(
    new IMechanism[] { new Rv32Mechanism(), new Rv32Mechanism() },
    new IMemory[]    { cache0, cache1 },
    entryPoints: new ulong[] { 0x00, 0x40 },
    issueWidth: 2
);
smt.Run(maxTicks: 100_000);
IArchState s0 = smt.StateOf(0); // hart 0 register state
IArchState s1 = smt.StateOf(1); // hart 1 register state
```

`SmtTrain` also implements `ISteppableTrain` and can be wrapped in `MultiHartPipeline` for nested multi-level
parallelism. Aggregate cycle/retired/stall/IPC counters appear in `FinishStepping().Dials`.

### DaeTrain (src/Core/Pipeline/)

`DaeTrain` is a single-hart **Decoupled Access-Execute** train (Smith, ISCA 1982): the hart is split into an Access
lane (address generation, loads, stores) and an Execute lane (everything else), each an independent in-order queue, so
a load stuck behind other Access-lane work doesn't block unrelated compute already queued on the Execute lane.

Unlike Smith's original split — which required a compiler that statically partitioned the program into two instruction
streams — this is a **runtime-slicing** implementation that runs stock, unmodified binaries. A single shared front end
fetches and decodes sequentially and classifies each instruction into a lane at dispatch time via a forward
dependency-chain heuristic: an ALU op is Access-class if any source register was produced by an address-generating
Access-class op (taint seeded the moment a load or store is observed reading that register, so e.g. a pointer
increment between array-element loads is correctly pulled onto the Access lane); a load's own result is explicitly
*not* tagged as address-chain, so "load a value, then compute with it" lands on the Execute lane instead. Branches,
CSR/ECALL, fences, atomics, vector, floating point, and UVE ops are all treated as synchronizing barriers: the front
end drains both lanes before executing them directly against the live architectural state, keeping PC redirection
precise without any speculation machinery.

Cross-lane RAW dependencies are satisfied through per-write `HandoffSlot` objects captured at dispatch time (not a
sequence-number scoreboard): a consumer in the other lane reads the exact producer instance it depended on, via an
`OverrideArchState`/`OverrideRegisterFile` decorator pair that intercepts just those register reads during one
`IExecutor.Execute` call. A same-lane dependency needs no slot at all — each lane retires its own queue strictly in
program order, so the producer has always already written the live register file by the time a same-lane consumer
executes.

**Precise exceptions** from a lane instruction (a faulting load/store, most notably) are handled without halting:
every lane-instruction register write is logged to an undo list tagged with its dispatch-order sequence number, and
every memory write goes through an `UndoLoggingMemory` decorator that logs the prior value the same way. When a lane
instruction traps, the front end pauses and both lanes keep draining — but only instructions strictly older, in
program order, than the trap — until nothing older remains in flight (cross-lane read dependencies only ever point
backward in program order, so this always terminates). At that point every logged write younger than the trap is
unwound in reverse order, both lane queues and any stale pending barrier are flushed, and the trap is raised against
now-precise architectural state. The undo log is cleared whenever a trap resolves or a barrier drains both lanes,
since those are exactly the points at which nothing still in flight can ever be older — bounding undo-log growth
without fine-grained incremental pruning.

```csharp
var dae = new DaeTrain(new Rv32Mechanism(), memory, entryPoint: 0x00, laneQueueDepth: 8);
dae.Run(maxTicks: 100_000);
IArchState s = dae.ArchState;
```

`access_issued`/`execute_issued` counters report per-lane dispatch counts and `cross_lane_stalls` counts lane-head
stalls waiting on a not-yet-ready cross-lane value, in `FinishStepping().Dials`.

### Per-instruction lifecycle events (src/Core/Orrery/Observation)

`PEventLog` captures structured per-instruction lifecycle events — Fetch, Decode, Dispatch, Issue, Execute, Retire,
Flush — tagged with an instruction ID, PC, and cycle number. A cycle-level `FetchStall` sentinel (instrId=0) marks
cycles where the OoO fetch unit is blocked (faulted PC). Every single-hart train accepts a `PEventLog` instance to
enable recording (null = zero overhead): `FiveStageTrain`, `SuperscalarTrain`, `OooeTrain`, `CprTrain`, and
`DaeTrain`, so the Face PEvents waterfall covers all of them. Query methods include `ForInstruction(id)`,
`OfKind(kind)`, and `InCycleRange(from, to)` for post-hoc filtering and phase analysis. Every instruction is
assigned a monotonically increasing `InstrId` at fetch time, unique across the full simulation run, so lifecycle
phases can be correlated even for wrong-path instructions that are later flushed.

FiveStage records Fetch/Decode/Execute/Retire/Flush. OoO records the full lifecycle: Fetch → Decode → Dispatch → Issue →
Execute → Retire/Flush. Flush events appear as an additional terminal event for wrong-path or squashed instructions.
Superscalar records Fetch at fetch time, Execute at issue, Retire at result completion, and Flush for wrong-path
fetch-queue entries discarded on a misprediction, trap, or interrupt redirect. CPR records Fetch → Rename (checkpoint
entry append) → Dispatch (scheduler entry) → Execute → Retire (bulk commit), with Flush for entries squashed by a
checkpoint rollback or full flush — a re-executed COVHD instruction shows a second Fetch→Execute pass under a fresh
InstrId. DAE records Fetch/Dispatch at its single-issue frontend and Execute/Retire at lane execution, so the F→EX
gap in the waterfall directly visualizes the Access/Execute lane slip; barriers execute synchronously and trap
rollbacks flush everything still queued.

`SuperscalarTrain` is a scoreboarded in-order machine: a pipelined frontend fetches up to `issueWidth` instructions
per cycle along the predicted path (always-not-taken by default; any `IBranchPredictor` plugs in, with RAS-steered
calls/returns and direct jumps always taken) into a fetch queue, and an instruction fetched at cycle T becomes
issueable at T + `frontendDepth` (default 2) — a misprediction's penalty is the emergent frontend refill, not a
constant. Issue is strictly in order behind a register scoreboard: it stops at the first instruction with a pending
source (RAW, full bypass — a latency-L producer feeds a consumer issuing L cycles later), a pending destination
(WAW, in-order writeback), exhausted per-class FU ports for the cycle, or a memory op while the blocking data cache
services a miss (one outstanding miss; independent ALU work continues underneath — stall-on-use via the scoreboard).
FU counts and latencies come from the same `FuLatencyConfig` the OoO trains use. Instructions execute functionally
at issue (exact for an in-order machine), so branches resolve at issue and train the predictor with no outstanding
speculation beyond the fetch queue. Superscalar honors HTIF tohost-exit stores (`RequestHalt`), and Superscalar,
DAE and SMT advance the cycle CSR (`ArchState.OnCycle`) every cycle — previously frozen `rdcycle` readings made
self-calibrating benchmarks (dhrystone) re-run their measurement loop forever on all three. The Face's pipeline
picker covers `single_cycle`, `five_stage`, `superscalar`, `ooo`, `cpr`, and `dae` (predictor config applies to
five_stage/superscalar/ooo/cpr; the PEvents waterfall supports five_stage, superscalar and ooo), and sweeps select
DAE with `"pipeline": "dae"` (`dae_lane_queue_depth`).

### Spike lock-step co-simulation (src/Isa/RiscV32/CoSim)

`SpikeCoSimReference` implements `ICommitObserver` and launches Spike as a live child process with `--log-commits`. For
each instruction that Horologium commits, it reads the next line from Spike's stderr stream (blocking until Spike
produces it), then immediately compares PC, raw encoding, and any integer register write — divergence is reported at the
exact failing instruction. Boot-ROM commits (PC below `baseAddress`) are skipped. Attach it via the optional
`commitObserver` parameter to `SingleCycleTrain`, `FiveStageTrain`, or `OooeTrain` — the in-order pipeline fires
`OnCommit` from `WritebackStage` on normal retire, and the out-of-order pipeline fires once per ROB-head commit in
program order, so the check covers the hazard/forwarding and speculative-memory datapaths too. Wrap in `using` to kill
Spike on completion. `SpikeCoSimTests` runs three fixtures across all three trains: `test.elf` (simple RV32I golden
path), `rich.elf` (RV32IM — multiply/divide, an insertion sort, and heavy data-dependent branching, to exercise the
multi-cycle functional units, store-to-load forwarding, and flush paths), and `htif.elf` (the same RV32IM workload but
terminating through the HTIF `tohost` register instead of EBREAK, so standalone Spike exits cleanly). Spike logs the
post-exit spin-loop an indeterminate number of times; each train commits up to the exit, then halts, so its stream is a
clean prefix of Spike's. `dtc` must be on PATH (the Nix dev-shell provides it); the tests can be excluded from CI
without Spike with `--filter "FullyQualifiedName!~SpikeCoSim"`.

### Program termination

Two halt mechanisms, both stopping all three trains at the terminator instead of spinning to `maxTicks`:

- **HTIF tohost exit (first-class).** When the mechanism is given the `tohost` address (
  `new Rv32Mechanism(htifTohost)`), a word store of an odd exit code to that register is flagged by the executor with
  `ExecuteResult.RequestHalt`. The trains carry that flag through commit (the in-order pipeline via `MemWbLatch`, the
  out-of-order core via the ROB) and halt *after* the store commits — the engine terminates at the exit write itself,
  not the spin that follows. The flag is ISA-agnostic: the trains act on it without knowing about HTIF. The address is
  surfaced generically as `IWorkload.HtifTohostAddress` (the ELF's `tohost` symbol), so `Experiment.Run`/`Trace`, the
  Runner, and the Face all wire it automatically for HTIF ELFs.
- **Unconditional jump-to-self (backstop).** For non-HTIF programs, a `jal`/`jalr` whose resolved target is its own PC (
  the conventional bare-metal `j .` terminator) halts the run. Gated on `ToothClass.Branch` so a conditional spin-wait —
  which may be waiting on an interrupt — is not mistaken for a halt.
- **HTIF syscall proxy (fesvr magic-mem protocol).** `HtifMemory` now fully implements the fesvr host-side: when the
  simulated program writes an even, non-zero pointer to `tohost` (a magic-mem syscall request), `HtifMemory` reads the
  8×uint64 struct (`magic_mem[0]` = syscall number, `[1..3]` = args), dispatches `SYS_write` (64), `SYS_read` (63),
  `SYS_close` (57), `SYS_lseek` (62), `SYS_fstat` (80), `SYS_open` (1024), and `SYS_openat` (56), writes the return
  value back to `magic_mem[0]`, then ACKs `fromhost`. An optional `TextWriter? output` parameter captures `SYS_write`
  output; when null the bytes are discarded. `Rv32ElfWorkload.WrapMemory(memory, output)` forwards the writer.
- **Linux syscall-emulation mode (`LinuxSyscallEmulator`).** A gem5 SE-style ECALL interceptor. Pass
  `syscallHandler: new LinuxSyscallEmulator(workload.InitialBreak, output)` to `Rv32Mechanism`. The executor routes
  every ECALL through the handler instead of trapping: `SYS_write`/`SYS_read` operate on simulated memory; `SYS_exit`/
  `SYS_exit_group` set `RequestHalt`; `SYS_brk` manages a software heap break; `SYS_mprotect`, `SYS_rt_sigaction`,
  `SYS_rt_sigprocmask`, `SYS_set_tid_address`, `SYS_getpid`, `SYS_gettid` return benign constants; `SYS_ioctl` returns
  `−ENOTTY`; all others return `−ENOSYS`. `SYS_writev` (`Writev`) walks the `iovec` array and delegates each entry to
  `Write` — real musl/glibc buffered stdio (`printf`, `fwrite`) flushes through `writev`, not plain `write`; see the
  batch-harness note below for how this was found. `Rv32ElfWorkload.InitialBreak` exposes the page-rounded end of the
  last PT_LOAD segment as the initial break address. `ISyscallHandler` (in `Mechanism/`) defines the interface so
  alternate emulators can be plugged in.
- **`LinuxSyscallEmulator` realism: file I/O, mmap, clock/random, fcntl.** `SYS_openat`/`SYS_read`/`SYS_write`/
  `SYS_close`/`SYS_lseek` are unrestricted host passthrough (the gem5-SE/Spike-pk convention — paths open exactly as
  given, against the simulator process's own cwd; no sandboxing, since the guest binary is the user's own,
  already-compiled, locally run program). `SYS_fstat` fills a real `struct stat`/`stat64` — RV32's `fstat` syscall
  (80) is `sys_fstat64` (104-byte layout), RV64's is `sys_newfstat` (128-byte layout); genuinely different structs
  under the same syscall number, selected by the new `wordSize` constructor parameter. Both layouts, and
  `clock_gettime`'s 16-byte `struct __kernel_timespec` (identical on RV32/RV64 — RISC-V never implemented the
  legacy 32-bit-time_t syscalls), were verified by compiling field-store probes with `riscv32-none-elf-gcc`/
  `riscv64-none-elf-gcc` and reading the emitted store offsets, not reconstructed from memory. `SYS_mmap` is a bump
  allocator over a caller-supplied `[mmapBase, mmapLimit)` arena (new constructor parameters; anonymous mappings
  only, file-backed mappings eagerly read the file into the region); `SYS_munmap` never reclaims; an unconfigured
  or exhausted arena returns ENOMEM, same as real mmap under memory pressure. `SYS_clock_gettime`/`SYS_getrandom`
  are deterministic (a synthetic incrementing clock; a seeded xorshift PRNG) rather than real host time/entropy,
  matching this project's reproducibility precedent. `SYS_fcntl` returns benign success for
  `F_GETFD`/`F_SETFD`/`F_GETFL`/`F_SETFL`. stdin (fd 0) is separate from the file-I/O passthrough: an optional
  `Stream? input` constructor parameter redirects `SYS_read` on fd 0 to it (read sequentially, never rewound, EOF
  once exhausted); omitted (the default) it stays always-EOF as before. `SYS_write`/`SYS_writev`/`SYS_exit_group`
  are now validated against a real, genuinely compiled and statically-linked musl RV64 binary (see the batch-harness
  note below) — but `openat`/`read`/`close`/`lseek`/`fstat`/`mmap` are still only hand-verified struct offsets plus
  unit tests calling `Handle` directly, plus one ELF-driven round-trip (`stdin_echo64.elf`) proving an injected
  stream reaches a real guest's `SYS_read` and comes back out through `SYS_write` — none of *those* syscalls have
  been exercised by a real linked binary yet.
- **RV64 syscall-emulation wiring.** `Rv64Mechanism` now takes the same `syscallHandler: ISyscallHandler?` constructor
  parameter as `Rv32Mechanism` — `Rv64Executor : Rv32Executor` already inherited the ECALL-dispatch arm unchanged, so
  this was the only missing wire. `Rv64ElfWorkload.InitialBreak` mirrors `Rv32ElfWorkload`'s PT_LOAD-scan computation.
- **psABI initial-stack builder (`InitialStackBuilder`, ISA-agnostic, in `Mechanism/`).** Bare-metal entry (PC = ELF
  entry point, registers untouched) is enough for the hand-written assembly test fixtures, but a real compiled
  binary's C-runtime `_start` reads its command line and environment straight off the initial stack. Building
  `_start` from bare-metal isn't possible without one. `BuildInitialStack(memory, stackTop, wordSize, argv, envp,
  auxv)` writes a standard argc/argv/envp/auxv layout (string blob → 16-byte-aligned auxv array, terminated by
  `AT_NULL` → envp/argv pointer arrays, NULL-terminated → argc word) and returns the resulting SP, 16-byte aligned
  per the RISC-V calling convention. One function serves RV32 and RV64 (`wordSize` 4 or 8). `BuildStandardAuxv`
  assembles the standards-minimal auxv set for a statically-linked binary (`AT_PAGESZ`, `AT_PHDR`/`AT_PHENT`/
  `AT_PHNUM`, `AT_ENTRY`, zeroed uid/gid/hwcap/secure). `IElfWorkload.PhdrAddress`/`PhEntrySize`/`PhNum`
  (`Rv32ElfWorkload`/`Rv64ElfWorkload`, computed from the ELF header) supply the real AT_PHDR/AT_PHENT/AT_PHNUM
  values every caller now passes — placeholder zeros here are not just imprecise but a real crash: musl's own
  `_start`/`__init_tls` walks the program header table itself (found via those three auxv entries) to locate
  PT_TLS and set the thread pointer with a plain register move (no syscall involved), so a zero AT_PHDR makes
  that walk dereference address 0 and fault on any binary declaring thread-local data — confirmed with a real
  compiled `__thread`-using binary (`TestBinaries/tls_probe.c`), which crashes with the placeholder auxv and
  runs correctly with the real one (`Tests/RiscV64/System/RealLinkedBinaryTests.cs`). Verified two ways besides
  that: `InitialStackBuilderTests` asserts the exact byte layout for both word sizes directly against a
  `FlatMemory`; `Tests/RiscV64/System/InitialStackTests.cs` loads hand-assembled RV64 probes (`abi_probe64.s`,
  `stdin_echo64.s`, each with its own independent offset arithmetic) that read the stack this function built and
  echo back what they find. Callers still inject the SP manually (`ArchState.IntegerRegisters.Write(2, sp)`
  before `Run()`) — there is no `IWorkload`/`Train` wiring for it.
- **Batch benchmark harness (`BenchmarkConfig`/`Experiment.RunBenchmark`, `src/Isa/RiscV32/Analysis/`).** Ties the
  three pieces above together into a real Linux-ABI entry, instead of each being exercised only in isolation:
  `BenchmarkConfig` (JSON, mirroring `NamedConfig`'s conventions) names an ELF, its argv, an optional stdin file, an
  optional reference-output file, and an optional memory-size override. `RunBenchmark` builds the psABI stack
  (`argv[0]` derived from the ELF's file name), wires a `LinuxSyscallEmulator` with captured output and the
  benchmark's stdin file (if any), runs to completion (or `maxTicks`) on a functional `SingleCycleTrain` — this is a
  correctness/regression harness, not a timing run — and diffs captured output against the reference file
  byte-for-byte (`Encoding.Latin1` on both sides, matching the emulator's `(char)byte` capture convention, so the
  comparison is exact regardless of content) unless `BenchmarkConfig.NormalizeTrailingWhitespace` is set, in which
  case both sides are trimmed of trailing whitespace before comparing — off by default, since a stray trailing
  newline in the reference file (the common SPEC-style convention, regardless of whether the guest's last write
  emitted one) would otherwise fail a benchmark that's actually correct.
  `BenchmarkResult.Halted` (via `SingleCycleTrain.IsIdle` after `Run()`, not a tick-count heuristic — a clean
  `SYS_exit` drains the Escapement, a timeout leaves events pending) tells a timeout apart from a real exit;
  `Checked`/`Passed` tell "no reference supplied" apart from "verified and matched". Kept ISA-agnostic (takes an
  already-built `IElfWorkload` and a `mechanismFactory` the caller supplies) the same way the rest of `Experiment`
  is. The Runner exposes it as `--bench-config <path.json>`, running every benchmark under `--xlen`'s ISA and
  exiting with status 1 if any times out or fails its reference check. A crashing benchmark (e.g. one that
  dereferences a failed mmap's negative return) is caught per-benchmark and reported as `ERROR` rather than
  aborting the rest of the batch. Verified end-to-end against bare-metal SE-mode probes (`abi_probe64.elf`,
  `stdin_echo64.elf`, `mmap_probe64.elf`); the toolchain gap that previously made a real linked-libc test
  impossible is now closed (`riscv64-unknown-linux-musl-gcc` in `flake.nix`). The first attempt, a hand-written
  `hello.c` compiled `-static` and run through `--bench-config`, halted cleanly and a raw `write()` syscall
  (bypassing stdio) was captured correctly, but `printf`/`fflush` produced no output at all — root-caused to a
  missing `SYS_writev`: musl's buffered stdio flushes via `writev`, not plain `write`, and the emulator's fallback
  `ENOSYS` for unimplemented syscalls is swallowed silently by musl's stdio error path rather than surfaced. Fixed
  with `LinuxSyscallEmulator.Writev` (walks the `iovec` array, delegating each entry to the existing `Write`,
  matching real `writev`'s zero-length-skip and short-write-stops-early semantics). `TestBinaries/hello64_musl.c`/
  `.elf` — a genuinely compiled and statically-linked binary, not a hand-assembled probe — now runs its `printf`
  and returns its `argv[0]` correctly end-to-end (`Tests/RiscV64/System/RealLinkedBinaryTests`), the first real
  linked binary to complete this pipeline's entry/syscall/stdio path successfully. This validates only that
  trivial path, though — the SimPoint/checkpoint sampling pipeline itself (the actual substance of the SPEC-harness
  TODO item) has not yet been run against a real compiled binary, only against bare-metal HTIF probes; see
  TODO.md. `BenchmarkConfig.MmapArenaBytes`
  optionally sizes an anonymous-mmap arena, appended past the workload's own memory so enabling it never shifts
  where the stack or `brk`-growable region end up (both keep the exact placement they'd have with it unset);
  omitted or 0 (the default) keeps `mmap` disabled — `SYS_mmap` returns `ENOMEM`, same as before this existed. The
  arena is still a bump allocator that never reclaims (`SYS_munmap` is a no-op, per the `LinuxSyscallEmulator`
  realism note above), so a long malloc-heavy run still exhausts a finite arena and ENOMEMs mid-run, and
  `FlatMemory` is `int`-sized, putting a real multi-GB SPEC heap out of reach regardless of arena config — sizing
  the arena per benchmark is a tuning knob, not a solved problem.

Because the five-stage and out-of-order trains previously spun HTIF binaries to `maxTicks`, adding these halts also
makes the HTIF benchmark suite finish in seconds. `HtifExitTests` covers both paths across all three trains without
requiring Spike.

### Hardware comparison (RiscV/Analysis)

`Experiment.Run(workload, configs, mechanism)` runs the same workload under multiple `NamedConfig` entries (each a named
`TrainConfig` describing forwarding, predictor, cache, TLB, and store-buffer parameters), returns an `ExperimentResult`,
and supports warmup ticks and periodic time-series snapshots. Results can be formatted as a Markdown table, summary CSV,
or time-series CSV for graphing. `NamedConfig` sweep files are plain JSON arrays, readable by the Runner's `--sweep`
flag.

### Top-Down Microarchitecture Analysis (Pipeline/TopDownAnalysis)

`OooeTrain`, `CprTrain` and `SuperscalarTrain` record the Top-Down Analysis slot-accounting events (Yasin,
ISPASS 2014) at their dispatch/issue stage — the frontend/backend border. On the in-order superscalar the flavor
simplifies: issue never speculates past an unresolved branch, so SlotsIssued equals SlotsRetired, Bad Speculation
consists purely of post-flush frontend-refill bubbles (split into branch mispredicts vs machine clears by cause),
and Backend Bound is the scoreboard/FU-port/LSU backpressure residual. On the OoO trains: `td_total_slots` (issueWidth × cycles), `td_slots_issued`, `td_fetch_bubbles` (unutilized
dispatch slots with no backend stall; I-fetch miss stall cycles count width slots each), `td_recovery_bubbles`
(flush/squash recovery cycles), plus cycle-denominated level-2 events (`td_fetch_latency_cycles`,
`td_exec_stall_cycles`, `td_memstall_load_cycles`, `td_memstall_store_cycles`). Level-1 dials classify every issue
slot into **Frontend Bound / Bad Speculation / Retiring / Backend Bound** per the paper's Table 2 formulas (summing
to 1; Retiring cross-validates as IPC ÷ width); level-2 dials split frontend into fetch latency vs bandwidth,
bad speculation into branch mispredicts vs machine clears (non-branch flushes: memory-order violations, traps,
interrupts), and backend into memory vs core bound (execution-stall cycles with/without an in-flight load, per the
paper's ExecutionStalls heuristic). All ten `td_*` dials flow through `ExperimentResult` sweep tables automatically;
`TopDownBreakdown.FromSnapshot(snapshot)` computes the same breakdown from any (warmup-subtracted) pipeline
`DialBoardSnapshot`, and its `ToString()` renders the hierarchy as a small tree.

`DaeTrain` and `SmtTrain` also record the same ten `td_*` events, each adapted to its own front end. DAE's
single-dispatch front end has no explicit issue width, so TotalSlots accrues one slot per real cycle (rather than
issueWidth × cycles); DAE has no branch speculation (barriers, including branches, execute in-order against precise
architectural state), so its only Bad Speculation source is a precise-trap undo-log rollback, wired as a
`RecoveryBubbles`/machine-clear event rather than a branch mispredict. SMT's slots are shared issueWidth-wide across
harts each cycle; it has no speculation either (each hart resolves its own PC synchronously), so Bad Speculation is
always zero, and the only source of unfilled slots is thread starvation (fewer runnable harts than issueWidth) —
correctly read as Frontend Bound, since there is no ROB/IQ-style backend resource in either design to structurally
block dispatch.

### CPI stacks via interval analysis (Pipeline/CpiStackAnalysis)

`OooeTrain` and `CprTrain` also build interval-analysis CPI stacks (Eyerman, Eeckhout, Karkhanis & Smith, ASPLOS 2006 — the
counter architecture Sniper's CPI stacks build on; interval model in their ACM TOCS 2009 paper). Total CPI decomposes
additively into a **base** plus per-miss-event components (`cpi_*_cycles` counters, `cpi_*` dials): L1/L2/L3 I-cache
and I-TLB miss delays, the branch misprediction penalty, L1/L2/L3 D-cache and D-TLB long-miss stalls, store
write stalls, and long-latency/dependence resource stalls. The mechanisms follow the paper adapted to this simulator:
I-side penalties accumulate provisionally and post to the globals only when an instruction carrying the sFMT
'I-cache miss' bit retires (wrong-path fetch penalties are discarded on flush, absorbed into the branch penalty);
a mispredicted branch's penalty is its ROB residency (dispatch → redirect, minus cycles already claimed by backend
components) plus dispatch-empty refill cycles; and backend completion stalls are counted when the backend
backpressures dispatch while an incomplete instruction blocks the ROB head, classified by the deepest level the
blocking load missed (recorded per-load at execute) or as a resource stall for non-loads — the paper's "ROB full"
trigger is widened to include IQ/LQ/SQ backpressure since this machine's per-class issue queues are the binding
window resource for serialized chains. On `CprTrain` the same accounting maps onto checkpoints: window-entry
stamps are taken when rename appends the checkpoint entry, the "blocked head" is the head checkpoint's oldest
*incomplete* entry (bulk commit waits on the slowest member, unlike a ROB head), the branch penalty window posts
at checkpoint rollback, and non-branch rollbacks (memory-order violations) plus full flushes count as machine
clears. `CpiStack.FromSnapshot(snapshot)` computes the stack from any pipeline snapshot (counter-based, so
warmup/ROI-window accurate); base + components equals total CPI by construction.

### SimPoint phase analysis (Pipeline/SimPointAnalysis)

Representative-sampling substrate after Sherwood, Perelman, Hamerly & Calder (ASPLOS 2002). `BbvProfiler` is an
`ICommitObserver` (typically attached to a functional `SingleCycleTrain`) that splits the committed stream into
fixed-length intervals and records per-interval basic-block vectors — block-entry counts weighted by block length,
with blocks identified dynamically (start = first instruction after a control-flow instruction or a trap
discontinuity) and per-PC decode info memoised. `SimPointAnalysis.Analyze` then normalizes each BBV, projects it to
15 dimensions through a seeded random linear projection (the matrix is derived from a hash of block PC × dimension,
never materialised), runs k-means for k = 1…10, scores each clustering with the Pelleg–Moore spherical-Gaussian BIC
(variance floored at a fraction of the global variance so duplicated interval vectors cannot drag k to the maximum),
and picks the smallest k whose score reaches 90% of the BIC spread. The result carries the per-interval phase
labels, one simulation point per phase (the interval closest to its cluster centroid) with its weight, and the
single simulation point closest to the whole-run centroid. `runner --simpoint <intervalSize> prog.elf` profiles and
prints the phase table; the simulation points feed the checkpoint/ROI handoff flows for detailed-model sampling
(on CoreMark at 20 K-instruction intervals this finds the iteration's interleaved kernels as ~7 recurring phases).

### LoopPoint loop-header detection (Pipeline/LoopPointAnalysis)

The region-marker half of LoopPoint (Sabu, Patil, Heirman & Carlson, HPCA 2022) — the multi-hart counterpart to
SimPoint above, staged as a sequence of independently-actionable prerequisites in `TODO.md` (thread pointer/PT_TLS,
`clone()`, `futex()`, per-hart `gettid`/thread-exit, and an OpenMP/pthreads toolchain fixture are done; the
methodology's own analysis pieces are still landing one at a time). `LoopHeaderTracker` is a standalone
`ICommitObserver`, structurally a sibling to `BbvProfiler` rather than an extension of it (not yet wired into its
interval slicing), that identifies a loop header the same way `BbvProfiler` identifies a block boundary — from the
committed PC stream's own discontinuities, without a real control-flow graph. A candidate is a backward transfer
(target ≤ source) that is both direct and not a call, via `IDecoder.GetFetchHint`'s `BranchTarget.HasValue && !IsCall`
— the same ISA-agnostic pre-decode hint branch predictors already use for RAS/indirect handling. Both halves of that
check are independently necessary: excluding indirect transfers (JALR — returns, virtual calls, computed gotos)
rules out a `ret` landing at a lower address than its own call site; excluding calls separately rules out a direct
`jal ra, target` to a function placed, in link order, before its caller. Real loop back-edges are essentially
always direct, non-call transfers (a conditional branch, or a compiler-emitted unconditional jump for a `goto`-style
loop), which is what makes this cut viable without a real dominator analysis — confirmed by a discriminating test
with a helper function placed *after* its caller's loop, so its `ret` lands backward every iteration, right beside
a genuine loop back-edge. `count` in the paper's `(PC, count)` markers is the number of times the backward edge has
been *taken* to reach that header, not the total iteration count — an N-iteration loop's first entry is a
fall-through from the code before it, not a discontinuity, and so isn't observable in a single streaming pass; the
final marker for such a loop reads `(header, N-1)`. `rangeStart`/`rangeEnd` scope detection to the loaded program's
own address space (a sanity bound) — they do not by themselves separate user code from statically-linked library
code sharing the same segment; that separation is `excludedRanges`, an optional constructor parameter checked
against the header's own PC only (not the backward edge's source), mirroring the paper's exclusion of
synchronization-library busy-waiting from loop-based work counting while still executing that code normally.
`IElfWorkload.EnumerateSymbols()` exposes every named, non-zero-size `.symtab` entry, and
`SyncLibrarySymbols.ExcludedRanges` turns a name-prefix list (`__tl_`/`__vm_`/`__wait`/`__lock`/`pthread_`/`sem_`/
`gomp_`/etc., a musl/libpthread/libgomp internal-symbol guess verified against `pthread_probe.elf`'s own compiled
symbol table, not assumed) into the `[Start, End)` ranges `LoopHeaderTracker` consumes. Each piece is unit-tested on
its own — symbol enumeration, prefix classification against the real ELF, and range-suppression against a synthetic
two-loop fixture — but end-to-end suppression of a real spin loop is still unproven: running `hello64_musl.elf`
(single-threaded, uncontended) through the tracker with real exclusion ranges produced zero markers inside any
excluded range, because an uncontended lock's CAS retry loop is never actually taken backward. Proving the
composition needs genuine multi-hart contention, which needs per-hart commit-observer wiring `MultiHartKernel`
doesn't have yet — deferred to the per-thread loop-iteration BBV item in `TODO.md`, where that wiring lands anyway.

The paper's next step, "flow-control" (restricting thread forward progress during profiling so no thread races
ahead of another), needed no new code here: it exists in the paper to correct skew from a real, non-deterministic
host OS scheduler running Pin instrumentation — an artifact `MultiHartKernel.Step()`/`MultiHartPipeline.Run()`
cannot produce, since both already advance every non-halted hart by exactly one instruction/cycle per call,
deterministically. `RunConcurrent`'s `Parallel.For` is the one mode with real host-thread parallelism, but each
tick is still a hard barrier — no hart can complete two cycles before another completes its first. Confirmed
(not just argued) with a test reading through independent architectural state: two harts each running an
infinite counting loop stay bit-for-bit in lockstep after every tick, both under `MultiHartKernel.Step()` and
under `MultiHartPipeline.RunConcurrent`.

### SMARTS sampling (Pipeline/SmartsDriver)

Systematic statistical sampling after Wunderlich, Wenisch, Falsafi & Hoe (ISCA 2003) — the sibling methodology to
SimPoint above, trading SimPoint's few large clustered intervals for many small, evenly-spaced ones with a
statistically quantified confidence interval instead of a phase classification. `SmartsDriver.Run` alternates a
functional fast-forward train (`SingleCycleTrain`) with a detailed warm-then-measure window per sampling unit —
`W` unmeasured instructions to rebuild pipeline-internal state a functional pass can't warm, then `U` measured
instructions — spaced `K` instructions apart, starting at offset `J`. Unlike SimPoint's per-point
`ArchitecturalCheckpoint` (a full memory snapshot, fine for ~10 points but far too costly at SMARTS's own n≈10,000
scale), state moves between the two trains via a cheap in-memory register-level copy
(`ArchStateTransfer.CopyInto`) — both trains share the same `MemoryLayers` (cache/TLB) and `IBranchPredictor`
instances for the whole run, so cache/TLB/branch-predictor state stays continuously warm through the
fast-forwarded majority of the stream rather than needing to be rebuilt from cold at every window (the paper's
"functional warming", Section 3.1) — `SingleCycleTrain` already ticks a shared cache/TLB on every access, and,
given a predictor, trains it on every resolved branch even though it never itself speculates. `FiveStageTrain` and
`OooTrain` are both supported as the detailed pipeline (`SmartsDriver.FiveStage`/`SmartsDriver.Ooo`); the OoO case
additionally drains in-flight ROB/IQ/LQ/SQ state (`OooTrain.Drain`) after each measured window, since an OoO train
can still hold not-yet-retired instructions at the exact tick the window ends. `SmartsStatistics` computes the
sample mean CPI, its coefficient of variation, the achieved confidence interval at a given z (95%/99.7%), and the
sample size needed for a target confidence — the paper's own two-step procedure (run with an initial n, check the
achieved confidence, rerun with a computed `n_tuned` if it falls short) is left to the caller rather than
auto-looped, matching how the paper itself describes it as a manual step.
`runner --smarts <U> <W> <K> [--smarts-n <n>] [--smarts-offset <j>] [--smarts-argv "<args>"] prog.elf` runs it
against the `--sweep` configs (or the default sweep), printing mean CPI/IPC, coefficient of variation, 95%/99.7%
confidence intervals, and the recommended `n` for ±3% at 99.7% confidence. `--smarts-argv` mirrors
`--simpoint-argv`: it opts into Linux-ABI entry for a real compiled binary (a psABI initial stack, argv[0] the
ELF's file name plus extra space-separated entries from the flag's value, and a `LinuxSyscallEmulator`), so real
compiled binaries — not just bare-metal HTIF ELFs — can be sampled. Unlike `--simpoint-argv`, which needs a fresh
`LinuxSyscallEmulator` per functional pass (`CaptureSimPointCheckpoints` recreates its mechanism at every
checkpoint/measure boundary) and therefore serializes brk/mmap/fd/stdin state through
`ICheckpointableSyscallHandler`, SMARTS needs none of that: `SmartsDriver.Run` reuses one mechanism instance for
the run's entire lifetime, so whatever `ISyscallHandler` it carries persists across every functional/detailed
switch by plain object identity — sound by construction, not a serialize/restore round-trip that could silently
no-op. The one-time seam this needs — seeding the psABI stack pointer into whichever train is constructed first,
since `ArchStateTransfer.CopyInto` only fires once something has already been handed off — is
`SmartsDriver.Run`'s `seedInitialState` hook. Because SMARTS is strictly forward (never re-executes a region:
`warmStart` is clamped to never precede wherever the stream already is), a real syscall a sampled run passes
through fires exactly once, the same as an unsampled run — safe under sampling with no double-`write()`. Known
gaps: the return-address stack isn't warmed by the functional pass (it lives outside `IBranchPredictor`); and
timing-dependent CSRs (e.g. `mcycle`) can't be sampled faithfully, since functional fast-forward doesn't advance
cycle count the way detailed windows do — an inherent boundary of the sampling approach itself, not a gap to close.

### Architecture scripting and checkpointing (Script/)

`ScriptHost.EvaluateFileAsync(path)` compiles and evaluates a `.csx` (Roslyn C#) or `.fsx` (F# Interactive) script file
whose last expression is a `MachineSpec`. All `Pipeline.Spec`, `Orrery.Spec`, `Orrery.Cache`, `RiscV32`, and `RiscV64`
namespaces are pre-imported — no `#r` directives or `using`/`open` statements needed, so a script can construct
`Rv64Mechanism()` exactly like `Rv32Mechanism()`. The result can be passed directly to `MachineSpec.Build()`:

```csharp
// example.fsx
let pipeline = FiveStageSpec(ForwardingEnabled = true)
let l1 = CacheLevelSpec(4096, 4, 64, 10)
let cache = CacheHierarchySpec.Unified(CachePathSpec([| l1 |]))
MachineSpec(pipeline, (fun () -> Rv32Mechanism()), cache)
```

The Runner exposes this as `--script <file.csx|fsx>`. `--xlen 32|64` (default 32) selects RV32I or
RV64I for the workload *and* the mechanism factories used across every Runner mode — the default
sweep, `--simpoint`, `--trace-json`, `--elastic-record`, `--stf-record`, and `--script` (including
`--roi-start`/`--checkpoint-save`/`--checkpoint-load`); `--xlen 64` requires an ELF path (the
built-in demo program is RV32-only), except `--checkpoint-load`, which restores its own memory/PC
from the checkpoint and never touches the workload. Two script handoff patterns are supported:

**Symbol-based region-of-interest (ROI)** — name ELF symbols to bracket the measurement window. The Runner fast-forwards
functionally (single-cycle) until the start symbol's PC is committed, then restores state into the script's pipeline and
runs the detailed model until the end symbol or `--max-ticks`:

```bash
dotnet run --project src/Apps/Runner -- prog.elf \
  --script scripts/ooo.fsx \
  --roi-start roi_begin --roi-end roi_end \
  --max-ticks 5000000
```

`--roi-start` requires an ELF workload with a matching symbol. `--roi-end` is optional; omit to run until `--max-ticks`.
The fast-forward phase uses `SingleCycleSpec` regardless of what the script specifies; the script's pipeline is used
only for the detailed ROI phase.

**Manual checkpoint handoff** — save and restore state across separate Runner invocations with `--checkpoint-save` and
`--checkpoint-load`:

```bash
# 1. Fast-forward 100 M instructions on a single-cycle model, save state.
dotnet run --project src/Apps/Runner -- prog.elf \
  --script scripts/single_cycle.fsx --max-ticks 100000000 --checkpoint-save fast.chk

# 2. Resume from the checkpoint on a detailed OoO model.
dotnet run --project src/Apps/Runner -- prog.elf \
  --script scripts/ooo.fsx --checkpoint-load fast.chk --max-ticks 10000000
```

**`ArchitecturalCheckpoint`** (`src/Core/Mechanism/ArchitecturalCheckpoint.cs`) is the serialization layer.
`Save(path, state, memory, tick)` writes PC, privilege level, integer/FP registers, memory, and an ISA-specific blob (
CSRs, VRF, UVE scalar state via `IArchState.WriteState`) to a binary file. `SaveAsync(path, state, memory, tick)`
captures the same state synchronously (safe even if the caller keeps mutating `state`/`memory` immediately after the
call returns) but defers the file write to a worker thread, returning a `Task` the caller awaits once it has no more
overlapping work; the Runner's `--checkpoint-save` path uses it. `Load(path)` deserialises without touching live state;
`chk.RestoreInto(state, memory)` applies it. `FlatMemory` implements the `ISnapshotableMemory` interface (`BaseAddress`,
`SizeBytes`, `CopyTo`, `LoadFrom`) required by the checkpoint API. `ISteppableTrain.ArchState` (default `null`) exposes
the committed hart state after or during a run; `MachineHandle.ArchState` forwards it. ROI uses an in-memory checkpoint
internally (no file I/O); both `--checkpoint-save` and the ROI path can be combined to persist the post-ROI state.

**Instruction-count-bounded warmup/measurement.** `Train.Run`'s own warmup/measure split (and the ROI/checkpoint-load
flows above) are tick-bounded; SimPoint-style sampling needs boundaries in dynamic instruction counts instead.
`InstructionCounter` (`src/Core/Mechanism/InstructionCounter.cs`) is an `ICommitObserver` that counts commits and,
given an ascending list of target counts, fires a callback as each is crossed — enough to save one checkpoint per
simulation point in a single functional pass. `WarmupMeasureDriver.RunWarmupThenMeasure` (`src/Core/Pipeline/`) then
steps a train (built with that counter as its `CommitObserver`) through an unmeasured warmup phase, snapshots a
baseline via the new `ISteppableTrain.SnapshotDials()`, steps through the measured phase, and returns the
baseline-subtracted result via the new `FinishStepping(baseline)` overload — the stepping-API equivalent of `Run`'s
own tick-based warmup, needed because a train's lifecycle is one-shot (`Reset()` would wipe warmed-up
microarchitectural state along with the counters). Only `SingleCycleTrain`, `FiveStageTrain`, and `OooeTrain`
implement `SnapshotDials`/baseline-`FinishStepping` — the trains that already support a commit observer. Building
this surfaced a real, previously undiscovered bug: `OooeTrain`'s physical register file started zeroed at
construction and was never re-seeded from `ArchState.IntegerRegisters`, so a checkpoint restored into an OoO train
was silently invisible to execution (`Wind()` now re-seeds it; a fresh, never-restored `ArchState` seeds zeros, so
ordinary runs are unaffected).

**SimPoint-interval → checkpoint glue.** `Experiment.CaptureSimPointCheckpoints`/`MeasureSimPointCheckpoints`
(`src/Isa/RiscV32/Analysis/Experiment.cs`) compose the three pieces above into the full SimPoint sampling workflow,
split into a config-independent capture half and a config-dependent measure half. `CaptureSimPointCheckpoints`
profiles the workload (`ProfileSimPoints`) and saves one checkpoint per simulation point in a single second
functional pass (`InstructionCounter`'s target-callback mode — interval-0 targets are captured from the pristine
pre-`StepCycle` state, since the callback itself only fires after a commit, one instruction too late for a target of
exactly zero), returning a `SimPointCheckpointSet`. `MeasureSimPointCheckpoints` restores each of its checkpoints
into a fresh detailed train (built by a caller-supplied factory) and measures via `WarmupMeasureDriver`; per-point
warmup is clamped to `min(warmupInstructions, intervalStart)` so an early interval's measured window is never
shifted off the interval it represents. Per-point CPI (not IPC — SimPoint intervals are equal-length, so CPI is the
domain a weighted mean is valid in) is combined by `SimulationPoint.Weight` into a whole-program estimate.
`RunWithSimPointCheckpoints` is a thin wrapper calling both halves, kept for the single-config case.
`SimPointCheckpointSet` also carries the profiling pass's own `TotalInstructions` count (from `BbvProfiler`), so a
caller doesn't need a second, separate `ProfileSimPoints` call just to report it. The Runner exposes this as
`--simpoint-warmup <n>`, run once per `--sweep` config (skipping pipelines other than ooo/five_stage/single_cycle,
which are the only ones `ISteppableTrain.SnapshotDials`/baseline-`FinishStepping` support) — the `--sweep` loop calls
`CaptureSimPointCheckpoints` once and `MeasureSimPointCheckpoints` per config, instead of repeating the (expensive,
at SPEC-scale interval counts) profiling+capture pass for every config the way calling `RunWithSimPointCheckpoints`
in the loop used to, and the profile report reads its instruction count off that one capture instead of a second,
separate `ProfileSimPoints` call — exactly one profiling pass total, in every mode. On
`TestBinaries/simpoint_kernel.elf` (742 intervals) an 8-config sweep went from not finishing in 90 s to ~18 s.
Building the original glue surfaced a second real bug:
`Train.FinishStepping(baseline)` returned the *absolute* Escapement tick instead of ticks-since-baseline, which
`WarmupMeasureDriver`'s zero-warmup callers never noticed but inflates every per-point CPI once warmup > 0 — fixed
by recording the tick at `SnapshotDials()` and subtracting it in `FinishStepping(baseline)`.

**Validated against a real compiled binary (`TestBinaries/simpoint_kernel.c`/`.elf`).** `ProfileSimPoints` and
`RunWithSimPointCheckpoints` gained optional `argv`/`wordSize` parameters so their functional passes can inject a real
psABI initial stack (`InitialStackBuilder`) instead of only bare-metal entry — the first time the *sampling* machinery
itself has been checked directly against genuinely compiled code rather than a hand-assembled probe. This is now
wired into the CLI too: `--simpoint-argv "<args>"` opts a single `--simpoint`/`--simpoint-warmup` workload into
Linux-ABI entry (the psABI stack above, plus a `Func<IMechanism>` that builds a fresh `LinuxSyscallEmulator` on every
call — mirroring `--bench-config`'s factory shape, since the emulator carries mutable per-run state) instead of the
bare-metal HTIF entry every other mode uses; omitting the flag keeps that other-modes behavior byte-for-byte
unchanged. Captured output is discarded — this mode estimates CPI/IPC, not output; see `--bench-config` for
output-checked runs.
`Tests/RiscV64/System/RealLinkedSimPointTests.cs` uses `simpoint_kernel.elf` — static arrays (no `malloc`, so no
`brk`/`mmap`) with one `printf` at the very end — and an independent commit-trace pass to verify every *selected*
simulation point's warmup+measure window is syscall-free except the two edge phases (startup/shutdown), which always
contain ECALLs by construction. Building this
surfaced a third real, independent bug — not part of the checkpoint machinery, reproduced on a plain straight-through
`OooeTrain` run too: `LinuxSyscallEmulator`'s ECALL handler reads/writes guest memory through the same `IMemory`
`OooeTrain` uses for real loads/stores, and `ExecResult.HasLoadAccess`/`HasStoreCapture` were derived unconditionally
from that memory wrapper's flags — any memory-touching ECALL (`write`/`writev`'s buffer read, `fstat`/
`clock_gettime`/`getrandom`'s struct write) was misread as owning a load/store-queue entry it was never allocated,
corrupting `_lq`/`_sq` indexing (`IndexOutOfRangeException`). Fixed by routing System-class instructions through the
same direct, non-speculative `DLayers.Accessor` path Vector/UVE already use (they're equally head-serialized), rather
than through the deferred-write `CapturingMemory` wrapper. Two regression tests in `InitialStackTests.cs`, verified
via revert-and-recheck. This surfaced two further hazards, both since fixed. First, a younger load could issue before
a head-serialized ECALL that writes overlapping memory and read stale data — `HasPrecedingVectorStore`'s vector-store
precedent never added ECALL to its blocking set. A widened-race-window regression test (a long dependent add-chain
ahead of the ECALL, so the race is deterministic rather than luck-of-scheduling) confirmed this was a real,
reproducible bug, not the rare case an earlier probe's inconclusive pass had suggested. Fixed via
`ITooth.MayAccessArbitraryMemory` (true only for ECALL), checked alongside the vector-store case. Second, a separate,
pre-existing gap: `stdin_echo64.elf` under `OooeTrain` produced empty output, because ECALL's result is delivered via
`SideEffect` at Commit and `RvEcall` never has a `DestinationRegister`, so it never participates in the RAT/PRF at
all — a younger consumer of a0 could resolve to whatever produced a0 *before* the syscall. Fixed the same way as
Zacas `amocas.d`'s register-pair high half: `RvEcall.SecondaryDestinationRegister = 10` (a0), reusing the existing
class-agnostic `HasPendingSecondaryDest` dispatch stall and `CommitRegisters` PRF sync with no pipeline-stage changes.
Both fixes verified via revert-and-recheck in `InitialStackTests.cs`; see TODO.md.

**Full syscall-emulator-state checkpointing.** `ArchitecturalCheckpoint` only ever covered guest architectural
state (registers, memory, ISA blob) — a `LinuxSyscallEmulator`'s own mutable state (brk/mmap cursors, fd table,
stdin position, plus the deterministic clock/PRNG cursors) lives outside that and needed its own capture/restore
path so a checkpoint landing mid-syscall-emulation restores faithfully instead of resetting to a fresh handler.
`ICheckpointableSyscallHandler` (`Mechanism`) adds `WriteState(BinaryWriter)`/`ReadState(BinaryReader)`, which
`LinuxSyscallEmulator` implements: the fd table serializes path + access mode + current position per open fd
(reopened with `FileMode.Open`, never truncating, regardless of how the fd was originally created) and stdin
position is either `Seek`'d or fast-forwarded by discarding bytes depending on whether the caller's redirected
stream is seekable. `IMechanism` exposes the resolved handler as `SyscallHandler` (default `null`; `Rv32Mechanism`/
`Rv64Mechanism` read it off whichever `Rv32Executor` is currently wired in, since `Executor` is settable post-
construction). `Experiment.CaptureSimPointCheckpoints`/`MeasureSimPointCheckpoints` capture/restore this alongside
each point's `ArchitecturalCheckpoint`, in a new `SimPointCheckpointSet.SyscallStates` array (null at any index
whose mechanism has no checkpointable handler — e.g. bare-metal HTIF workloads). `Tests/RiscV32/System/
SyscallCheckpointTests.cs` proves the round-trip directly (brk continuation, a reopened fd landing at the right
position, stdin continuing past what a fresh stream over the same content already delivered, and getrandom/
clock_gettime continuing their sequence rather than repeating it — each checked against an independent reference,
not a self-consistent recomputation); `Tests/RiscV32/Analysis/SimPointCheckpointTests.cs` drives a hand-assembled
brk-extend-then-query program through `MeasureSimPointCheckpoints` itself and shows the query only sees the
extended break when the syscall state was actually restored, with a same-shape control that omits the restore and
gets the stale answer. `simpoint_kernel.elf`'s own ECALLs (`set_tid_address`/`ioctl`/`writev`/`exit_group`) turned
out to be state-inert w.r.t. every cursor tracked here — a static-array, no-`malloc` kernel, by design — so
`RealLinkedSimPointTests.cs` only proves the wiring fires against real compiled code (`SyscallStates` populated,
`MeasureSimPointCheckpoints` restores without error), not the semantic case; that's what the two unit-level tests
above are for. mmap-cursor and stdin-position serialization is exercised only at the unit level: the
`--simpoint-argv` CLI path always builds its `LinuxSyscallEmulator` with `mmapBase=0`/`input=null` (mmap disabled,
stdin always EOF) on both the capture and measure side, a separate pre-existing limitation of that specific CLI
wiring, so those two fields are currently serialized-but-unexercised there rather than validated end-to-end.

**Warm microarchitectural checkpoint (`MicroarchitecturalCheckpoint`, `OooeTrain.Drain`/`SaveMicroCheckpoint`/
`RestoreMicroCheckpoint`).** `ArchitecturalCheckpoint` above is enough to resume *correctly*, but a freshly-loaded
`OooeTrain` starts with cold caches/TLBs/branch predictor — the warmup a per-interval SimPoint sampling flow already
pays around it. This layers a second checkpoint on top that also carries the trained tables across, so a reload can
skip that warmup. It only applies at a **drained** boundary (no in-flight instructions anywhere in the pipeline) —
mirroring gem5's own `drain()`-before-`serialize()` precedent — because `RobEntry.SideEffect` is a raw
`Action<IArchState>` closure that cannot be generically serialized; at a drained boundary the ROB/issue
queues/load-store queues/decode-rename latches/exec-CDB buffers are all empty by construction, so there's nothing
closure-bearing left to capture. `OooeTrain.Drain(maxTicks)` stops admitting new fetches and steps until the
pipeline empties (or throws if it doesn't within `maxTicks`, or if the train halts first). `SaveMicroCheckpoint`
then writes an `ArchitecturalCheckpoint` plus tagged sections for each configured I/D cache (all levels), I/D TLB,
the branch predictor, and the RAS (both the speculative and committed-shadow copies) — each section is
length-prefixed and, for the branch predictor and each cache's replacement policy, additionally tagged with the
concrete type name, so a restore into a differently-configured train (missing a level, or a different
predictor/policy type) skips the mismatched section and cold-starts it instead of throwing or feeding it foreign
bytes. `RestoreMicroCheckpoint` must be called with the same `entryPoint` convention as `ArchitecturalCheckpoint`
(pass the checkpoint's PC to the constructor — `RestoreInto` only writes `ArchState.Pc`, not the pipeline's internal
fetch-PC latch). Caches/TLB/predictor gained `WriteState`/`ReadState` via the same default-no-op-then-override
pattern as `IArchState` (`IBranchPredictor`, `IReplacementPolicy`, `IValuePredictor`, `ICriticalityPredictor`);
`SetAssociativeCache`, `Tlb`, `NBitBp`, `StoreSetPredictor`, `SmbPredictor`, `RdipPrefetcher`,
`TokenPassingCriticalityPredictor` implement real bodies, as do all five `IValuePredictor` implementations
(`LvpVp`, `StrideVp`, `VtageVp`, `HybridVp`, `DynamicClassificationVp`) and eight `IReplacementPolicy`
implementations (`LruPolicy`, `FifoPolicy`, `MruPolicy`, `ClockPolicy`, `PlruPolicy`, the `RripPolicyBase`
family — `SrripPolicy`/`BrripPolicy`/`DrripPolicy` — `ShipPolicy`, and `HawkeyePolicy`) — `RandomPolicy`
(RNG-only state) and `RtlFfiReplacementPolicy` (native-owned state) cold-start by design, matching the same
FFI/RNG exclusion already established for the branch predictor side. Composed predictors (`HybridVp`,
`DynamicClassificationVp`) hold no state of their own beyond what they delegate to their two component
predictors' own `WriteState`/`ReadState`. Every `IBranchPredictor` implementation now has a real
`WriteState`/`ReadState` except `OracleBp` (trace-driven, no table), `StaticBp`'s stateless variants, and
`CbpFfiBp`/`CbpNgFfiBp`/`CbpNgCommitDrivenBp` (FFI-owned native state) — see the BP zoo section below for
the full breakdown. `FdipPrefetcher` is deliberately excluded entirely: it has no
trained table, only a lookahead FTQ that rebuilds itself within `ftqCapacity` cycles of `Wind()` regardless, so
there's nothing worth carrying over. `PhysicalRegisterFile`/`RenameMap` are deliberately not serialized: at a
drained boundary they carry no information the architectural register values (already covered by
`ArchitecturalCheckpoint`, restored into the PRF by the existing `Wind()` re-seed) don't already reconstruct.
`MSHR`/write-back-buffer/victim-buffer/in-flight-prefetch state in `SetAssociativeCache` is not serialized either —
`WriteState` throws if any of it is non-empty at save time, rather than silently dropping it, since a proper drain
leaves it empty for every config this covers today.

**The key design rule for the OoO-side predictors** (found while extending past the vertical slice): serialize
only tables keyed by PC, address, or other *content*; skip anything keyed by, or compared against, a monotonic
per-train counter (InstrId/SeqNo/commit count) — those restart at 0/1 on a freshly restored train, so a
carried-over counter value can never match again. `StoreSetPredictor`'s SSIT (PC → store-set) is safe and
serialized; its LFST (keyed by store `SeqNo`) is deliberately left cold — at a drained boundary every tracked
store has already issued, so a fresh (all-zero) LFST is the *correct* state, not an approximation, and carrying a
stale SeqNo over could stall a load's dependence prediction forever. `TokenPassingCriticalityPredictor` follows
the same rule: only its PC-indexed hysteresis table (`_cpTable`) is serialized; its ROB-slot/token/commit-counter
bookkeeping is not. `SmbPredictor` (distances are *relative* SSN deltas, not absolute SeqNos) and `RdipPrefetcher`
(everything is keyed by call-stack signature or physical address) needed no such split — both serialize wholesale.
`StrideVp`'s `_inFlight` (a per-PC renamed-but-uncommitted occurrence counter, incremented at predict/rename and
decremented at update/commit) follows the same category and is skipped — verified, not just asserted, by a
temporary instrumented build that logged any nonzero entry inside `WriteState`, producing no output across its
equivalence test. The rule has a real trap, though: `DynamicClassificationVp`'s `_armed`/`_missStreak` fields
*look* like the same kind of transient counter but aren't — `Update` (the commit-time call) never clears them,
only eviction/reclassification/a squash do, so a PC's "has this component ever predicted confidently since
classification" bit is real trained state that must round-trip, not a per-instance in-flight depth that resets
every commit. Both are serialized. Hawkeye's `_absTime`/`_absLineTime` counters look similar to the SeqNo case at
a glance but are safe to serialize wholesale for a different reason: they're self-referential (every comparison
is between values produced and restored by the same policy instance, never checked against another component's
independently-resetting counter), the same property that already let `SmbPredictor`'s relative deltas serialize
wholesale.

**The BP zoo (complete).** Every equivalence test up to this point used `NBitBp` or a stateless
`AlwaysNotTaken` predictor, neither of which carries global-history state — so before this pass, history
serialization through a *live pipeline* (as opposed to a standalone unit-level round trip) had never
actually been exercised, which is exactly the kind of thing a counter-keyed bug hides in.
`SpeculativeGlobalHistory`/`SpeculativeLocalHistory` — the shared speculative/committed history-shadow
helpers most predictors in `Mechanism.BranchPred` are built on — gained `WriteState`/`ReadState` once
(mirroring RAS/CRAS and `VtageVp`'s pair), benefiting every predictor built on them for free.

Work started scoped to three representative families (TAGE via `LTageBp`, perceptron via
`HashedPerceptronBp`, local/global-hybrid via `TournamentBp`) — a user decision to avoid a multi-session
exhaustive grind — but a follow-up ask ("do them now since it's relevant") extended it to every remaining
predictor. `LTageBp.WriteState`/`ReadState` (made `virtual`) serializes the bimodal base, tagged TAGE
tables, loop predictor, BTB, and both history shadows; per its own `CaptureHistory` doc comment, every
folded-history index is recomputed on the fly from the raw GHR, so restoring the raw register alone keeps
every derived index consistent. Every class that extends `LTageBp` or `TageScLBp` — `TageScLBp` itself
(the Statistical Corrector, also the base of eight other predictors), `BatageBp`, `BullseyeBp`,
`MultiperspectivePerceptronBp`, `LlbpBp`/`LlbpXBp`, `TeaBp`, `LvcpBp`, `RunltsBp`, `VlaTageBp` — overrides
`WriteState`/`ReadState` to call `base` first (the inherited TAGE/loop/history state) and then add only
its own layered tables. Every remaining standalone predictor also got a real implementation:
`PerceptronBp`, `CorrelatedBp`/`GselectPredictor`/`GshareBp`, `IttagePredictor` (ITTAGE — confirmed
wireable as a standalone `IBranchPredictor`, per its own doc comment, before including it), and
`ImliPredictor`. Two composed-baseline predictors needed individual attention rather than the established
pattern, exactly as flagged before starting: `BranchNetBp` holds a `TageScLBp` *field* rather than
extending it, so its `WriteState` delegates to `_baseline.WriteState` explicitly instead of calling
`base`; its per-branch CNN models are an offline-training artifact frozen at construction (the same
"shape, not state" category as `TeaBp`'s dependence chains) and are not serialized. `HypreBp`
(hyperdimensional/sparse-distributed-memory) is a genuinely different data structure — HD vectors
(a saturating-counter array plus a derived sign-bit array) rather than any table shape used elsewhere in
this codebase.

Only two predictors needed their own full pipeline equivalence test, rather than a unit-level round trip:
`LTageBp` (an alternating-parity branch pattern — `beq` on a loop counter's parity bit — a plain bimodal
counter can never learn but a history-indexed predictor can, forcing real tagged-table allocations before
the checkpoint) and `ImliPredictor` (the one standalone predictor with genuine speculative-vs-committed
*counter* semantics, distinct from the history-*register* case `LTageBp` already proves — it reuses the
same alternating-parity program, since the outer loop branch is itself a real backward taken branch
driving the IMLI counter and the inner branch's PHT index depends on it directly). Every other predictor
gets a unit-level round trip using a shared generic helper: train with a varying, biased pattern: trained
must equal restored, *and* trained must be distinguishable from a fresh cold instance (the anti-theater
check). The bias direction and iteration count aren't universal — different predictors default cold to
different directions (all-zero-weight perceptrons default "taken"; saturating counters initialized
"weakly not-taken" default the other way), and `HypreBp`'s 1024-bit HD vectors need hundreds of
repetitions to move their Hamming-distance threshold, not the two dozen every other predictor needs — both
discovered by running the anti-theater assertion and watching it fail, not by reasoning it through in
advance.

`Tests/Pipeline/MicroCheckpointTests.cs` has per-table round trips (where the type is `public` and cheap to
construct standalone) plus equivalence tests: drain a running train mid-program, save, and continue it as the
reference (`Drain` never discards in-flight work, only delays new fetches by a few cycles, so this is a faithful
continuation); separately reload the checkpoint into a fresh train and run to completion. The main D-cache/
predictor/RAS equivalence test asserts final architectural state, ticks, retired-instruction count, cache miss
count, and branch-misprediction count match exactly; cache hit count is allowed a ±1 tolerance for wrong-path
(later-squashed) memory accesses right at a branch-resolution boundary, whose exact count depends on cycle-exact
pipeline occupancy that a drained-boundary checkpoint doesn't claim to preserve — confirmed by direct tag/data/age
snapshot comparison that the restored cache table itself is bit-identical at the checkpoint instant. The
`StoreSetPredictor` equivalence test is deliberately adversarial (a store address delayed behind a long dependency
chain racing an immediately-ready load address, to reliably trigger a real memory-order violation) and needs its
own small tolerance on ticks/violation count for the same wrong-path-timing reason — but asserts `retired` matches
exactly and that the divergence stays bounded rather than scaling with remaining loop iterations, which a genuine
stale-SeqNo stall bug would do. `StoreSetPredictor` and `SmbPredictor` are `internal`, so their equivalence tests
are the only reachable verification for them (no unit-level round trip is possible from the test project).
`StrideVp` (a genuinely exercisable equivalence case — a monotonic-counter loop, the pattern its own doc comment
builds and measures against, since `LvpVp`/value-repetition predictors can never predict it), `LTageBp`, and
`ImliPredictor` get a full drain/save/restore/continue pipeline equivalence test each; every other newly-covered
predictor/policy/value-predictor gets a unit-level (optionally history/cold-baseline-checked) round trip. 55
tests total.

**Runner CLI wiring**: `--checkpoint-save-micro <path>`/`--checkpoint-load-micro <path>`, mirroring the
architectural-only `--checkpoint-save`/`--checkpoint-load`. Both require `--script` and an OoOE pipeline train —
a non-OoOE train prints a warning and skips the save (or falls back to an architectural-only restore on load)
rather than throwing. The load side has to parse the checkpoint's `Architectural.Pc`/memory geometry *before*
`spec.Build()` constructs the train (an `OooeTrain`'s fetch PC is fixed at construction, same convention as
`ArchitecturalCheckpoint`), so it reads the file into a `MemoryStream` once and replays it into
`RestoreMicroCheckpoint` rather than re-reading from disk twice. The save side drains at the simplest possible
trigger — the end of the run — since the CLI has no way to name a mid-run boundary; a run that reached program
exit (rather than being cut short by `--max-ticks`) has already halted with nothing left to drain, so that case
prints a message and skips rather than crashing on `Drain`'s `InvalidOperationException`. Manually smoke-tested
end-to-end (save mid-run, reload into a fresh train, confirm ticks/PC/register/cache-hit continuity) rather than
covered by an automated Runner-process test — no existing test in this repo spawns the Runner CLI as a
subprocess, and the underlying `Drain`/`SaveMicroCheckpoint`/`RestoreMicroCheckpoint` API this wiring calls is
already covered by the 55 tests above.

Not yet done: nothing outstanding for the Option B predictor/policy surface — every `IValuePredictor`,
`IReplacementPolicy`, and non-excluded `IBranchPredictor` implementation now has a real checkpoint. The
Runner CLI wiring above is the only remaining entry point that isn't covered by an automated test (manual
smoke-test only — see its own paragraph for why).

### Instruction trace output (Olympia, RiscV32/Trace)

`Experiment.WriteOlympiaTrace(workload, mechanism, output)` runs the workload functionally on the single-cycle train (
via `OlympiaJsonTraceWriter`, an `ICommitObserver`) and emits
an [Olympia](https://github.com/riscv-software-src/riscv-perf-model)-compatible JSON instruction trace — one object per
retired instruction with `mnemonic`, `rs1`/`rs2`/`rd`, `csr`, and `vaddr` (loads/stores). The CLI exposes it as
`--trace-json <path>`. It reuses `RvDisassembler` for the mnemonic, `ITooth` for registers, and a `TracingMemory`
wrapper for the effective address. Olympia is trace-driven (it replays the stream through its timing model without
functional execution), so this is the prerequisite for *timing* co-simulation against the Sparta-based RISC-V
performance model the project is patterned on — distinct from the *functional* Spike co-sim, which verifies ISA
correctness.

Olympia (and its Sparta framework) are packaged by the flake from source — `nix/{softfloat,sparta,olympia}.nix`, exposed
as `packages.{softfloat,sparta,olympia}` and on PATH inside `nix develop`. End to end:

```bash
dotnet run --project src/Apps/Runner -- TestBinaries/rich.elf --trace-json trace.json
nix run .#olympia -- trace.json --report-all report.txt   # IPC / cycles / retired in report.txt
```

### Elastic DDG trace (HELF format, RiscV32/Trace)

`Experiment.WriteElasticTrace(workload, mechanism, output)` records a **dynamic dependence graph** (DDG) trace in the
Horologium-native `HELF` binary format. Each committed instruction becomes one record containing its sequence number,
PC, raw encoding, instruction type (COMP/LOAD/STORE), estimated computation latency (`compDelay` from
`FuLatencyConfig.Default`), effective address and access size (loads/stores), register RAW producer seqnos (`robDeps`),
and memory RAW producer seqnos (`addrDeps`).

Register dependences track the unified 0–63 namespace (0–31 = integer, 32–63 = FP) and vector registers v0–v31
separately. Memory dependences map effective address → most-recent store seqno. `TracingMemory` supplies the effective
address; the vAddr gate is class-based (not `HasAccess`) to avoid conflating instruction-fetch addresses with data
addresses.

```bash
# Record a DDG trace
dotnet run --project src/Apps/Runner -- prog.elf --elastic-record prog.helf

# Replay the critical-path dataflow DAG and print IPC upper bound
dotnet run --project src/Apps/Runner -- --elastic-replay prog.helf
# → Elastic replay: 1234567 instructions, 890123 cycles (critical path), IPC upper bound = 1.386
```

`ElasticTraceReplayer.Replay(records)` computes the critical-path length: for each instruction,
`completionTime = max(completionTime[dep] for dep in robDeps ∪ addrDeps) + compDelay`. The result is a **dataflow IPC
upper bound** — infinite issue width, no structural hazards; real-hardware IPC will be lower.

**gem5 Protobuf translators** convert HELF to the two trace files gem5 TraceCPU requires. Fields and framing are
verified against `gem5/src/proto/inst_dep_record.proto`, `packet.proto`, and `protoio.cc`:

```bash
# Convert HELF → gem5 inst_dep_record.proto stream (dataTraceFile)
dotnet run --project src/Apps/Runner -- --elastic-to-gem5 prog.helf prog.gem5data

# Convert HELF → gem5 packet.proto fetch-trace stream (instTraceFile)
dotnet run --project src/Apps/Runner -- --fetch-to-gem5 prog.helf prog.gem5fetch

# Replay via gem5 TraceCPU (gem5 must be on PATH; see nix build .#gem5)
gem5 gem5-scripts/trace_cpu_riscv.py \
    --data-trace-file prog.gem5data \
    --inst-trace-file prog.gem5fetch
```

`Gem5ElasticTraceConverter` produces the `dataTraceFile`: LE magic `0x356d6567` + varint32-length-prefixed
`InstDepRecordHeader` then one `InstDepRecord` per committed instruction. `Gem5FetchTraceConverter` produces the
`instTraceFile`: same framing, `PacketHeader` + one `Packet` per instruction (cmd=ReadReq, addr=PC, size=4,
flags=INST\_FETCH). The fetch trace is an approximation: one 4-byte read per committed instruction, not the wrong-path
cache-line fetches a real O3 CPU would generate.

### STF binary trace (Sparcians stf\_lib format, RiscV32/Trace)

`Experiment.WriteStfTrace(workload, mechanism, output)` records a **Simulation Trace Format** binary trace. STF is the
native trace format of [Olympia](https://github.com/riscv-software-src/riscv-perf-model) and
the [Sparcians stf\_lib](https://github.com/sparcians/stf_lib). Unlike the JSON Olympia trace, the STF output includes *
*operand values** for integer and floating-point registers (feature flag `STF_CONTAIN_OPERAND_VALUE`), memory access
addresses and data, and taken-branch targets.

```bash
# Record an STF trace
dotnet run --project src/Apps/Runner -- prog.elf --stf-record prog.stf

# Replay through Olympia (stf_lib-based tools: stf_dump, stf_check, etc.)
# nix run .#olympia -- --input-file prog.stf ...
```

Format details: version 1.5, ISA=RISCV, IEM=RV32. Per-instruction record group (in ascending descriptor order):
`STF_INST_PC_TARGET` (taken branches only) → `STF_INST_REG` source records → `STF_INST_REG` dest record →
`STF_INST_MEM_ACCESS` + `STF_INST_MEM_CONTENT` (loads/stores/atomics) → `STF_INST_OPCODE32/16` (instruction boundary).
FP registers are tracked via the unified integer+FP register file (indices 32–63 = f0–f31). Vector register records are
omitted since VRF values are not accessible through `IArchState`. Generator: `STF_GEN_RESERVED` (0).

### ChampSim trace replay (RiscV32/Trace)

`ChampSimTraceReader`/`ChampSimTraceReplayer` replay a [ChampSim](https://github.com/ChampSim/ChampSim) binary trace —
the baseline format for the CBP (branch prediction) and CRC (cache replacement) competitions — through Horologium's
`IBranchPredictor` and `IReplacementPolicy` surfaces, so competition submissions can be cross-checked against real
trace corpuses rather than only Horologium-generated workloads. This is a standalone replay: it needs no ELF workload,
since a ChampSim trace already carries the full dynamic instruction/branch/memory stream.

```bash
# Evaluate a built-in predictor and cache policy against a trace (raw or gzip)
dotnet run --project src/Apps/Runner -- --champsim-trace bzip2.trace.gz \
    --champsim-predictor tage_sc_l --champsim-cache-policy Ship

# Evaluate a native CBP-3/5 plugin instead (see native/CbpShim)
dotnet run --project src/Apps/Runner -- --champsim-trace bzip2.trace.gz --champsim-cbp-lib ./libpredictor.so

# Skip cache evaluation
dotnet run --project src/Apps/Runner -- --champsim-trace bzip2.trace.gz --champsim-cache-policy none
```

Format details: a dense stream of 64-byte `input_instr` records (`inc/trace_instruction.h`) — `ip` (u64), `is_branch`/
`branch_taken` (u8 each), 2 destination + 4 source register indices (u8 each), then 2 destination + 4 source memory
addresses (u64 each, 0 = unused slot). Little-endian, no header; `.gz`-compressed streams are decompressed
transparently, `.xz` must be decompressed externally first. ChampSim traces carry no static decode information, so a
branch's actual target is taken to be the next record's `ip` — the same inference ChampSim's own `tracereader.h` uses
— meaning the final branch in a trace is unscored. Cache replay runs against a synthetic `ChampSimBackingMemory` (data
is irrelevant to hit/miss accounting); loads come from `source_memory` slots, stores from `destination_memory` slots.

### RTL unit substitution (native/RtlFu, Mechanism/RtlFu)

Individual pipeline units can be swapped for cycle-accurate RTL driven through
[Verilator](https://www.veripool.org/verilator/), so the surrounding pipeline exercises a synthesizable design instead
of the C# model for that unit — useful for validating a custom design against the rest of the system before
tapeout/FPGA. Two unit kinds so far:

- **Functional units**: `RtlBackedExecutor` decorates the ISA executor, and instructions an ISA-side selector claims
  execute on the verilated model — the RTL result becomes the register write, and the model's observed cycle count
  becomes the instruction's FU latency (`ExecuteResult.LatencyOverride`, honored by the `ooo` and `cpr` pipelines in
  place of the static `FuLatencyConfig` entry). Decorators compose, so multiple selectors can each claim their
  instruction class. The first unit is a Chisel sequential restoring divider with early termination
  (`native/RtlFu/DivUnit.scala`, selector `RvRtlDiv`: DIV/DIVU/REM/REMU), so div latency is data-dependent — 1 cycle
  for the RISC-V special cases, up to 33 for a full-width dividend. The second is a fully pipelined 3-stage 33×33
  multiplier (`native/RtlFu/MulUnit.scala`, selector `RvRtlMul`: MUL/MULH/MULHSU/MULHU) under the same port contract
  with `req.ready` constantly high; its constant 3-cycle latency equals the `MulDivLatency` default, so an RTL-mul
  run is cycle-identical to the static model. `rtl_div_lib` and `rtl_mul_lib` (sweep JSON) chain to substitute the whole
  M extension. The third is an FP divide/square-root unit (`native/RtlFu/FDivSqrtUnit.scala`, `rtl_fdiv_lib`):
  iterative IEEE binary32 FDIV.S/FSQRT.S with full subnormal support, round-to-nearest-even, canonical NaNs, and
  exception flags — carried across the FFI by a flags-reporting shim variant (`rtl_fpu_shim.cpp`,
  `rtl_execute_flags`) and delivered by the ISA-side `RvRtlFpExecutor` decorator, which replicates the §11.3
  NaN-boxing check and ORs the RTL's flags into the fflags CSR through the same SideEffect path the C# model uses.
  The differential sweep demands bit-identical NaN-boxed results *and* bit-identical fflags across IEEE specials,
  subnormals, rounding edges, and random bit patterns — including the C# model's flag quirks (overflow raises OF
  without NX; UF requires an inexact nonzero subnormal). Latency is data-dependent: 1 cycle for special cases, ~30
  for the iterative paths, vs. the static `FloatDivSqrtLatency` default of 16.
- **Branch predictors**: two shim ABIs, auto-detected by `RtlBranchPredictorLoader` so one flag/config serves both.
  `RtlFfiBranchPredictor` wraps a plain predictor — combinational predict at fetch, one-clock-edge update at commit;
  speculative-history hooks stay at their interface defaults (committed history only, like `CbpFfiPredictor`); the
  first is a Chisel gshare (`native/RtlFu/GshareBp.scala`) mirroring the C# `GsharePredictor` bit-for-bit.
  `RtlFfiHistoryBranchPredictor` wraps a predictor that manages its own speculative global history in RTL, carrying
  the full `IBranchPredictor` contract across the FFI — fetch-time history folds, flush recovery, and per-branch
  checkpoints for OoO partial squashes, where the checkpoint is the model's working-history value itself (TAGE-family
  folded indices derive from it, so a single value is a complete snapshot and no checkpoint RAM is needed); the first
  is a Chisel L-TAGE (`native/RtlFu/LTageBp.scala`: bimodal base, four tagged tables with geometric 8/13/21/34-bit
  folded histories, and a loop-predictor overlay) mirroring the C# `LTagePredictor` bit-for-bit — the differential
  test replays fetch/commit/flush/partial-squash sequences demanding identical predictions and identical checkpoints,
  and an OoO nested-loop run is cycle-for-cycle identical to the C# predictor. Selectable per sweep config
  (`{"type": "rtl_bp_plugin", "library_path": ...}`) or as the evaluated predictor of a `--champsim-trace` replay
  (`--champsim-rtl-bp-lib`).
- **Cache replacement policies**: `RtlFfiReplacementPolicy` implements `IReplacementPolicy` over a verilated policy —
  combinational victim selection with the aging write-back, hit promotion, and fill insertion each consuming one clock
  edge. Geometry is fixed at Chisel elaboration and exposed through the model (`io_cfgSets`/`io_cfgWays`); the policy
  attaches only to cache levels whose sets×ways match, others keep the configured C# policy kind. The first policy is
  a Chisel SRRIP (`native/RtlFu/SrripRp.scala`, default 64 sets × 4 ways) mirroring the C# `SrripPolicy` bit-for-bit —
  the differential test demands identical victims and RRPV metadata, and a cache-level test demands identical hit/miss
  counts from `SetAssociativeCache` under either policy. The second is a Chisel DRRIP (`native/RtlFu/DrripRp.scala`):
  the SRRIP base plus Set Dueling — SDM leader sets, a 10-bit PSEL duel, and 1/32 bimodal BRRIP inserts — mirroring
  the C# `DrripPolicy`, demonstrating global cross-set state (PSEL, shared bimodal counter) through the unchanged shim
  ABI; its differential stream phases between SDM-heavy and follower-heavy set biases so the duel swings both ways.
  Selectable per sweep config (`"rtl_cache_policy_lib"`) or as the evaluated policy of a `--champsim-trace` replay
  (`--champsim-rtl-rp-lib`; strict geometry check against the `--champsim-cache-*` flags).
- **Cache prefetchers**: `RtlFfiPrefetcher` implements `IPrefetcher` over a verilated prefetcher, with two shim
  shapes sharing one C ABI. Single-target models (`rtl_pf_shim.cpp`): each demand access is presented once, the
  prefetch decision is read combinationally (post-update semantics live in the model), and the prediction-table
  update commits on one clock edge — the first is a Chisel RPT stride prefetcher (`native/RtlFu/StridePf.scala`,
  64 PC-indexed entries with a 64-bit datapath) mirroring the C# `StridePrefetcher` bit-for-bit. Multi-degree models
  (`rtl_mpf_shim.cpp`): the access edge loads an internal drain queue and the shim pops one address per clock — the
  first is a Chisel Jouppi stream-buffer prefetcher (`native/RtlFu/StreamPf.scala`, 4 streams × depth 8 with LRU
  allocation) mirroring the C# `StreamPrefetcher`, whose allocation burst issues 8 lines from a single access; its
  differential test interleaves more sequential walkers than there are stream buffers so LRU eviction is exercised,
  demanding identical counts and target sequences. Selectable per sweep config (`"rtl_prefetcher_lib"`).

Port contracts and C ABIs for wrapping further units are documented in `native/RtlFu/README.md`. Desktop-only
(`NativeLibrary`), like the CBP FFI predictors. All four surfaces are also reachable from `.csx`/`.fsx` architecture
scripts (the script hosts pre-import the RTL namespaces): `BranchPredictorFactory` on a pipeline spec,
`RtlBackedExecutor` around the mechanism factory's executor, and per-level `PolicyFactory`/`PrefetcherFactory` on
`CacheLevelSpec` — see `scripts/example-rtl.csx`, and `examples/rtl-machine/` for a self-contained
bring-your-own-RTL project: a custom Chisel ALU and branch predictor wired into an OoO train by an F# script whose
inline selector defines the instruction→opcode mapping, with no C#-side changes.

```bash
# Verilate the Chisel units, then drive all four surfaces from the pipeline
native/RtlFu/build.sh native/RtlFu/generated/DivUnit.sv DivUnit /tmp/rtl_div.so
native/RtlFu/build.sh native/RtlFu/generated/MulUnit.sv MulUnit /tmp/rtl_mul.so
native/RtlFu/build.sh native/RtlFu/generated/GshareBp.sv GshareBp /tmp/rtl_gshare.so rtl_bp_shim.cpp
native/RtlFu/build.sh native/RtlFu/generated/SrripRp.sv SrripRp /tmp/rtl_srrip.so rtl_rp_shim.cpp
native/RtlFu/build.sh native/RtlFu/generated/StridePf.sv StridePf /tmp/rtl_stride.so rtl_pf_shim.cpp
# ...then name them per sweep config in the JSON spec:
#   {"predictor": {"type": "rtl_bp_plugin", "library_path": "/tmp/rtl_gshare.so"},
#    "rtl_cache_policy_lib": "/tmp/rtl_srrip.so", "rtl_prefetcher_lib": "/tmp/rtl_stride.so",
#    "rtl_div_lib": "/tmp/rtl_div.so", "rtl_mul_lib": "/tmp/rtl_mul.so", "rtl_fdiv_lib": "/tmp/rtl_fdiv.so"}
dotnet run --project src/Apps/Runner -- prog.elf --sweep rtl.json
```

## Co-simulation contract

Spike is the reference of record for ISA correctness. The contract: **every change to the decoder, executor,
register/CSR/trap state, or any train's commit path must keep `SpikeCoSimTests` green.** Those tests run all three
trains (`SingleCycleTrain`, `FiveStageTrain`, `OooeTrain`) against `test.elf`, `rich.elf`, and `htif.elf`, comparing
every committed instruction's PC, encoding, and integer register writes to Spike commit-for-commit (see *Spike lock-step
co-simulation* above). A green run means the simulated datapath agrees with a real RISC-V reference
instruction-by-instruction — the strongest correctness signal in the project.

Beyond the three hand-written fixtures, `SingleCycle_Conformance_MatchesSpike` co-simulates all 71 official
`riscv-tests` `rv32ui`/`rv32um`/`rv32ua`/`rv32uc`/`rv32uf` ELFs (already shipped under `TestBinaries/isa/`)
commit-for-commit — per-instruction verification on top of the self-checking `RiscVTestSuiteTests`, which only inspect
the final `gp` pass code. (`ma_data` is excluded: it tests misaligned access, which Spike traps and a handler fixes up
while Horologium's `FlatMemory` permits directly, so the two diverge by design.)

If you add an instruction, extension, pipeline behaviour, or fixture, add or extend a co-sim fixture so the new path is
covered, and run:

```bash
dotnet test Tests/ --filter "FullyQualifiedName~SpikeCoSim"                            # run the co-sim contract
HOROLOGIUM_REQUIRE_COSIM=1 dotnet test Tests/ --filter "FullyQualifiedName~SpikeCoSim" # CI mode: missing toolchain → failure
```

**Toolchain.** The tests need `spike` and `dtc`; the Nix dev-shell (`flake.nix` + `direnv`) provides both. When the
toolchain is absent the tests **skip** rather than fail, so the suite stays runnable everywhere. Set *
*`HOROLOGIUM_REQUIRE_COSIM=1`** to flip a missing toolchain into a hard failure so the contract cannot be satisfied by
silently skipping. The GitHub Actions workflow (`.github/workflows/ci.yml`) enforces exactly this on every pull request
and every push to `trunk`, running the full suite (benchmarks excluded) in the flake's lightweight `ci` dev shell.
ISA-correctness coverage that does *not* need Spike (e.g. HTIF termination, the official `riscv-tests` self-checks)
lives in `HtifExitTests` / `RiscVTestSuiteTests` and always runs.

> Note: Spike sees only the standard ISA. UVE and other custom extensions are invisible to it, so their correctness is
> covered by Horologium's own integration tests, not co-sim.
