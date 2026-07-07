# Next

Short-horizon work items in rough priority order.
See `TODO.md` for the full backlog and research ideas.

---

## In progress

- [x] Waterfall gutter dimming + config UI predictor cleanup (uncommitted diff in Face/)

---

## Face / UI

- [ ] **Rename event in PEventLog** — emit a distinct `Rn` PEventKind at decode-queue drain, separate from
  Dispatch (`Ds`), so the waterfall shows the rename stage as its own bar. Matches gem5 O3CPU stage breakdown;
  makes rename timing visible for OoOE analysis.

- [ ] **Waveform/signal viewer** — plot pipeline signals (IPC, cache hit rate, branch misprediction rate,
  ROB occupancy) over simulation time as a scrollable chart panel. Natural complement to the waterfall.

- [ ] **Cache and virtual-addressing visualization** — per-level occupancy heatmap, hit/miss timeline,
  page-table walk annotation on memory-access rows in the waterfall.

- [ ] **Vector operation visualization** — decide on a representation (lane-level bars? separate panel?),
  then implement. Needs design thought before coding.

- [ ] **Scripting configurator tab** (Phase 5 UI) — AvaloniaEdit code editor, hot-reload on file change via
  `FileSystemWatcher`, workload selector, live cache/TLB stat display.

- [ ] **Browser assembly support** — pure C# two-pass RV32 assembler so the Assemble command works in FaceWeb
  without a GAS subprocess.

---

## Cache replacement

- [ ] **LFU** — frequency-based eviction; baseline for frequency-aware policies. Straightforward to add
  alongside existing `IReplacementPolicy` implementations.

- [ ] **TinyLFU** — Count-Min sketch + doorkeeper + SLRU; frequency admission filter. — Einziger et al.,
  IEEE Trans. Computers 2017.

- [ ] **ARC** — two LRU lists with ghost-entry self-tuning split point. — Megiddo & Modha, FAST 2003.

---

## OoOE / pipeline

- [ ] **Store sets** — memory-dependence prediction: predict which loads depend on which stores to avoid
  unnecessary stalls. — Chrysos & Emer, ISCA 1998.

- [ ] **µop cache (loop buffer)** — cache decoded µop bundles; skip re-decode on repeated loops.

- [ ] **Register renaming** — explicit rename stage with RAT and free list, replacing implicit PRF indexing.

---

## Analysis

- [ ] **SimPoint phase analysis** — BBV profiling + k-means clustering for representative sampling.
  — Sherwood et al., ASPLOS 2002.

- [ ] **Full µarch checkpoint (Option B)** — serialize every Gear (ROB, LSQ, issue queues, pipeline latches,
  cache/TLB arrays, predictor tables) for suspend/resume with µarch fidelity. Mirrors gem5 `serialize`/`unserialize`.

---

## RISC-V

- [ ] **ELF64 loader + Sv39 walker** — prerequisite for running RV64 binaries end-to-end.

- [ ] **Zfh / Zfhmin** — half-precision FP; small extension surface, rounds out the FP story.

---

## Correctness / co-simulation

- [ ] **Co-sim watchdog on `ReadLine`** — fail cleanly on over-run instead of hanging.

- [ ] **CI workflow with `HOROLOGIUM_REQUIRE_COSIM=1`** — automated Spike lock-step on every push.
