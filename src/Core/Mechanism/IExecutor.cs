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

    /// <summary>
    ///     Resolves a store's effective address without requiring its data operand to be
    ///     known — lets a train release address-only dependents (aliasing checks, forwarding
    ///     candidate matching) as soon as the address operand is ready, independent of a
    ///     slower data-producing chain. Returns null if <paramref name="instruction" /> isn't
    ///     a store, if the ISA doesn't support early address resolution, or if translation
    ///     would fault — a swallowed fault here is not a missed trap: the store's own real
    ///     <see cref="Execute" /> call still translates and raises it normally once the store
    ///     fully issues. Default: unsupported.
    /// </summary>
    ulong? TryComputeStoreAddress(ITooth instruction, IArchState state, IMemory memory) => null;
}