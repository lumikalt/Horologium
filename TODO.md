# To-Do

- [ ] Reorganize: split into a prioritized TODO and a separate Ideas/backlog file so near-term work is
  distinguishable from long-horizon research items.

## Face

- [x] Rollback/Back step: single-step backward in the assembler debugger.
- [x] Pipeline stage viewer in Assembler tab.
- [ ] L2 and L3 caches.
- [x] Assembler to simulate RISC-V in-place.
- [x] Compile from C to disassembly and simulate that.
- [ ] Browser assembly support: pure C# RV32 two-pass assembler so the Assemble command works in FaceWeb without a GAS
  subprocess.
  - Also a C compiler…
- [ ] Cache and virtual addressing visualization.
- [x] Execution visualization: Argos-style pipeline waterfall.
- [x] Light mode.
- [ ] Work with other ISAs, not just RISC-V.
- [ ] Waveform/signal viewer: plot pipeline signals (IPC, cache hit rate, branch mispredictions) over simulation time.
- [ ] Power/energy estimation display alongside performance (McPAT-style: dynamic + leakage per unit).
- [ ] Vector operation visualization.
  - Gotta think of how this should be done.
- [ ] Cache management policy selection.
- More cache settings like Ripes.
- [ ] gem5-style architecture configurator: UI surface for the scripting host and pipeline builder — edit `.csx` scripts
  in-app and hot-reload the resulting pipeline, cache hierarchy, branch predictor, and FU configuration without
  restarting.

## CHIP8

- [x] Screen and keyboard.

## RISC-V

- [x] 64-bit support.
  - [ ] ELF64 loader for running RV64 binaries.
  - [ ] Sv39 page-table walker for RV64 virtual memory.
  - [ ] RV64 M extension.
  - [ ] RV64 F/D extension.
- [ ] 128-bit support.
- [x] Enable or disable specific extensions.

### Extensions

- [x] V extension: V1.0 fully implemented (VLEN=128; all integer, FP, mask, permute, reduction, segment, and memory
  ops).
- [x] Zba, Zbb, Zbs, Zicond, Zbc.
- [x] D extension (RV32D): 64-bit FP registers, NaN-boxing for F values, FLD/FSD, all D
  arithmetic/FMA/conversion/comparison ops.
- [ ] Zfh / Zfhmin: half-precision FP.
- [ ] Zfinx / Zdinx / Zhinx: FP operations in integer register file.
- [x] Zicbom / Zicboz / Zicbop: cache management operations.
- [x] Zawrs: wrs.nto and wrs.sto.
- [x] Zimop: mop.r.N and mop.rr.N.
- [ ] Zcmop: compressed may-be-operations.
- [x] Zicntr: hardware performance counters.
- [x] Zihpm: hardware performance monitor CSRs.
- [x] Zabha: byte/halfword atomics.
- [x] Zacas: compare-and-swap word.
- [ ] Zabha+Zacas narrower variants: amocas.b / amocas.h; amocas.d for RV32.
- [ ] Scalar crypto: Zknd/Zkne/Zknh, Zksd/Zkse/Zksh, Zkr.
- [ ] Vector bit manipulation (Zvbb), carry-less multiply (Zvbc), crypto (Zvkn/Zvkg/Zvks).
- [ ] Zvfh / Zvfhmin: vector half-precision FP.
- [ ] H extension (hypervisor): VS-mode, VU-mode, two-stage address translation.
- [ ] Svnapot / Svpbmt / Svadu / Svinval: Sv32/Sv39 page-table extensions.
- [ ] Smaia / Ssaia: Advanced Interrupt Architecture.
- [ ] Smstateen: state-enable CSRs.
- [ ] Smnpm / Ssnpm: pointer masking.
- [x] Supervisor and user-privileged execution (trap delegation, Sv32, page faults, interrupt dispatch).
- [x] Instruction fetch translation through Sv32Walker.
- [-] UVE (Unlimited Vector Extension) — 1D and multi-dimensional streams.
  - [ ] Confirm what's missing. Indirect memory access optimization?
- [ ] UVE 2 (ISCA 2024): predicates, scatter/gather, widening/narrowing.
- [ ] `ss.cfg.vec` effect: vector-width element delivery from load streams.
- [x] SUM: honor `sstatus.SUM` so S-mode can access user pages.
- [ ] gem5 ROI instrumentation for treesum: wire `setStats(1)`/`setStats(0)` markers into the gem5 SE
  simulation so gem5 measures the same kernel interval as Horologium's `SetStatsObserver` and the IPC
  comparison is apples-to-apples.
- [ ] treesum bypass=0 D-cache regression: measure wrong-path load counts to confirm that deeper
  wrong-path execution under 0-cycle forwarding is the source of the +138 D-cache misses and +1 479
  dispatch stall cycles observed when switching from bypass=1 to bypass=0.
- [ ] Implement the rest of the extensions.

### Analysis

