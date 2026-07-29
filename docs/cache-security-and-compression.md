# Cache Compression and Side-Channel Defense

Two standalone `IMemory` cache variants that don't fit `SetAssociativeCache`'s tag-array assumptions:
Base-Delta-Immediate compression, and randomized-index side-channel defenses (CEASER/CEASER-S, ScatterCache).

## Cache compression (src/Core/Orrery/Cache)

`BdiCompressor` implements Base-Delta-Immediate (BΔI) compression (Pekhimenko, Seshadri, Mutlu, Kozuch, Gibbons &amp;
Mowry, PACT 2012): a pure, stateless algorithm that views a cache line as `n = C/k` signed elements of size `k ∈ {8, 4,
2}` bytes and finds the smallest of several encodings — all-zero (1 byte), a single repeated 8-byte value (8 bytes), or
a base+delta encoding for a (k, Δ) pair with Δ < k, falling back to storing the line uncompressed. BΔI's refinement
over plain Base+Delta is a second, *implicit zero* base: each element is first tested against zero (does the raw value
fit in Δ signed bytes?); only elements that fail get tested against a second, real base — the first element that
failed the zero-base test. The line compresses at a given (k, Δ) iff every element passes one test or the other. When
every element in a line resolves to the *same* base (all-zero or all-real — both of the paper's own worked examples,
Figures 3 and 4, happen to be uniform-base lines) the encoding matches the paper's Table 2 byte counts exactly; a
genuinely mixed-base line adds `⌈n/8⌉` mask bytes (one bit per element, needed for `Decompress` to reconstruct it) that
Table 2's simplified formula doesn't itemize. `Decompress` infers mask presence from the compressed length alone
(exactly one of two possible values for a given encoding and line size), rather than a separate stored flag.

`BdiCache` is a standalone `IMemory` cache built on `BdiCompressor` — deliberately *not* an option on
`SetAssociativeCache`, since BΔI's variable per-line footprint breaks the single-victim-per-miss invariant every one
of that class's orthogonal features (inclusion cascade, Jouppi victim buffer, write-back buffer, sectoring, Exclusive
hand-off, MSHR bookkeeping) assumes; `MoesifCache` already establishes the precedent of a separate, focused class for
a fundamentally different residency model rather than folding it into the class every other feature depends on. Each
resident line keeps its full uncompressed bytes (a real byte-accurate functional simulator has no reason to actually
shrink a `byte[]` to match a compressed size) alongside a *simulated footprint*, in `segmentBytes`-sized segments
(default 8, matching the paper), computed by `BdiCompressor.Compress` on the real fill/write bytes — decompression
latency itself is not separately modeled, matching the paper's own finding that 1-to-5-cycle decompression latency
changes performance by under 1%. Two independent capacity constraints per set, both enforced by evicting LRU (or the
supplied `IReplacementPolicy`'s) victims one at a time until they hold again: (1) at most `2 × physicalWays` tags (
*doubled tags*, the paper's own design point — a real cap on resident line count, independent of size); (2) the sum of
resident lines' segment footprints must not exceed `physicalWays × blockBytes / segmentBytes` segments (the real,
unchanged physical data budget). A single incoming or growing line can require evicting *several* other lines in a row
purely from segment pressure, not just tag-slot pressure — verified directly (`BdiCacheTests
.SegmentPressure_ForcesMultipleEvictionsNotJustOne`) with four two-segment compressible lines occupying all four tag
slots and the full eight-segment budget, where installing one four-segment incompressible line forces exactly two
evictions, not one. The residency effect this whole feature exists for — more lines resident, in the same physical
budget, than an equivalent uncompressed cache could hold — is verified end to end
(`BdiCacheTests.CompressibleWorkingSet_StaysResidentWhereUncompressedCacheThrashes`): a four-line compressible working
set stays fully resident in a `BdiCache` while an uncompressed `SetAssociativeCache` with the identical physical byte
budget thrashes on the same access pattern.

