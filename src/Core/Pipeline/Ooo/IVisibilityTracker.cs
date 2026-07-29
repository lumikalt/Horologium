namespace Pipeline.Ooo;

/// <summary>
///     Common interface for the shared STT/InvisiSpec visibility-point tracker, selectable
///     between the Spectre model (<see cref="SpectreVisibilityTracker" />) and the Futuristic
///     model (<see cref="FuturisticVisibilityTracker" />) — Yan et al., MICRO 2018, Table I;
///     Yu et al., MICRO 2019. Every STT/InvisiSpec gating call site (<c>IsSafe</c>) is written
///     against this interface, so switching models is purely a matter of which concrete tracker
///     backs <c>_vpTracker</c> — no gating call site needs to know which model is active.
/// </summary>
public interface IVisibilityTracker {
    /// <summary>Registers a branch entering the ROB at Dispatch.</summary>
    void OnDispatchBranch(ulong instrId);

    /// <summary>Marks a branch resolved (its predicted-vs-actual direction is now known).</summary>
    void OnBranchResolved(ulong instrId);

    /// <summary>True when no older in-flight instruction could still squash <paramref name="instrId" />.</summary>
    bool IsSafe(ulong instrId);

    /// <summary>Drops entries younger than <paramref name="instrId" /> (execute-time partial squash).</summary>
    void TruncateYoungerThan(ulong instrId);

    /// <summary>Drops every tracked entry (full pipeline flush).</summary>
    void Clear();
}
