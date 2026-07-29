# Checkpoint Processing and Recovery (CprTrain)

`CprTrain` is the ROB-free out-of-order sibling of [`OooeTrain`](out-of-order-execution.md); see
[pipeline-trains.md](pipeline-trains.md) for the other trains.

`CprTrain` is a ROB-free out-of-order pipeline implementing **Checkpoint Processing and Recovery** (Akkary,
  Rajwar & Srinivasan, MICRO 2003) with optional **Continual Flow Pipelines** (Srinivasan, Rajwar, Akkary, Gandhi &
  Upton, ASPLOS 2004) via `enableCfp: true`. Instead of a reorder buffer, a small FIFO of rename-map checkpoints
  (`CheckpointList`, `checkpointCount`, default 8) is created selectively at **low-confidence branches** (JRS
  estimator, `CheckpointConfidencePredictor`: 4-bit counters indexed by PC⊕history, reset to zero on a mispredict,
  high-confidence only at saturation), plus forced checkpoints every `checkpointMaxInstructions` (counter-overflow
  prevention), at serializing instructions (CSR/fence/atomic/halt, which then issue only when architecturally
  oldest), and at the first branch after a recovery (forward-progress rule). A misprediction restores the
  containing checkpoint's RAT snapshot in one shot — no per-instruction walk-back — re-executing the instructions
  between the checkpoint and the branch (COVHD, counted by `covhd_squashed`) with the recorded branch outcome
  **replayed by distance** so the same branch cannot mispredict twice. **Macro-fusion** (same opt-in
  `Rv32Mechanism(enableMacroFusion: true)` flag and SLT+branch idiom as `SuperscalarTrain`/`OooeTrain`) fuses at
  Rename, before checkpoint-entry append, into a single checkpoint-entry + IQ entry — CPR has no ROB, but each
  renamed instruction still costs one checkpoint-entry slot (bounded by `checkpointMaxInstructions`) and one
  per-class IQ slot, so the same halving applies. `ITooth.BranchComponent` redirects both the branch predictor's
  and the JRS confidence estimator's training to the fused pair's real branch PC, since the confidence table is
  keyed by fetch-time Pc and training the compare's Pc instead would orphan the entry future fetches look up.
  Checkpoint *count* is unaffected by fusion (one opens per low-confidence branch either way); the benefit instead
  shows under checkpoint-entry pressure once the checkpoint buffer fills and instructions pile onto the still-open
  tail checkpoint — fused pairs cost that tail one entry instead of two. **Load+ALU micro-fusion** and **store
  address/data decomposition** (see `OooeTrain` in [out-of-order-execution.md](out-of-order-execution.md)) apply identically here — `enableEarlyStoreAddress`'s early
  address resolution updates `HierarchicalStoreQueue` and the inline Store Sets release check in `TryIssueSlot`
  (which reads `SqEntry.DataKnown`, not just `AddressKnown`, for the same reason `TryForwardFromStore` does on
  `OooeTrain`) rather than `StoreQueue` directly. Instructions retire in **bulk**: a whole
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
  the full TMA (`td_*`) and CPI-stack (`cpi_*`) sets described in [performance-analysis.md](performance-analysis.md). Three liveness rules keep
  bulk commit deadlock-free on real workloads: a checkpoint never holds more loads (stores) than the LQ (HSQ)
  capacity, never accepts appends behind a started commit cursor (checkpoint opening also precedes the
  free-register stall, so a fully-committed lone checkpoint can always gain the successor it needs to retire and
  release its snapshot's register references), and a serializing instruction sits alone in its epoch (a fence
  sharing a checkpoint with a younger load would deadlock: the load's issue gates on the fence committing, which
  requires the load to complete). Store sets should stay enabled: violation recovery re-executes the whole
  checkpoint, so without memory-dependence learning the same load can re-violate forever. Sweeps select the train
  with `"pipeline": "cpr"` (shared OoO knobs: width, IQ, physical registers, predictor, caches, FU latencies).