- [x] Per-instruction lifecycle events (PEvents).
- [x] Region-of-interest simulation: fast-forward outside named ELF symbol ranges. `--roi-start`/`--roi-end` in
  Runner; fast-forward with SingleCycleTrain until target symbol PC, checkpoint, rebuild with script pipeline, run to
  end symbol or `--max-ticks`.
- [x] Simulation state checkpoint/restore: Option A — architectural-state-only checkpoint (PC, privilege,
  integer/FP registers, CSRs, VRF, UVE scalars, memory). `ArchitecturalCheckpoint.Save/Load/RestoreInto`;
  `--checkpoint-save`/`--checkpoint-load` in Runner; enables fast-forward→detailed pipeline handoffs.
- [ ] Simulation state checkpoint/restore: Option B — full microarchitectural checkpoint: serialize every
  Gear's internal state (ROB, LSQ, issue queues, pipeline latches, cache/TLB line arrays, branch predictor
  tables) so simulation can be suspended and resumed with microarchitectural fidelity — mirrors gem5's
  `serialize`/`unserialize` Checkpoint interface. Requires each `Gear` to implement a serialization contract.
- [x] Elastic trace recording + replay: Horologium-native HELF binary DDG format; register and memory RAW
  dependence capture; critical-path dataflow replay (IPC upper bound); `--elastic-record`/`--elastic-replay` in
  Runner.
- [x] gem5 elastic trace converter: `Gem5ElasticTraceConverter` translating HELF → `inst_dep_record.proto` binary
  stream (LE magic + varint32 length per message, `InstDepRecordHeader` + `InstDepRecord` messages); `--elastic-to-gem5`
  in Runner. Field mapping verified against gem5 source; `decode_inst_dep_trace.py` parses output correctly.
- [x] gem5 fetch trace converter: `Gem5FetchTraceConverter` translating HELF → `packet.proto` binary stream
  (LE magic + varint32, `PacketHeader` + `Packet` messages; one 4-byte `ReadReq` per committed instruction);
  `--fetch-to-gem5` in Runner. Produces the `instTraceFile` companion to `dataTraceFile` for gem5 TraceCPU.
- [x] gem5 nix flake derivation: `nix/gem5.nix` — RISCV build with `HAVE_PROTOBUF=y`, SCons + protobuf + zlib +
  m4; `nix build .#gem5` or available as `gem5` in `nix develop`. `gem5-scripts/trace_cpu_riscv.py` replay config.
- [x] Olympia JSON instruction-trace output; flake packaging; calibration study.
- [ ] JSON-format limitations: no PC/opcode, FP register numbering, vector/UVE ops.
- [x] STF (Simulation Trace Format) binary output.
- [ ] SimPoint phase analysis: basic-block vector (BBV) profiling + k-means clustering for representative sampling. —
  Sherwood et al., ASPLOS 2002
- [ ] ChampSim trace import: run CBP/CRC competition branch predictor and cache replacement plug-ins against Horologium
  workloads.
- [ ] Intel PT (Processor Trace) binary format import: decode hardware-captured execution traces into the elastic replay
  path.
- [ ] RISC-V-PAPI integration: cross-validate Horologium's Zicntr/Zihpm counter output against hardware readings
  collected via the INESC-ID RISC-V PAPI backend on a real core (CVA6 or
  SiFive). https://github.com/hpc-ulisboa/RISC-V-PAPI
- [ ] Paraver trace export: emit Extrae-format traces so carm-paraver can overlay roofline plots on a per-timestep
  execution view. https://github.com/champ-hub/carm-paraver
- [ ] DRAM timing model: integrate Ramulator2 or DRAMSim3 behind `IMemory` so off-chip latency reflects DDR4/5 timing.
- [ ] Power and area estimation: emit gem5-compatible stat dumps consumable by McPAT. — Li et al., MICRO 2009
- [ ] Cache-Aware Roofline Model (CARM) output: compute per-cache-level bandwidth and arithmetic-intensity ceilings from
  simulation statistics and render a roofline plot. — Ilic, Pratas & Sousa, IEEE CAL 2013; Williams, Waterman &
  Patterson, CACM 2009 (base Roofline)
- [ ] Mansard Roofline extension: split each cache-level roof into a read roof and a write roof for more accurate
  mixed-access characterization. — Marques, Ilic & Sousa, ACM TOMPECS 2021

## Performance

- [x] Guard cache-stat collection calls when no cache is configured.
- [x] Eliminate nullable `ulong?` overhead on hot-path structs.
- [ ] Memoization of instructions, results, and branches.
- [ ] O(1) executor dispatch (jump table or virtual dispatch on op kind).
- [ ] Structural stage-model rework for in-order trains: struct latches, fewer interface hops.
- [ ] Value prediction: predict ALU/load results to break dependence chains; commit only if prediction correct. —
  Lipasti & Shen, MICRO 1996 (LVPT); Perais & Seznec, MICRO 2014 (VTAGE/EOLE)

### Parallelism

