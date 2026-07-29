#region

using System.Collections.Generic;
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
    public ITooth? TryFuse(ITooth first, ITooth second) =>
        TryFuseCompareBranch(first, second) ?? TryFuseLoadAlu(first, second);

    private static ITooth? TryFuseCompareBranch(ITooth first, ITooth second) {
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

    /// <summary>
    ///     Micro-fusion: a load immediately followed by an ALU op that both reads and
    ///     overwrites the load's own destination register (a common compiler
    ///     read-modify idiom, e.g. <c>lw t0,0(a0); addi t0,t0,4</c>). Safe without a
    ///     general liveness analysis — see <see cref="RvFusedLoadAlu" />'s doc comment.
    /// </summary>
    private static ITooth? TryFuseLoadAlu(ITooth first, ITooth second) {
        if (first.Payload is not RvOp load) return null;
        if (second.Payload is not RvOp aluOp) return null;

        int rdLoad = load switch {
            RvLw(var rd, _, _)  => rd,
            RvLh(var rd, _, _)  => rd,
            RvLhu(var rd, _, _) => rd,
            RvLb(var rd, _, _)  => rd,
            RvLbu(var rd, _, _) => rd,
            _                   => -1,
        };
        if (rdLoad <= 0) return null; // not a load, or writes x0 — dead, nothing to fuse toward

        if (second.Class != ToothClass.IntegerAlu) return null;
        if (second.DestinationRegister != rdLoad) return null;
        if (!second.SourceRegisters.Contains(rdLoad)) return null; // must actually consume the loaded value

        // The ALU op may read a second, independent register (register-register form,
        // e.g. `add t0,t0,t1`) that the load's own SourceRegisters knows nothing about —
        // the fused entry must still list it so the issue queue waits for/wakes on it.
        List<int>? extraSources = null;
        foreach (int src in second.SourceRegisters) {
            if (src == rdLoad) continue;
            (extraSources ??= []).Add(src);
        }

        IReadOnlyList<int> sources = extraSources is null
            ? first.SourceRegisters
            : [..first.SourceRegisters, ..extraSources,];

        var fused = new RvFusedLoadAlu(load, aluOp);
        return new RvInstruction(
            first.Pc,
            first.RawEncoding,
            rdLoad,
            sources,
            ToothClass.Load,
            fused,
            first.SizeBytes + second.SizeBytes,
            archInstructionCount: 2,
            branchComponent: second
        );
    }
}
