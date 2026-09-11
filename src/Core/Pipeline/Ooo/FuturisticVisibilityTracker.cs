namespace Pipeline.Ooo;

/// <summary>
///     Tracks the Futuristic-model visibility point (Yan et al., MICRO 2018, §V-A1, Table I; Yu
///     et al., MICRO 2019): an instruction is safe once it either (i) is at the head of the ROB,
///     or (ii) is "speculative non-squashable" — preceded in the ROB only by instructions that
///     individually cannot be squashed by any of Table I's squashing events (control-flow
///     misprediction, exception, load-store/load-load address alias, memory consistency
///     violation, interrupt). This is strictly more conservative than the Spectre model
///     (<see cref="SpectreVisibilityTracker" />), which only tracks branches: here, an instruction
///     stays unsafe while ANY older in-flight instruction — a branch not yet resolved, a store
///     whose address isn't known yet, a load still awaiting SMB-bypass verification, or a
///     value-predicted/EOLE-Late-Execution instruction still awaiting verification — could still
///     trigger a squash. Multi-hart coherence-invalidation squashes (Table I's own "memory
///     consistency model violations" row) and single-core load-load aliasing are out of scope,
///     matching every other InvisiSpec/STT slice in this codebase — Horologium's OoOE trains have
///     no coherence-invalidation-triggered load-squash hook, and (unlike the paper's multi-core
///     setting) a single core never needs to reorder two of its own loads to the same line.
///     <para>
///         Condition (i) is enforced by the caller, independent of this class's bitmask state:
///         <c>OooTrain</c> force-resolves the current ROB head once per cycle (see
///         <see cref="ForceResolve" />) before anything reads <see cref="IsSafe" /> that cycle —
///         mirroring the paper's own reasoning that an instruction at the ROB head "cannot itself
///         be transient; it can be squashed (e.g., due to an exception), but it is a correct
///         instruction." This also guarantees no entry can wedge forever: whatever squash source
///         is still pending when an instruction reaches the ROB head is force-cleared right there,
///         at the latest — a dynamically-timed source this tracker can't pin down early (see
///         <see cref="Sources.Smb" />'s doc) simply rides along until then, which only costs
///         performance fidelity, never safety.
///     </para>
///     <para>
///         Each in-flight instruction is registered at Dispatch (<see cref="Register" />) with a
///         bitmask of the squash sources that apply to it — every instruction gets
///         <see cref="Sources.Trap" />; a branch additionally gets <see cref="Sources.Branch" />;
///         a store gets <see cref="Sources.StoreAddr" />; an SMB-bypassing load gets
///         <see cref="Sources.Smb" />; a value-predicted or EOLE-Late-Execution instruction gets
///         <see cref="Sources.Vp" />. <see cref="Register" /> is additive (a second call for the
///         same InstrId ORs in the new bits) since a branch is registered once generically and
///         once specifically, in the same Dispatch step. Resolving a bit only when its outcome is
///         "no squash" — leaving it set when the outcome IS a squash, exactly like a mispredicted
///         branch under the Spectre tracker — means an instruction that turns out to squash
///         something is never mistaken for safe before that squash actually fires (resolving
///         early would be a security hole; resolving late, including the commit-time fallback
///         above, only costs performance). An instruction is fully resolved, and removed from the
///         FIFO, only once every bit in its mask has cleared.
///     </para>
/// </summary>
public sealed class FuturisticVisibilityTracker : IVisibilityTracker {
    [Flags]
    public enum Sources : byte {
        None = 0,

        /// <summary>Every instruction: could still turn out to raise an exception.</summary>
        Trap = 1 << 0,

        /// <summary>Branches/conditional branches: could still turn out mispredicted.</summary>
        Branch = 1 << 1,

        /// <summary>Stores/atomics: address not yet known — younger loads could still alias it.</summary>
        StoreAddr = 1 << 2,

