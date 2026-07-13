using Mechanism;

namespace Orrery.Devices;

/// <summary>
///     Memory-mapped Platform-Level Interrupt Controller (PLIC) compatible with
///     the RISC-V PLIC specification v1.0 and the QEMU virt machine layout.
///     Base address: 0x0C000000, region size: 64 MiB (0x04000000).
///     <para>
///         Register map (offsets from device base):
///         0x000000 + src×4    — source priority (src 0 is always 0; src 1..1023 configurable)
///         0x001000 + word×4   — interrupt pending bits (read-only externally; 32 sources per word)
///         0x002000 + ctx×0x80 + word×4 — interrupt enable bits per context
///         0x200000 + ctx×0x1000       — priority threshold for context ctx
///         0x200004 + ctx×0x1000       — claim/complete register for context ctx (side-effecting read)
///     </para>
///     <para>
///         For a single hart the two contexts are:
///         context 0 = hart 0 M-mode  →  drives mip.MEIP (bit 11)
///         context 1 = hart 0 S-mode  →  drives mip.SEIP (bit 9)
///     </para>
///     <para>
///         ISA isolation: this class has no dependency on RiscV32. The ISA-specific
///         trap controller (RvTrapController) calls <see cref="ExternalPending" /> and
///         updates mip accordingly — the same pattern used for ClintDevice.
///     </para>
///     <para>
///         Modeling simplification: mip.SEIP is driven entirely by ExternalPending(1).
///         The architectural definition allows M-mode software to also set SEIP, but
///         OpenSBI/Linux-on-virt never does this — the PLIC is the sole SEIP source.
///     </para>
///     <para>
///         Claim-on-read note: reading the claim register is a side-effecting operation
///         that clears the pending bit and marks the source as in-service. This is safe
///         on in-order (non-speculative) trains only; a speculative load on an OoO path
///         would consume an interrupt on a squashed path.
///     </para>
/// </summary>
public sealed class PlicDevice : IMemory {
    public const ulong DefaultBase = 0x0C000000UL;
    public const ulong RegionSize = 0x04000000UL;

    private const int MaxSources = 1024;
    private const int WordCount = PlicDevice.MaxSources / 32; // 32 words cover all 1024 source bits

    // Level-asserted state: for re-asserting pending after Complete when source is still high
    private readonly uint[] _asserted = new uint[PlicDevice.WordCount];

    // Enable bits per context
    private readonly uint[][] _enable;

    private readonly int _numContexts;

    // Pending bits: set by Assert(), cleared on Claim
    private readonly uint[] _pending = new uint[PlicDevice.WordCount];

    // Source priorities (source 0 is hardwired to 0)
    private readonly uint[] _priority = new uint[PlicDevice.MaxSources];

    // Priority threshold per context; interrupt fires only when priority > threshold
    private readonly uint[] _threshold;

    public PlicDevice(int numContexts = 2) {
        _numContexts = numContexts;
        _enable = new uint[numContexts][];
        for (var i = 0; i < numContexts; i++) _enable[i] = new uint[PlicDevice.WordCount];
        _threshold = new uint[numContexts];
    }

    // ── IMemory ─────────────────────────────────────────────────────────────

    public ulong Read(ulong address, int bytes) {
        ulong offset = address - PlicDevice.DefaultBase;

        // Source priority: 0x000000..0x000FFC
        if (offset < 0x001000) {
            var src = (int)(offset >> 2);
            return src < PlicDevice.MaxSources ? _priority[src] : 0;
        }

        // Pending bits: 0x001000..0x00107C (read-only)
        if (offset < 0x002000) {
            var word = (int)((offset - 0x001000) >> 2);
            return word < PlicDevice.WordCount ? _pending[word] : 0;
        }

        // Enable bits: 0x002000..0x1FFFFF
        if (offset < 0x200000) {
            var ctx = (int)((offset - 0x002000) / 0x80);
            var word = (int)(((offset - 0x002000) % 0x80) >> 2);
            if ((uint)ctx < (uint)_numContexts && (uint)word < PlicDevice.WordCount) return _enable[ctx][word];
            return 0;
        }

        // Threshold / claim: 0x200000 + ctx * 0x1000 + {0, 4}
        {
            ulong ctxRegion = offset - 0x200000;
            var ctx = (int)(ctxRegion / 0x1000);
            ulong ctxOffset = ctxRegion % 0x1000;
            if ((uint)ctx >= (uint)_numContexts) return 0;
            if (ctxOffset == 0) return _threshold[ctx];
            if (ctxOffset == 4) return (ulong)Claim(ctx);
        }

        return 0;
    }