- [x] Parallelize config sweep in `Experiment.Run`.
- [x] Parallelize multi-workload sweeps.
- [x] Multi-hart concurrency (distinct from run-level parallelism).

## Mechanism

- [x] Generic interfaces for external devices: SiFive UART0 MMIO peripheral (TX/RX) and address-routing peripheral bus.
- [x] ns16550a UART: QEMU virt console UART at 0x10000000 (TX/RX, LSR THRE/TEMT, scratch register).
- [x] CLINT MMIO device: mtime/mtimecmp/msip registers at 0x02000000; MTIP/MSIP delivery via RvTrapController. — RISC-V
  Privileged Spec §3.1.10
- [x] PMP CSRs (pmpcfg0-3, pmpaddr0-15), mstatush, menvcfg, senvcfg — stubbed; accepts writes, no enforcement.
- [x] PLIC MMIO device: interrupt routing, per-context claim/complete cycle, level-triggered pending. — RISC-V PLIC Spec
  v1.0
- [x] VirtIO block device: virtqueue descriptor table, used/avail rings; back with a host file for rootfs. — VirtIO 1.2
  Spec §5.2
- [x] VirtIO DTS node + DTB regen: virtio_mmio node in virt.dts at 0x10001000, PLIC interrupt #1, recompiled
  VirtDtb.Bytes.
- [x] Device Tree Blob (DTB): hand-written virt.dts compiled to virt.dtb and embedded in RiscV32 assembly; exposes via
  VirtDtb.Bytes.
- [x] Raw-binary workload loader: RawBinaryWorkload loads a flat binary at a base address; embeds DTB at a configurable
  address; caller sets a0=hartid, a1=DtbAddress before Run().
- [x] OpenSBI bring-up (milestone 1): boot OpenSBI generic platform (rv32, fw_jump) to banner on SingleCycleTrain —
  fw_jump.bin built with nix build .#opensbi-rv32.
- [x] Linux kernel bring-up (milestone 2): boot Linux 6.12 RV32 NOMMU (nommu_virt_defconfig + M-mode) directly on
  SingleCycleTrain; "Linux version" banner verified on ns16550a UART — nix build .#linux-rv32.
- [x] Cache pre-fetching: next-line and stride (RPT) prefetchers.
- [x] Cache pre-fetching: stream prefetcher (stream buffers for sequential access). — Jouppi, ISCA 1990
- [ ] Cache pre-fetching: spatial memory streaming (SMS) for irregular access patterns. — Somogyi et al., ISCA 2006
- [ ] Cache pre-fetching: IP-based spatial prefetching (IPCP). — Singh et al., ISCA 2020
- [ ] Cache pre-fetching: local-delta prefetcher (Berti). — Bakhshalipour et al., MICRO 2022
- [ ] Cache pre-fetching: RL-driven prefetcher selection (Pythia). — Bera et al., MICRO 2021
- [x] Non-blocking cache with MSHR.
- [ ] Victim cache: small fully-associative buffer to absorb conflict misses. — Jouppi, ISCA 1990
- [ ] Make `ToothClass` a tag instead of an enum?

### Cache Model Realism

- [ ] Write-back buffer (eviction buffer): dirty victims drain to the next level asynchronously from a small
  (4–8 entry) buffer instead of charging the full miss latency synchronously at eviction; stall only when the buffer
  is full or a demand miss targets a line still queued in it. Write-through counterpart: a coalescing write buffer so
  stores don't pay backing latency individually.
- [ ] Cache-level MSHRs with hit-under-miss: move outstanding-miss tracking from the pipeline into each cache level;
  a secondary miss to a line already in flight merges into the existing MSHR entry instead of paying a second full
  miss, and the cache continues serving hits while misses are outstanding. Makes L2/L3 non-blocking too. — Kroft,
  ISCA 1981
- [ ] Sequential tag/data access mode: gem5's third timing knob alongside tag/data latency — hit latency
  = tag + data (probe tags first, then read only the matching way) instead of max(tag, data); typical for large
  lower-level caches.
- [ ] Inclusion policy per level pair: inclusive (Intel-style — lower-level eviction back-invalidates the line in
  upper levels), exclusive (AMD-style — lower levels act as victim caches for the level above), or NINE
  (non-inclusive non-exclusive, the current behavior).
- [ ] Critical-word-first / early restart: a miss fill returns the demanded word first so the load resumes after the
  leading edge while the rest of the line streams in; matters when block size is large relative to miss latency.
- [ ] Banked caches and port limits: N banks with conflict stalls on same-bank concurrent accesses; configurable
  read/write port counts (the OoO train currently has unlimited D-cache bandwidth).
- [ ] Per-sector dirty/valid bits: sectored lines so writebacks transfer only dirty sectors and fills can be partial;
  bandwidth refinement over whole-line granularity.
- [ ] Zicbom write-back semantics: wire cbo.clean/cbo.flush/cbo.inval into dirty-line state now that write-back
  caches track it (clean = writeback and keep, flush = writeback and invalidate, inval = discard without writeback).

