using Mechanism;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.State;

namespace RiscV64.Execute;

/// <summary>
/// Executes RV64I instructions — extends Rv32Executor with:
///   • W-suffix instructions (ADDW, SUBW, …, ADDIW, SLLIW, …): operate on lower 32 bits,
///     sign-extend the 32-bit result to 64 bits.
///   • New loads/stores: LWU (zero-extend), LD (64-bit), SD (64-bit).
///   • Override of Reg() to return the full 64-bit value (no truncation).
///   • Override of Load() for 64-bit sign/zero extension.
///   • Semantic fixes for RV64: 6-bit shift amounts, 64-bit signed comparisons, LW sign-extension.
/// </summary>
public class Rv64Executor : Rv32Executor {
    // Sign-extend the lower 32 bits of v to 64 bits.
    private static ulong SexW(ulong v) => (ulong)(int)(uint)v;

    protected override ExecuteResult Reg(ulong value) => ExecuteResult.WithResult(value);

    protected override ExecuteResult Load(
        IMemory memory,
        IArchState state,
        ulong pc,
        ulong @base,
        int imm,
        int bytes,
        bool signExtend,
        int bits
    ) {
        ulong vaddr = @base + (ulong)imm;
        (ulong addr, int fault) = Translate(memory, state, vaddr, false, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        ulong value = memory.Read(addr, bytes);
        if (!signExtend) return Reg(value);
        int shift = 64 - bits;
        return Reg((ulong)((long)(value << shift) >> shift));
    }

    public override ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        if (instruction.Payload is not RvOp op)
            throw new InvalidOperationException(
                $"Rv64Executor received an instruction with an unexpected payload type: " +
                $"{instruction.Payload?.GetType().Name ?? "null"}"
            );

        IRegisterFile regs = state.IntegerRegisters;
        ulong pc = instruction.Pc;

        // Handle RV64-specific ops and base instruction overrides.
        // null means "fall through to base".
        ExecuteResult? r = op switch {
            // ── W-suffix (OP-32) ──────────────────────────────────────────────────
            RvAddw(_, var rs1, var rs2) =>
                Reg(SexW(regs.Read(rs1) + regs.Read(rs2))),
            RvSubw(_, var rs1, var rs2) =>
                Reg(SexW(regs.Read(rs1) - regs.Read(rs2))),
            RvSllw(_, var rs1, var rs2) =>
                Reg(SexW((uint)regs.Read(rs1) << (int)(regs.Read(rs2) & 0x1F))),
            RvSrlw(_, var rs1, var rs2) =>
                Reg(SexW((uint)regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x1F))),
            RvSraw(_, var rs1, var rs2) =>
                Reg(SexW((ulong)((int)regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x1F)))),

            // ── W-suffix (OP-IMM-32) ──────────────────────────────────────────────
            RvAddiw(_, var rs1, var imm) =>
                Reg(SexW(regs.Read(rs1) + (ulong)imm)),
            RvSlliw(_, var rs1, var sh) =>
                Reg(SexW((uint)regs.Read(rs1) << sh)),
            RvSrliw(_, var rs1, var sh) =>
                Reg(SexW((uint)regs.Read(rs1) >> sh)),
            RvSraiw(_, var rs1, var sh) =>
                Reg(SexW((ulong)((int)regs.Read(rs1) >> sh))),

            // ── New RV64I loads/stores ─────────────────────────────────────────────
            RvLwu(_, var rs1, var imm) =>
                Load(memory, state, pc, regs.Read(rs1), imm, 4, false, 32),
            RvLd (_, var rs1, var imm) =>
                Load(memory, state, pc, regs.Read(rs1), imm, 8, false, 64),
            RvSd (var rs1, var rs2, var imm) =>
                Store(memory, state, pc, regs.Read(rs1), imm, regs.Read(rs2), 8),

            // ── RV64 semantic overrides for base instructions ──────────────────────
            // ORI: immediate must be sign-extended to 64 bits, not zero-extended via (uint).
            RvOri(_, var rs1, var imm) =>
                Reg(regs.Read(rs1) | (ulong)(long)imm),

            // Shifts: 6-bit shamt mask in RV64 (RV32 uses 5-bit).
            RvSll(_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) << (int)(regs.Read(rs2) & 0x3F)),
            RvSrl(_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x3F)),
            RvSra(_, var rs1, var rs2) =>
                Reg((ulong)((long)regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x3F))),
            RvSlli(_, var rs1, var sh) =>
                Reg(regs.Read(rs1) << sh),
            RvSrli(_, var rs1, var sh) =>
                Reg(regs.Read(rs1) >> sh),
            RvSrai(_, var rs1, var sh) =>
                Reg((ulong)((long)regs.Read(rs1) >> sh)),

            // SLT/SLTI/SLTIU: 64-bit signed/unsigned comparisons.
            RvSlt(_, var rs1, var rs2) =>
                Reg((long)regs.Read(rs1) < (long)regs.Read(rs2) ? 1UL : 0UL),
            RvSlti(_, var rs1, var imm) =>
                Reg((long)regs.Read(rs1) < imm ? 1UL : 0UL),
            RvSltiu(_, var rs1, var imm) =>
                Reg(regs.Read(rs1) < (ulong)(long)imm ? 1UL : 0UL),

            // BLT/BGE: 64-bit signed comparisons.
            RvBlt(var rs1, var rs2, var imm) =>
                Branch((long)regs.Read(rs1) < (long)regs.Read(rs2), pc, imm, instruction.SizeBytes),
            RvBge(var rs1, var rs2, var imm) =>
                Branch((long)regs.Read(rs1) >= (long)regs.Read(rs2), pc, imm, instruction.SizeBytes),

            // LW: sign-extend 32→64 in RV64 (base executor zero-extends).
            RvLw(_, var rs1, var imm) =>
                Load(memory, state, pc, regs.Read(rs1), imm, 4, true, 32),

            _ => null,
        };

        return r ?? base.Execute(instruction, state, memory);
    }
}