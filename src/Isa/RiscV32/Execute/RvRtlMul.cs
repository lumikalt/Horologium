using Mechanism;
using Mechanism.RtlFu;
using RiscV32.Decode;

namespace RiscV32.Execute;

/// <summary>
///     Maps RV32M multiply instructions onto the <c>native/RtlFu</c> MulUnit's opcode
///     encoding (funct3 order: 0=MUL, 1=MULH, 2=MULHSU, 3=MULHU) so an
///     <see cref="RtlBackedExecutor" /> can run them on the pipelined Chisel multiplier
///     instead of <see cref="Rv32Executor" />'s C# model. All other instructions return
///     null and fall through to the wrapped executor — chain with
///     <see cref="RvRtlDiv" /> to substitute the whole M extension.
/// </summary>
public static class RvRtlMul {
    /// <summary>Selector for <see cref="RtlBackedExecutor" /> covering MUL/MULH/MULHSU/MULHU.</summary>
    public static RtlRequest? Select(ITooth instruction, IArchState state) {
        (uint op, int rs1, int rs2) = instruction.Payload switch {
            RvMul(_, var a, var b)    => (0u, a, b),
            RvMulh(_, var a, var b)   => (1u, a, b),
            RvMulhsu(_, var a, var b) => (2u, a, b),
            RvMulhu(_, var a, var b)  => (3u, a, b),
            _                         => (uint.MaxValue, 0, 0),
        };
        if (op == uint.MaxValue) return null;

        IRegisterFile regs = state.IntegerRegisters;
        return new RtlRequest(op, (uint)regs.Read(rs1), (uint)regs.Read(rs2));
    }
}