        /// <summary>
        ///     SMB (NoSQ)-bypassing loads: bypass not yet verified against the load's own shadow
        ///     execution. Whether this resolves via the load's own completion or rides along to
        ///     the commit-time fallback depends on execution interleaving (see the class doc).
        /// </summary>
        Smb = 1 << 3,

        /// <summary>Value-predicted or EOLE-Late-Execution instructions: prediction not yet verified.</summary>
        Vp = 1 << 4,
    }

    private readonly LinkedList<ulong> _order = new();
    private readonly Dictionary<ulong, Sources> _pending = new();

    /// <summary>InstrId of the oldest still-unresolved in-flight instruction, or null if none.</summary>
    public ulong? OldestUnresolvedInstrId => _order.First?.Value;

    public void OnDispatchBranch(ulong instrId) => Register(instrId, Sources.Trap | Sources.Branch);
    public void OnBranchResolved(ulong instrId) => Resolve(instrId, Sources.Branch);

    /// <summary>True when no older in-flight instruction is still unresolved.</summary>
    public bool IsSafe(ulong instrId) => OldestUnresolvedInstrId is not { } oldest || oldest >= instrId;

    /// <summary>Drops entries younger than <paramref name="instrId" /> (execute-time partial squash).</summary>
    public void TruncateYoungerThan(ulong instrId) {
        while (_order.Last is { } node && node.Value > instrId) {
            _pending.Remove(node.Value);
            _order.RemoveLast();
        }
    }

    /// <summary>Drops every tracked entry (full pipeline flush).</summary>
    public void Clear() {
        _order.Clear();
        _pending.Clear();
    }

    /// <summary>
    ///     Registers an instruction entering the ROB at Dispatch with its applicable squash
    ///     sources. Additive: a second call for an InstrId already present ORs the new bits into
    ///     the existing pending mask rather than overwriting it.
    /// </summary>
    public void Register(ulong instrId, Sources sources) {
        if (sources == Sources.None) return;
        if (_pending.TryGetValue(instrId, out Sources existing)) {
            _pending[instrId] = existing | sources;
            return;
        }

        _order.AddLast(instrId);
        _pending[instrId] = sources;
    }

    /// <summary>Clears the trap-squash bit — call only when the instruction did NOT trap.</summary>
    public void ResolveTrap(ulong instrId) => Resolve(instrId, Sources.Trap);

    /// <summary>Clears the store-address bit — call once the store's address is known.</summary>
    public void ResolveStoreAddr(ulong instrId) => Resolve(instrId, Sources.StoreAddr);

    /// <summary>Clears the SMB-bypass bit — call only when verification found no mismatch.</summary>
    public void ResolveSmb(ulong instrId) => Resolve(instrId, Sources.Smb);

    /// <summary>Clears the value-prediction bit — call only when verification found no mismatch.</summary>
    public void ResolveVp(ulong instrId) => Resolve(instrId, Sources.Vp);

    private void Resolve(ulong instrId, Sources bit) {
        if (!_pending.TryGetValue(instrId, out Sources m)) return;
        m &= ~bit;
        if (m == Sources.None)
            _pending.Remove(instrId);
        else
            _pending[instrId] = m;
        TrimHead();
    }

    /// <summary>
    ///     Condition (i): unconditionally clears every remaining pending bit for
    ///     <paramref name="instrId" />, once it becomes the head of the ROB — see the class doc's
    ///     second paragraph. Safe even if this instruction is about to be squashed this same
    ///     cycle (e.g. a trap firing at commit): per the paper, that is a correct-path retirement
    ///     outcome, not a transient-execution leak.
    /// </summary>
    public void ForceResolve(ulong instrId) {
        if (_pending.Remove(instrId)) TrimHead();
    }

    private void TrimHead() {
        while (_order.First is { } node && !_pending.ContainsKey(node.Value)) _order.RemoveFirst();
    }
}