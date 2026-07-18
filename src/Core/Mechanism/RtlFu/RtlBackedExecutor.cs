namespace Mechanism.RtlFu;

/// <summary>
///     One operation for an RTL functional unit: an opcode in the unit's own encoding
///     plus two 32-bit operand values. Produced by an ISA-specific selector (which knows
///     how to map its decoded instructions onto the unit's opcodes and read the operand
///     registers); consumed opaquely by <see cref="RtlBackedExecutor" />.
/// </summary>
public readonly record struct RtlRequest(uint Op, uint A, uint B);

/// <summary>
///     Decorator that substitutes a cycle-accurate RTL model for one functional unit:
///     instructions the <c>selector</c> claims are executed on the RTL unit (its result
///     becomes the register write, its observed cycle count becomes
///     <see cref="ExecuteResult.LatencyOverride" />); everything else is delegated to the
///     wrapped executor unchanged.
///     <para>
///         The selector reads operand values through <c>state.IntegerRegisters</c> at
///         call time, so it observes exactly what the wrapped executor would — including
///         pipeline forwarding overlays and the OoO train's speculative register dance.
///     </para>
///     <para>
///         Stateless per the <see cref="IExecutor" /> contract: the RTL unit returns to
///         idle after every operation, so re-executing an instruction (e.g. on a runahead
///         shadow lane or after a squash) reproduces the same result.
///     </para>
/// </summary>
public sealed class RtlBackedExecutor(
    IExecutor inner,
    RtlFfiFunctionalUnit unit,
    Func<ITooth, IArchState, RtlRequest?> selector
) : IExecutor {
    /// <inheritdoc />
    public ExecuteResult Execute(
        ITooth instruction,
        IArchState state,
        IMemory memory
    ) {
        if (selector(instruction, state) is not { } req) return inner.Execute(instruction, state, memory);

        (uint result, int cycles) = unit.Execute(req.Op, req.A, req.B);
        return new ExecuteResult {
            RegisterResult = (result, true), // zero-extended, matching RV32's Reg() truncation
            LatencyOverride = cycles,
        };
    }
}