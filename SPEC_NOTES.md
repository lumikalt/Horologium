# UVE 2.0 spec discrepancies and implementation interpretations — Horologium

Notes accumulated while implementing UVE 2.0 in Horologium, a C# cycle-level
RISC-V simulator that serves as the golden model for a companion RTL
implementation. Sources, in order of authority used here:

- *A functional validation framework for the Unlimited Vector Extension*
  (Fernandes, MSc dissertation, University of Coimbra 2025) — the spec.
- AnaBSF/riscv-isa-sim, uve branch, commit `a048271` (pinned by
  `nix/uve-spike.nix`) — the reference implementation ("Spike" below).

Working policy: when Spike and the dissertation disagree, Horologium follows
Spike (it is what UVE binaries are actually built against); when a Spike
*bug* is identified, it is documented here rather than reproduced. The
companion RTL repository keeps a sibling `SPEC_NOTES.md` recording the same
questions from the hardware side; entries marked *validated* have been
settled by the RTL↔Horologium address/EOD trace-diff harness (random +
directed stream descriptors traced through both the RTL AGU and Horologium's
`StreamingEngine`).

Horologium's UVE implementation lives in `src/Core/Orrery/Streaming/`
(descriptor iteration, modifiers), `src/Isa/RiscV32/Decode/` (instruction
decode), `src/Isa/RiscV32/Execute/Rv32Executor.cs` (ISA semantics), and
`src/Isa/RiscV32/State/UveState.cs` (u-register / predicate register state).

## Places where the spec contradicts itself

### Numeric dimension references: Spike is outermost-first, the listings innermost-first

Spike keeps stream dimensions in a deque `push_back`-ed in configuration
order: index 0 = first configured = **outermost** (UVE 2.0 configures
outermost first; iteration advances `dimensions.back()`, the innermost).
Every numeric dimension reference in the ISA indexes that deque directly:
modifier tdim suffix `.N` targets the N-th *configured* dimension, and
`so.b.[n]dc.D` tests `EODTable.at(D-1)`, filled in deque order. The
dissertation's own listings contradict this: Listing 2.4 labels the
first-configured dimension "D3 (i)" and the last "D1 (j)" (D1 = innermost)
and comments the `.1` modifier with "# Target: D1" — under Spike semantics
`.1` targets D3 there. The prose never states a direction.

**Implemented: Spike order at the ISA level.** `ss.app`/`ss.end` build
descriptors outermost-first (`ss.end` adds the innermost dimension); modifier
tdim, the header vdim, explicit `ss.cfg.vec` dimension indices, and the
branch dimension field are all outermost-first. Internally the
`StreamingEngine` indexes dimensions innermost-first; the remap happens once
at `ss.end` time (`ExecuteUveSsEnd` in `Rv32Executor.cs`). *Validated.*

Historical note: Horologium originally configured innermost-first at the ISA
level too; writing a GEMM kernel exposed the mismatch, including a
`so.b.ndc` bug where the branch's raw funct3 was passed straight into the
engine's innermost-first EOD index — the branch tested the wrong dimension
and the innermost dimension was unreachable. Both fixed on trunk (commits
`363fcbb`, `5859c6f`).

### Branch `d` field: Appendix B vs §2.3.2 prose

§2.3.2 says d = 111 selects the EOS flag; Appendix B encodes the opposite
end: `SO.B.[N]C` (EOS) at funct3 = 000 and `SO.B.[N]DC.2`..`.8` at 001..111.
Both cannot hold (the prose leaves no encoding for dc.8).

**Implemented:** Appendix B / Spike: d = 0 selects EOS, d = 1..7 selects EOD
of the (d+1)-th dimension counting from the outermost
(`RvInstruction.cs`, so.b decode). There is deliberately no dc.1 —
innermost-dimension completion is implicit in each delivered vector.

### StreamSet opcode called "custom-2", encoded as custom-0

§2.3.1 prose places ss.* "in the custom-2 opcode region"; every Appendix B
row encodes `0001011`, which is RISC-V custom-0. **Implemented:** the bits —
ss.* = `0001011`, so.* = `0101011` — matching Spike (all Horologium UVE
encodings were aligned bit-for-bit to Spike in commit `2490b73`).

### Appendix B lists only INC/DEC static modifier mnemonics

Table 2.3 defines five modifier behaviours (INC/DEC/ADD/SUB/SET) and the
UMOD b field is 3 bits, but Appendix B's `SS.APP.MOD.*` rows enumerate only
INC and DEC; only the dynamic `SS.APP.IND.*` rows get all five.

