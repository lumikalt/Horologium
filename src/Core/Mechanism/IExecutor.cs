namespace Mechanism;

/// <summary>
///     Executes a single decoded instruction against the current architectural state.
///     <para>
///         The executor is the only place ISA semantics live. It reads registers,
///         computes results, and returns an ExecuteResult. It does not write back
///         to IArchState directly — the pipeline applies the result at writeback.
///     </para>
///     <para>
///         The executor is stateless with respect to the pipeline — calling it
///         twice with the same instruction and state always produces the same result.
///     </para>
/// </summary>
public interface IExecutor {
    /// <summary>
    ///     Executes <paramref name="instruction" /> given <paramref name="state" />
    ///     and <paramref name="memory" />.
    /// </summary>
    ExecuteResult Execute(
        ITooth instruction,
        IArchState state,
        IMemory memory
    );
}