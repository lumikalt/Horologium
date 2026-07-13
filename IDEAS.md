# Ideas / Backlog

Long-horizon research items, external-tool integrations, and speculative directions. Near-term,
actionable work lives in [TODO.md](TODO.md); items graduate from here to there when they become
the active thread.

## RISC-V

- [ ] 128-bit support.
- [ ] Zfinx / Zdinx / Zhinx: FP operations in integer register file.
- [ ] Scalar crypto: Zknd/Zkne/Zknh, Zksd/Zkse/Zksh, Zkr.
- [ ] Vector bit manipulation (Zvbb), carry-less multiply (Zvbc), crypto (Zvkn/Zvkg/Zvks).
- [ ] Zvfh / Zvfhmin: vector half-precision FP.
- [ ] H extension (hypervisor): VS-mode, VU-mode, two-stage address translation.
- [ ] Svnapot / Svpbmt / Svadu / Svinval: Sv32/Sv39 page-table extensions.
- [ ] Smaia / Ssaia: Advanced Interrupt Architecture.
- [ ] Smstateen: state-enable CSRs.
- [ ] Smnpm / Ssnpm: pointer masking.
- [ ] Implement the rest of the extensions.

## Analysis

- [ ] Simulation state checkpoint/restore: Option B — full microarchitectural checkpoint: serialize every
  Gear's internal state (ROB, LSQ, issue queues, pipeline latches, cache/TLB line arrays, branch predictor
  tables) so simulation can be suspended and resumed with microarchitectural fidelity — mirrors gem5's
  `serialize`/`unserialize` Checkpoint interface. Requires each `Gear` to implement a serialization contract.
- [ ] SimPoint phase analysis: basic-block vector (BBV) profiling + k-means clustering for representative sampling. —
  Sherwood et al., ASPLOS 2002
- [ ] SMARTS: systematic statistical sampling with functional warming between detailed sample windows. — Wunderlich
  et al., ISCA 2003
- [ ] LoopPoint: checkpoint-driven sampling methodology for multi-threaded workloads; the multi-hart counterpart to
  SimPoint. — Sabu et al., HPCA 2022
- [ ] Top-Down Microarchitecture Analysis (TMA): frontend-bound / backend-bound / bad-speculation / retiring slot
  accounting computed from dial values. — Yasin, ISPASS 2014
- [ ] CPI stacks via interval analysis: per-miss-event cycle accounting for OoO cores (as in Sniper), attributing
  stall cycles to branch mispredictions, cache misses, and dependences. — Eyerman et al., ASPLOS 2006 / ACM TOCS 2009
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

- [ ] Value prediction: predict ALU/load results to break dependence chains; commit only if prediction correct. —
  Lipasti & Shen, MICRO 1996 (LVPT); Perais & Seznec, MICRO 2014 (VTAGE/EOLE)

## µops

- [ ] µop cache (decoded instruction cache / loop buffer): cache decoded µop bundles so the front-end skips re-decode on
  repeated loops.
- [ ] Macro-fusion: fuse compare+branch pairs into a single issue-slot µop (as in Intel Sandy Bridge onward).
- [ ] Micro-fusion: fuse load+ALU or store-address+store-data into a single dispatch slot.
- [ ] Loop stream detector: detect short loops and replay µops from a small buffer, bypassing fetch and decode.
- [ ] µop decomposition for complex instructions: atomics, vector ops, and CSR accesses emit multi-µop sequences through
  the Tooth interface.

## Front-End

- [ ] Boomerang / Shotgun: metadata-free front-end prefetching that unifies BTB prefill and I-cache prefetch under the
  branch predictor. — Kumar et al., HPCA 2017 / ASPLOS 2018
- [ ] EIP (entangling instruction prefetcher): links the instruction that gives timely coverage ("entangler") to the
  miss it hides. — Ros & Jimborean, ISCA 2021

## Cache Prefetching

- [ ] Spatio-temporal memory streaming (STeMS) extending SMS with temporal miss-sequence recording. — Somogyi et al.,
  ISCA 2009
- [ ] Best-Offset Prefetcher (BOP): offset-selection tournament with timeliness scoring; DPC-2 winner. — Michaud,
  HPCA 2016
- [ ] Signature Path Prefetcher (SPP): compressed access-pattern signatures with path-confidence lookahead; optional
  perceptron prefetch filter (PPF) on top. — Kim et al., MICRO 2016; Bhatia et al., ISCA 2019
- [ ] MLOP (multi-lookahead offset prefetcher): BOP generalized to score offsets at multiple lookahead depths; DPC-3
  winner. — Shakerinava et al., DPC-3 2019
- [ ] ISB (irregular stream buffer): linearizes PC-localized correlated irregular streams into a structural address
  space for temporal prefetching. — Jain & Lin, MICRO 2013
- [ ] Temporal memory streaming: record long miss sequences in off-chip metadata and replay them on a matching miss
  (STMS; Domino). — Wenisch et al., ISCA 2005 / HPCA 2009; Bakhshalipour et al., HPCA 2018
- [ ] Bingo: spatial prefetcher associating footprints with multiple event signatures in a single table. —
  Bakhshalipour et al., HPCA 2019
- [ ] Hermes: off-chip load prediction — a perceptron predicts which loads will miss the entire hierarchy and starts
  the DRAM access early, in parallel with cache lookup. — Bera et al., MICRO 2022
- [ ] Voyager: hierarchical neural data prefetcher (offline-trained LSTM over page and offset vocabularies). — Shi et
  al., ASPLOS 2021

## Memory System

