# Cache Timing and Replacement Policies

Core `SetAssociativeCache` timing model and pluggable replacement policies. See [prefetching.md](prefetching.md)
for instruction/data prefetchers, [cache-security-and-compression.md](cache-security-and-compression.md) for
BΔI compression and the CEASER/ScatterCache side-channel defenses, and [cache-coherence.md](cache-coherence.md)
for MOESIF multi-hart coherence.

## Cache timing model (src/Core/Orrery/Cache)

`SetAssociativeCache` (write-through, no-write-allocate by default; write-back/write-allocate optional) models several
timing effects beyond a flat hit/miss latency, all configured on `CacheLevelSpec`/`MemoryConfig` and independently
combinable except where noted:

- **MSHRs with hit-under-miss** (`mshrCount`): outstanding misses are tracked per line in a small table instead of
  blocking the cache; a second access to a line already in flight merges into the existing entry and pays only the
  remaining countdown (`MshrMerges`), while unrelated addresses continue to hit normally. A miss that arrives when every
  MSHR slot is occupied pays the minimum remaining countdown plus the full `missLatency` (`MshrCapacityStalls`). Call
  `TickMshr()` once per cycle to advance the countdowns — L2/L3 levels are non-blocking the same way L1 is.
- **Sequential tag/data access** (`accessMode: CacheAccessModeKind.Sequential`): `HitLatency` becomes
  `tagLatency + dataLatency` (probe tags first, read only the matching way) instead of the default `Parallel` mode's
  `max(tagLatency, dataLatency)` — typical of large lower-level caches.
- **Inclusion policy** (`inclusionPolicy`, via `AttachInner`): `Inclusive` (Intel-style — a lower-level eviction
  back-invalidates the line in attached upper levels), `Exclusive` (AMD-style — the lower level acts as a victim cache,
  receiving evicted lines from the level above and reclaiming them on a re-fill so the same line never lives in both),
  or `Nine` (non-inclusive non-exclusive, the default — no cross-level invalidation). No effect until an inner cache is
  attached.
- **Critical-word-first / early restart** (`criticalWordLatency`): a fresh demand miss charges only this latency to the
  requester (the demanded word is assumed to arrive first off the bus) while the MSHR entry keeps counting down the full
  `missLatency` in the background, so a different access hitting the same in-flight line before it fully arrives still
  pays the remaining full-line latency. Requires `mshrCount > 0` and cannot exceed `missLatency`.
- **Banked caches and port limits** (`bankCount`, `readPorts`, `writePorts`): the line address selects one of
  `bankCount` independent banks; `readPorts`/`writePorts` cap concurrent accesses per bank per cycle (0 = unlimited),
  charging a 1-cycle structural-hazard stall to an access that finds its bank already at capacity. Call `TickPorts()`
  once per cycle to reset per-bank usage — this is what bounds the OoO train's D-cache bandwidth when configured.
- **Per-sector dirty/valid bits** (`sectorBytes`): splits each line into `blockSizeBytes / sectorBytes` independently
  valid (and, for write-back, independently dirty) sectors. A fresh miss fetches only the sector covering the triggering
  address; a later access to a different, still-untouched sector on an otherwise-resident line pays `missLatency` again
  for that sector alone; evictions write back only dirty sectors instead of the whole line. Not currently combinable
  with `wbCapacity > 0`.
- **Zicbom cache-maintenance semantics** (`CleanLine`/`FlushLine`/`InvalidateLine` on `IMemory`, wired through `Tlb` and
  `MoesifCache` as well): `cbo.clean` writes back a dirty line and keeps it resident; `cbo.flush` writes back and then
  invalidates; `cbo.inval` discards any dirty data without writing it back, then invalidates. All three reuse the same
  dirty-flush path as ordinary eviction, so they honor sectoring automatically and charge no pipeline stall.
