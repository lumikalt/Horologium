namespace Mechanism;

/// <summary>
///     Cracks a macro-instruction into one or more micro-operations (Impulses).
///     <para>
///         Complex instructions (e.g. load-modify-store, push/pop, string ops)
///         are normalised here into simpler Impulses that the backend pipeline
///         can schedule uniformly.
///     </para>
///     <para>
///         Simple instructions crack into exactly one Impulse.
///         The cracker is optional — Trains that do not use µops never call it.
///     </para>
/// </summary>
public interface IImpulseCracker {
    /// <summary>
    ///     Cracks <paramref name="instruction" /> into one or more Impulses.
    ///     The returned span is valid until the next call to Crack on this instance.
    /// </summary>
    ReadOnlySpan<Impulse> Crack(ITooth instruction);
}

/// <summary>
///     A micro-operation — the atomic unit of work in the backend pipeline.
///     <para>
///         An Impulse carries enough information for the backend to:
///         <list type="bullet">
///             <item>
///                 <description>Rename its register operands</description>
///             </item>
///             <item>
///                 <description>Assign it to an execution unit</description>
///             </item>
///             <item>
///                 <description>Track it in the reorder buffer</description>
///             </item>
///             <item>
///                 <description>Commit its result in program order</description>
///             </item>
///         </list>
///     </para>
/// </summary>
public readonly record struct Impulse() {
    /// <summary>The macro-instruction this µop belongs to.</summary>
    public required ITooth Parent { get; init; }

    /// <summary>
    ///     The index of this µop within its parent instruction (0-based).
    ///     The last µop in a group has IsLast = true.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>True if this is the last µop of its parent instruction.</summary>
    public required bool IsLast { get; init; }

    /// <summary>The execution unit class this µop targets.</summary>
    public required ToothClass Class { get; init; }

    /// <summary>
    ///     The physical destination register index after renaming.
    ///     -1 if this µop does not produce a result.
    ///     Set by the Rename stage, not the cracker.
    /// </summary>
    public int PhysicalDestination { get; init; } = -1;

    /// <summary>
    ///     ISA-specific payload for this µop.
    ///     The execution unit casts this to its concrete type.
    /// </summary>
    public object? Payload { get; init; }
}