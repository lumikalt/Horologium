# MOESIF Cache Coherence

Multi-hart cache coherence: `MoesifCache`/`MoesifBus` (snooping) and `DirectoryBus` (directory-based).
See [multi-hart.md](multi-hart.md) for the hart-coordination layer these plug into.

`MoesifCache` is an N-way set-associative write-back cache that participates in a MOESIF coherence protocol with
cache-to-cache supply. Unlike `SetAssociativeCache` (write-through, no-write-allocate), `MoesifCache` is write-back and
write-allocate: writes stay in the cache as Modified lines until eviction or a snoop, not every write goes to backing
memory.

`MoesifBus` coordinates snooping between all registered `MoesifCache` instances sharing a physical address space. Three
bus transactions cover the full protocol:

- **BusRead** (read miss): a peer holding the line in M, O, E, or F supplies the block directly to the requester (
  cache-to-cache) instead of the requester filling from backing. A dirty supplier (M/O) keeps the line as **Owned** — no
  writeback to backing occurs; the owner retains writeback responsibility until eviction or invalidation, and later
  readers install plain S. A clean supplier (E/F) downgrades to S and the requester installs **Forward**: exactly one
  sharer of a clean line holds F and keeps answering later read misses cache-to-cache, so memory stays silent; the F
  role migrates to the most recent requester on each supply (Intel MESIF semantics). If only plain S peers hold the
  line (the forwarder was evicted), the requester fills from backing — guaranteed clean in that case — and becomes the
  new forwarder. With no peers at all it fills from backing as E.
- **BusReadForOwnership** (write miss): all peers transition to I, and an M/O/E/F holder forwards the block to the
  requester along with the invalidation — no writeback; the requester installs the line as M, making its copy
  authoritative. Only if no such holder exists does the requester fill from backing (which is guaranteed current in that
  case).
- **BusReadInvalidate** (S/O/F→M upgrade, block-boundary-crossing writes): all peers transition to I; dirty M/O holders
  write back first. No data transfer — the upgrading requester already holds the bytes.

Silent E→M upgrade (write hit on an Exclusive line) requires no bus transaction — the cache takes M without notifying
peers. While a line is Owned, backing memory is stale; every path that removes the Owned copy (eviction,
snoop-invalidate, `cbo` maintenance, `Flush()`) writes it back. Accesses that straddle a block boundary read backing
directly after a **BusSyncToBacking** transaction forces dirty holders — including the requesting cache itself — to
write back.

```csharp
var backing = new FlatMemory(0x10000);
var bus     = new MoesifBus(backing);
var cache0  = new MoesifCache(bus, capacityBytes: 4096, ways: 2, blockSizeBytes: 64);
var cache1  = new MoesifCache(bus, capacityBytes: 4096, ways: 2, blockSizeBytes: 64);

// cache0 reads 0x00  → Exclusive
// cache1 reads 0x00  → BusRead: cache0 E→S supplies the block, cache1 installs Forward
// cache1 writes 0x00 → BusReadInvalidate (F→M): cache0→I, cache1→M
// cache0 reads 0x00  → BusRead: cache1 M→O supplies cache-to-cache (no writeback),
//                      cache0 installs S and sees cache1's value; backing stays stale
```

`StateOf(address)` returns the current MOESIF state of the line covering an address (for test assertions). `Flush()`
writes all dirty (M/O) lines to backing without evicting them — useful for inspecting backing memory from tests.
`ConsumePendingStalls()` returns accumulated miss-penalty cycles for pipeline integration; a fill supplied
cache-to-cache is charged `PeerSupplyLatency` (constructor parameter, defaults to `MissLatency`) instead of the full
miss penalty, and `PeerSupplies` counts such fills.

`DirectoryBus` is a drop-in `IBus` alternative to the snooping `MoesifBus` for sequential multi-hart simulation: it
keeps a precise per-line directory (designated responder + sharer set, maintained via eviction notifications) so
invalidations snoop only actual holders and forwarder-less shared read misses need no probe at all. Cache-to-cache
supply is directed: the directory contacts the single M/O/E/F responder. It cannot be wrapped by `DeferredBus` (
two-phase concurrent mode), which is hardcoded to `MoesifBus`.

`MoesifCache` and `MoesifBus` are ISA-agnostic (`Orrery.Cache`). Use the `MultiHartKernel(IMemory[] perHartMemory, …)`
overload to give each hart its own cache. Pass the `ReservationTable` to `MoesifBus` so that LR/SC reservations are
cancelled on every `BusReadForOwnership` (write miss) and `BusReadInvalidate` (S/O/F→M upgrade):

```csharp
var flat   = new FlatMemory(0x10000);
var table  = new ReservationTable();
var bus    = new MoesifBus(flat, table: table);
var cache0 = new MoesifCache(bus, capacityBytes: 4096, ways: 2, blockSizeBytes: 64);
var cache1 = new MoesifCache(bus, capacityBytes: 4096, ways: 2, blockSizeBytes: 64);

var kernel = new MultiHartKernel([cache0, cache1],
    new Rv32Mechanism(reservationTable: table, hartId: 0),
    new Rv32Mechanism(reservationTable: table, hartId: 1));
```

Instruction fetch and data access both route through the per-hart cache (unified I/D model). `ReservationAwareMemory` is
not required in this stack. All three write paths cancel reservations via the bus:

| Path                                                | Bus transaction       | Reservation cancellation                      |
|-----------------------------------------------------|-----------------------|-----------------------------------------------|
| Write miss (write-allocate)                         | `BusReadForOwnership` | `table.InvalidateAt` in `BusReadForOwnership` |
| S/O/F→M upgrade (write hit on Shared/Owned/Forward) | `BusReadInvalidate`   | `table.InvalidateAt` in `BusReadInvalidate`   |
| E→M upgrade (write hit on Exclusive)                | none (silent)         | `table.InvalidateAt` in `BusSilentUpgrade`    |

