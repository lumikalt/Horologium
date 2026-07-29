# Per-Instruction Lifecycle Events

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