### Cache Replacement

Pluggable replacement policies via `IReplacementPolicy`; `SetAssociativeCache` accepts `ReplacementPolicyKind`.
Implemented:

- [x] Random: random victim selection; baseline with no recency tracking.
- [x] FIFO: circular pointer replacement; evicts oldest-installed block, ignores hits.
- [x] MRU (Most-Recently-Used): evicts the most recently hit block; new installs placed at LRU position; scan-resistant
  complement to LRU.
- [x] CLOCK (Second-Chance): one reference bit per way, circular hand; referenced ways get a second chance (bit cleared,
  hand advances); unreferenced way at hand is evicted. OS-style LRU approximation.
- [x] RRIP (Re-Reference Interval Prediction): SRRIP-HP, BRRIP-HP, DRRIP-HP with Set Dueling (32-set SDMs, 10-bit PSEL,
  ε=1/32). — Jaleel et al., ISCA 2010
- [x] SHiP (Signature-based Hit Predictor): SHiP-Mem variant; SHCT 16K × 3-bit saturating counters layered on
  SRRIP-HP. — Wu et al., MICRO 2011
- [x] SHiP-PC: SHiP variant using load PC as signature; requires threading PC through the cache access path. — Wu et
  al., MICRO 2011
- [x] Tree-PLRU (Pseudo-LRU): binary tree of bits per set; exact LRU for 2-way, hardware-friendly approximation for
  wider associativity (Intel P6 and later).
- [x] Hawkeye: OPTgen-based Belady-inspired replacement; PC-indexed 3-bit saturating-counter predictor; cache-friendly
  lines insert at RRPV=0, cache-averse at RRPV=7; SRRIP-style victim selection. — Jain & Lin, ISCA 2016
- [ ] LFU (Least Frequently Used): frequency-based eviction; evicts the line with the lowest access count;
  straightforward baseline for frequency-aware policies.
- [ ] TinyLFU: compact approximate-frequency sketch (Count-Min or counting Bloom filter) gated by a doorkeeper;
  frequency admission filter for SLRU-style main cache. — Einziger et al., IEEE Trans. Computers 2017
- [ ] ARC (Adaptive Replacement Cache): two LRU lists (T1 recency, T2 frequency) with a ghost-entry feedback loop that
  self-tunes the split point p. — Megiddo & Modha, FAST 2003
- [ ] Hyperbolic caching (HyperbolicPolicy): each line assigned a priority = hits / age; evict the line with the lowest
  priority at miss time; pure frequency × time trade-off with no parameters. — Blankstein et al., USENIX ATC 2017
- [ ] LECAR (Least Expected Cost under Adaptive Replacement): hybrid of LFU and LRU using a two-armed bandit (
  exponential-weight update) to dynamically pick between the two policies based on measured regret. — Vietri et al.,
  HotStorage 2018
- [ ] LRB (Learning-based Replacement beyond Belady): per-line feature vector (reuse distance, frequency, access
  pattern) fed to a lightweight learned predictor trained with gradient boosting to approximate Belady's offline optimal
  policy. — Song & Elber, ASPLOS 2020; Shi et al., ASPLOS 2019 (variant)
- [ ] Cache replacement competition (CRC) plug-in interface: match ChampSim's policy API so research policies drop in.

### Out-of-Order Execution

- [x] Functional-unit classes with configurable count and per-class latency.
- [x] Memory order violation detection and squash.
- [x] Separate load queue and store queue for speculative memory disambiguation.
- [x] Streaming Engine for UVE wired into OooeTrain.
- [ ] Store sets for memory dependence prediction: predict which loads depend on which stores to avoid unnecessary
  stalls. — Chrysos & Emer, ISCA 1998
- [x] Register renaming: explicit rename stage with a register alias table (RAT) and free list, replacing implicit PRF
  indexing.
- [x] Rename event in PEventLog: emit a distinct Rn event at decode-queue drain (separate from Dispatch/Ds) to match
  gem5's O3CPU stage breakdown and make Konata-style rename timing visible in the waterfall.
- [x] True oracle branch predictor: two-pass simulation (pre-run to collect outcomes, replay with perfect prediction)
  for IPC upper-bound measurement.

### µops

- [ ] µop cache (decoded instruction cache / loop buffer): cache decoded µop bundles so the front-end skips re-decode on
  repeated loops.
- [ ] Macro-fusion: fuse compare+branch pairs into a single issue-slot µop (as in Intel Sandy Bridge onward).
- [ ] Micro-fusion: fuse load+ALU or store-address+store-data into a single dispatch slot.
- [ ] Loop stream detector: detect short loops and replay µops from a small buffer, bypassing fetch and decode.
- [ ] µop decomposition for complex instructions: atomics, vector ops, and CSR accesses emit multi-µop sequences through
  the Tooth interface.

### Branch Prediction