- [ ] Memory controller scheduling: FR-FCFS baseline plus thread-aware policies (TCM, BLISS); pairs with the
  Ramulator2/DRAMSim3 integration. — Rixner et al., ISCA 2000; Kim et al., MICRO 2010; Subramanian et al., ICCD 2014
- [ ] Cache compression: base-delta-immediate (BΔI) compressed caches with variable effective capacity. — Pekhimenko
  et al., PACT 2012
- [ ] Die-stacked DRAM cache: Alloy cache — direct-mapped tag-and-data alloying for latency-optimized giga-scale
  caches; concrete design for the memory-side-cache item. — Qureshi & Loh, MICRO 2012
- [ ] MMU translation research: page-walk caches / translation caching ("skip, don't walk") and TLB prefetching;
  builds on the Sv32 walker. — Barr, Cox & Rixner, ISCA 2010; Kandiraju & Sivasubramaniam, ISCA 2002

## Security

- [ ] Transient-execution defense modeling: invisible speculative loads (InvisiSpec) and speculative taint tracking
  (STT); measure the IPC cost of each defense on the OoO train. — Yan et al., MICRO 2018; Yu et al., MICRO 2019
- [ ] Randomized/partitioned cache side-channel defenses: CEASER(-S) encrypted-address remapping and ScatterCache
  skewed randomization. — Qureshi, MICRO 2018 / ISCA 2019; Werner et al., USENIX Security 2019

## Multicore & Fabric

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
- [ ] Coherence protocol DSL: SLICC-style state-machine description so protocols (MESI, MOESI, token coherence) are
  pluggable specifications rather than hard-coded bus logic. — GEMS/Ruby, Martin et al., SIGARCH CAN 2005

## Co-simulation

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
- [ ] RTL functional-unit substitution: swap one pipeline FU (e.g. a custom ALU or accelerator) for cycle-accurate RTL
  via Verilator, so the surrounding pipeline drives real hardware instead of the C# functional/latency model for that
  unit — useful for validating a custom-unit design against the rest of the system before tapeout/FPGA. See
  `~/dl/citations.csv` for candidate references.

## Orrery

- [ ] Generic definition for a parser.
- [ ] Clock domain crossing: model multiple frequency domains (e.g., core at 3 GHz, uncore/LLC at 1.5 GHz) with
  synchronization FIFOs.
- [ ] APLIC (Advanced Interrupt Architecture PLC): MSI delivery, domain model, direct and MSI modes. — RISC-V AIA Spec

## Face

- [ ] Work with other ISAs, not just RISC-V.
- [ ] Power/energy estimation display alongside performance (McPAT-style: dynamic + leakage per unit).

## Open Questions

- [ ] Make `ToothClass` a tag instead of an enum?

## Emerging Technologies

- [ ] NPU / systolic-array accelerator: weight-stationary TPU-style matrix-multiply unit as a memory-mapped
  accelerator on the fabric; validate timing against SCALE-Sim. — Jouppi et al., ISCA 2017 (TPU); Samajdar et al.,
  ISPASS 2020 (SCALE-Sim)
- [ ] Dataflow accelerator design space: row-stationary dataflow (Eyeriss) and flexible reduction interconnects
  (MAERI); STONNE as reference simulator. — Chen et al., ISCA 2016; Kwon et al., ASPLOS 2018; Muñoz-Martínez et al.,
  IISWC 2021
- [ ] RISC-V matrix extension: integrated (IME) or attached (AME) matrix-multiply ISA proposals as a decoder/executor
  plugin; Gemmini as the reference tightly-coupled accelerator design. — RISC-V AME task group; Genc et al., DAC 2021
- [ ] Processing-in-memory (PIM): UPMEM-style general-purpose DRAM processing units with explicit host offload;
  PrIM benchmark suite for workload characterization. — Gómez-Luna et al., IEEE Access 2022; Mutlu et al., "A Modern
  Primer on Processing in Memory", 2022
- [ ] Bank-level PIM in HBM/GDDR: FIMDRAM/HBM-PIM and AiM — SIMD FP units at the DRAM bank, near-bank operand reuse,
  host-visible PIM command protocol. — Kwon et al., ISSCC 2021; Lee et al., ISSCC 2022
- [ ] In-DRAM bulk bitwise operations: charge-sharing triple-row activation (Ambit) and the SIMDRAM end-to-end
  framework for arbitrary bit-serial computation. — Seshadri et al., MICRO 2017; Hajinazar et al., ASPLOS 2021
- [ ] In-SRAM / compute-cache computing: bit-line computing in cache arrays (Compute Caches), Neural Cache, Duality
  Cache. — Aga et al., HPCA 2017; Eckert et al., ISCA 2018; Fujiki et al., ISCA 2019
- [ ] CGRA fabric: Plasticine-style reconfigurable spatial array of pattern compute/memory units; map kernels via
  MIDAS. — Prabhakar et al., ISCA 2017
- [ ] Chiplet / multi-die topology: UCIe-style die-to-die links with distinct latency/bandwidth/energy from on-die
  interconnect; interposer-based disaggregation. — UCIe spec; Kannan, Jerger & Loh, MICRO 2015
- [ ] CXL memory tiering: far-memory pooling with hotness-based page placement/migration (Pond, TPP); extends the
  existing CXL persistent-memory item. — Li et al., ASPLOS 2023; Maruf et al., ASPLOS 2023
- [ ] Neuromorphic / spiking core: Loihi-style asynchronous SNN mesh (spike routing, per-core synapse/neuron state) as
  an exotic non-von-Neumann plugin alongside the Other-ISAs list. — Davies et al., IEEE Micro 2018; TrueNorth:
  Akopyan et al., IEEE TCAD 2015

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
  the authoritative source for the UVE 2 backlog item. https://github.com/hpc-ulisboa/UVE2
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
