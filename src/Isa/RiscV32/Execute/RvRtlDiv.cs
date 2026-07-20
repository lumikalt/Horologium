#region

using Mechanism;
using Mechanism.RtlFu;
using RiscV32.Decode;

#endregion

namespace RiscV32.Execute;

/// <summary>
///     Maps RV32M divide/remainder instructions onto the <c>native/RtlFu</c> DivUnit's
///     opcode encoding (0=DIV, 1=DIVU, 2=REM, 3=REMU) so an
///     <see cref="RtlBackedExecutor" /> can run them on the Chisel divider instead of
///     <see cref="Rv32Executor" />'s C# model. All other instructions return null and
///     fall through to the wrapped executor.
/// </summary>
public static class RvRtlDiv {
    /// <summary>Selector for <see cref="RtlBackedExecutor" /> covering DIV/DIVU/REM/REMU.</summary>
    public static RtlRequest? Select(ITooth instruction, IArchState state) {
        (uint op, int rs1, int rs2) = instruction.Payload switch {
            RvDiv(_, var a, var b)  => (0u, a, b),
            RvDivu(_, var a, var b) => (1u, a, b),
            RvRem(_, var a, var b)  => (2u, a, b),
            RvRemu(_, var a, var b) => (3u, a, b),
            _                       => (uint.MaxValue, 0, 0),
        };
        if (op == uint.MaxValue) return null;

        IRegisterFile regs = state.IntegerRegisters;
        return new RtlRequest(op, (uint)regs.Read(rs1), (uint)regs.Read(rs2));
    }
}