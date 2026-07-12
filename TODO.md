# To-Do

Near-term, actionable work, in rough priority order. Long-horizon research items, external-tool
integrations, and speculative directions live in [IDEAS.md](IDEAS.md). Completed items are checked
off here until a periodic cleanup removes them; the durable record is git history and README.md.

## UVE (Unlimited Vector Extension)

- [ ] ~~Suspended-stream data exchange: `so.v.vload`/`so.v.vstor`~~ — **hold**: dissertation gives one sentence
  with no operand semantics; Spike has no instruction files for it. Skip until the spec is clarified.

## Cache Model Realism

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
- [ ] Victim cache: small fully-associative buffer to absorb conflict misses. — Jouppi, ISCA 1990

## Cache Replacement

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
- [ ] Mockingjay: reuse-distance-predicting Belady mimic; finer-grained successor to Hawkeye using estimated
  time-of-reuse instead of binary friendly/averse classification. — Shah, Jain & Lin, HPCA 2022
- [ ] Perceptron reuse prediction: multi-feature perceptron predicting dead blocks for bypass and early eviction;
  extended to placement/promotion/bypass (MPPPB). — Teran, Wang & Jiménez, MICRO 2016; Jiménez & Teran, MICRO 2017
- [ ] EVA (economic value added): replacement planned under uncertainty from measured reuse-distance distributions. —
  Beckmann & Sanchez, HPCA 2017
- [ ] Shared-LLC partitioning: utility-based way partitioning (UCP) and fine-grained partitioning (Vantage) for the
  multi-hart shared cache. — Qureshi & Patt, MICRO 2006; Sanchez & Kozyrakis, ISCA 2011
- [ ] Cache replacement competition (CRC) plug-in interface: match ChampSim's policy API so research policies drop in.

## Performance

- [ ] Memoization of instructions, results, and branches.
- [ ] Structural stage-model rework for in-order trains: struct latches, fewer interface hops.

### Parallelism

- [ ] Deterministic parallel multi-hart tick: BSP-style barrier synchronization deferring every cross-hart-visible
  coherence action (snoop state transitions included) into per-hart queues drained in fixed hart order — a
  run-to-run-reproducible replacement for the racy two-phase concurrent mode.
- [ ] Parallelize the benchmark test suite across per-binary test collections; each run is fully independent.
- [ ] Background checkpoint and report serialization: write checkpoint files and result tables on a worker thread
  after a synchronous state copy.

## Face

- [ ] Browser assembly support: pure C# RV32 two-pass assembler so the Assemble command works in FaceWeb without a GAS
  subprocess.
  - Also a C compiler…
- [ ] L2 and L3 caches.
- [ ] Cache and virtual addressing visualization.
- [ ] Waveform/signal viewer: plot pipeline signals (IPC, cache hit rate, branch mispredictions) over simulation time.
- [ ] Vector operation visualization.
  - Gotta think of how this should be done.
- [ ] Cache management policy selection.
  - More cache settings like Ripes.
- [ ] gem5-style architecture configurator: UI surface for the scripting host and pipeline builder — edit `.csx` scripts
  in-app and hot-reload the resulting pipeline, cache hierarchy, branch predictor, and FU configuration without
  restarting. (Phase 5 UI of the architecture builder: AvaloniaEdit code editor, hot-reload on file change via
  `FileSystemWatcher`, workload selector, and live cache/TLB stat display.)
