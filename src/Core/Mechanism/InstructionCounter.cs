namespace Mechanism;

/// <summary>
///     Counts committed instructions via <see cref="ICommitObserver" />, optionally firing a
///     callback as a caller-supplied, ascending list of target counts is crossed. Two uses:
///     <list type="bullet">
///         <item>
///             <description>
///                 As a plain counter (<paramref name="targets" /> omitted): drive an
///                 instruction-count-bounded stepping loop by polling <see cref="Count" /> between
///                 <c>StepCycle()</c> calls — <c>Train.Run</c>/<c>ISteppableTrain.Run</c> only
///                 support tick-bounded stops.
///             </description>
///         </item>
///         <item>
///             <description>
///                 With <paramref name="targets" />: fire <paramref name="onTargetReached" /> (e.g.
///                 to save an <see cref="ArchitecturalCheckpoint" />) each time the running count
///                 crosses one of the targets, in a single functional pass — used to save one
///                 checkpoint per SimPoint simulation point without re-running the workload from
///                 scratch for each one.
///             </description>
///         </item>
///     </list>
/// </summary>
/// <param name="targets">
///     Ascending target instruction counts. A target may be crossed by more than one commit at once
///     on a wide pipeline (one <c>OnCommit</c> call per instruction, but several may land in the
///     same tick) — <see cref="OnCommit" /> handles that by firing every target whose threshold the
///     new count has reached or passed, in a loop, not just the first.
/// </param>
/// <param name="onTargetReached">Invoked with the index into <paramref name="targets" /> that was just reached.</param>
public sealed class InstructionCounter(
    IReadOnlyList<long>? targets = null,
    Action<int>? onTargetReached = null
) : ICommitObserver {
    private int _nextTargetIndex;

    /// <summary>Total instructions committed so far.</summary>
    public long Count { get; private set; }

    /// <inheritdoc />
    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        Count++;
        while (targets is not null && _nextTargetIndex < targets.Count && Count >= targets[_nextTargetIndex]) {
            onTargetReached?.Invoke(_nextTargetIndex);
            _nextTargetIndex++;
        }
    }
}