**Implemented:** `StreamModifierBehavior` marks ADD/SUB/SET as
indirect-only. For indirect modifiers they are one-shot from the
*configured* value (`CalculateIndirectValue` in `StreamingEngine.cs`:
Add → configured + value, Set → value); INC/DEC accumulate on the working
copy. The RTL accepts ADD/SUB/SET on static modifiers too (same one-shot
reading); the trace harness generator emits only INC/DEC statics, so the
static-ADD question remains open but unexercised.

## Underspecified behaviour — interpretation chosen here

### Modified parameters restore when the trigger dimension resets

The spec says when a modifier fires (its trigger dimension advances) but not
what happens when the trigger dimension itself resets (a dimension above it
advances). Without a restore, repeating patterns would drift every outer
iteration; no spec example exercises this.

**Implemented:** the working copy of every target field reverts to its
configured value when the trigger dimension wraps. On each wrap of dimension
d, modifiers whose trigger itself wrapped are reset *first*, then modifiers
triggered by d are applied — matching Spike's `updateIteration` order
(`AdvanceFetchIndex` / `ResetFetchModifiers` in `StreamingEngine.cs`).
*Validated* against the RTL, including a directed `mod_restore` case.

### The `.L` modifier suffix (tdim = 3'b111)

Appendix B encodes `SS.APP.MOD.<t>.<b>.L` with tdim = 111 but the prose
never defines what `.L` targets. §2.1.3's history discussion says the
original (pre-tdim) behaviour was that a modifier affected the dimension
directly below the one it iterates with, so `.L` ("linked") is read as that
default.

**Implemented:** tdim = 7 resolves to the dimension configured right after
the trigger (`spikeTarget = spikeTrigger + 1` in `Rv32Executor.cs`), the
same dimension as the RTL's link_dim − 1 reading. This cannot be validated
against Spike, because Spike's `.L` is broken (see Spike bugs below); the
two working implementations agree.

### UVE2 static modifier re-encoding; the E (max-applications) field

UVE1-era Horologium carried a home-grown modifier encoding with an explicit
E field bounding how many times a modifier applies. UVE2's revised encoding
(funct3 = MOD under the APP transfer code, literal b/ta fields, 3-bit tdim)
has no E field, and Spike's E enforcement is commented out. **Implemented:**
the UVE2 encoding, E dropped; the trigger dimension is positional (the most
recently appended dimension at decode of the modifier), decoupled from the
target (commit `4c0d0c5`).

### Predication: inactive lanes always merge; pm governs only the tail

§2.2 prose reads as though zeroing is the default for predicated-off lanes
with the header pm bit switching to merging. Spike does something subtler on
compute writebacks, and Horologium follows it (`UveWriteResult` in
`Rv32Executor.cs`): predicate-inactive lanes **within vl always merge**
(keep the old destination value; for store streams the stream cursor
advances without a memory write), and zeroing vs merging applies only to
lanes **past vl** — zeroed unless a source register carries merging
predication from its stream's pm bit. `sadde`/`fsadde` reductions skip
predicate-inactive elements. *Validated* (RTL follows the same model).

The `_z` comparison variants write a per-predicate-register zeroing tag
(`UveState.PredZeroing`) exactly as Spike does; in both references nothing
ever produces an element-level difference from it — it is carried as a tag
only.

### Offset modifier accumulator clamps at 0