- [x] Hashed Perceptron. — Jiménez & Lin, HPCA 2001
- [x] Path-based Perceptron. — Jiménez, MICRO 2003
- [x] ITTAGE (tagged geometric history; indirect targets). — Seznec & Michaud, JILP 2006 (TAGE base); Seznec, CBP-4 2011
- [x] BATAGE. — Seznec, CBP 2016
- [x] IMLI: inter-iteration loop branch predictor (counts loop iterations in hardware). — Jiménez, IEEE CAL 2018
- [x] LLBP: The Last-Level Branch Predictor — Schall et al., MICRO 2024. Context-addressed backing store on top of
  TAGE-SC-L, keyed by a Rolling Context Register (RCR) that hashes recent taken-branch
  PCs. https://ieeexplore.ieee.org/abstract/document/11408567/
- [x] LLBP-X: The Last-Level Branch Predictor Revisited — Schall et al., HPCA 2026. Extends LLBP with dynamic
  context-depth adaptation via a Context Tracking Table (CTT): shallow W=2 contexts promote to deep W=64 when their
  pattern set fills and history-length trends long; short/long history ranges are kept in separate storage partitions.
  `LlbpXPredictor : LlbpPredictor`. https://ieeexplore.ieee.org/document/11408567
- [x] VLA-TAGE: Vector-Loop-Aware TAGE — Zhang et al., IEEE CAL 2026. Power-gating extension of TAGE-SC-L: gates T1–T3 +
  SC when the innermost loop is vector-intensive with ≥32 estimated remaining iterations; Loop Monitor estimates from
  comparison-register operand values; PEN deasserts 5 iterations early. https://ieeexplore.ieee.org/document/11417886
- [ ] Branch pre-computation (TEA): https://hps.ece.utexas.edu/pub/TEA.pdf
- [ ] CBP-2025 front runner: correlate on register values rather than history.
- [ ] BranchNet: CNN predictor. — Zangeneh et al., MICRO 2020
- [ ] Multiperspective Perceptron. — Tarjan & Skadron, IEEE Trans. Computers 2005
- [ ] Bullseye/SDM as H2P helpers.
- [ ] Indirect branch predictor: VTAGE/iBMETA variant for computed jumps and virtual dispatch.

## GPU

- [ ] SIMT execution model: warp scheduling, thread divergence, and stack-based reconvergence (PDOM). — Fung et al.,
  MICRO 2007
- [ ] Warp occupancy and resource partitioning: warps, registers, and shared memory per streaming multiprocessor.
- [ ] GPU register file banking: per-warp interleaved allocation to hide RAW latency via warp switching.
- [ ] GPU memory hierarchy: per-SM L1 / shared memory scratchpad, unified L2, global memory.
- [ ] Memory coalescing: merge warp-wide 32-thread loads/stores into minimal cache-line-sized transactions.
- [ ] Thread block scheduler: distribute thread blocks across SMs subject to occupancy constraints.
- [ ] PTX / SASS instruction set (NVIDIA) or SPIR-V (vendor-agnostic) as a new ISA plugin.
- [ ] GPGPU-Sim integration for co-simulation and workload comparison. — Bakhoda et al., ISPASS 2009
- [ ] Accel-Sim: trace-driven GPU microarchitecture simulation framework. — Khairy et al., ISCA 2020
- [ ] MGPUSim: multi-GPU simulation (AMD GCN architecture). — Sun et al., ISCA 2019
- [ ] GPU performance model from PTX: static LSTM-based prediction of execution time, power, and energy under DVFS from
  PTX instruction sequences; see gpuPTXModel (INESC-ID). https://github.com/hpc-ulisboa/gpuPTXModel
- [ ] GPU power modeling: data-driven power model for GPU kernels; see gpupowermodel (
  INESC-ID). https://github.com/hpc-ulisboa/gpupowermodel

## Co-simulation

- [x] Spike online lock-step co-simulation (SingleCycle, FiveStage, OoOE; HTIF tohost; riscv-tests; torture tests).
- [ ] Watchdog on `ReadLine` to fail cleanly on over-run instead of hanging.
- [ ] CI workflow with `HOROLOGIUM_REQUIRE_COSIM=1`.
- [ ] gem5 timing co-simulation.
- [ ] QEMU lock-step co-simulation: use QEMU as a fast functional oracle for full-system workloads, hand off to
  Horologium for timing.
- [ ] dromajo co-simulation: WD's RISC-V checkpoint-based reference model; supports importing architectural state
  mid-program.
- [ ] whisper co-simulation: Intel's RISC-V ISS with fine-grained CSR and trap comparison hooks.
- [ ] SAIL RISC-V integration: use the formal ISA model as the instruction-semantics oracle instead of Spike.
- [ ] riscv-formal: SymbiYosys-based bounded model checking of ISA-compliance properties against the decoder/executor.
- [ ] FireSim: FPGA-accelerated cycle-exact simulation for large-scale multicore validation against RTL.
- [ ] Snipersim co-comparison: interval-simulation IPC model as a lightweight cross-check. — Carlson et al., ISCA 2011
- [ ] UVE-patched Spike co-simulation: use the INESC-ID/HPCAS Spike fork (Baptista MSc 2023) as a functional oracle for
  UVE streaming instructions, the same way scalar Spike is used
  today. https://hpcas.inesc-id.pt/~unify/papers/MSc_JoaoBaptista23.pdf
