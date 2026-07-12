# Horologium

[![CI](https://github.com/lumikalt/Horologium/actions/workflows/ci.yml/badge.svg)](https://github.com/lumikalt/Horologium/actions/workflows/ci.yml)

[▶ Face demo](docs/face.mp4)

A discrete-event CPU pipeline simulator written in C# targeting .NET 11. The simulation engine is ISA-agnostic; concrete ISAs are plugged in as separate assemblies without modifying the engine. The primary goal is comparing hardware configurations (branch predictors, caches, pipelines) and generating measurement data for analysis.

## Projects

| Project | Purpose |
|---|---|
| **Orrery** | The simulation engine. Knows nothing about instructions or ISAs. |
| **Mechanism** | Interfaces only. Defines the ISA-plugin contract. |
| **Pipeline** | ISA-agnostic pipeline trains (`SingleCycleTrain`, `FiveStageTrain`, `SuperscalarTrain`, `OooeTrain`, `SmtTrain`), pipeline registers, `HazardUnit`, and stage implementations. No dependency on any ISA. |
| **RiscV32** | RV32IMAFDCV implementation of the Mechanism contract. Includes Zba/Zbb/Zbc/Zbs/Zicond/Zawrs/Zicbom/Zicboz/Zimop/Zcmop/Zicntr and UVE. |
| **RiscV64** | RV64IMAFDAC implementation extending RiscV32 via inheritance. Adds W-suffix ops (ADDW/SUBW/…/ADDIW/…), LD/LWU/SD, LR.D/SC.D/AMO*.D doubleword atomics, C.LD/C.SD/C.ADDIW/C.LDSP/C.SDSP compressed quadrant reassignments, RV64-only Zba (ADD.UW/SH1-3ADD.UW/SLLI.UW) and 64-bit-width overrides of the inherited Zbb/Zbs immediate and register-form ops (6-bit shift-amount mask included), and corrects shift/comparison/LW semantics for 64-bit. Includes an ELF64 loader and an Sv39 page-table walker (satp held in a dedicated 64-bit-wide RV64 CSR rather than RiscV32's 32-bit `CsrFile`, to hold the Sv39 MODE field). |
| **Chip8** | A second ISA implementation, demonstrating that the engine is genuinely ISA-agnostic. Full display (64×32 XOR-sprite framebuffer) and 16-key keyboard support. |
| **Subleq** | SUBLEQ OISC implementation. One 12-byte instruction, no register file. Validates that the Mechanism contract accepts the simplest possible ISA. |
| **Pdp8** | PDP-8 (1965) 12-bit accumulator machine. Eight opcodes: AND, TAD, ISZ, DCA, JMS, JMP, IOT, OPR. Full Group 1/2 micro-operations (CLA, CLL, CMA, CML, RAR/RTR, RAL/RTL, BSW, IAC, SMA/SZA/SNL with RSS complement mode). Page-zero and current-page addressing, indirect access, auto-increment (words 8–15). |
| **J1** | J1 Forth (James Bowman, 2010) 16-bit stack machine. Fixed 16-bit instruction width, four instruction types (Literal, Jump, CondJump, ALU). 32-entry data stack (T/N) and return stack (R), full ALU encoding (16 T' selectors, T→N, T→R, N→[T] store, 2-bit DDelta/RDelta). Implements `DUP`, `DROP`, `SWAP`, `OVER`, `+`, `AND`, `OR`, `XOR`, `INVERT`, `=`, `<`, `U<`, `@`, `!`, `>R`, `R>`, `R@`, `EXIT`, and countdown loops. |
| **Move** | TTA/MOVE (Transport Triggered Architecture, Corporaal 1995) 16-bit machine. Fixed 32-bit instruction format `[dst|src|imm]`. Computation is a side effect of transport: writing to a trigger port (`alu.in2`, `mem.load`, `mem.store`, `br.target`) fires the FU. Register file r0–r7; ALU FU (16 operations: ADD/SUB/AND/OR/XOR/NOT/SHL/SHR/SRA/EQ/LT/ULT/NEG/INC/DEC/COPY); memory FU (16-bit word loads and stores); branch FU (conditional redirect). `SingleCycleTrain` only — FU state is not exposed as register hazards. |
| **F18A** | GreenArrays GA144 F18A (2010) 18-bit stack computer. 29 opcodes packed four-per-word (5+5+5+3 bits) using the canonical GA144 encoding (0x00–0x1F). Includes `-if` (MinusIf 0x07: branch when T≥0) and `+*` (MulStep 0x10: shift-and-add multiply step). Data stack (T/S/8-deep) and return stack (8-deep); A and B address registers; 9-bit word-addressed PC (P). Canonical `if` semantics: branch when T==0 (false). Per-node memory: 64-word RAM, 64-word ROM, 256-word port space. Inter-node communication via synchronous `RendezvousArbor` channels (transfer completes only when both sides participate in the same tick). `F18AGrid` coordinates a rows×cols array of nodes; each node runs `SingleCycleTrain`; `F18AGrid.Step()` pre-checks `WillBlock` before driving decode→execute→commit. First multi-core ISA in the engine. |
| **Face** | Avalonia desktop UI. Opens with an **ISA launcher** so the user picks RISC-V or CHIP-8 before entering the appropriate view. The RISC-V side includes a workload preset picker, a **PEvents tab** with a scrollable Argos-style pipeline waterfall (rows = instructions, columns = cycles, cells = stage abbreviation F/DC/D/IS/EX/RT/FL), a **SpecPC** gutter column showing the fetch-window start address, flush/misprediction cycles highlighted red, fetch-stall cycles dimmed, and an **Assembler tab** with a three-pane RISC-V assembly editor (editor + decoded listing + register file). The Assembler tab has a sidebar **language toggle (RISC-V ASM / C)**: in C mode the source is compiled with `riscv32-none-elf-gcc` (selectable `-O` level) against a tiny `_start` stub, the resulting `.text` is disassembled into the listing, and single-cycle stepping highlights the current C source line via `objdump -dl` line info. The CHIP-8 side renders the 64×32 pixel framebuffer at 10× scale with a 60 fps game loop, keyboard input (QWERTY layout mapped to the CHIP-8 hex keypad), and ROM load/start/pause/reset controls. |
| **Runner** | Console entry point. Runs ELF binaries under named hardware configurations and emits results as Markdown or CSV. Accepts `--script <file.csx>` to evaluate a C# script that returns a `MachineSpec` and run the workload against it. |
| **Script** | C# and F# scripting host. `ScriptHost.EvaluateFileAsync(path)` compiles and runs a `.csx` (Roslyn) or `.fsx` (F# Interactive) file returning a `MachineSpec`, with all Spec/Cache/RiscV32 namespaces pre-imported and assemblies pre-referenced — no `#r` or `using`/`open` needed in the script. |
| **Tests** | xUnit tests. Engine tests under `Tests/Orrery`, `Tests/Pipeline`, `Tests/Mechanism`; small-ISA and RV64 tests under `Tests/Isa` (`Tests/Isa/RiscV64`); RV32 tests under `Tests/RiscV32/{Isa,Extensions,Pipelines,MultiHart,System,CoSim,Analysis}`. |

Projects live under `src/`: the ISA-agnostic core in `src/Core/`, ISA plugins in `src/Isa/`, and applications in `src/Apps/`.

## Commands

```bash
dotnet build                                                    # build the whole solution
dotnet test                                                     # run all tests
dotnet test --filter "FullyQualifiedName~DecoderTests"          # one test class
dotnet test --filter "Name=SpecificTestMethod"                  # one test method
dotnet run --project src/Apps/Runner                                     # run the console entry point
dotnet run --project src/Apps/Runner -- --help                           # CLI usage
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

All simulation activity is driven by the `Escapement` (`src/Core/Orrery/Scheduling/Escapement.cs`), a single-threaded priority-queue discrete-event scheduler keyed by `(tick, phase)`. Nothing in the simulation happens except by the Escapement scheduling it.

Within a single tick, events execute in a fixed phase order. Pipeline stages depend on this ordering as a contract:

```
Fetch(0) → Dispatch(1) → Issue(2) → Execute(3) → ArborUpdate(4) → Complete(5) → Writeback(6) → Commit(7) → Flush(8) → Collection(9)
```

In-order pipelines use Fetch, Execute, ArborUpdate, Writeback, Commit, Flush, and Collection. The Dispatch, Issue, and Complete phases are reserved for out-of-order execution and are never scheduled by in-order code.

**Gears** (`src/Core/Orrery/Gears/Gear.cs`) are the simulated components. They register Arbors (ports) and Settings during construction and `Initialize()`, then schedule work via the Escapement. Gears communicate only through typed `Arbor` channels. `OutArbor<T>.Send()` schedules delivery to a bound `InArbor<T>` at `currentTick + latency` at phase `ArborUpdate`. Latency must be at least 1; zero-latency connections would collapse sender and receiver within the same tick and break phase-ordering guarantees.

**Lifecycle** is a strict one-way state machine enforced by the `SimNode` tree (`src/Core/Orrery/Tree/SimNode.cs`); the entire tree moves together:

```
Building → Finalizing (bind Arbors here) → Running → Finished
```

The **Train** (`src/Core/Orrery/Train/Train.cs`) owns the Gears and the Escapement and drives `Build()` → `Run(maxTicks)` → `Reset()`. `Build()` calls `Initialize()` on all Gears, transitions to Finalizing, calls `Seal()` (where Arbors are bound), then locks Settings. `Run()` returns a `RevolutionResult` containing a snapshot of every Gear's `DialBoard`, and optionally periodic `TimeSeries` snapshots for tracking how metrics evolve during execution. The Train knows nothing about ISAs or instruction semantics.

### ISA plugins (Mechanism)

`IMechanism` is the factory and registry for one ISA. It produces an `IArchState` and exposes the `Decoder`, `Executor`, optional `ImpulseCracker`, and `TrapController`. A Train is constructed from a single `IMechanism`; swapping the Mechanism swaps the entire ISA without touching any Train code.

### Pipeline trains (src/Core/Pipeline/)

Four Trains, all ISA-agnostic — they operate on `IArchState` and `ExecuteResult` closures with no dependency on any ISA assembly. When used with RISC-V they pair with `Rv32Mechanism` (RV32IMAFCV) or `Rv64Mechanism` (RV64IMAFDAC):

- **`SingleCycleTrain`** — one Gear, one instruction per tick (fetch → decode → execute → writeback, all inline). Used to validate the Mechanism independently of pipeline complexity.
- **`FiveStageTrain`** — classic IF/ID/EX/MEM/WB pipeline. Each stage is its own Gear wired in sequence via Arbors. A `HazardUnit` handles RAW stall detection and register forwarding (controlled by a `forwardingEnabled` flag). Branch handling uses a pluggable `IBranchPredictor`; built-in implementations include static predictors (`AlwaysNotTaken`, `AlwaysTaken`, `AlwaysBackwardNotForwards`), 1-bit and 2-bit saturating counter predictors, correlated (m,n), Gselect, Gshare, L-TAGE (TAGE with a loop predictor overlay), IMLI (Inter-Mediated Loop Iteration — single shared loop-iteration counter indexes the PHT so body-branch predictions are iteration-specific; Jiménez, IEEE CAL 2018), LLBP (Last-Level Branch Predictor — context-addressed backing store over TAGE-SC-L; Rolling Context Register hashes recent taken-branch PCs into a context ID, patterns indexed by TAGE's PC×GHR tags; Schall et al., MICRO 2024), LLBP-X (LLBP Revisited — adds a Context Tracking Table that promotes high-contention contexts from shallow W=2 to deep W=64 history depth, splitting storage into short/long history ranges; Schall et al., HPCA 2026), VLA-TAGE (Vector-Loop-Aware TAGE — extends TAGE-SC-L with a power-gating mechanism; a Vector Loop Table tracks backward branches and a Loop Monitor estimates remaining iterations from comparison-register operand values at execute time; when the innermost loop is confirmed vector-intensive with ≥32 estimated remaining iterations the PEN signal bypasses tagged history tables T1–T3 and the SC predictor, relying only on bimodal T0 and the loop predictor; PEN is deasserted 5 iterations early to give the full predictor time to re-engage before loop exit; `GatedPredictions` counter enables power-reduction modeling; Zhang et al., IEEE CAL 2026), plus a `ReturnAddressStack` wrapper for call/return prediction, and a `TrueOraclePredictor` that runs a `SingleCycleTrain` functional pre-pass to collect the complete branch trace and replay it with zero mispredictions (useful as an IPC upper bound). Both instruction and data memory support optional set-associative caches and TLBs. A `StoreBuffer` provides deferred writes with store-to-load forwarding.
- **`OooeTrain`** — superscalar out-of-order pipeline using Tomasulo's algorithm. Physical register renaming, ROB-based in-order commit, and a unified issue queue. Functional units are configurable per class (`FuLatencyConfig`): each class (integer ALU, multiplier/divider, pipelined FP, FP divide/sqrt, load-store, branch, system) has an independent issue-port count and execution latency; multi-cycle results flow through a countdown-based in-flight buffer before CDB broadcast. Default latencies: integer ALU 1 cycle, integer mul/div 3, pipelined FP 4 (add/sub/mul/fma/compare/convert), FP div/sqrt 16, load-store 1. Dedicated `LoadQueue` and `StoreQueue` circular buffers track in-flight speculative loads and stores independently of the ROB. Loads and stores share a monotonic sequence number at dispatch so program order can be determined across queues without ROB-index wrap. Loads issue speculatively without waiting for older stores; store-to-load forwarding supplies the correct value when the store has already executed, and a memory-order violation squash (flush + re-execute from the load's PC) recovers when the store resolved after the load. A `mem_order_violations` counter tracks re-executions. A **store-set predictor** (`StoreSetPredictor`, enable with `enableStoreSets: true`) avoids unnecessary squashes by predicting, at dispatch, which stores a load depends on: a Store-Set Identifier Table (SSIT, 1024 entries, PC-indexed) maps instructions to store-set IDs; a Last Fetched Store Table (LFST, 1024 entries) tracks the most-recently-dispatched store per set; loads stall at issue until their predicted store's address is known; violations train the predictor via four SSID-merge rules (Chrysos &amp; Emer, ISCA 1998). Because the SSIT carries no address information, one genuine conflict permanently merges every future dynamic instance of that load/store PC pair — costly for recursive/generic functions that reuse one PC pair across many independent addresses. Both tables are cleared every 4096 load dispatches (`clearPeriod`) to bound that cost; the value was chosen by sweeping the full gem5-compare benchmark suite (see `docs/gem5-comparison.md`). Control-flow speculation follows gem5: direct unconditional jumps (`jal`/`j`, flagged by `FetchHint.IsUnconditional`) are resolved straight to their statically known target at fetch instead of being routed through the direction predictor; every direct branch (conditional included) takes its taken-target from the decode hint (`FetchHint.BranchTarget`) rather than a possibly-cold predictor BTB, so a stale/aliased BTB entry can never send speculative fetch to a null address (only indirect branches use the predictor's target, and a cold indirect target falls through); the `ReturnAddressStack` is checkpointed against an architectural shadow (advanced only when a call/return retires) that restores it on every flush, so wrong-path push/pop corruption does not leak into later return predictions; and history-based predictors keep a **speculative global history** advanced at fetch (`IBranchPredictor.SpeculativeHistoryUpdate`) and restored on flush (`RecoverSpeculativeHistory`) against an architectural committed-history shadow, so TAGE/LTage lookups index up-to-date history across the ROB window rather than stale commit-time history (the in-order trains are unaffected — they retain commit-time history bit-for-bit). Branch mispredicts are resolved at **execute** rather than commit, matching gem5's `iew`: a branch that resolves off its predicted path before reaching the ROB head triggers a **partial squash** that redirects fetch immediately, discarding only the younger in-flight instructions while the branch and everything older stay live and commit normally (a full flush is reserved for traps, load-order violations, and a mispredict that is already the ROB head). Exact recovery of speculative predictor state uses a per-branch history checkpoint captured at fetch (`IBranchPredictor.CaptureHistory`/`RestoreHistory`, covering the shared global- and local-history helpers and the whole TAGE family) plus a `ReturnAddressStack` rebuilt from the committed shadow and a replay of the surviving in-flight calls/returns; a branch younger than an older in-flight halt or trap is left to the commit-time path since that older entry will redirect first. Memory-level parallelism is modelled on both sides: each missed load carries the miss penalty in its own in-flight countdown so independent misses overlap (load-side MLP); a bounded write buffer (`writeBufferCapacity` parameter, default 0) absorbs committed store write-miss penalties asynchronously so the pipeline is not frozen while the write bus drains (store-side MLP). The D-cache is write-through / no-write-allocate, so writes reach memory the instant they are issued and write-buffer occupancy is a pure bus-latency model with no forwarding implications. TSO fences are modeled: a FENCE whose predecessor set contains W and successor set contains R (`ITooth.IsStoreLoadFence`, including FENCE.TSO) issues only at the ROB head once the write buffer has fully drained, and younger loads may not issue while it is in the ROB — closing the store→load window, the only reordering the train performs; all other fence flavours are timing no-ops because TSO already provides their ordering. The `OooeTrain` public API is a thin wrapper; `OoOPipelineCore` is the single-Gear implementation. A `StreamingEngine` (`src/Core/Orrery/Streaming/StreamingEngine.cs`) is embedded in every `OoOPipelineCore`: it manages up to 8 independently configured affine memory streams (`StreamDescriptor` in `src/Core/Mechanism/`: base address, element width in bytes, element count, byte stride), each backed by a prefetch buffer of configurable depth. The engine's `Step()` is called unconditionally every pipeline cycle so streams prefetch ahead of consumption; streams are architectural state and survive pipeline flushes. UVE (Unlimited Vector Extension) instructions consume streams in the OoO pipeline: `ToothClass.Uve` ops are head-serialized (like `Vector`); the pipeline injects load-stream elements into `IUveScalars` before calling the executor; Issue stalls when a required load stream has no buffered element; `ExecuteResult.StreamConfig` carries `ss.ld.w` descriptors for `StreamingEngine.Configure`.

The five-stage pipeline timing: an instruction is fetched at cycle T, decoded at T+1, executed at T+2, accesses memory at T+3, and writes back at T+4. Writeback is scheduled at `Phase.Writeback` (6) before Decode runs at `Phase.Commit` (7), so a register written this cycle is visible to a dependent instruction reading the register file in the same cycle.

ISA mutations (register writes, vector state, CSRs, trap returns) are delivered to the Train through a `SideEffect Action<IArchState>` closure on `ExecuteResult`, keeping the trains free of any ISA-specific fields.

**ISA coverage:** I/M/A/F/D (standard), C (compressed 16-bit instructions), Zba (address generation: sh1add/sh2add/sh3add), Zbb (basic bit manipulation: andn/orn/xnor, clz/ctz/cpop, min/minu/max/maxu, rol/ror/rori, sext.b/sext.h/zext.h, orc.b/rev8), Zbc (carry-less multiply: clmul/clmulh/clmulr), Zbs (single-bit: bclr/bext/binv/bset and immediate forms), Zicond (czero.eqz/czero.nez), Zawrs (wrs.nto/wrs.sto — NOP in single-core), Zicbom (cbo.inval/clean/flush — NOP), Zicboz (cbo.zero — zeros 64-byte cache-line-aligned block), Zicbop (prefetch.i/r/w — NOP via ORI path), Zifencei (fence.i — NOP; I-cache invalidation on self-modifying code is not modeled), Zimop (mop.r.N/mop.rr.N — return 0), Zcmop (c.mop.N, N odd 1–15 — compressed NOP hints, reserved C.LUI nzimm=0 encoding space), Zicntr (cycle/cycleh/time/timeh/instret/instreth user-level counter shadows; mcycle/mcycleh/minstret/minstreth M-mode counters; pipeline trains drive IArchState.OnCycle()/OnRetire() hooks), V (vector, VLEN=128, V1.0 fully implemented), and UVE (Unlimited Vector Extension, scalar subset; target spec is UVE2 — Fernandes, U. Coimbra 2025 — with the AnaBSF/riscv-isa-sim uve branch as reference implementation). The UVE scalar subset (encoded in RISC-V custom-0/custom-1 opcode space) covers: stream setup (`ss.sta.ld.w`, `ss.sta.st.w`, `ss.app`, `ss.end`, `ss.cfg.vec` — dimensions configured outermost-first, Spike deque order, with `ss.end` adding the innermost; modifier and vec-dim indices are likewise outermost-first, remapped to the engine's innermost-first order at `ss.end`); stream modifiers with UVE2 semantics — `ss.app.mod` (static) and `ss.app.ind` (indirect) attach to the most recently configured dimension as trigger and carry an explicit target dimension (`tdim`); the target's fields reset to configured values when the trigger dimension itself wraps; Offset displacements and indirect offset values are element-scaled; scalar broadcast (`so.v.dp.w`); element-wise FP arithmetic (`so.a.mul.fp`, `so.a.add.fp`, `so.a.sub.fp`, `so.a.mac.fp`); stream-loop branches (`so.b.nc`, `so.b.ndc.D`); SO_P predicate register file (16 registers, VLEN/8=16 bytes each, reg 0 all-ones; `so.p.{zero,one,vr,not,mv,mvt}` simple manipulation ops with governing predicate and zeroing mode; `so.p.{ge,eq,lt}.{us,fp,sg}` element-wise comparisons and `_z` zeroing-mode variants); `so.v.mv` / `so.v.mvt` predicate-gated vector register move/transpose. `StreamDescriptor` holds N-dim `StreamDimension[]`; `StreamingEngine` tracks per-dim consume-side pass-complete flags for `so.b.ndc.D` loop control. Stream-loop dimension branches (`so.b.ndc.D` / `so.b.dc.D`) count dimensions from the outermost with funct3 = D−1 (Spike EODTable convention); the pipeline remaps to the engine's innermost-first index. `UveStoreStream` supports N-dimensional layouts with per-dim index carry, matching `StreamState` for load streams. Configure-once kernels are exercised end-to-end through the OoO pipeline: 3D-stream GEMM with stride-0 repeat dimensions (`GemmTests`), lower-triangular sum via a single `ss.app.mod` Size modifier (`TriangularSumModifierTests`), and sparse·dense dot product via `ss.sta.ld.w_inds` + `ss.app.ind` indirect gather (`SparseDotProductTests`), alongside SAXPY and per-row-reconfiguring triangular/trisolv variants.

**Privilege model:** Three privilege levels (User=0, Supervisor=1, Machine=3). Trap delegation: when an exception's `medeleg` bit is set and the hart is below Machine privilege, `RaiseTrap` enters S-mode (writes `sepc`/`scause`/`stval`, updates `sstatus` SPP/SPIE/SIE, sets privilege to Supervisor, returns `stvec` base); otherwise the existing M-mode path applies. Interrupt causes (bit 31 set in mcause/scause) are delegated via `mideleg` rather than `medeleg`. `MRET` is guarded to Machine mode; `SRET` requires at least Supervisor. `ECALL` emits the correct cause code for the current privilege level (8=U, 9=S, 11=M). CSR accesses from an insufficient privilege level or writes to read-only CSRs raise `IllegalInstruction`.

**Virtual memory (Sv32):** The `satp` CSR (0x180, Supervisor-mode) controls address translation. When `satp.MODE=1`, both instruction fetch and data loads/stores go through a two-level Sv32 page table walk (`Sv32Walker`). `MODE=0` (bare) uses virtual address = physical address and preserves the existing behaviour for all benchmark workloads. A/D bits are enforced using a fault-on-access model: `A=0` or (`D=0` on a store) raises the corresponding page fault (`LoadPageFault`/`StorePageFault`). Supervisor User Memory (SUM) is not implemented; S-mode always faults on user pages (PTE.U=1). Instruction fetch translation crosses the ISA isolation boundary via the `IFetchTranslator` interface in src/Core/Mechanism/: `RvFetchTranslator` (in src/Isa/RiscV32/) calls `Sv32Walker` with `isExec=true`, returning `(physAddr, 0)` on success or `(0, 12)` for `InstructionPageFault`; M-mode fetches bypass the walk entirely (RISC-V priv spec §3.1.6). All four pipeline trains wire up the translator and propagate the pre-baked `TrapInfo` through the pipeline latch chain to Writeback, where it is raised via the normal trap path.

**Interrupt dispatch:** `ITrapController.PeekInterrupt(IArchState)` returns the highest-priority pending interrupt (per the RISC-V §3.1.9 priority order: MEI > MSI > MTI > SEI > SSI > STI) when `mip & mie` has a pending bit and the global interrupt enable for the current privilege and delegation state permits delivery. All four pipeline trains call `PeekInterrupt` at each retire/commit boundary and invoke `RaiseTrap` when a non-null result is returned. `mideleg` routes delegated interrupts to S-mode.

### Benchmark workloads (TestBinaries/benchmarks)

Seven bare-metal RISC-V benchmarks compiled from the riscv-tests suite: `median`, `memcpy`, `multiply`, `qsort`, `rsort`, `towers`, and `vvadd`. They are built against a minimal `crt0.s` + `bmarks.ld` (code at `0x80000000`, Spike's `DRAM_BASE`, 4 MB RAM) and exit via the HTIF `tohost` symbol. All are available as preset workloads in the Face UI and can be passed to the Runner as ELF arguments. Note: benchmark tests are slow — run them selectively with `--filter`.

ISA conformance tests (`TestBinaries/isa/`) are also linked at `0x80000000`. `FlatMemory` accepts an optional `baseAddress` constructor parameter so the backing byte array starts at the first PT_LOAD segment (e.g. `0x80000000`) rather than at address 0, avoiding a 2 GB allocation. `Rv32ElfWorkload.BaseAddress` exposes this value; `IWorkload.BaseAddress` defaults to 0 for zero-based images.

### Full-system booting (src/Isa/RiscV32/Memory, src/Core/Orrery/Devices)

Two full-system boot milestones are verified by tests in `Tests/RiscV32/`:

- **OpenSBI v1.8** (`SingleCycle_OpenSBI_PrintsBanner`): `fw_jump.bin` (generic platform, RV32) boots on a `SingleCycleTrain` and prints its version banner on the ns16550a UART. Built via `nix build .#opensbi-rv32`.
- **Linux 6.12 RV32 NOMMU** (`SingleCycle_Linux_PrintsBanner`): a `nommu_virt_defconfig + 32-bit.config + M-mode` kernel loads at `0x80000000` (PAGE_OFFSET) and prints `Linux version …` via earlycon on the UART within 10 M instructions. No OpenSBI — the kernel runs entirely in M-mode, so it is launched directly. Built via `nix build .#linux-rv32`.

The peripheral bus is a `PeripheralBus` routing three devices: a `ClintDevice` (MTIP/MSIP at 0x02000000), a `PlicDevice` (external interrupt routing at 0x0C000000), and an `Ns16550aUart` (ns16550a console at 0x10000000; TX writes flush immediately to a `TextWriter`). `VirtDtb.Bytes` is a hand-crafted device tree blob (`src/Isa/RiscV32/Memory/virt.dts`) declaring 128 MiB RAM, all three devices, and one `virtio_mmio` block device slot.

### Multi-hart kernel (src/Isa/RiscV32/MultiCore)

`MultiHartKernel` drives N RISC-V harts round-robin against a shared physical memory. Each call to `Step()` advances every non-halted hart by one instruction and returns the number still active; `Run(maxTicks)` loops until all harts halt or the tick limit is reached. Each hart has its own `IArchState` (created by `Rv32Mechanism.CreateArchState()`). Halt detection covers EBREAK (`result.IsHalt`), HTIF tohost (`result.RequestHalt`), and the infinite-self-loop idiom (`PC == pc && class == Branch`). The kernel operates in physical address space (no fetch translation), making it suited for bare-metal multi-hart workloads.

Two constructors are available: `MultiHartKernel(IMemory sharedMemory, …)` gives every hart the same `IMemory` (simplest path, used with `ReservationAwareMemory` for LR/SC); `MultiHartKernel(IMemory[] perHartMemory, …)` gives each hart its own cache (e.g. a `MoesifCache` backed by a shared `MoesifBus`) — both instruction fetch and data access route through the per-hart memory.

`Rv32Mechanism` now accepts optional `reservationTable` and `hartId` constructor parameters, forwarded to `Rv32Executor` for LR/SC routing. Typical setup:

```csharp
var table   = new ReservationTable();
var guarded = new ReservationAwareMemory(flat, table);
var kernel  = new MultiHartKernel(guarded,
    new Rv32Mechanism(reservationTable: table, hartId: 0),
    new Rv32Mechanism(reservationTable: table, hartId: 1));
```

`ReservationTable` tracks per-hart LR/SC reservations. Each hart registers a reservation on `LR.W`; any write from any hart to the same 4-byte-aligned granule cancels all overlapping reservations so a subsequent `SC.W` fails correctly. `ReservationAwareMemory` is a thin `IMemory` wrapper whose `Write()` calls `table.InvalidateAt()` before the actual write, ensuring cancellation fires on every store. Single-hart setups leave `ReservationTable` null and use the existing private `_reservation` field unchanged — no API or behaviour change for existing code.

### Cache replacement policies (src/Core/Orrery/Cache)

`SetAssociativeCache` supports a pluggable replacement policy via `IReplacementPolicy` and the `ReplacementPolicyKind` enum: **LRU** (default), **MRU** (`Mru`): inverse of LRU — a hit promotes the way to age 0 (next eviction candidate); new installs are placed at the LRU position so they survive until first use; useful for sequential-scan workloads where the just-accessed block is unlikely to be reused soon, **CLOCK** (`Clock`): one reference bit per way and a circular hand per set; on eviction the hand sweeps forward clearing bits=1 (second chance) until it finds a bit=0 victim; on hit or install the bit is set to 1; O(1) amortized victim search, hardware-cheap LRU approximation common in OS page replacement, **FIFO** (circular-pointer eviction, ignores hits — ordering baseline), **Random** (uniform random victim, deterministically seeded), **Tree-PLRU** (`Plru`): binary tree of `ways−1` bits per set; on every access the bits on the root-to-leaf path are pointed away from the accessed subtree; victim selection follows bits root-to-leaf; exact LRU for 2-way, hardware-friendly approximation for wider associativity (Intel P6 and later), **SRRIP-HP** (scan-resistant; inserts at RRPV 2^M−2, promotes hits to 0), **BRRIP-HP** (thrash-resistant; inserts at distant RRPV 2^M−1 with probability 1−ε, long with probability ε=1/32), **DRRIP-HP** (scan- and thrash-resistant; uses Set Dueling — 32-set SDMs, 10-bit PSEL — to dynamically choose between SRRIP and BRRIP per set) — Jaleel et al., ISCA 2010; and **SHiP-Mem** (`Ship`) and **SHiP-PC** (`ShipPc`): layer a 16K×3-bit Signature History Counter Table (SHCT) on top of SRRIP-HP — inserts at RRPV=3 (distant) when SHCT[sig]=0, RRPV=2 (long) when SHCT[sig]>0; increments SHCT on every hit; decrements on eviction without reuse. SHiP-Mem indexes the SHCT by the upper address bits of the miss address; SHiP-PC indexes by the load PC via `IMemory.SetRequestPc(pc)`, which all pipeline trains call before every execute. Prefetches always use address-based signatures since no PC is available at prefetch time — Wu et al., MICRO 2011; and **Hawkeye** (`Hawkeye`): reconstructs Belady's optimal replacement decisions for the observed access stream using OPTgen (a circular occupancy vector of length 8×ways per set), trains a PC-indexed 8K×3-bit saturating-counter predictor, and uses the predictor's label at each install — cache-friendly (counter ≥ 4) inserts at RRPV=0, cache-averse at RRPV=7; demand hits decrement RRPV toward 0; victim selection is SRRIP-style (scan for RRPV=7, age all lines if none found) — Jain &amp; Lin, ISCA 2016. The `ReplacementPolicy` field on `MemoryConfig` (and `CacheReplacementPolicy` string on `TrainConfig`) selects the policy for all cache levels.

### Instruction prefetcher (src/Core/Pipeline)

**RDIP** (RAS-Directed Instruction Prefetching — Kolli, Saidi &amp; Wenisch, MICRO 2013): associates I-cache miss sequences with call-stack signatures derived from the commit-time RAS (4-entry, per the paper's sensitivity study). On every call or return at commit the prefetcher (1) computes a signature = XOR of the top RAS entries | direction bit (0=call, 1=return), (2) flushes the Current Signature Misses buffer (up to 16 miss addresses accumulated since the last signature change) into the Miss Table under the previous signature, and (3) looks up the new signature's Miss Table entry and issues prefetches. The Miss Table is 1024 sets × 4 ways (LRU); each entry holds up to 3 trigger records, each a base address + 8-bit block mask covering an 8-block window; new misses are merged into the nearest existing window or replace the oldest trigger round-robin. RDIP is wired into `FiveStageTrain` and `OooeTrain` (commit-hook calls `OnCommit`; fetch-hook calls `OnIcacheMiss`); enable with `rdip: true`.

**FDIP** (Fetch Directed Instruction Prefetching — Reinman, Calder &amp; Austin, MICRO 1999): decouples the branch predictor from instruction fetch via a Fetch Target Queue (FTQ). Each cycle the predictor steps ahead of the fetch PC using the raw backing memory (bypassing the cache to avoid charging stall latency to the pipeline), filling the FTQ with predicted cache-line base addresses. A configurable prefetch window (default: entries 1–10, skipping entry 0 which is too close to benefit) drives `SetAssociativeCache.Prefetch` directly on the L1 I-cache. The FTQ is drained by position (one head entry per cache-line boundary crossed by fetch), not by address magnitude, so it stays correctly aligned across backward branches (loops). Mispredictions reset lookahead state to the flush target. The lookahead does not maintain a RAS, so call/return targets rely on the BTB; mispredicted targets are recovered by the normal flush mechanism. `FdipPrefetcher` is wired into `FiveStageTrain` (via `FetchStage`) and `OooeTrain` (directly in `StepFetch`/`StepFlush`); enable with `fdipFtqCapacity: 32` (0 = disabled, default). FDIP is bare-mode only (no virtual-to-physical lookahead translation).

### Cache prefetchers (src/Core/Orrery/Cache)

`IPrefetcher.OnAccess(pc, address, wasHit, Span<ulong> targets)` writes zero or more prefetch addresses into the caller-provided span and returns the count; the `OooeTrain` execute stage drives it once per demand load and calls `MemoryLayers.TryPrefetch` for each result, subject to MSHR capacity. Seven prefetchers are implemented: **NextLine** — always prefetches the cache line immediately following the access; bandwidth-greedy but effective for sequential workloads. **Stride / RPT** — Reference Prediction Table (per-PC stride tracking with a 0–3 saturating confidence counter); issues a prefetch at `address + stride` once the stride is confirmed (confidence ≥ 2). **Stream** — multi-way sequential stream buffer (Jouppi, ISCA 1990): maintains up to N independent stream buffers in parallel (default 4, controlled by `PrefetcherTableSize`); on a cache miss that matches no buffer, the LRU buffer is evicted and restarted at the missed address, issuing `depth` lines at once (default 8, controlled by `PrefetcherDepth`); on each subsequent sequential access the frontier is advanced by one line to keep exactly `depth` lines pre-loaded; LRU replacement across buffers; targets sequential and near-sequential patterns including RVV vector loads and UVE streams. **IPCP** — IP Classifier-based Spatial Prefetcher (Pakalapati & Panda, ISCA 2020): classifies each load PC into one of three classes and issues spatially-targeted prefetches; **CS** (Constant Stride) tracks per-PC stride with a 2-bit saturating confidence counter and issues up to 3 prefetches at the confirmed stride; **CPLX** (Complex Stride) maintains a 7-bit rolling signature of recent strides (`sig = (sig<<1) XOR stride`) indexing a 128-entry CSPT table, issuing up to 3 prefetches when a pattern repeats (confidence ≥ 1); **GS** (Global Stream) tracks 2 KB regions in an 8-entry LRU Region Stream Table (RST) with a 64-bit access bitvector, classifying a PC as a global-stream if ≥75% of its region's lines have been touched (dense), then issuing up to 6 prefetches in the stream direction; a tentative GS prefetch fires when an IP enters a new region and its previous region was dense; no prefetch crosses a page boundary; a 32-entry recent-request filter suppresses duplicate prefetch requests. Priority: GS > CS > CPLX. **Berti** — accurate local-delta L1D prefetcher (Navarro-Torres et al., MICRO 2022): for each load IP maintains an 8-set × 16-way FIFO History Table (HT) of recent (line address, tick) pairs; on a demand miss it searches the IP's HT set for "timely" entries (entries whose tick satisfies `entry.tick + latency ≤ current_tick`, i.e., a prefetch issued then would have arrived before the miss) and accumulates the signed line-count deltas to the current miss address into a 16-entry fully-associative Table of Deltas (ToD); an epoch counter trips at 16 training events and assigns statuses by coverage fraction: >10/16 → L1DPref, 6–10/16 → L2Pref (or L2PrefRepl if <8/16), ≤5/16 → NoPref; at most 12 deltas may be active (L1DPref+L2Pref+L2PrefRepl combined); warmup mode issues a delta only when counter ≥ 8 and coverage > 80% of the counter value; latency is approximated by a configurable tick count (default 10) since no MSHR timestamps are available. **Pythia** — online reinforcement learning prefetcher (Bera et al., MICRO 2021): formulates prefetching as a SARSA RL problem; the agent observes two program features per demand — PC+Delta (current load PC XOR'd with the current cacheline delta) and the last-4-deltas rolling hash — and selects one prefetch offset from a 16-entry pruned action list {−6,−3,−1,0,+1,+3,+4,+5,+10,+11,+12,+16,+22,+23,+30,+32} (lines); Q-values are stored in a hierarchical Q-Value Store (QVStore): 2 vaults × 3 tile-coded planes × 128 feature-entries × 16 actions; Q(S,A) = max over vaults of the sum of plane partial Q-values; rewards: RAT=+20 (accurate+timely), RAL=+12 (accurate+late), RCL=−12 (page-crossing), RIN=−8 (inaccurate), RNP=−4 (no-prefetch); a 256-entry FIFO Evaluation Queue (EQ) defers SARSA updates (α=0.0065, γ=0.556) until the evicted entry's reward is known; ε=0.002 greedy exploration; per-access overhead is a 16-way Q-value lookup over 2 vaults × 3 planes. **SMS** — Spatial Memory Streaming (Somogyi et al., ISCA 2006): learns spatial access patterns over fixed 2 KB address regions and prefetches all blocks predicted to be accessed during a region generation; indexed by the PC and block offset of the trigger (first) access. The Active Generation Table (AGT) is split into a 32-entry fully-associative filter table (holds single-access generations; entries are discarded on eviction) and a 64-entry fully-associative accumulation table (promotes from filter on the second distinct block access; accumulates a 64-bit spatial pattern bitvector); accumulation entries are retired to the Pattern History Table on AGT capacity pressure, matching the paper's explicit description of capacity-based generation termination. The PHT (16 K entries, 16-way set-associative, LRU) stores one pattern bitvector per (trigger PC, block offset) hash key; on a trigger access the PHT is consulted first and matching predicted blocks (excluding the trigger block itself) are immediately emitted as prefetch targets. Select with `Prefetcher = PrefetcherKind.{NextLine,Stride,Stream,Ipcp,Berti,Pythia,Sms}` on `MemoryConfig`/`CacheLevelSpec`, or `d_prefetcher: "next_line"/"stride"/"stream"/"ipcp"/"berti"/"pythia"/"sms"` in `TrainConfig` JSON.

### MOESIF cache coherence (src/Core/Orrery/Cache)

`MoesifCache` is an N-way set-associative write-back cache that participates in a MOESIF coherence protocol with cache-to-cache supply. Unlike `SetAssociativeCache` (write-through, no-write-allocate), `MoesifCache` is write-back and write-allocate: writes stay in the cache as Modified lines until eviction or a snoop, not every write goes to backing memory.

`MoesifBus` coordinates snooping between all registered `MoesifCache` instances sharing a physical address space. Three bus transactions cover the full protocol:

- **BusRead** (read miss): a peer holding the line in M, O, E, or F supplies the block directly to the requester (cache-to-cache) instead of the requester filling from backing. A dirty supplier (M/O) keeps the line as **Owned** — no writeback to backing occurs; the owner retains writeback responsibility until eviction or invalidation, and later readers install plain S. A clean supplier (E/F) downgrades to S and the requester installs **Forward**: exactly one sharer of a clean line holds F and keeps answering later read misses cache-to-cache, so memory stays silent; the F role migrates to the most recent requester on each supply (Intel MESIF semantics). If only plain S peers hold the line (the forwarder was evicted), the requester fills from backing — guaranteed clean in that case — and becomes the new forwarder. With no peers at all it fills from backing as E.
- **BusReadForOwnership** (write miss): all peers transition to I, and an M/O/E/F holder forwards the block to the requester along with the invalidation — no writeback; the requester installs the line as M, making its copy authoritative. Only if no such holder exists does the requester fill from backing (which is guaranteed current in that case).
- **BusReadInvalidate** (S/O/F→M upgrade, block-boundary-crossing writes): all peers transition to I; dirty M/O holders write back first. No data transfer — the upgrading requester already holds the bytes.

Silent E→M upgrade (write hit on an Exclusive line) requires no bus transaction — the cache takes M without notifying peers. While a line is Owned, backing memory is stale; every path that removes the Owned copy (eviction, snoop-invalidate, `cbo` maintenance, `Flush()`) writes it back. Accesses that straddle a block boundary read backing directly after a **BusSyncToBacking** transaction forces dirty holders — including the requesting cache itself — to write back.

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

`StateOf(address)` returns the current MOESIF state of the line covering an address (for test assertions). `Flush()` writes all dirty (M/O) lines to backing without evicting them — useful for inspecting backing memory from tests. `ConsumePendingStalls()` returns accumulated miss-penalty cycles for pipeline integration; a fill supplied cache-to-cache is charged `PeerSupplyLatency` (constructor parameter, defaults to `MissLatency`) instead of the full miss penalty, and `PeerSupplies` counts such fills.

`DirectoryBus` is a drop-in `IBus` alternative to the snooping `MoesifBus` for sequential multi-hart simulation: it keeps a precise per-line directory (designated responder + sharer set, maintained via eviction notifications) so invalidations snoop only actual holders and forwarder-less shared read misses need no probe at all. Cache-to-cache supply is directed: the directory contacts the single M/O/E/F responder. It cannot be wrapped by `DeferredBus` (two-phase concurrent mode), which is hardcoded to `MoesifBus`.

`MoesifCache` and `MoesifBus` are ISA-agnostic (`Orrery.Cache`). Use the `MultiHartKernel(IMemory[] perHartMemory, …)` overload to give each hart its own cache. Pass the `ReservationTable` to `MoesifBus` so that LR/SC reservations are cancelled on every `BusReadForOwnership` (write miss) and `BusReadInvalidate` (S/O/F→M upgrade):

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

Instruction fetch and data access both route through the per-hart cache (unified I/D model). `ReservationAwareMemory` is not required in this stack. All three write paths cancel reservations via the bus:


| Path | Bus transaction | Reservation cancellation |
|------|----------------|--------------------------|
| Write miss (write-allocate) | `BusReadForOwnership` | `table.InvalidateAt` in `BusReadForOwnership` |
| S/O/F→M upgrade (write hit on Shared/Owned/Forward) | `BusReadInvalidate` | `table.InvalidateAt` in `BusReadInvalidate` |
| E→M upgrade (write hit on Exclusive) | none (silent) | `table.InvalidateAt` in `BusSilentUpgrade` |

### MultiHartPipeline (src/Core/Pipeline/)

`MultiHartPipeline` coordinates N full pipeline trains (`ISteppableTrain`) in round-robin cycle-interleaved order — the pipeline-train analogue of `MultiHartKernel`. Each hart owns its own train instance (and typically its own `MoesifCache`); the coordinator advances every non-halted train by one tick per logical cycle.

`ISteppableTrain` (`src/Core/Orrery/Train/`) is a minimal interface: `BeginStepping()`, `StepCycle() → bool`, `IsIdle`, `FinishStepping() → RevolutionResult`. All five train types implement it: `SingleCycleTrain`, `FiveStageTrain`, `SuperscalarTrain`, `OooeTrain`, `SmtTrain`.

```csharp
var flat   = new FlatMemory(0x10000);
var bus    = new MoesifBus(flat);
var cache0 = new MoesifCache(bus, 4096, 2, 64);
var cache1 = new MoesifCache(bus, 4096, 2, 64);

var train0 = new SingleCycleTrain(new Rv32Mechanism(), cache0, entryPoint: 0x00);
var train1 = new SingleCycleTrain(new Rv32Mechanism(), cache1, entryPoint: 0x40);

RevolutionResult[] results = new MultiHartPipeline(train0, train1).Run(maxTicks: 100_000);
```

`Run` returns one `RevolutionResult` per hart. Combine with `MoesifBus(flat, table:)` + `Rv32Mechanism(reservationTable:, hartId:)` for LR/SC atomics between pipeline trains.

**OoO timing note:** `OooeTrain`'s physical register file starts zeroed; `ArchState.IntegerRegisters.Write()` updates the architectural register file but not the PRF, so register values pre-set before `Run()` are invisible to the pipeline. For OoO MOESIF coherence tests or any test that requires non-zero initial register values, compute those values inside the program (e.g. `lui`+`addi` sequences). Also, OoO stores commit to the cache at ROB-head (several cycles after fetch), so a cross-hart load must be issued late enough to see the committed store — pad H1 with nops in the decode stream before the load's source-register computation.

### SmtTrain (src/Core/Pipeline/)

`SmtTrain` is a barrel-processor SMT train: N independent hart contexts share a single issue window of width `issueWidth`. Each tick the coordinator distributes the available slots round-robin across active harts, rotating the starting hart every cycle for long-run fairness. This interleaves hart instructions at issue-slot granularity rather than the whole-tick round-robin of `MultiHartPipeline`.

Each hart has its own `IArchState` and `MemoryLayers` (typically backed by per-hart `MoesifCache` instances sharing a `MoesifBus`). All harts share the same `Escapement` and advance in lock-step. A hart that hits a branch, halt, trap, or MRET is blocked for the rest of the current cycle's issue window; the remaining slots go to other harts. When all harts have halted the Gear stops scheduling itself.

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

`SmtTrain` also implements `ISteppableTrain` and can be wrapped in `MultiHartPipeline` for nested multi-level parallelism. Aggregate cycle/retired/stall/IPC counters appear in `FinishStepping().Dials`.

### Per-instruction lifecycle events (src/Core/Orrery/Observation)

`PEventLog` captures structured per-instruction lifecycle events — Fetch, Decode, Dispatch, Issue, Execute, Retire, Flush — tagged with an instruction ID, PC, and cycle number. A cycle-level `FetchStall` sentinel (instrId=0) marks cycles where the OoO fetch unit is blocked (faulted PC). Pass a `PEventLog` instance to `FiveStageTrain` or `OooeTrain` to enable recording (null = zero overhead). Query methods include `ForInstruction(id)`, `OfKind(kind)`, and `InCycleRange(from, to)` for post-hoc filtering and phase analysis. Every instruction is assigned a monotonically increasing `InstrId` at fetch time, unique across the full simulation run, so lifecycle phases can be correlated even for wrong-path instructions that are later flushed.

FiveStage records Fetch/Decode/Execute/Retire/Flush. OoO records the full lifecycle: Fetch → Decode → Dispatch → Issue → Execute → Retire/Flush. Flush events appear as an additional terminal event for wrong-path or squashed instructions.

### Spike lock-step co-simulation (src/Isa/RiscV32/CoSim)

`SpikeCoSimReference` implements `ICommitObserver` and launches Spike as a live child process with `--log-commits`. For each instruction that Horologium commits, it reads the next line from Spike's stderr stream (blocking until Spike produces it), then immediately compares PC, raw encoding, and any integer register write — divergence is reported at the exact failing instruction. Boot-ROM commits (PC below `baseAddress`) are skipped. Attach it via the optional `commitObserver` parameter to `SingleCycleTrain`, `FiveStageTrain`, or `OooeTrain` — the in-order pipeline fires `OnCommit` from `WritebackStage` on normal retire, and the out-of-order pipeline fires once per ROB-head commit in program order, so the check covers the hazard/forwarding and speculative-memory datapaths too. Wrap in `using` to kill Spike on completion. `SpikeCoSimTests` runs three fixtures across all three trains: `test.elf` (simple RV32I golden path), `rich.elf` (RV32IM — multiply/divide, an insertion sort, and heavy data-dependent branching, to exercise the multi-cycle functional units, store-to-load forwarding, and flush paths), and `htif.elf` (the same RV32IM workload but terminating through the HTIF `tohost` register instead of EBREAK, so standalone Spike exits cleanly). Spike logs the post-exit spin-loop an indeterminate number of times; each train commits up to the exit, then halts, so its stream is a clean prefix of Spike's. `dtc` must be on PATH (the Nix dev-shell provides it); the tests can be excluded from CI without Spike with `--filter "FullyQualifiedName!~SpikeCoSim"`.

### Program termination

Two halt mechanisms, both stopping all three trains at the terminator instead of spinning to `maxTicks`:

- **HTIF tohost exit (first-class).** When the mechanism is given the `tohost` address (`new Rv32Mechanism(htifTohost)`), a word store of an odd exit code to that register is flagged by the executor with `ExecuteResult.RequestHalt`. The trains carry that flag through commit (the in-order pipeline via `MemWbLatch`, the out-of-order core via the ROB) and halt *after* the store commits — the engine terminates at the exit write itself, not the spin that follows. The flag is ISA-agnostic: the trains act on it without knowing about HTIF. The address is surfaced generically as `IWorkload.HtifTohostAddress` (the ELF's `tohost` symbol), so `Experiment.Run`/`Trace`, the Runner, and the Face all wire it automatically for HTIF ELFs.
- **Unconditional jump-to-self (backstop).** For non-HTIF programs, a `jal`/`jalr` whose resolved target is its own PC (the conventional bare-metal `j .` terminator) halts the run. Gated on `ToothClass.Branch` so a conditional spin-wait — which may be waiting on an interrupt — is not mistaken for a halt.

Because the five-stage and out-of-order trains previously spun HTIF binaries to `maxTicks`, adding these halts also makes the HTIF benchmark suite finish in seconds. `HtifExitTests` covers both paths across all three trains without requiring Spike.

### Hardware comparison (RiscV/Analysis)

`Experiment.Run(workload, configs, mechanism)` runs the same workload under multiple `NamedConfig` entries (each a named `TrainConfig` describing forwarding, predictor, cache, TLB, and store-buffer parameters), returns an `ExperimentResult`, and supports warmup ticks and periodic time-series snapshots. Results can be formatted as a Markdown table, summary CSV, or time-series CSV for graphing. `NamedConfig` sweep files are plain JSON arrays, readable by the Runner's `--sweep` flag.

### Architecture scripting and checkpointing (Script/)

`ScriptHost.EvaluateFileAsync(path)` compiles and evaluates a `.csx` (Roslyn C#) or `.fsx` (F# Interactive) script file whose last expression is a `MachineSpec`. All `Pipeline.Spec`, `Orrery.Spec`, `Orrery.Cache`, and `RiscV32` namespaces are pre-imported — no `#r` directives or `using`/`open` statements needed. The result can be passed directly to `MachineSpec.Build()`:

```csharp
// example.fsx
let pipeline = FiveStageSpec(ForwardingEnabled = true)
let l1 = CacheLevelSpec(4096, 4, 64, 10)
let cache = CacheHierarchySpec.Unified(CachePathSpec([| l1 |]))
MachineSpec(pipeline, (fun () -> Rv32Mechanism()), cache)
```

The Runner exposes this as `--script <file.csx|fsx>`. Two handoff patterns are supported:

**Symbol-based region-of-interest (ROI)** — name ELF symbols to bracket the measurement window. The Runner fast-forwards functionally (single-cycle) until the start symbol's PC is committed, then restores state into the script's pipeline and runs the detailed model until the end symbol or `--max-ticks`:

```bash
dotnet run --project src/Apps/Runner -- prog.elf \
  --script scripts/ooo.fsx \
  --roi-start roi_begin --roi-end roi_end \
  --max-ticks 5000000
```

`--roi-start` requires an ELF workload with a matching symbol. `--roi-end` is optional; omit to run until `--max-ticks`. The fast-forward phase uses `SingleCycleSpec` regardless of what the script specifies; the script's pipeline is used only for the detailed ROI phase.

**Manual checkpoint handoff** — save and restore state across separate Runner invocations with `--checkpoint-save` and `--checkpoint-load`:

```bash
# 1. Fast-forward 100 M instructions on a single-cycle model, save state.
dotnet run --project src/Apps/Runner -- prog.elf \
  --script scripts/single_cycle.fsx --max-ticks 100000000 --checkpoint-save fast.chk

# 2. Resume from the checkpoint on a detailed OoO model.
dotnet run --project src/Apps/Runner -- prog.elf \
  --script scripts/ooo.fsx --checkpoint-load fast.chk --max-ticks 10000000
```

**`ArchitecturalCheckpoint`** (`src/Core/Mechanism/ArchitecturalCheckpoint.cs`) is the serialization layer. `Save(path, state, memory, tick)` writes PC, privilege level, integer/FP registers, memory, and an ISA-specific blob (CSRs, VRF, UVE scalar state via `IArchState.WriteState`) to a binary file. `Load(path)` deserialises without touching live state; `chk.RestoreInto(state, memory)` applies it. `FlatMemory` implements the `ISnapshotableMemory` interface (`BaseAddress`, `SizeBytes`, `CopyTo`, `LoadFrom`) required by the checkpoint API. `ISteppableTrain.ArchState` (default `null`) exposes the committed hart state after or during a run; `MachineHandle.ArchState` forwards it. ROI uses an in-memory checkpoint internally (no file I/O); both `--checkpoint-save` and the ROI path can be combined to persist the post-ROI state.

### Instruction trace output (Olympia, RiscV32/Trace)

`Experiment.WriteOlympiaTrace(workload, mechanism, output)` runs the workload functionally on the single-cycle train (via `OlympiaJsonTraceWriter`, an `ICommitObserver`) and emits an [Olympia](https://github.com/riscv-software-src/riscv-perf-model)-compatible JSON instruction trace — one object per retired instruction with `mnemonic`, `rs1`/`rs2`/`rd`, `csr`, and `vaddr` (loads/stores). The CLI exposes it as `--trace-json <path>`. It reuses `RvDisassembler` for the mnemonic, `ITooth` for registers, and a `TracingMemory` wrapper for the effective address. Olympia is trace-driven (it replays the stream through its timing model without functional execution), so this is the prerequisite for *timing* co-simulation against the Sparta-based RISC-V performance model the project is patterned on — distinct from the *functional* Spike co-sim, which verifies ISA correctness.

Olympia (and its Sparta framework) are packaged by the flake from source — `nix/{softfloat,sparta,olympia}.nix`, exposed as `packages.{softfloat,sparta,olympia}` and on PATH inside `nix develop`. End to end:

```bash
dotnet run --project src/Apps/Runner -- TestBinaries/rich.elf --trace-json trace.json
nix run .#olympia -- trace.json --report-all report.txt   # IPC / cycles / retired in report.txt
```

### Elastic DDG trace (HELF format, RiscV32/Trace)

`Experiment.WriteElasticTrace(workload, mechanism, output)` records a **dynamic dependence graph** (DDG) trace in the Horologium-native `HELF` binary format. Each committed instruction becomes one record containing its sequence number, PC, raw encoding, instruction type (COMP/LOAD/STORE), estimated computation latency (`compDelay` from `FuLatencyConfig.Default`), effective address and access size (loads/stores), register RAW producer seqnos (`robDeps`), and memory RAW producer seqnos (`addrDeps`).

Register dependences track the unified 0–63 namespace (0–31 = integer, 32–63 = FP) and vector registers v0–v31 separately. Memory dependences map effective address → most-recent store seqno. `TracingMemory` supplies the effective address; the vAddr gate is class-based (not `HasAccess`) to avoid conflating instruction-fetch addresses with data addresses.

```bash
# Record a DDG trace
dotnet run --project src/Apps/Runner -- prog.elf --elastic-record prog.helf

# Replay the critical-path dataflow DAG and print IPC upper bound
dotnet run --project src/Apps/Runner -- --elastic-replay prog.helf
# → Elastic replay: 1234567 instructions, 890123 cycles (critical path), IPC upper bound = 1.386
```

`ElasticTraceReplayer.Replay(records)` computes the critical-path length: for each instruction, `completionTime = max(completionTime[dep] for dep in robDeps ∪ addrDeps) + compDelay`. The result is a **dataflow IPC upper bound** — infinite issue width, no structural hazards; real-hardware IPC will be lower.

**gem5 Protobuf translators** convert HELF to the two trace files gem5 TraceCPU requires. Fields and framing are verified against `gem5/src/proto/inst_dep_record.proto`, `packet.proto`, and `protoio.cc`:

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

`Gem5ElasticTraceConverter` produces the `dataTraceFile`: LE magic `0x356d6567` + varint32-length-prefixed `InstDepRecordHeader` then one `InstDepRecord` per committed instruction. `Gem5FetchTraceConverter` produces the `instTraceFile`: same framing, `PacketHeader` + one `Packet` per instruction (cmd=ReadReq, addr=PC, size=4, flags=INST\_FETCH). The fetch trace is an approximation: one 4-byte read per committed instruction, not the wrong-path cache-line fetches a real O3 CPU would generate.

### STF binary trace (Sparcians stf\_lib format, RiscV32/Trace)

`Experiment.WriteStfTrace(workload, mechanism, output)` records a **Simulation Trace Format** binary trace. STF is the native trace format of [Olympia](https://github.com/riscv-software-src/riscv-perf-model) and the [Sparcians stf\_lib](https://github.com/sparcians/stf_lib). Unlike the JSON Olympia trace, the STF output includes **operand values** for integer and floating-point registers (feature flag `STF_CONTAIN_OPERAND_VALUE`), memory access addresses and data, and taken-branch targets.

```bash
# Record an STF trace
dotnet run --project src/Apps/Runner -- prog.elf --stf-record prog.stf

# Replay through Olympia (stf_lib-based tools: stf_dump, stf_check, etc.)
# nix run .#olympia -- --input-file prog.stf ...
```

Format details: version 1.5, ISA=RISCV, IEM=RV32. Per-instruction record group (in ascending descriptor order): `STF_INST_PC_TARGET` (taken branches only) → `STF_INST_REG` source records → `STF_INST_REG` dest record → `STF_INST_MEM_ACCESS` + `STF_INST_MEM_CONTENT` (loads/stores/atomics) → `STF_INST_OPCODE32/16` (instruction boundary). FP registers are tracked via the unified integer+FP register file (indices 32–63 = f0–f31). Vector register records are omitted since VRF values are not accessible through `IArchState`. Generator: `STF_GEN_RESERVED` (0).

## Co-simulation contract

Spike is the reference of record for ISA correctness. The contract: **every change to the decoder, executor, register/CSR/trap state, or any train's commit path must keep `SpikeCoSimTests` green.** Those tests run all three trains (`SingleCycleTrain`, `FiveStageTrain`, `OooeTrain`) against `test.elf`, `rich.elf`, and `htif.elf`, comparing every committed instruction's PC, encoding, and integer register writes to Spike commit-for-commit (see *Spike lock-step co-simulation* above). A green run means the simulated datapath agrees with a real RISC-V reference instruction-by-instruction — the strongest correctness signal in the project.

Beyond the three hand-written fixtures, `SingleCycle_Conformance_MatchesSpike` co-simulates all 71 official `riscv-tests` `rv32ui`/`rv32um`/`rv32ua`/`rv32uc`/`rv32uf` ELFs (already shipped under `TestBinaries/isa/`) commit-for-commit — per-instruction verification on top of the self-checking `RiscVTestSuiteTests`, which only inspect the final `gp` pass code. (`ma_data` is excluded: it tests misaligned access, which Spike traps and a handler fixes up while Horologium's `FlatMemory` permits directly, so the two diverge by design.)

If you add an instruction, extension, pipeline behaviour, or fixture, add or extend a co-sim fixture so the new path is covered, and run:

```bash
dotnet test Tests/ --filter "FullyQualifiedName~SpikeCoSim"                            # run the co-sim contract
HOROLOGIUM_REQUIRE_COSIM=1 dotnet test Tests/ --filter "FullyQualifiedName~SpikeCoSim" # CI mode: missing toolchain → failure
```

**Toolchain.** The tests need `spike` and `dtc`; the Nix dev-shell (`flake.nix` + `direnv`) provides both. When the toolchain is absent the tests **skip** rather than fail, so the suite stays runnable everywhere. Set **`HOROLOGIUM_REQUIRE_COSIM=1`** to flip a missing toolchain into a hard failure so the contract cannot be satisfied by silently skipping. The GitHub Actions workflow (`.github/workflows/ci.yml`) enforces exactly this on every pull request and every push to `trunk`, running the full suite (benchmarks excluded) in the flake's lightweight `ci` dev shell. ISA-correctness coverage that does *not* need Spike (e.g. HTIF termination, the official `riscv-tests` self-checks) lives in `HtifExitTests` / `RiscVTestSuiteTests` and always runs.

> Note: Spike sees only the standard ISA. UVE and other custom extensions are invisible to it, so their correctness is covered by Horologium's own integration tests, not co-sim.
