# Prefetching

Instruction prefetchers (fetch-side) and the thirteen data-cache prefetchers implemented via `IPrefetcher`.
See [cache-fundamentals.md](cache-fundamentals.md) for the cache timing model these plug into.

## Instruction prefetcher (src/Core/Pipeline)

**RDIP** (RAS-Directed Instruction Prefetching — Kolli, Saidi &amp; Wenisch, MICRO 2013): associates I-cache miss
sequences with call-stack signatures derived from the commit-time RAS (4-entry, per the paper's sensitivity study). On
every call or return at commit the prefetcher (1) computes a signature = XOR of the top RAS entries | direction bit (
0=call, 1=return), (2) flushes the Current Signature Misses buffer (up to 16 miss addresses accumulated since the last
signature change) into the Miss Table under the previous signature, and (3) looks up the new signature's Miss Table
entry and issues prefetches. The Miss Table is 1024 sets × 4 ways (LRU); each entry holds up to 3 trigger records, each
a base address + 8-bit block mask covering an 8-block window; new misses are merged into the nearest existing window or
replace the oldest trigger round-robin. RDIP is wired into `FiveStageTrain` and `OooeTrain` (commit-hook calls
`OnCommit`; fetch-hook calls `OnIcacheMiss`); enable with `rdip: true`.

**FDIP** (Fetch Directed Instruction Prefetching — Reinman, Calder &amp; Austin, MICRO 1999): decouples the branch
predictor from instruction fetch via a Fetch Target Queue (FTQ). Each cycle the predictor steps ahead of the fetch PC
using the raw backing memory (bypassing the cache to avoid charging stall latency to the pipeline), filling the FTQ with
predicted cache-line base addresses. A configurable prefetch window (default: entries 1–10, skipping entry 0 which is
too close to benefit) drives `SetAssociativeCache.Prefetch` directly on the L1 I-cache. The FTQ is drained by position (
one head entry per cache-line boundary crossed by fetch), not by address magnitude, so it stays correctly aligned across
backward branches (loops). Mispredictions reset lookahead state to the flush target. The lookahead does not maintain a
RAS, so call/return targets rely on the BTB; mispredicted targets are recovered by the normal flush mechanism.
`FdipPrefetcher` is wired into `FiveStageTrain` (via `FetchStage`) and `OooeTrain` (directly in `StepFetch`/
`StepFlush`); enable with `fdipFtqCapacity: 32` (0 = disabled, default). FDIP is bare-mode only (no virtual-to-physical
lookahead translation).

## Cache prefetchers (src/Core/Orrery/Cache)

`IPrefetcher.OnAccess(pc, address, wasHit, Span<ulong> targets)` writes zero or more prefetch addresses into the
caller-provided span and returns the count; the `OooeTrain` execute stage drives it once per demand load and calls
`MemoryLayers.TryPrefetch` for each result, subject to MSHR capacity. Thirteen prefetchers are implemented: **NextLine** —
always prefetches the cache line immediately following the access; bandwidth-greedy but effective for sequential
workloads. **Stride / RPT** — Reference Prediction Table (per-PC stride tracking with a 0–3 saturating confidence
counter); issues a prefetch at `address + stride` once the stride is confirmed (confidence ≥ 2). **Stream** — multi-way
sequential stream buffer (Jouppi, ISCA 1990): maintains up to N independent stream buffers in parallel (default 4,
controlled by `PrefetcherTableSize`); on a cache miss that matches no buffer, the LRU buffer is evicted and restarted at
the missed address, issuing `depth` lines at once (default 8, controlled by `PrefetcherDepth`); on each subsequent
sequential access the frontier is advanced by one line to keep exactly `depth` lines pre-loaded; LRU replacement across
buffers; targets sequential and near-sequential patterns including RVV vector loads and UVE streams. **IPCP** — IP
Classifier-based Spatial Prefetcher (Pakalapati & Panda, ISCA 2020): classifies each load PC into one of three classes
and issues spatially-targeted prefetches; **CS** (Constant Stride) tracks per-PC stride with a 2-bit saturating
confidence counter and issues up to 3 prefetches at the confirmed stride; **CPLX** (Complex Stride) maintains a 7-bit
rolling signature of recent strides (`sig = (sig<<1) XOR stride`) indexing a 128-entry CSPT table, issuing up to 3
prefetches when a pattern repeats (confidence ≥ 1); **GS** (Global Stream) tracks 2 KB regions in an 8-entry LRU Region
Stream Table (RST) with a 64-bit access bitvector, classifying a PC as a global-stream if ≥75% of its region's lines
have been touched (dense), then issuing up to 6 prefetches in the stream direction; a tentative GS prefetch fires when
an IP enters a new region and its previous region was dense; no prefetch crosses a page boundary; a 32-entry
recent-request filter suppresses duplicate prefetch requests. Priority: GS > CS > CPLX. **Berti** — accurate local-delta
L1D prefetcher (Navarro-Torres et al., MICRO 2022): for each load IP maintains an 8-set × 16-way FIFO History Table (HT)
of recent (line address, tick) pairs; on a demand miss it searches the IP's HT set for "timely" entries (entries whose
tick satisfies `entry.tick + latency ≤ current_tick`, i.e., a prefetch issued then would have arrived before the miss)
and accumulates the signed line-count deltas to the current miss address into a 16-entry fully-associative Table of
Deltas (ToD); an epoch counter trips at 16 training events and assigns statuses by coverage fraction: >10/16 → L1DPref,
6–10/16 → L2Pref (or L2PrefRepl if <8/16), ≤5/16 → NoPref; at most 12 deltas may be active (L1DPref+L2Pref+L2PrefRepl
combined); warmup mode issues a delta only when counter ≥ 8 and coverage > 80% of the counter value; latency is
approximated by a configurable tick count (default 10) since no MSHR timestamps are available. **Pythia** — online
reinforcement learning prefetcher (Bera et al., MICRO 2021): formulates prefetching as a SARSA RL problem; the agent
observes two program features per demand — PC+Delta (current load PC XOR'd with the current cacheline delta) and the
last-4-deltas rolling hash — and selects one prefetch offset from a 16-entry pruned action list
{−6,−3,−1,0,+1,+3,+4,+5,+10,+11,+12,+16,+22,+23,+30,+32} (lines); Q-values are stored in a hierarchical Q-Value Store (
QVStore): 2 vaults × 3 tile-coded planes × 128 feature-entries × 16 actions; Q(S,A) = max over vaults of the sum of
plane partial Q-values; rewards: RAT=+20 (accurate+timely), RAL=+12 (accurate+late), RCL=−12 (page-crossing), RIN=−8 (
inaccurate), RNP=−4 (no-prefetch); a 256-entry FIFO Evaluation Queue (EQ) defers SARSA updates (α=0.0065, γ=0.556) until
the evicted entry's reward is known; ε=0.002 greedy exploration; per-access overhead is a 16-way Q-value lookup over 2
vaults × 3 planes. **SMS** — Spatial Memory Streaming (Somogyi et al., ISCA 2006): learns spatial access patterns over
fixed 2 KB address regions and prefetches all blocks predicted to be accessed during a region generation; indexed by the
PC and block offset of the trigger (first) access. The Active Generation Table (AGT) is split into a 32-entry
fully-associative filter table (holds single-access generations; entries are discarded on eviction) and a 64-entry
fully-associative accumulation table (promotes from filter on the second distinct block access; accumulates a 64-bit
spatial pattern bitvector); accumulation entries are retired to the Pattern History Table on AGT capacity pressure,
matching the paper's explicit description of capacity-based generation termination. The PHT (16 K entries, 16-way
set-associative, LRU) stores one pattern bitvector per (trigger PC, block offset) hash key; on a trigger access the PHT
is consulted first and matching predicted blocks (excluding the trigger block itself) are immediately emitted as
prefetch targets. **BOP** — Best-Offset prefetcher (Michaud, HPCA 2016; the DPC-2 winner): a degree-one offset
prefetcher — on each eligible access to line X (demand miss or first demand touch of a prefetched line) it prefetches
X + D, never crossing a page boundary. The offset D is re-selected by a scoring tournament that accounts for prefetch
*timeliness*: a 256-entry direct-mapped Recent Requests (RR) table records the base address of each *completed*
prefetch, and learning tests one candidate offset d per eligible access (round-robin over the paper's 52-entry list —
all offsets 1–256 with prime factors ≤ 5, pruned to the page size in lines); if X − d hits in the RR table, a prefetch
with offset d issued back then would have completed in time, so d scores. A phase ends at SCOREMAX (31) or after
ROUNDMAX (100) rounds; the top scorer becomes D, and a winning score ≤ BADSCORE (1) turns prefetching off (learning
continues against demand fills so it can re-enable). The L2 prefetch bit of the paper is tracked internally, and
completion time is approximated by a configurable tick count (default 10), as in Berti. **SPP** — Signature Path
Prefetcher (Kim et al., MICRO 2016): a PC-free lookahead prefetcher that compresses per-page delta history into a
12-bit signature (`sig = (sig << 3) XOR delta`, sign+magnitude deltas) indexing a 512-entry global Pattern Table of
(delta, confidence) predictions shared across all pages. Prediction recursively walks a *signature path*: the
highest-confidence delta extends the signature speculatively (no confirmation), producing a new signature to predict
from, and the walk continues until path confidence `P_d = α·C_d·P_(d−1)` (`P_0 = C_d`) falls below the prefetch
threshold (25%); α is the measured global accuracy (useful / total prefetches, from a 1024-entry direct-mapped
Prefetch Filter that also drops redundant requests), throttling lookahead depth to the current program phase. A
prediction that would cross the 4KB page boundary is not issued but recorded in an 8-entry Global History Register
(signature, confidence, last offset, delta); the first access to an untracked page searches the GHR for an entry
whose predicted landing offset matches, and if found inherits that signature — so complex patterns continue into a
new physical page with no per-page warmup, SPP's signature contribution beyond plain lookahead prefetching. Beats
BOP/Berti/next-line on workloads with complex, non-strided-but-learnable access patterns (coremark: cuts D$ misses
~39% vs. no-prefetch, where next-line/BOP manage ~8% and Berti is a no-op) but can lag simpler prefetchers when
capacity/conflict misses dominate a tiny cache. **PPF** — Perceptron-based Prefetch Filter (Bhatia, Chacon, Teran,
Gratz &amp; Jiménez, ISCA 2019): reimplements the same Signature/Pattern-Table/GHR core as SPP but discards its
confidence-throttling entirely — the lookahead walk runs until the Pattern Table has no more information for the
current signature (or a 64-depth safety cap), regardless of confidence — and instead routes every delta candidate the
de-throttled walk produces through a hashed-perceptron filter that decides admit/reject per candidate. Nine hashed
features (address, cache line, page, PC⊕depth, a 3-PC path hash, PC⊕delta, confidence, page⊕confidence,
signature⊕delta) each index an independent table of signed 5-bit saturating weights (paper's Table 3: 4×4096-entry +
2×2048-entry + 2×1024-entry + 1×128-entry, cross-checked against its 113,280-bit total); the nine partial weights sum
to a single score thresholded against an admit line. Training: a demand hit on an admitted line (tracked via a
1024-entry Prefetch Table) trains its contributing weights toward "useful"; a demand hit on a *rejected* candidate
(tracked via a matching 1024-entry Reject Table) is a false negative and trains toward "should have admitted." The
paper's third trigger — an L2 eviction of a still-unused prefetched line — is fed a real signal via
`SetAssociativeCache.OnEviction` (an `Action<ulong>?` callback fired at the same site `Evictions` is counted, wired
onto the innermost cache by `MemoryLayers.Build` whenever PPF is the configured prefetcher): `PpfPrefetcher
.OnLineEvicted` trains contributing weights toward "should have rejected" for a still-unused admitted line. When no
cache wires that callback (PPF used standalone), the same signal is still approximated by training a departing,
never-marked-useful Prefetch Table entry toward "should have rejected" when a slot collision evicts it — a fallback,
not the primary mechanism, for builds where the real per-line capacity-pressure signal isn't available. The paper's L2-vs-
LLC fill-level split (τ_hi/τ_lo) collapses into one admit threshold, matching the same simplification already
documented for SPP's own T_F. On coremark PPF cuts D\$ misses to roughly a sixth of plain SPP's (188 vs. 1104 misses
at 32KB/8-way, vs. 1806 with no prefetching) — the paper's central claim that de-throttling plus perceptron filtering
beats a throttled lookahead prefetcher outright, not just a marginal gain. **STeMS** — Spatio-Temporal Memory
Streaming (Somogyi, Wenisch, Ailamaki &amp; Falsafi, ISCA 2009): extends SMS with temporal miss-sequence recording so
prefetching can cross region boundaries, which SMS alone cannot do. A trigger (first miss to a region) is recorded in
a Region Miss Order Buffer (RMOB, 128K-entry circular buffer of `(block address, trigger PC, trigger offset, delta)`)
alongside a block-address → most-recent-RMOB-slot map. SMS's AGT/PHT are kept structurally identical (32-entry
filter/64-entry accumulation AGT, 16K-entry 16-way PST) but store an *ordered sequence* of `(offset, delta)` pairs per
generation instead of a bit vector — each block appears once, in first-access order. Every recorded entry's delta is
the count of *other* misses (from any region) interleaved before it since the previous entry of the same sequence
(trigger stream or a region's own spatial stream); reconstruction re-derives absolute positions from these deltas via
one recurrence, `pos[entry] = pos[previous same-sequence entry] + delta + 1`, merging the trigger stream and every
region's spatial stream into a single ordered prediction. On a trigger miss whose address has a prior RMOB
occurrence, this reconstruction runs synchronously and returns the whole predicted sequence at once (bounded by a
256-entry reconstruction window and the caller's target span — replacing the paper's decoupled stream-queue/SVB
throttling the same way SPP/PPF's lookahead walks replace theirs), with ±2-position collision resolution matching the
paper's own (§4.2). Verified directly against the paper's own worked example (Fig. 3/5): training on the observed
order A, A+4, B, A+2, B+6, A−1, C, D, D+1, D+2 and re-triggering A reconstructs the exact original continuation.
**Bingo** — Bingo Spatial Data Prefetcher (Bakhshalipour, Shakerinava, Lotfi-Kamran &amp; Sarbazi-Azad, HPCA 2019):
reuses SMS's AGT unchanged (same 32-entry filter/64-entry accumulation FIFO tables, same 2 KB region, same footprint
bitmask accumulation) but replaces SMS's single-event Pattern History Table with a TAGE-like *dual-event* history
table (16K entries, 16-way, same sizing as SMS's PHT): each footprint is associated with a *long* event (trigger PC +
the exact trigger address) and, implicitly, a *short* event (trigger PC + trigger block offset, always derivable from
the long one) — so the table is indexed once, using only the short event, and looked up up to twice against the same
set: first for a precise match against the full long event (high accuracy, low match probability), and — only on a
miss — a fallback match against just the short event, generalizing the learned footprint across every page ever
touched at that PC+offset (lower accuracy, much higher match probability). One physical table instead of two cascaded
TAGE-style tables, because a footprint is only ever written once (under its long event) yet stays reachable via the
short event too. When a short-event fallback lookup matches more than one entry (several different pages sharing the
same trigger PC+offset), conflicting footprints are combined by majority: a block is prefetched only if present in the
footprint of at least 20% of matching entries, the paper's own literal threshold (§IV). Entries store `Pc`/
`RegionBase`/`Offset` as literal fields rather than a bit-packed tag+index split — behaviorally identical to the
paper's hardware design (the index is still computed from `(Pc, Offset)` alone, so a write and a lookup sharing the
same short event always land in the same set) but avoids a tag-decomposition trick that only matters for real silicon
bit width. Like SMS/STeMS (and unlike PPF), relies on AGT capacity pressure rather than a real per-line eviction
callback to terminate a page generation — Bingo's own contribution is the history-table lookup scheme, not the
AGT/termination model it inherits unchanged from SMS.
**MLOP** — Multi-Lookahead Offset Prefetcher (Shakerinava, Bakhshalipour, Lotfi-Kamran &amp; Sarbazi-Azad, DPC-3 2019):
generalizes BOP by scoring candidate offsets at 16 independent lookahead levels instead of committing to one. A
256-entry direct-mapped Access Map Table (AMT), keyed by a 64-line-aligned region, holds a 64-bit spatial bitvector per
region plus its last 15 accessed positions in order. On each eligible access at region-relative position P, offset d
scores at lookahead level L if bit (P − d) is set in the region's bitvector after excluding its L − 1 most recently
recorded positions — level 1 excludes nothing (any prior access counts), level 16 excludes the 15 most recent (the
qualifying access must be further back), so low levels favor raw coverage and high levels favor only genuinely timely
offsets. Every 500 eligible accesses the top-scoring offset per level becomes that level's selected offset and scores
reset; a level with no scoring evidence stays silent (offset 0) until it wins one. Issuance emits one prefetch per
level's selected offset, level 1 (soonest-needed) through level 16 (most lead time) in priority order, deduplicated and
clamped to a page boundary. Candidate offsets are 1..63 (positive-only, matching this codebase's BOP simplification —
nothing larger could ever score against a 64-line region anyway). For a dense stride-k demand-miss stream this
converges deterministically to `bestOffset[L] = k·L` for every level — the property a unit test verifies directly.
Select with `Prefetcher = PrefetcherKind.{NextLine,Stride,Stream,Ipcp,Berti,Pythia,Sms,Bop,Spp,Ppf,Stems,Mlop,Bingo}` on
`MemoryConfig`/`CacheLevelSpec`, or `d_prefetcher:
"next_line"/"stride"/"stream"/"ipcp"/"berti"/"pythia"/"sms"/"bop"/"spp"/"ppf"/"stems"/"mlop"/"bingo"` in `TrainConfig`
JSON.