- **Victim cache** (`victimCacheEntries`, `victimCacheHitLatency`; Jouppi, ISCA 1990): a small fully-associative FIFO
  buffer beside the main array that captures a line evicted by set conflict instead of flushing/discarding it — capture
  is free (no stall, no writeback even if dirty). A later access that hits in the buffer performs a full swap (not an "
  extra way"): the line installs into the main array via the normal replacement policy, and whatever it displaces takes
  the vacated buffer slot, so both levels stay populated with genuine working-set data. A hit charges
  `victimCacheHitLatency` instead of the full miss latency, allocates no MSHR, and counts as a `Hit`, not a `Miss` (so
  prefetcher training isn't corrupted). A line that eventually leaves the buffer for good — FIFO overflow while dirty,
  or an explicit Zicbom clean/flush — is billed exactly like an ordinary write-back-mode eviction. Not currently
  combinable with `sectorBytes > 0`. Distinct from the unrelated `InsertVictim`/`VictimInserts` (the pre-existing
  Exclusive-inclusion-policy hand-off) and `IReplacementPolicy.ChooseVictim` (generic eviction-way selection) — a level
  can have both a local Jouppi buffer and be the Exclusive receiver for the level above it.

## Cache replacement policies (src/Core/Orrery/Cache)

`SetAssociativeCache` supports a pluggable replacement policy via `IReplacementPolicy` and the `ReplacementPolicyKind`
enum: **LRU** (default), **MRU** (`Mru`): inverse of LRU — a hit promotes the way to age 0 (next eviction candidate);
new installs are placed at the LRU position so they survive until first use; useful for sequential-scan workloads where
the just-accessed block is unlikely to be reused soon, **CLOCK** (`Clock`): one reference bit per way and a circular
hand per set; on eviction the hand sweeps forward clearing bits=1 (second chance) until it finds a bit=0 victim; on hit
or install the bit is set to 1; O(1) amortized victim search, hardware-cheap LRU approximation common in OS page
replacement, **FIFO** (circular-pointer eviction, ignores hits — ordering baseline), **Random** (uniform random victim,
deterministically seeded), **Tree-PLRU** (`Plru`): binary tree of `ways−1` bits per set; on every access the bits on the
root-to-leaf path are pointed away from the accessed subtree; victim selection follows bits root-to-leaf; exact LRU for
2-way, hardware-friendly approximation for wider associativity (Intel P6 and later), **SRRIP-HP** (scan-resistant;
inserts at RRPV 2^M−2, promotes hits to 0), **BRRIP-HP** (thrash-resistant; inserts at distant RRPV 2^M−1 with
probability 1−ε, long with probability ε=1/32), **DRRIP-HP** (scan- and thrash-resistant; uses Set Dueling — 32-set
SDMs, 10-bit PSEL — to dynamically choose between SRRIP and BRRIP per set) — Jaleel et al., ISCA 2010; and **SHiP-Mem
** (`Ship`) and **SHiP-PC** (`ShipPc`): layer a 16K×3-bit Signature History Counter Table (SHCT) on top of SRRIP-HP —
inserts at RRPV=3 (distant) when SHCT[sig]=0, RRPV=2 (long) when SHCT[sig]>0; increments SHCT on every hit; decrements
on eviction without reuse. SHiP-Mem indexes the SHCT by the upper address bits of the miss address; SHiP-PC indexes by
the load PC via `IMemory.SetRequestPc(pc)`, which all pipeline trains call before every execute. Prefetches always use
address-based signatures since no PC is available at prefetch time — Wu et al., MICRO 2011; and **Hawkeye** (`Hawkeye`):
reconstructs Belady's optimal replacement decisions for the observed access stream using OPTgen (a circular occupancy
vector of length 8×ways per set), trains a PC-indexed 8K×3-bit saturating-counter predictor, and uses the predictor's
label at each install — cache-friendly (counter ≥ 4) inserts at RRPV=0, cache-averse at RRPV=7; demand hits decrement
RRPV toward 0; victim selection is SRRIP-style (scan for RRPV=7, age all lines if none found) — Jain &amp; Lin, ISCA

2016. The `ReplacementPolicy` field on `MemoryConfig` (and `CacheReplacementPolicy` string on `TrainConfig`) selects the
      policy for all cache levels.