Selectable per level via `MemoryConfig.L2Compression`/`L3Compression` (`CompressionKind.{None,Bdi}`, plus
`L2SegmentBytes`/`L3SegmentBytes`), or `CacheHardwareConfig.Compression`/`SegmentBytes` on the `L2Cache`/`L3Cache`
entries of `TrainConfig` JSON — matching the paper's own L1-exclusion (Section 1: L1 hit latency is too critical to
spend on decompression), only L2/L3 have the option; L1 is always a plain `SetAssociativeCache`. When a level is
compressed, `MemoryLayers.Build` constructs a `BdiCache` for it instead of a `SetAssociativeCache` — that level's
`L2Cache`/`L3Cache` property is then null and its stats live on the new `L2Bdi`/`L3Bdi` properties instead. A
compressed level does not participate in the `AttachInner`-driven inclusion cascade (see `BdiCache`'s own docs) or
accept a `PolicyFactory`-supplied custom replacement policy (it always uses its own default LRU) — both documented
scope trims, not oversights. Verified end to end, not just type-checked, in
`ExperimentTests.FullChain_L1PlusCompressedL2_ReadsAndWritesCorrectly`. `MemoryLayers.ConsumeAllStalls()` — what every
pipeline train calls each cycle to charge miss latency — now drains `L2Bdi`/`L3Bdi` alongside the typed
`L2Cache`/`L3Cache`; without that, a compressed level's miss latency never reached cycle accounting at all (verified
directly in `ExperimentTests.CompressedL2_MissStallsAreActuallyDrainedIntoCycleAccounting`, which fails at 0 instead
of the expected miss latency if that drain line is removed). A compressed level also has its own `WriteState`/
`ReadState` (mirroring `SetAssociativeCache`'s, with a geometry check on restore) and participates in `OooeTrain`'s
microarchitectural checkpoint (`IL2BDI`/`DL2BDI`/`IL3BDI`/`DL3BDI` sections), and every train's
(SingleCycle/FiveStage/Superscalar/Ooo/Cpr) PEventLog L2/L3 hit/miss dial counters and (Cpr/Ooo) CPI-stack miss
classification now fold `L2Bdi`/`L3Bdi` in alongside the typed `L2Cache`/`L3Cache`. The `CacheLevelSpec`/
`CachePathSpec`-driven `MemoryLayers.Build` overload also supports `Compression`/`SegmentBytes` per level (rejecting
it on the innermost/L1 level, matching the flat-config path's L1 exclusion), and the Face cache-config UI exposes a
compression selector and segment-size control alongside the other L2 fields.

## Randomized-index cache side-channel defense (src/Core/Orrery/Cache)

`CeaserCache` implements CEASER (Qureshi, MICRO 2018) and CEASER-S (Qureshi, ISCA 2019) — a
keyed, periodically re-randomized address→set mapping defending against cache-based side-channel
attacks (Prime+Probe and relatives) that rely on a conventional cache's static bit-slice indexing
to build eviction sets. Like `BdiCache`, it's a standalone `IMemory` class rather than an option on
`SetAssociativeCache`: that class's tag storage assumes the classic `tag = address >> (offsetBits +
indexBits)` split and reconstructs a resident line's address by concatenating the stored tag with
its physical set index at 8+ call sites (writeback, inclusion invalidation, eviction callbacks,
checkpointing) — a keyed/randomized index breaks that reconstruction, since the set a line lives in
no longer determines its address's low index bits.

The papers' hardware design stores the *encrypted* line address (ELA) as the tag — to keep the tag
array the same width as an ordinary cache — and must decrypt ELA→PLA on writeback, which is why
they need an invertible block cipher (a 4-stage Feistel network, their "LLBC"). That requirement is
an artifact of minimizing real tag-array bits; it doesn't apply to a functional simulator.
`CeaserCache` stores the *plaintext* line address directly as the tag instead — observable-
equivalent (identical hit/miss, eviction address, timing, and remap behavior; nothing about tag
storage format is user-visible) and it eliminates the need for invertibility entirely, since nothing
ever needs to be decrypted: writeback, the remap sweep, and eviction all read the address straight
off the tag. The index function is accordingly just a keyed avalanche hash — murmur3's `fmix64`
finalizer applied to `address XOR key` (the key is folded in before avalanching, not appended after,
so a key change genuinely redistributes the mapping) — with no Feistel/S-box/P-box machinery and no
per-line EpochID bit (the paper's 1-bit disambiguator between two same-set ELA tags that could
coincidentally collide — moot here, since a full-address tag comparison is never ambiguous). This
models CEASER's mapping-randomization and periodic-remap behavior — the part that affects miss
rate, latency, and data placement, everything a simulator can actually measure — but makes no claim
of cryptographic hardness against a real attacker.

Each partition keeps a persistent sweeping set pointer (`SPtr`) and access counter (`ACtr`). Every
access increments `ACtr`; once it reaches `Aplr × waysPerPartition`, the set at `SPtr` is remapped —
each resident line there is re-indexed under the partition's `NextKey` and relocated if its target
set changed — then `SPtr` advances (mod sets) and `ACtr` resets. When `SPtr` wraps to 0 a full epoch
has swept every set: `CurrKey` becomes `NextKey` and a fresh `NextKey` is drawn. A lookup for
address `A` checks `A`'s `CurrKey`-indexed set if that set hasn't been swept yet this epoch (still
`>= SPtr`), otherwise its `NextKey`-indexed set (already swept, so if resident `A` was already
relocated there). CEASER-S (the paper's own framing: "CEASER-S1 is the same as the original CEASER
design") is the same class with a `partitions` constructor parameter — P=1 is plain CEASER; P>1
splits the ways into P contiguous ranges, each with independent keys/`SPtr`/`ACtr` over the same set
count. A lookup checks every partition; a hit in any one is a cache hit. A miss installs into a
uniformly-randomly chosen partition (the paper: "CEASER-S randomly picks the half in which to
install the line"), evicting via that partition's own replacement policy.

Scope matches `BdiCache`'s own fidelity level: no sectoring, MSHR modeling, victim buffer, bus
banking, or inclusion cascade. `GetSnapshot()` (per-line `(Partition, Set, Way, Valid, Address,
LruAge, Dirty)` introspection) and `SetOf(partition, address)` (the set an address currently maps to
in one partition, non-mutating) support tooling and testing without exposing the private index
function. Selectable per level via `MemoryConfig.L2Variant`/`L3Variant`
(`CacheVariantKind.{None,Ceaser}`, plus `L2CeaserPartitions`/`L3CeaserPartitions`/`L2CeaserAplr`/
`L3CeaserAplr`/`L2CeaserSeed`/`L3CeaserSeed`/`L2CeaserEncryptLatency`/`L3CeaserEncryptLatency`), or
`CacheHardwareConfig.Variant`/`CeaserPartitions`/`CeaserAplr`/`CeaserSeed`/`CeaserEncryptLatency` on
the `L2Cache`/`L3Cache` entries of `TrainConfig` JSON — mutually exclusive with `Compression`, same
L1-exclusion as BΔI (LLC-scoped, matching both papers' own evaluation). When a level uses the CEASER
variant, `MemoryLayers.Build` constructs a `CeaserCache` for it instead of a `SetAssociativeCache` —
that level's `L2Cache`/`L3Cache` property is then null and its stats live on the new
`L2Ceaser`/`L3Ceaser` properties instead, mirroring `L2Bdi`/`L3Bdi` exactly: `ConsumeAllStalls()`,
every train's (SingleCycle/FiveStage/Superscalar/Ooo/Cpr) dial counters and (Cpr/Ooo) CPI-stack miss
classification, and `OooeTrain`'s microarchitectural checkpoint (`IL2CEASER`/`DL2CEASER`/
`IL3CEASER`/`DL3CEASER` sections) all fold `L2Ceaser`/`L3Ceaser` in alongside the typed
`L2Cache`/`L3Cache` and `L2Bdi`/`L3Bdi`.

`ScatterCache` implements ScatterCache (Werner, Unterluggauer, Giner, Schwarz, Gruss, Mangard,
USENIX Security 2019) — a cache where each of the `nways` ways is indexed *independently* by a
keyed, security-domain-aware Index Derivation Function (IDF), rather than CEASER's single shared
index per set. No fixed "set" of `nways` lines exists for a given address: the lines that happen to
hold an address's data are usually scattered across `nways` different rows, one per way, computed
separately. This makes building the full eviction sets Prime+Probe needs (addresses that collide
across *every* way at once) combinatorially harder than defeating a single shared index, on top of
the address→index unpredictability CEASER already provides. It's a standalone `IMemory` class, not
a `CeaserCache` extension: CEASER's shape (one shared set index, all ways of that set considered
together) has no way to represent "each way has its own independently-computed row" — there's no
single set for `IReplacementPolicy.ChooseVictim` to operate over, so `ScatterCache` needed its own
per-way (not per-set) parallel tag/block/dirty arrays.

Same plaintext-line-address-as-tag simplification as `CeaserCache`, same rationale: the papers'
hardware needs an invertible cipher because it stores the *encrypted* address as the tag; a
functional simulator storing the plaintext address sidesteps invertibility entirely. This also means
the paper's SCv1 (hashing) variant is sufficient — SCv2's tag-dependent permutation exists purely to
avoid *performance*-degrading birthday-bound index collisions in real hardware, not a correctness
concern here. The IDF is the same murmur3 `fmix64` avalanche mix `CeaserCache.SetIndex` uses, with
the way index and the Security-Domain ID (SDID, see below) folded in before avalanching alongside
the key, so each way and each domain get an independent mapping for the same address.

Replacement is uniform-random among the `nways` candidates — hardcoded, not `IReplacementPolicy`-
pluggable (the paper mandates this specifically to avoid a systematic bias and simplify security
analysis, and no `IReplacementPolicy` could express the per-way-independent shape anyway). A miss
first checks whether any of the `nways` candidate slots is empty and fills there; only once every
candidate is occupied does it draw a uniformly random way to evict. Rekeying is always a full flush,
not CEASER's incremental sweep: the paper is explicit that a key change means flushing every dirty
line (write-back mode) then invalidating everything before drawing a fresh key — `Rekey()` does this
on demand, and an optional `RekeyInterval` (0 = manual only, matching the paper's own stated
preference for occasional flushes over added remap hardware) triggers it automatically.

**Security-Domain ID (SDID).** `IMemory.SetRequestSdid(int)` is a default-no-op pass-through
mirroring `IMemory.SetRequestPc`'s "supply extra addressing context before the next access"
pattern — consulted by every `ScatterCache` lookup. In the single-hart pipeline
(`MemoryConfig`/`CacheLevelSpec`/`TrainConfig`) SDID always defaults to 0, matching the paper's own
"still provides protection without software support" fallback; there's no per-hart-varying source
in that path to plumb it from. The one place a genuinely varying SDID matters is the multi-hart
coherence topology: `HartSpec.Sdid` (defaults to the hart's own index in `MulticoreSpec.Harts`, so
each hart is isolated by default while still allowing an explicit shared SDID for cooperating
harts) flows into `MoesifCache.Sdid` — set once at construction, since a hart's identity doesn't
change — and every direct `_bus.Backing.*` access `MoesifCache` makes calls
`SetRequestSdid(Sdid)` immediately first, mirroring how `SetRequestPc` is already threaded through.
`BusCoherentMemory` (the no-private-cache wrapper) does the same with its own `sdid` constructor
parameter. A resident line's location depends on which SDID it was written under — a bulk backing
write (`Load`, used for coherence writebacks and initial program images) only invalidates a
`ScatterCache` line if it still resolves under the *current* SDID at call time, matching the
paper's own explicit constraint that software is responsible for not leaving dirty lines behind
when reassigning a domain, not a bug to work around.

Scope matches `CeaserCache`/`BdiCache`'s fidelity level: no sectoring, MSHR modeling, victim buffer,
bus banking, or inclusion cascade. `GetSnapshot()` (per-line `(Way, Row, Valid, Address, Dirty)`
introspection) and `IdfRow(way, address)` (the row an address currently maps to in one way under the
current SDID, non-mutating) mirror `CeaserCache`'s own introspection surface. Selectable per level
via `MemoryConfig.L2Variant`/`L3Variant` (`CacheVariantKind.{None,Ceaser,ScatterCache}`, plus
`L2ScatterRekeyInterval`/`L3ScatterRekeyInterval`/`L2ScatterSeed`/`L3ScatterSeed`), or
`CacheHardwareConfig.ScatterRekeyInterval`/`ScatterSeed` on the `L2Cache`/`L3Cache` entries of
`TrainConfig` JSON — mutually exclusive with `Compression` and with CEASER, same L1-exclusion as
BΔI/CEASER. When a level uses the ScatterCache variant, `MemoryLayers.Build` constructs a
`ScatterCache` for it instead of a `SetAssociativeCache`; that level's stats live on the new
`L2Scatter`/`L3Scatter` properties, mirroring `L2Ceaser`/`L3Ceaser` exactly: `ConsumeAllStalls()`,
every train's dial counters and CPI-stack miss classification, and `OooeTrain`'s microarchitectural
checkpoint (`IL2SCATTER`/`DL2SCATTER`/`IL3SCATTER`/`DL3SCATTER` sections) all fold `L2Scatter`/
`L3Scatter` in alongside the typed `L2Cache`/`L3Cache`, `L2Bdi`/`L3Bdi`, and `L2Ceaser`/`L3Ceaser`.

At the multi-hart shared-LLC surface (`MulticoreSpec.SharedLlc`), a ScatterCache-variant
`CacheLevelSpec` builds a `ScatterCache` instead of a `SetAssociativeCache`, exposed via
`MulticoreHandle.SharedScatterLlc`/`SharedScatterLlcs` (mirroring `SharedLlc`/`SharedLlcs`) and
drained by `FlushAllToBacking()`. This is the one surface in `MulticoreSpec` where `HartSpec.Sdid`
means anything — other `Variant`/`Compression` kinds (CEASER, BΔI) aren't wired into the shared-LLC
path and a `SharedLlc` spec requesting one throws `NotSupportedException` rather than silently
falling back to a plain cache with the requested protection/compression dropped.

Purnal & Verbauwhede's ScatterCache-profiling follow-up (arXiv 2019) found that real-world
eviction-set construction against ScatterCache is faster than the original paper's own threat model
assumed — a caveat about the defense's real-world strength, not a mechanism this simulator models;
noted here for reference only.