Horologium clamps modifier-driven per-dimension offsets and modified sizes
at 0 (`ApplyFetchModifiers` in `StreamingEngine.cs`), following Spike. The
RTL does not clamp (two's-complement wrap, well-formed programs converge).
The harness sidesteps the question (offset modifiers INC-only, shrinking
sizes kept ≥ 1); if a real kernel needs DEC-below-configured offsets the
clamp semantics must be settled first.

### Predicate / vector width conversion data loss

Behaviour of data lost on width conversion is "not clearly defined yet" per
the spec itself (§2.3.4, around Fig. 2.8). Horologium implements both
conversions with a conservative reading rather than rejecting them:
`so.p.cv` maps the active bit of each of the `PredBytes / max(srcW, destW)`
elements that fit in *both* widths and clears the rest of the destination;
`so.v.cv` converts the source's valid lanes (capped at the lane budget) and
sets the destination's valid-element count accordingly
(`ExecuteUveSoPCv` / `ExecuteUveSoVCv`). The RTL keeps both
decode-rejected. Open to spec clarification.

### so.v.vload / so.v.vstor — no semantic spec

The dissertation gives one sentence ("available to load and store data from
and to suspended streams") with no operand semantics or addressing model;
Spike has only MATCH/MASK constants and no instruction files. **Implemented
as illegal instructions** (SO_C group, funct3 = 4/5 fall through to
`IllegalInstructionException` in `Rv32Decoder.cs`) pending clarification.

### ABS has no unsigned variant

Spike's `MATCH_SO_A_ABS_SG` occupies the funct3 = 0 slot that the so.a.*
type scheme would call US, and no US/FP abs encodings exist. Horologium
decodes that slot as abs with Signed forced true (`Rv32Decoder.cs`).

## Bugs found in the Spike reference implementation

### `.L` static modifiers crash

`ss_app_mod_*_l.h` passes targetDim = 7 and nothing remaps it;
`modDimension` does `dims.at(7)`, which throws `std::out_of_range` for any
stream with fewer than 8 dimensions. `.L`'s intended meaning therefore
cannot be validated against Spike at all; Horologium and the RTL implement
the trigger+1 reading and agree with each other.

### Scatter-gather modifier applies a stale value when the index stream runs dry

In `descriptors.cc`, `scatterGModifier_t::modDimension` checks `sourceEnd`
*before* calling `getIndirectRegisterValues()`, which is what updates it. On
the first call after the index stream is exhausted, execution has already
entered the not-ended branch, so the *previous* index value is re-applied to
the gather stream's offset; EOD is only raised one call later. (It also
never resets `sourceEnd` after marking EOD, unlike `dynamicModifier_t`.) The
bug is unreachable when the index-stream and gather-stream element counts
match, which all well-formed kernels satisfy.

**Horologium stalls instead:** `ApplySgiMod` in `StreamingEngine.cs` returns
false (no address generated this cycle) when the source stream has no
element ready, rather than reproducing the stale-value behaviour. The RTL
does the same.

Related sgi width nuance, matched deliberately: Spike consumes indirect
source values as `(long)(int)value` — truncate to 32 bits, then
sign-extend — regardless of the source stream's element width, and
Horologium does the same (`ApplySgiMod`). Non-negative indices that fit 32
bits (all sane index streams) are unaffected. Also note that because a
gather dimension's configured base offset is 0 in this pattern, Add/Sub
collapse to Set/Negate for sgi modifiers.

### E-field enforcement dead code

Spike's static-modifier max-applications (E) enforcement is commented out
entirely; UVE2's encoding drops the field. Recorded here because
Horologium's UVE1-era implementation *did* enforce it and the removal is
deliberate, not an omission.

## Historical divergences, since fixed on trunk

These made Horologium-assembled instruction streams non-portable to
Spike/the RTL (or vice versa) until fixed; recorded so old traces or test
expectations are not trusted blindly.

- **ISA-level stride units** (fixed, commit `905c8c9`): Horologium stored
  the raw stride register value as a *byte* count; §2.1.1, Spike, and the
  RTL treat the operand as an *element* count scaled by the element width
  at address time. `ss.app`/`ss.end` now multiply the stride register by
  `ElementBytes` before storing, and stride-modifier displacements are
  scaled likewise. All pipeline-level UVE tests were re-expressed in
  element counts.
- **Dimension configuration order** (fixed, commits `5859c6f`/`363fcbb`):
  originally innermost-first at the ISA level; now outermost-first
  throughout, as above.
- **Horologium-specific modifier encoding** (replaced, commit `4c0d0c5`):
  the UVE1-era funct2 = 3 modifier encoding with an E field was
  Horologium-private; replaced by the UVE2 encoding.

## Remaining known divergence from Spike

so.p.{ge,eq,lt} comparison writes: Spike builds a fresh zero-initialised
predicate vector, fills elements < vl, and replaces the whole register — so
predicate bytes past vl become 0. Horologium fills elements < vl in place
and leaves bytes past vl untouched. The RTL follows Spike (tail zeroed). No
kernel studied so far reads a predicate register past the vl of the
comparison that last wrote it; to be aligned (Horologium → Spike behaviour)
if it ever matters. Also, the governing ps3/ps2 fields are 3 bits wide, so
only p0–p7 can govern an operation — a spec-level asymmetry with the 16-entry
predicate file, matched by all three implementations.

Unimplemented features are tracked in TODO.md, not here.