- [ ] UVE gem5 model cross-check: compare Horologium's UVE timing (issue latency, stream-engine fill cycles) against the
  gem5 UVE branch from hpc-ulisboa. https://github.com/hpc-ulisboa/UVE

## Multicore

- [x] F18A: 144-node GA144 grid with RendezvousArbor channels.
- [x] Multi-hart simulation: multiple OoOE trains sharing a memory hierarchy.
- [x] MOESIF cache coherence: MOESI + MESIF Forward state; snooping bus and directory bus.
- [x] Cache-to-cache supply on read and write misses (RFO forwarding).
- [x] TSO fence modeling: store→load FENCE drains the write buffer.
- [x] LR/SC memory safeguard across pipeline trains and cache buses.
- [x] `MultiHartKernel`: round-robin scheduler for N RISC-V harts against shared memory.
- [x] `MultiHartPipeline`: ISA-agnostic coordinator for N `ISteppableTrain` instances.
- [x] `SmtTrain`: barrel-processor SMT with N per-hart contexts sharing an issue window.
- [x] `RunConcurrent`: two-phase parallel tick with `DeferredBus` for well-synchronized programs.
- [ ] AMBA CHI (Coherent Hub Interface): point-to-point request/response/snoop channels as an alternative to the
  snooping bus; needed for large core counts where broadcast is impractical.
- [ ] NUMA topology: model non-uniform memory access latency across banks or NUMA nodes.
- [ ] Memory-side cache (HBM-style): a large, flat last-level cache sitting between the coherence fabric and off-chip
  DRAM.
- [ ] Network-on-chip (NoC) model: mesh or torus with wormhole routing and virtual channels, replacing the broadcast
  bus. — BookSim2 (Jiang et al., ISPASS 2013); Garnet (gem5)
- [ ] Persistent memory (CXL / PMDK): model CXL.mem-attached byte-addressable storage with ordering and persistence
  semantics.
- [ ] Near-Data Processing (NDP): attach compute units at the cache or DRAM level and model their interaction with the
  coherence fabric; see NDPmulator (INESC-ID, gem5-based) as a reference
  design. https://github.com/hpc-ulisboa/NDPmulator

## Orrery

- [x] Roslyn C# scripting host: embed `Microsoft.CodeAnalysis.CSharp.Scripting` so `.csx` files can instantiate and
  configure trains, caches, and predictors directly against the live assemblies — no wrapper layer needed.
  - [x] Maybe also F#?
- [ ] gem5-style architecture builder: a composable builder API covering pipeline topology, cache hierarchy, branch
  predictor, FU counts and latencies, and multicore interconnect — the structural wiring that goes beyond `TrainConfig`'
  s flat parameter record; the Roslyn scripting host is the primary consumer.
  - [x] Phase 1 — Cache hierarchy shape: structural description of the full cache stack — per-level capacity,
    associativity,
    block size, and access latency; private-vs-shared topology across levels; replacement policy and prefetcher choice
    per I/D path.
  - [x] Phase 2 — Pipeline topology: structural description of a single pipeline — train variant (single-cycle through
    OoO),
    forwarding, store buffer depth, issue width, reorder buffer and issue queue depth, physical register count,
    functional unit class counts and latencies, branch predictor kind and parameters.
  - [x] SmtTrain spec variant: extend PipelineSpec hierarchy with an SmtSpec that handles the N-hart, multi-mechanism
    Build() signature; requires a different contract than the current single-hart Build(IMechanism, IMemory, …).
  - [x] Phase 3 — Multicore topology: structural description of N-hart configurations — per-hart private caches, shared
    last-level cache descriptor, and coherence bus topology (snooping vs directory).
    - [x] Expose `RunConcurrent` from `MulticoreHandle`: two-phase parallel tick backed by `DeferredBus` for
      well-synchronized workloads; wrap `MultiHartPipeline.RunConcurrent(DeferredBus[])` behind the handle's API.
  - [x] Phase 4 — Machine assembly layer: `MachineSpec` + `MachineHandle` — single-hart analog of `MulticoreSpec`;
    builds the full `CacheHierarchySpec` stack externally (unified I/D, per-level policy, arbitrary depth) and passes
    the accessor top as backing; exposes `MemoryLayers` for cache statistics; `ISteppableTrain` now declares `Run(maxTicks,
    warmupTicks, snapshotInterval)` as a formal contract.
    - [x] Split I/D support: teach `*Core` gears to accept pre-built I and D `MemoryLayers` so `MachineSpec` can pass
      separate I and D cache chains; requires new overload on each train constructor and `PipelineSpec.Build()`.
    - [x] TLB support in `MachineSpec`: `CacheHierarchySpec` has no TLB concept; TLB must come from a separate spec
      field or `MemoryConfig` integration.
  - [x] Phase 5 — Scripting surface (headless): `Script` project wraps `Microsoft.CodeAnalysis.CSharp.Scripting`;
    `ScriptHost.EvaluateFileAsync(path)` evaluates a `.csx` returning a `MachineSpec`; `runner --script <file.csx>`
    runs the workload against it; `scripts/example.csx` ships as a worked sample.
    - [ ] Phase 5 — Scripting surface (UI): Face configurator tab — AvaloniaEdit code editor, hot-reload on file change
      via `FileSystemWatcher`, workload selector, and live cache/TLB stat display.
