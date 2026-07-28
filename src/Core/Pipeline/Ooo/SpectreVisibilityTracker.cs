namespace Pipeline.Ooo;

/// <summary>
///     Tracks the Spectre-model visibility point (Yan et al., MICRO 2018, Table 1): an
///     instruction is "safe" once every strictly older branch in the ROB has resolved
///     (predicted-vs-actual direction confirmed). Until then it is "unsafe" — its
///     effects (a speculative load's cache footprint, a tainted register's use as a
///     transmitter address) must be treated as still-revocable.
///     <para>
///         Maintained incrementally as a FIFO of in-flight branch InstrIds (dispatch
///         order is InstrId order) so querying or updating never rescans the ROB. Only
///         branches are tracked; the oldest entry still unresolved is the visibility
///         threshold — any instruction with InstrId ≤ that threshold has no older
///         unresolved branch and is therefore safe.
///     </para>
///     <para>
///         The Futuristic model (visibility point = ROB head, i.e. "preceded only by
///         non-squashable instructions") is deliberately not implemented here — it
///         additionally requires tracking unresolved loads and traps as squash sources,
///         a larger scope left for a follow-up.
///     </para>
/// </summary>
public sealed class SpectreVisibilityTracker {
    private readonly LinkedList<ulong> _branches = new();
    private readonly HashSet<ulong> _resolved = [];

    /// <summary>Registers a branch entering the ROB at Dispatch.</summary>
    public void OnDispatchBranch(ulong instrId) => _branches.AddLast(instrId);

    /// <summary>Marks a branch resolved (its predicted-vs-actual direction is now known).</summary>
    public void OnBranchResolved(ulong instrId) {
        _resolved.Add(instrId);
        while (_branches.First is { } node && _resolved.Contains(node.Value)) {
            _resolved.Remove(node.Value);
            _branches.RemoveFirst();
        }
    }

    /// <summary>InstrId of the oldest still-unresolved in-flight branch, or null if none.</summary>
    public ulong? OldestUnresolvedBranchInstrId => _branches.First?.Value;

    /// <summary>True when no older branch is still unresolved for the given InstrId.</summary>
    public bool IsSafe(ulong instrId) => OldestUnresolvedBranchInstrId is not { } oldest || oldest >= instrId;

    /// <summary>Drops branches younger than <paramref name="instrId" /> (execute-time partial squash).</summary>
    public void TruncateYoungerThan(ulong instrId) {
        while (_branches.Last is { } node && node.Value > instrId) {
            _resolved.Remove(node.Value);
            _branches.RemoveLast();
        }
    }

    /// <summary>Drops every tracked branch (full pipeline flush).</summary>
    public void Clear() {
        _branches.Clear();
        _resolved.Clear();
    }
}
