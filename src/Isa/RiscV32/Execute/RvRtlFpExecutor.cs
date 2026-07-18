using Mechanism;
using Mechanism.RtlFu;
using RiscV32.Decode;
using RiscV32.State;

namespace RiscV32.Execute;

/// <summary>
///     Decorator running FDIV.S and FSQRT.S on the flag-reporting RTL FP unit
///     (<c>native/RtlFu/FDivSqrtUnit.scala</c> via rtl_fpu_shim) instead of
///     <see cref="Rv32Executor" />'s C# model. Unlike the integer selectors this is a
///     full ISA-side decorator rather than an <see cref="RtlBackedExecutor" /> selector,
///     because the result assembly is RISC-V-specific: operands go through the §11.3
///     NaN-boxing check exactly like the C# <c>FBits</c>, the 32-bit RTL result is
///     NaN-boxed back, and the RTL's IEEE exception flags are delivered by ORing into
///     the fflags CSR via <see cref="ExecuteResult.SideEffect" /> — the same path the
///     C# <c>FloatRegF</c> uses. The RTL always emits the canonical NaN for NaN
///     results, so no fixup is needed here.
///     <para>
///         The model's observed cycle count (1 for special cases, ~30 for the iterative
///         paths) becomes the instruction's FU latency via
///         <see cref="ExecuteResult.LatencyOverride" /> — a real timing change from the
///         static <c>FloatDivSqrtLatency</c> default of 16.
///     </para>
/// </summary>
public sealed class RvRtlFpExecutor(IExecutor inner, RtlFfiFunctionalUnit unit) : IExecutor {
    public ExecuteResult Execute(
        ITooth instruction,
        IArchState state,
        IMemory memory
    ) {
        (uint op, int rs1, int rs2) = instruction.Payload switch {
            RvFdivS(_, var a, var b) => (0u, a, b),
            RvFsqrtS(_, var a)       => (1u, a, 0),
            _                        => (uint.MaxValue, 0, 0),
        };
        if (op == uint.MaxValue) return inner.Execute(instruction, state, memory);

        IRegisterFile regs = state.IntegerRegisters; // unified int/fp file, like FBits
        uint a32 = FpBits(regs, rs1);
        uint b32 = op == 0u ? FpBits(regs, rs2) : 0u;
        (uint result, uint flags, int cycles) = unit.ExecuteWithFlags(op, a32, b32);

        return new ExecuteResult {
            RegisterResult = (0xFFFFFFFF00000000UL | result, true), // NaN-boxed, like FloatRegF
            LatencyOverride = cycles,
            SideEffect = flags == 0
                ? null
                : s => ((Rv32ArchState)s).CsrFile.OrFflags(flags),
        };
    }

    // §11.3 NaN-boxing check, mirroring Rv32Executor.FBits: an improperly boxed
    // value reads as the canonical NaN.
    private static uint FpBits(IRegisterFile regs, int rs) {
        ulong raw = regs.Read(rs);
        return raw >> 32 == 0xFFFFFFFFu ? (uint)raw : 0x7FC00000u;
    }
}