    public void Write(ulong address, ulong value, int bytes) {
        ulong offset = address - PlicDevice.DefaultBase;

        // Source priority: 0x000000..0x000FFC
        if (offset < 0x001000) {
            var src = (int)(offset >> 2);
            if (src > 0 && src < PlicDevice.MaxSources) _priority[src] = (uint)value;
            return;
        }

        // Pending bits: read-only externally; writes ignored
        if (offset < 0x002000) return;

        // Enable bits: 0x002000..0x1FFFFF
        if (offset < 0x200000) {
            var ctx = (int)((offset - 0x002000) / 0x80);
            var word = (int)(((offset - 0x002000) % 0x80) >> 2);
            if ((uint)ctx < (uint)_numContexts && (uint)word < PlicDevice.WordCount) _enable[ctx][word] = (uint)value;
            return;
        }

        // Threshold / complete: 0x200000 + ctx * 0x1000 + {0, 4}
        {
            ulong ctxRegion = offset - 0x200000;
            var ctx = (int)(ctxRegion / 0x1000);
            ulong ctxOffset = ctxRegion % 0x1000;
            if ((uint)ctx >= (uint)_numContexts) return;
            if (ctxOffset == 0) {
                _threshold[ctx] = (uint)value;
                return;
            }

            if (ctxOffset == 4) Complete((int)(uint)value);
        }
    }

    public void Load(ulong address, ReadOnlySpan<byte> data) { }

    // ── External signal injection ───────────────────────────────────────────

    /// <summary>Asserts (raises) an interrupt source. Sets the pending bit.</summary>
    public void Assert(int sourceId) {
        if ((uint)sourceId is 0 or >= PlicDevice.MaxSources) return;
        int word = sourceId >> 5, bit = sourceId & 31;
        _asserted[word] |= 1u << bit;
        _pending[word] |= 1u << bit;
    }

    /// <summary>
    ///     Deasserts an interrupt source. Clears the asserted latch; pending
    ///     remains set until the source is claimed (level-triggered semantics).
    /// </summary>
    public void Deassert(int sourceId) {
        if ((uint)sourceId is 0 or >= PlicDevice.MaxSources) return;
        _asserted[sourceId >> 5] &= ~(1u << (sourceId & 31));
    }

    // ── Query ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Returns true when context <paramref name="contextId" /> has at least one
    ///     pending, enabled interrupt whose priority exceeds the context threshold.
    ///     Called by the ISA trap controller each instruction to update mip.
    /// </summary>
    public bool ExternalPending(int contextId) {
        if ((uint)contextId >= (uint)_numContexts) return false;
        uint[] en = _enable[contextId];
        uint threshold = _threshold[contextId];
        for (var src = 1; src < PlicDevice.MaxSources; src++) {
            if (_priority[src] == 0) continue; // priority 0 → never fires
            int word = src >> 5, bit = src & 31;
            if (((_pending[word] >> bit) & 1) == 0) continue;
            if (((en[word] >> bit) & 1) == 0) continue;
            if (_priority[src] > threshold) return true;
        }

        return false;
    }

    // ── Claim / complete ────────────────────────────────────────────────────

    private int Claim(int contextId) {
        uint[] en = _enable[contextId];
        uint threshold = _threshold[contextId];
        var bestSrc = 0;
        uint bestPri = 0;

        for (var src = 1; src < PlicDevice.MaxSources; src++) {
            uint pri = _priority[src];
            if (pri == 0 || pri <= threshold || pri <= bestPri) continue;
            int word = src >> 5, bit = src & 31;
            if (((_pending[word] >> bit) & 1) == 0) continue;
            if (((en[word] >> bit) & 1) == 0) continue;
            bestPri = pri;
            bestSrc = src;
        }

        if (bestSrc > 0) _pending[bestSrc >> 5] &= ~(1u << (bestSrc & 31)); // clear pending on claim

        return bestSrc;
    }

    private void Complete(int sourceId) {
        if ((uint)sourceId is 0 or >= PlicDevice.MaxSources) return;
        // Re-assert pending if the source is still level-high
        int word = sourceId >> 5, bit = sourceId & 31;
        if (((_asserted[word] >> bit) & 1) != 0) _pending[word] |= 1u << bit;
    }
}