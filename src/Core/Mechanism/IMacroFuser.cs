namespace Mechanism;

/// <summary>
///     Detects and builds fused macro-ops from two adjacent decoded instructions —
///     the inverse of <see cref="IImpulseCracker" />. Optional: null (via
///     <see cref="IMechanism.MacroFuser" />) for ISAs or configurations that define
///     no fusible pairs.
///     <para>
///         The pipeline is the one that guarantees adjacency: it only ever offers a
///         pair that was fetched back-to-back along the same speculative path with
///         nothing between them (no redirect landed on the second instruction's PC
///         from elsewhere). <see cref="TryFuse" /> itself is pure opcode/operand
///         pattern matching — it never needs to re-check control flow.
///     </para>
/// </summary>
public interface IMacroFuser {
    /// <summary>
    ///     Returns a single <see cref="ITooth" /> representing the combined effect of
    ///     <paramref name="first" /> followed immediately by <paramref name="second" />,
    ///     or null if this pair does not form a recognised fusible pattern.
    /// </summary>
    ITooth? TryFuse(ITooth first, ITooth second);
}
