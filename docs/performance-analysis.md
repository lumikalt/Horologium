# Top-Down Analysis and CPI Stacks

Two complementary interval-analysis performance-breakdown methodologies, both recorded live during a run
(not post-hoc from a trace).

## Top-Down Microarchitecture Analysis (Pipeline/TopDownAnalysis)

`OooeTrain`, `CprTrain` and `SuperscalarTrain` record the Top-Down Analysis slot-accounting events (Yasin,
ISPASS 2014) at their dispatch/issue stage — the frontend/backend border. On the in-order superscalar the flavor
simplifies: issue never speculates past an unresolved branch, so SlotsIssued equals SlotsRetired, Bad Speculation
consists purely of post-flush frontend-refill bubbles (split into branch mispredicts vs machine clears by cause),
and Backend Bound is the scoreboard/FU-port/LSU backpressure residual. On the OoO trains: `td_total_slots` (issueWidth ×
cycles), `td_slots_issued`, `td_slots_retired`, `td_fetch_bubbles` (unutilized
dispatch slots with no backend stall; I-fetch miss stall cycles count width slots each), `td_recovery_bubbles`
(flush/squash recovery cycles), plus cycle-denominated level-2 events (`td_fetch_latency_cycles`,
`td_exec_stall_cycles`, `td_memstall_load_cycles`, `td_memstall_store_cycles`). `td_slots_retired` is deliberately
distinct from the pre-existing `retired` counter: `retired` scales by `ITooth.ArchInstructionCount` (2 for a
macro-fused pair), while a slot is a pipeline-width unit that a fused pair still only occupies once at both
dispatch and retirement — feeding `retired` into the slot math directly (as an earlier version of this file did)
inflated Retiring and could mask real Bad Speculation on a fusion-heavy program. Level-1 dials classify every issue
slot into **Frontend Bound / Bad Speculation / Retiring / Backend Bound** per the paper's Table 2 formulas (summing
to 1; Retiring cross-validates as IPC ÷ width when fusion is off — with fusion enabled, Retiring is slot-based
(`SlotsRetired`/`TotalSlots`) while IPC is instruction-based (`retired`/cycles), and the two diverge precisely
because a fused pair retires 2 architectural instructions through 1 slot); level-2 dials split frontend into
fetch latency vs bandwidth,
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

## CPI stacks via interval analysis (Pipeline/CpiStackAnalysis)

`OooeTrain` and `CprTrain` also build interval-analysis CPI stacks (Eyerman, Eeckhout, Karkhanis & Smith, ASPLOS 2006 —
the
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

