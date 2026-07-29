# Pipeline Trains

Detailed reference for the ISA-agnostic pipeline trains in `src/Core/Pipeline/` (see
[project-layout.md](project-layout.md) for the top-level orientation). `OooeTrain`'s own large feature set (fusion, store sets, value prediction,
runahead, transient-execution defense, ...) lives in [out-of-order-execution.md](out-of-order-execution.md); `CprTrain`
lives in [checkpoint-processing.md](checkpoint-processing.md). ISA coverage (extensions, privilege, virtual memory) is in
[isa-extensions.md](isa-extensions.md).

## SingleCycleTrain and FiveStageTrain (src/Core/Pipeline/)

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
  targets depending on execution history, e.g. virtual dispatch; Seznec, CBP-3/JWAC-2, 2007), IMLI (Inter-Mediated Loop
  Iteration — single shared
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

The five-stage pipeline timing: an instruction is fetched at cycle T, decoded at T+1, executed at T+2, accesses memory
at T+3, and writes back at T+4. Writeback is scheduled at `Phase.Writeback` (6) before Decode runs at `Phase.Commit` (
7), so a register written this cycle is visible to a dependent instruction reading the register file in the same cycle.

ISA mutations (register writes, vector state, CSRs, trap returns) are delivered to the Train through a
`SideEffect Action<IArchState>` closure on `ExecuteResult`, keeping the trains free of any ISA-specific fields.

## SuperscalarTrain (src/Core/Pipeline/)

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
speculation beyond the fetch queue. Macro-fusion (opt-in via `Rv32Mechanism(enableMacroFusion: true)`, off by
default) recognizes RV32's SLT(U)/SLTI(U) + BEQ/BNE-against-zero idiom — the RISC-V analogue of x86 cmp+jcc,
since RV32 branches already embed their own comparison — through `IMechanism.MacroFuser`, and issues the pair as
one issue-slot µop instead of paying the RAW-bypass round-trip between them; `_retiredCounter`/instret still count
both original instructions, so IPC stays meaningful across a fusion-on/off comparison. **Load+ALU micro-fusion**
(same flag, see `OooeTrain` in [out-of-order-execution.md](out-of-order-execution.md)) applies the identical scoreboard-stall-collapse logic here — a load's
destination becomes ready `LoadHitLatency` cycles after issue, so a dependent same-register ALU op fails the RAW
check and issues a cycle later; fusion folds both into one issue slot/cycle, tracked via a separate
`micro_fusions` counter. Store address/data decomposition doesn't apply to `SuperscalarTrain`: it's strictly
in-order issue, so a store already can't issue at all until every source is ready, same as any instruction — there
is no independent per-operand scheduling to split. A **µop cache**
(`UopCache`, Solomon et al. ISLPED 2001; opt-in via `uopCacheSets > 0`, off by default) caches decoded basic
blocks tagged by their start PC — a hit serves the whole cached block straight into the fetch queue, skipping
the I-cache access and decode entirely, with a live predictor call still run for the block's trailing branch (never
a cached target). This is a power paper, not a performance one: decode already costs zero modeled cycles here and
RISC-V's fixed-length ISA never hits the variable-length decode-bandwidth wall the paper solves for x86, so no
cycle-count benefit is expected — the win is a documented one (a hot loop's body builds once, then gets reused),
not a timing claim; skipping the I-cache access on a hit (rather than running it in parallel, as the paper does) is
a noted modeling divergence. Superscalar honors HTIF
tohost-exit stores (`RequestHalt`), and Superscalar,
DAE and SMT advance the cycle CSR (`ArchState.OnCycle`) every cycle — previously frozen `rdcycle` readings made
self-calibrating benchmarks (dhrystone) re-run their measurement loop forever on all three. The Face's pipeline
picker covers `single_cycle`, `five_stage`, `superscalar`, `ooo`, `cpr`, and `dae` (predictor config applies to
five_stage/superscalar/ooo/cpr; the PEvents waterfall supports five_stage, superscalar and ooo), and sweeps select
DAE with `"pipeline": "dae"` (`dae_lane_queue_depth`).

## SmtTrain (src/Core/Pipeline/)

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

## DaeTrain (src/Core/Pipeline/)

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