- [ ] Generic definition for a parser.
- [ ] Clock domain crossing: model multiple frequency domains (e.g., core at 3 GHz, uncore/LLC at 1.5 GHz) with
  synchronization FIFOs.
- [x] Interrupt controller model (PLIC): route external and inter-processor interrupts to the correct hart and
  privilege level.
- [ ] APLIC (Advanced Interrupt Architecture PLC): MSI delivery, domain model, direct and MSI modes. — RISC-V AIA Spec

## Tools

External tools worth evaluating for integration, co-sim, or methodology comparison.

### Simulators and trace frameworks

- **ChampSim** — trace-based µarch simulator; the baseline infrastructure for CBP and CRC (cache replacement)
  competitions. Useful for validating Horologium's branch predictor and cache replacement implementations against
  competition submissions. https://github.com/ChampSim/ChampSim
- **Snipersim** — interval-simulation model driven by a Pin front-end; fast parallel simulation for many-core
  studies. https://snipersim.org
- **ZSim** — fast x86 simulation with interval timing and detailed cache/coherence models. — Sanchez & Kozyrakis, ISCA
  2013. https://github.com/s5z/zsim
- **DynamoRIO** / **Intel PIN** — dynamic binary instrumentation for generating instruction and memory access traces
  that feed Horologium's replay path.
- **Simpoint** — program phase analysis: generates representative samples via BBV clustering to avoid full-program
  simulation. https://cseweb.ucsd.edu/~calder/simpoint/
- **gem5-SST bridge** — couple gem5's detailed memory model to SST's network/system simulation for heterogeneous
  platform studies.

### Memory and DRAM

- **Ramulator2** — flexible, validated DRAM timing model (DDR4/5, LPDDR5, HBM2/3); drop-in backend behind
  `IMemory`. https://github.com/CMU-SAFARI/ramulator2
- **DRAMSim3** — cycle-accurate DRAM timing with bandwidth and power traces. https://github.com/umd-memsys/DRAMsim3
- **NVMain2** — NVM timing model for PCM, ReRAM, 3D XPoint; useful for persistent memory studies.
- **CACTI** — cache area, access time, and power estimation given capacity, associativity, and technology node.

### Power and energy

- **McPAT** — processor power, area, and timing estimation from architectural event counts; consumes gem5-format
  statistics. https://github.com/HewlettPackard/mcpat
- **CARM Tool** (CHAMP Hub / INESC-ID) — cross-platform Cache-Aware Roofline Model benchmarking suite; measures peak
  FLOP/s and per-level bandwidth ceilings on real Intel/AMD/ARM/RISC-V hardware, providing the empirical baseline
  against which Horologium's roofline output can be validated. Supports
  RVV. https://champ-hub.github.io/projects/The_CARM_Tool/

### INESC-ID / HPCAS

- **UVE gem5 model** — gem5 branch with full UVE streaming-engine implementation; primary timing reference for
  Horologium's UVE execution model. https://github.com/hpc-ulisboa/UVE
- **UVE2 spec** — C reference implementation of the UVE 2 extension (predicates, scatter/gather, widening/narrowing);
  the authoritative source for the UVE 2 TODO item. https://github.com/hpc-ulisboa/UVE2
- **Spike UVE patch** — INESC-ID's Spike fork adding UVE decode and functional execution; the co-simulation oracle for
  UVE instructions (Baptista MSc 2023). https://hpcas.inesc-id.pt/~unify/papers/MSc_JoaoBaptista23.pdf
- **RISC-V-PAPI** — PAPI hardware-counter backend for RISC-V; use to cross-validate Horologium's Zicntr/Zihpm dial
  values against measurements on real cores (CVA6, SiFive). https://github.com/hpc-ulisboa/RISC-V-PAPI
- **NDPmulator** — gem5-based Near-Data Processing simulation framework; reference design for NDP compute units attached
  at cache or DRAM level. https://github.com/hpc-ulisboa/NDPmulator
- **MIDAS** — CGRA mapping infrastructure: takes C kernels, emits dataflow graphs and PE schedules for synchronous
  streaming arrays; relevant for understanding how UVE streams map to hardware. https://github.com/hpc-ulisboa/MIDAS
