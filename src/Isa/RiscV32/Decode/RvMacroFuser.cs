#region

using Mechanism;

#endregion

namespace RiscV32.Decode;

/// <summary>
///     RV32I macro-fusion: recognises the compiler-emitted SLT(U)/SLTI(U) + BEQ/BNE
///     idiom that RISC-V code uses for comparisons its branch encodings can't express
///     directly (signed/unsigned "greater than" and "less-or-equal") — e.g. <c>a &gt; b</c>
///     lowers to <c>slt tmp, b, a</c> followed by <c>bne tmp, zero, target</c>.
///     <para>
///         Unlike x86, RISC-V branches already embed their own comparison (no flags
///         register), so there is no literal "cmp+jcc" pair to fuse — the SLT-family
///         instruction <em>is</em> the compare, and the only branches worth fusing it
///         with are the ones that test its result against <c>x0</c>.
///     </para>
/// </summary>
public sealed class RvMacroFuser : IMacroFuser {
    public ITooth? TryFuse(ITooth first, ITooth second) {
        if (first.Payload is not RvOp cmp) return null;
        if (second.Payload is not RvOp br) return null;

        int cmpRd = cmp switch {
            RvSlt(var rd, _, _)   => rd,
            RvSltu(var rd, _, _)  => rd,
            RvSlti(var rd, _, _)  => rd,
            RvSltiu(var rd, _, _) => rd,
            _                     => -1,
        };
        if (cmpRd <= 0) return null; // not a compare, or writes x0 (dead — nothing to fuse toward)

        (int rs1, int rs2, int imm, bool takenWhenNonZero) = br switch {
            RvBeq(var a, var b, var i) => (a, b, i, false),
            RvBne(var a, var b, var i) => (a, b, i, true),
            _                          => (-1, -1, 0, false),
        };
        if (rs1 < 0) return null; // not beq/bne

        bool comparesAgainstZero = (rs1 == cmpRd && rs2 == 0) || (rs2 == cmpRd && rs1 == 0);
        if (!comparesAgainstZero) return null;

        var fused = new RvFusedCompareBranch(cmp, takenWhenNonZero, second.Pc, imm);
        return new RvInstruction(
            first.Pc,
            first.RawEncoding,
            cmpRd,
            first.SourceRegisters,
            ToothClass.ConditionalBranch,
            fused,
            first.SizeBytes + second.SizeBytes,
            archInstructionCount: 2,
            branchComponent: second
        );
    }
}