- **carm-paraver** — overlays CARM roofline plots on Paraver execution traces (BSC's trace analysis format), enabling
  per-timestep performance/bandwidth visualization. https://github.com/champ-hub/carm-paraver
- **gpuPTXModel** — LSTM-based static GPU performance model from PTX instruction sequences; predicts execution time,
  power, and energy under DVFS. https://github.com/hpc-ulisboa/gpuPTXModel
- **gpupowermodel** — data-driven GPU power model for kernel-level energy
  characterization. https://github.com/hpc-ulisboa/gpupowermodel

### Formal and RTL verification

- **SAIL RISC-V** — the authoritative formal ISA model; generates test vectors and can serve as an instruction-semantics
  oracle. https://github.com/riscv/sail-riscv
- **riscv-formal** — SymbiYosys-based bounded model checking for ISA compliance properties against a decoder/executor;
  useful for formal correctness proofs. https://github.com/SymbioticEDA/riscv-formal
- **Verilator** — RTL→C++ cycle-accurate simulation; enables co-simulation against synthesizable RISC-V core RTL (e.g.,
  CVA6, BOOM, Ibex).

## Other ISAs

| Architecture       | Paradigm to test                                                                       | Level |
|--------------------|----------------------------------------------------------------------------------------|-------|
| ~~SUBLEQ~~         | OISC, no opcode field                                                                  | 1     |
| ~~PDP-8~~          | Accumulator, 12b, minimal opcodes                                                      | 2     |
| ~~J1 Forth~~       | Stack machine, packed opcodes                                                          | 2     |
| LGP-30             | Drum memory, bit-serial arithmetic, rotational latency scheduling                      | 2     |
| Nintendo CIC (SM5) | 4-bit copy-protection MCU; minimal accumulator, external ROM, hardware handshake loop  | 1     |
| MN101              | Panasonic 8-bit MCU; conventional accumulator with bit-manipulation and multiply ops   | 2     |
| RL78               | Renesas 16-bit Harvard MCU; CISC addressing modes and bit-addressable I/O registers    | 2     |
| ~~TTA/MOVE~~       | Triggered side-effect execution                                                        | 3     |
| ~~GA144 F18A~~     | Async, multi-core, packed 5-op words                                                   | 3     |
| MIL-STD-1750A      | Committee designed, spec driven                                                        | 3     |
| IBM 650            | Instructions contain their successor's drum address (optimum programming)              | 3     |
| IBM 1401           | Variable-length decimal fields delimited by word marks, character-addressable          | 3     |
| EE KDF9            | Dual hardware stacks ("nesting stores"), zero-address arithmetic                       | 3     |
| Setun              | Balanced ternary (trits: -1, 0, +1), no binary anywhere                                | 3     |
| Parallax Propeller | 8 symmetric cogs, deterministic hub-cycle slots, no interrupts, wait-based I/O         | 3     |
| HP Saturn          | 4-bit bus, 64-bit registers addressed by nibble fields, BCD-centric                    | 3     |
| TeakLite/XpertTeak | DSi/3DS coprocessor DSP; dual-MAC pipeline, zero-overhead loops, circular addr regs    | 3     |
| SuperFX (GSU)      | Argonaut/Nintendo SNES coprocessor; cached RISC with dedicated PLOT pixel-write op     | 3     |
| µ'nSP              | SunPlus 16-bit MCU (V.Smile, toys); segmented addressing, compact 16-bit encoding      | 3     |
| MAXQ               | Maxim/Dallas move-only stack machine; all computation as moves through a Transfer Map  | 3     |
| VS_DSP4            | SunPlus/embedded DSP; multiply-accumulate with saturation, bit-reversed addressing     | 3     |
| OpenRISC 1000      | Open-source RISC; multiple implementations with known spec divergences                 | 3     |
| Burroughs B5000    | Tagged stack-machine, segmented memory                                                 | 4     |
| Symbolica/CADR     | Full tagged LISP machine                                                               | 4     |
| IA-64/Itanium      | VLIW with templates and predication                                                    | 4     |
| CDC 6600           | 60-bit ones' complement (two zeros!), scoreboard, plus 10 barrel-threaded PPUs         | 4     |
| Transputer T800    | CSP channels in hardware, on-chip process scheduler, workspace-relative addressing     | 4     |
| Tera MTA           | 128-way barrel multithreading, full/empty bits on every memory word, no cache          | 4     |
| Pendulum (PISA)    | Fully reversible ISA — every instruction must be invertible, no destructive writes     | 4     |
| FR-V               | Fujitsu VLIW; 1–8 issue slots per bundle, no hardware interlocks, all hazards visible  | 4     |
| Mill Belt          | Belt-machine, no register file                                                         | 5     |
| TRIPS/WaveScalar   | True dataflow, no PC                                                                   | 5     |
| Intel iAPX 432     | Bit-aligned variable-length instructions (6–321 bits), capability objects, hardware GC | 5     |
| Burroughs B1700    | Bit-addressable memory, reconfigurable microarchitecture                               | 5     |
| Rekursiv           | Object-oriented down to the microcode, persistent objects instead of memory            | 5     |
