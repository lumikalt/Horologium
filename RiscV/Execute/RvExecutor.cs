using Mechanism;
using RiscV.Decode;
using RiscV.Memory;
using RiscV.Registers;
using RiscV.State;

namespace RiscV.Execute;

/// <summary>
/// Executes a single decoded RV32I instruction.
/// Reads from IArchState, returns an ExecuteResult — never writes back directly.
/// </summary>
public sealed class RvExecutor : IExecutor {
    public ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        if (instruction.Payload is not RvOp op)
            throw new InvalidOperationException(
                $"RvExecutor received an instruction with an unexpected payload type: " +
                $"{instruction.Payload?.GetType().Name ?? "null"}"
            );

        IRegisterFile regs = state.IntegerRegisters;
        ulong pc = instruction.Pc;

        return op switch {
            // ── R-type ────────────────────────────────────────────────────────
            RvAdd (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) + regs.Read(rs2)),
            RvSub (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) - regs.Read(rs2)),
            RvXor (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) ^ regs.Read(rs2)),
            RvOr (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) | regs.Read(rs2)),
            RvAnd (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) & regs.Read(rs2)),
            RvSll (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) << (int)(regs.Read(rs2) & 0x1F)),
            RvSrl (_, var rs1, var rs2) =>
                Reg((uint)regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x1F)),
            RvSra (_, var rs1, var rs2) =>
                Reg((ulong)((int)regs.Read(rs1) >> (int)(regs.Read(rs2) & 0x1F))),
            RvSlt (_, var rs1, var rs2) =>
                Reg((int)regs.Read(rs1) < (int)regs.Read(rs2) ? 1UL : 0UL),
            RvSltu (_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) < regs.Read(rs2) ? 1UL : 0UL),

            // ── I-type ALU ────────────────────────────────────────────────────
            RvAddi (_, var rs1, var imm) =>
                Reg(regs.Read(rs1) + (ulong)imm),
            RvXori (_, var rs1, var imm) =>
                Reg(regs.Read(rs1) ^ (ulong)imm),
            RvOri (_, var rs1, var imm) =>
                Reg(regs.Read(rs1) | (uint)imm),
            RvAndi (_, var rs1, var imm) =>
                Reg(regs.Read(rs1) & (ulong)imm),
            RvSlli (_, var rs1, var shamt) =>
                Reg(regs.Read(rs1) << shamt),
            RvSrli (_, var rs1, var shamt) =>
                Reg((uint)regs.Read(rs1) >> shamt),
            RvSrai (_, var rs1, var shamt) =>
                Reg((ulong)((int)regs.Read(rs1) >> shamt)),
            RvSlti (_, var rs1, var imm) =>
                Reg((int)regs.Read(rs1) < imm ? 1UL : 0UL),
            RvSltiu(_, var rs1, var imm) =>
                Reg((uint)regs.Read(rs1) < (uint)imm ? 1UL : 0UL),

            // ── Loads ─────────────────────────────────────────────────────────
            RvLb (_, var rs1, var imm) => Load(
                memory, state, pc,
                regs.Read(rs1), imm, 1, true, 8
            ),
            RvLh (_, var rs1, var imm) => Load(
                memory, state, pc,
                regs.Read(rs1), imm, 2, true, 16
            ),
            RvLw (_, var rs1, var imm) => Load(
                memory, state, pc,
                regs.Read(rs1), imm, 4, false, 32
            ),
            RvLbu (_, var rs1, var imm) => Load(
                memory, state, pc,
                regs.Read(rs1), imm, 1, false, 8
            ),
            RvLhu (_, var rs1, var imm) => Load(
                memory, state, pc,
                regs.Read(rs1), imm, 2, false, 16
            ),

            // ── Stores ────────────────────────────────────────────────────────
            RvSb (var rs1, var rs2, var imm) =>
                Store(memory, state, pc, regs.Read(rs1), imm, regs.Read(rs2), 1),
            RvSh (var rs1, var rs2, var imm) =>
                Store(memory, state, pc, regs.Read(rs1), imm, regs.Read(rs2), 2),
            RvSw (var rs1, var rs2, var imm) =>
                Store(memory, state, pc, regs.Read(rs1), imm, regs.Read(rs2), 4),

            // ── Branches ──────────────────────────────────────────────────────
            RvBeq (var rs1, var rs2, var imm) =>
                Branch(regs.Read(rs1) == regs.Read(rs2), pc, imm, instruction.SizeBytes),
            RvBne (var rs1, var rs2, var imm) =>
                Branch(regs.Read(rs1) != regs.Read(rs2), pc, imm, instruction.SizeBytes),
            RvBlt (var rs1, var rs2, var imm) =>
                Branch((int)regs.Read(rs1) < (int)regs.Read(rs2), pc, imm, instruction.SizeBytes),
            RvBge (var rs1, var rs2, var imm) =>
                Branch((int)regs.Read(rs1) >= (int)regs.Read(rs2), pc, imm, instruction.SizeBytes),
            RvBltu(var rs1, var rs2, var imm) =>
                Branch(regs.Read(rs1) < regs.Read(rs2), pc, imm, instruction.SizeBytes),
            RvBgeu(var rs1, var rs2, var imm) =>
                Branch(regs.Read(rs1) >= regs.Read(rs2), pc, imm, instruction.SizeBytes),

            // ── Jumps ─────────────────────────────────────────────────────────
            RvJal (_, var imm) =>
                new ExecuteResult {
                    RegisterResult = (pc + (ulong)instruction.SizeBytes, true),
                    BranchTaken = true,
                    BranchTarget = (ulong)((long)pc + imm),
                },
            RvJalr(_, var rs1, var imm) =>
                new ExecuteResult {
                    RegisterResult = (pc + (ulong)instruction.SizeBytes, true),
                    BranchTaken = true,
                    BranchTarget = (regs.Read(rs1) + (ulong)imm) & ~1UL,
                },

            // ── Upper immediates ──────────────────────────────────────────────
            RvLui (_, var imm)  => Reg((ulong)imm),
            RvAuipc(_, var imm) => Reg(pc + (ulong)imm),

            // ── System ────────────────────────────────────────────────────────
            RvEcall => ExecuteResult.WithTrap(
                new TrapInfo(EcallCause(state.PrivilegeLevel), 0, pc)
            ),

            RvEbreak => new ExecuteResult { IsHalt = true, },

            RvMret => state.PrivilegeLevel == RvPrivilege.Machine
                ? new ExecuteResult { IsReturnFromTrap = true, ReturnPrivilege = RvPrivilege.Machine, }
                : ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc)),

            RvSret => state.PrivilegeLevel >= RvPrivilege.Supervisor
                ? new ExecuteResult { IsReturnFromTrap = true, ReturnPrivilege = RvPrivilege.Supervisor, }
                : ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc)),

            RvWfi => WfiResult(state),

            RvFence => ExecuteResult.Clean, // NOP in single-core simulation

            // ── M extension ───────────────────────────────────────────────────
            // MUL: lower 32 bits of product (signed or unsigned — same result)
            RvMul(_, var rs1, var rs2) =>
                Reg(regs.Read(rs1) * regs.Read(rs2)),

            // MULH: upper 32 bits, signed × signed
            RvMulh(_, var rs1, var rs2) =>
                Reg((uint)(((int)regs.Read(rs1) * (Int128)(int)regs.Read(rs2)) >> 32)),

            // MULHSU: upper 32 bits, signed × unsigned
            RvMulhsu(_, var rs1, var rs2) =>
                Reg((uint)(((int)regs.Read(rs1) * (Int128)(uint)regs.Read(rs2)) >> 32)),

            // MULHU: upper 32 bits, unsigned × unsigned
            RvMulhu(_, var rs1, var rs2) =>
                Reg((uint)(((uint)regs.Read(rs1) * (UInt128)(uint)regs.Read(rs2)) >> 32)),

            // DIV: signed truncated division; div-by-zero → -1; INT_MIN/-1 → INT_MIN
            RvDiv(_, var rs1, var rs2) => DivSigned(regs, rs1, rs2),

            // DIVU: unsigned division; div-by-zero → 2^32-1
            RvDivu(_, var rs1, var rs2) => (uint)regs.Read(rs2) == 0
                ? Reg(0xFFFF_FFFF)
                : Reg((uint)regs.Read(rs1) / (uint)regs.Read(rs2)),

            // REM: signed remainder; div-by-zero → rs1; INT_MIN/-1 → 0
            RvRem(_, var rs1, var rs2) => RemSigned(regs, rs1, rs2),

            // REMU: unsigned remainder; div-by-zero → rs1
            RvRemu(_, var rs1, var rs2) => (uint)regs.Read(rs2) == 0
                ? Reg(regs.Read(rs1))
                : Reg((uint)regs.Read(rs1) % (uint)regs.Read(rs2)),

            // ── An extension (single-core: SC always succeeds, no reservation needed) ──
            RvLrW(_, var rs1) =>
                Load(memory, state, pc, regs.Read(rs1), 0, 4, false, 32),

            RvScW(_, var rs1, var rs2) => AmoSc(memory, state, pc, regs, rs1, rs2),

            RvAmoswapW(_, var rs1, var rs2) => Amo(memory, state, pc, regs, rs1, rs2, (_, v) => v),
            RvAmoaddW (_, var rs1, var rs2) => Amo(memory, state, pc, regs, rs1, rs2, (a, v) => a + v),
            RvAmoxorW (_, var rs1, var rs2) => Amo(memory, state, pc, regs, rs1, rs2, (a, v) => a ^ v),
            RvAmoandW (_, var rs1, var rs2) => Amo(memory, state, pc, regs, rs1, rs2, (a, v) => a & v),
            RvAmoorW (_, var rs1, var rs2)  => Amo(memory, state, pc, regs, rs1, rs2, (a, v) => a | v),
            RvAmominW (_, var rs1, var rs2) =>
                Amo(memory, state, pc, regs, rs1, rs2, (a, v) => (uint)Math.Min((int)a, (int)v)),
            RvAmomaxW (_, var rs1, var rs2) =>
                Amo(memory, state, pc, regs, rs1, rs2, (a, v) => (uint)Math.Max((int)a, (int)v)),
            RvAmominuW(_, var rs1, var rs2) =>
                Amo(memory, state, pc, regs, rs1, rs2, Math.Min),
            RvAmomaxuW(_, var rs1, var rs2) =>
                Amo(memory, state, pc, regs, rs1, rs2, Math.Max),

            // ── F extension ───────────────────────────────────────────────────
            // FLW: address computed from int rs1; result is raw bits stored in fp rd.
            RvFlw(_, var rs1, var imm) => Load(memory, state, pc, regs.Read(rs1), imm, 4, false, 32),

            // FSW: rs1 = int base address, rs2 = fp data register (unified index).
            RvFsw(var rs1, var rs2, var imm) =>
                Store(memory, state, pc, regs.Read(rs1), imm, regs.Read(rs2), 4),

            RvFaddS (_, var rs1, var rs2) => FloatReg(FBits(regs, rs1) + FBits(regs, rs2)),
            RvFsubS (_, var rs1, var rs2) => FloatReg(FBits(regs, rs1) - FBits(regs, rs2)),
            RvFmulS (_, var rs1, var rs2) => FloatReg(FBits(regs, rs1) * FBits(regs, rs2)),
            RvFdivS (_, var rs1, var rs2) => FloatReg(FBits(regs, rs1) / FBits(regs, rs2)),
            RvFsqrtS(_, var rs1)          => FloatReg(MathF.Sqrt(FBits(regs, rs1))),

            RvFsgnjS (_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    ((uint)regs.Read(rs1) & 0x7FFFFFFF) | ((uint)regs.Read(rs2) & 0x80000000)
                ),
            RvFsgnjnS(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    ((uint)regs.Read(rs1) & 0x7FFFFFFF) | (~(uint)regs.Read(rs2) & 0x80000000)
                ),
            RvFsgnjxS(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    ((uint)regs.Read(rs1) & 0x7FFFFFFF) |
                    (((uint)regs.Read(rs1) ^ (uint)regs.Read(rs2)) & 0x80000000)
                ),

            RvFminS(_, var rs1, var rs2) => FloatReg(FMin(FBits(regs, rs1), FBits(regs, rs2))),
            RvFmaxS(_, var rs1, var rs2) => FloatReg(FMax(FBits(regs, rs1), FBits(regs, rs2))),

            RvFeqS(_, var rs1, var rs2) => Reg(FBits(regs, rs1).Equals(FBits(regs, rs2)) ? 1UL : 0UL),
            RvFltS(_, var rs1, var rs2) => Reg(FBits(regs, rs1) < FBits(regs, rs2) ? 1UL : 0UL),
            RvFleS(_, var rs1, var rs2) => Reg(FBits(regs, rs1) <= FBits(regs, rs2) ? 1UL : 0UL),

            RvFclassS(_, var rs1) => Reg(FClass((uint)regs.Read(rs1))),

            RvFcvtWs (_, var rs1) => Reg((uint)(int)FBits(regs, rs1)),
            RvFcvtWuS(_, var rs1) => Reg((uint)FBits(regs, rs1)),
            RvFcvtSw (_, var rs1) => FloatReg((int)regs.Read(rs1)),
            RvFcvtSWu(_, var rs1) => FloatReg((uint)regs.Read(rs1)),

            RvFmvXw(_, var rs1) => Reg(regs.Read(rs1)), // fp bits → int (bit-exact)
            RvFmvWx(_, var rs1) => ExecuteResult.WithResult(regs.Read(rs1) & 0xFFFFFFFF),

            RvFmaddS (_, var rs1, var rs2, var rs3) =>
                FloatReg(MathF.FusedMultiplyAdd(FBits(regs, rs1), FBits(regs, rs2), FBits(regs, rs3))),
            RvFmsubS (_, var rs1, var rs2, var rs3) =>
                FloatReg(MathF.FusedMultiplyAdd(FBits(regs, rs1), FBits(regs, rs2), -FBits(regs, rs3))),
            RvFnmsubS(_, var rs1, var rs2, var rs3) =>
                FloatReg(MathF.FusedMultiplyAdd(-FBits(regs, rs1), FBits(regs, rs2), FBits(regs, rs3))),
            RvFnmaddS(_, var rs1, var rs2, var rs3) =>
                FloatReg(MathF.FusedMultiplyAdd(-FBits(regs, rs1), FBits(regs, rs2), -FBits(regs, rs3))),

            RvCsrrw (_, var rs1, var csr) => ExecuteCsr(
                state, rs1, csr, pc,
                (_, src) => src
            ),
            RvCsrrs (_, var rs1, var csr) => ExecuteCsr(
                state, rs1, csr, pc,
                (old, src) => old | src,
                false
            ),
            RvCsrrc (_, var rs1, var csr) => ExecuteCsr(
                state, rs1, csr, pc,
                (old, src) => old & ~src,
                false
            ),
            RvCsrrwi (_, var zimm, var csr) => ExecuteCsrImm(
                state, zimm, csr, pc,
                (_, src) => src
            ),
            RvCsrrsi (_, var zimm, var csr) => ExecuteCsrImm(
                state, zimm, csr, pc,
                (old, src) => old | src,
                false
            ),
            RvCsrrci (_, var zimm, var csr) => ExecuteCsrImm(
                state, zimm, csr, pc,
                (old, src) => old & ~src,
                false
            ),

            // ── V extension ───────────────────────────────────────────────────
            RvVsetvli (var rd, var rs1, var vtypei)   => ExecuteVsetvli(state, rd, rs1, vtypei),
            RvVsetivli (var rd, var zimm, var vtypei) => ExecuteVsetivli(state, rd, zimm, vtypei),
            RvVsetvl (var rd, var rs1, var rs2)       => ExecuteVsetvl(state, rd, rs1, rs2, regs),

            RvVleVv (var vd, var rs1, var sew, var masked) =>
                ExecuteVle(state, memory, vd, rs1, sew, masked),
            RvVlm (var vd, var rs1) =>
                ExecuteVlm(state, memory, vd, rs1),
            RvVseVv (var vs3, var rs1, var sew, var masked) =>
                ExecuteVse(state, memory, vs3, rs1, sew, masked),
            RvVsm (var vs3, var rs1) =>
                ExecuteVsm(state, memory, vs3, rs1),

            RvVIntAluVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVIntAlu(
                    state, op2, vd, vs2, masked,
                    (i, ew) => VReadElem(state, vs1, i, ew)
                ),
            RvVIntAluVx (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVIntAlu(
                    state, op2, vd, vs2, masked,
                    (_, _) => regs.Read(rs1)
                ),
            RvVIntAluVi (var op2, var vd, var vs2, var imm, var masked) =>
                ExecuteVIntAlu(
                    state, op2, vd, vs2, masked,
                    (_, _) => (ulong)imm
                ),

            RvVMaskCmpVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVMaskCmp(
                    state, op2, vd, vs2, masked,
                    (i, ew) => VReadElem(state, vs1, i, ew)
                ),
            RvVMaskCmpVx (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVMaskCmp(
                    state, op2, vd, vs2, masked,
                    (_, _) => regs.Read(rs1)
                ),
            RvVMaskCmpVi (var op2, var vd, var vs2, var imm, var masked) =>
                ExecuteVMaskCmp(
                    state, op2, vd, vs2, masked,
                    (_, _) => (ulong)imm
                ),

            // ── UVE extension ─────────────────────────────────────────────────
            RvUveSsLdW    (var ud, var rs1, var rs2, var rs3) => ExecuteUveSsLd(regs, ud, rs1, rs2, rs3),
            RvUveSsStW    (var ud, var rs1, var rs2, var rs3) => ExecuteUveSsSt(state, regs, ud, rs1, rs2, rs3),
            RvUveSsStaLdW (var ud, var rs1, var rs2, var rs3) => ExecuteUveSsSta(state, regs, ud, rs1, rs2, rs3, isLoad: true),
            RvUveSsStaStW (var ud, var rs1, var rs2, var rs3) => ExecuteUveSsSta(state, regs, ud, rs1, rs2, rs3, isLoad: false),
            RvUveSsApp    (var ud, var rs2, var rs3)           => ExecuteUveSsApp(state, regs, ud, rs2, rs3),
            RvUveSsEnd    (var ud, var rs2, var rs3)           => ExecuteUveSsEnd(state, regs, ud, rs2, rs3),
            RvUveSsCfgVec (var ud)                             => ExecuteUveSsCfgVec(state, ud),
            RvUveSoVDpW   (var ud, var rs1)                   => ExecuteUveSoVDpW(state, regs, ud, rs1),
            RvUveSoAFp    (var fpOp, var ud, var usrc1, var usrc2) => ExecuteUveSoAFp(state, memory, fpOp, ud, usrc1, usrc2),
            RvUveSoBNc    (var urs, var imm)                   => ExecuteUveSoBNc(state, pc, urs, imm),
            RvUveSoBNdc   (var urs, var dim, var imm)          => ExecuteUveSoBNdc(state, pc, urs, dim, imm),

            _ => throw new InvalidOperationException(
                $"Unhandled RvOp: {op.GetType().Name}"
            ),
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int EcallCause(PrivilegeLevel priv) => (int)priv switch {
        0 => RvTrapCause.EnvironmentCallFromU,
        1 => RvTrapCause.EnvironmentCallFromS,
        _ => RvTrapCause.EnvironmentCallFromM,
    };

    private static ExecuteResult WfiResult(IArchState state) {
        if (state.SystemRegisters is CsrFile csrs) {
            uint pendingAndEnabled = csrs.DirectRead(CsrFile.Sip)
                                   & csrs.DirectRead(CsrFile.Sie);
            if (pendingAndEnabled != 0) return ExecuteResult.Clean; // interrupt pending → wake immediately
        }

        return new ExecuteResult { IsHalt = true, };
    }

    private static ExecuteResult Reg(ulong value) =>
        ExecuteResult.WithResult(value & 0xFFFFFFFF); // truncate to 32 bits

    private static ExecuteResult DivSigned(IRegisterFile regs, int rs1, int rs2) {
        var a = (int)regs.Read(rs1);
        var b = (int)regs.Read(rs2);
        if (b == 0) return Reg(0xFFFF_FFFF);                                         // div-by-zero → -1
        if (a == int.MinValue && b == -1) return Reg(unchecked((uint)int.MinValue)); // overflow
        return Reg(unchecked((uint)(a / b)));
    }

    private static ExecuteResult RemSigned(IRegisterFile regs, int rs1, int rs2) {
        var a = (int)regs.Read(rs1);
        var b = (int)regs.Read(rs2);
        if (b == 0) return Reg(regs.Read(rs1));          // div-by-zero → rs1
        if (a == int.MinValue && b == -1) return Reg(0); // overflow → 0
        return Reg(unchecked((uint)(a % b)));
    }

    // Atomic read-modify-write. Returns original value; combines with rs2 and stores.
    private static ExecuteResult Amo(
        IMemory memory,
        IArchState state,
        ulong pc,
        IRegisterFile regs,
        int rs1,
        int rs2,
        Func<uint, uint, uint> combine
    ) {
        ulong vaddr = regs.Read(rs1);
        (ulong addr, int fault) = Translate(memory, state, vaddr, true, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        var old = (uint)memory.Read(addr, 4);
        memory.Write(addr, combine(old, (uint)regs.Read(rs2)), 4);
        return Reg(old);
    }

    // SC.W always succeeds in single-core (no competing stores possible).
    private static ExecuteResult AmoSc(
        IMemory memory,
        IArchState state,
        ulong pc,
        IRegisterFile regs,
        int rs1,
        int rs2
    ) {
        ulong vaddr = regs.Read(rs1);
        (ulong addr, int fault) = Translate(memory, state, vaddr, true, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        memory.Write(addr, regs.Read(rs2), 4);
        return Reg(0); // 0 = success
    }

    private static (ulong paddr, int faultCause) Translate(
        IMemory memory,
        IArchState state,
        ulong vaddr,
        bool isWrite,
        bool isExec
    ) {
        if (state.SystemRegisters is not CsrFile csrs) return (vaddr, 0);
        return Sv32Walker.Translate(
            memory, csrs.DirectRead(CsrFile.Satp), vaddr, isWrite, isExec, state.PrivilegeLevel
        );
    }

    private static ExecuteResult Load(
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
        if (!signExtend || bits >= 32) return ExecuteResult.WithResult(value & 0xFFFFFFFF);
        int shift = 32 - bits;
        value = (uint)((int)(value << shift) >> shift);
        return ExecuteResult.WithResult(value & 0xFFFFFFFF);
    }

    private static ExecuteResult Store(
        IMemory memory,
        IArchState state,
        ulong pc,
        ulong @base,
        int imm,
        ulong value,
        int bytes
    ) {
        ulong vaddr = @base + (ulong)imm;
        (ulong addr, int fault) = Translate(memory, state, vaddr, true, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        memory.Write(addr, value, bytes);
        return ExecuteResult.Clean;
    }

    private static ExecuteResult Branch(bool taken, ulong pc, int imm, int instrSize) =>
        ExecuteResult.WithBranch(taken, taken ? (ulong)((long)pc + imm) : pc + (ulong)instrSize);

    private static ExecuteResult ExecuteCsr(
        IArchState state,
        int rs1,
        uint csr,
        ulong pc,
        Func<ulong, ulong, ulong> combine,
        bool writeIfSrcZero = true
    ) {
        ISystemRegisters csrFile = state.SystemRegisters;
        try {
            ulong old = csrFile.Read(csr, state.PrivilegeLevel);
            ulong src = state.IntegerRegisters.Read(rs1);
            // Per spec §2.8: CSRRS/CSRRC with rs1==x0 must not write the CSR.
            if (writeIfSrcZero || rs1 != 0) csrFile.Write(csr, combine(old, src), state.PrivilegeLevel);
            return ExecuteResult.WithResult(old & 0xFFFFFFFF);
        }
        catch (SystemRegisterAccessException) {
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));
        }
    }

    // ── FP helpers ────────────────────────────────────────────────────────────

    // Read a float register as a C# float (bit-exact reinterpret).
    private static float FBits(IRegisterFile regs, int rs) =>
        BitConverter.Int32BitsToSingle((int)regs.Read(rs));

    // Produce a float result stored as raw 32-bit bits.
    private static ExecuteResult FloatReg(float value) =>
        ExecuteResult.WithResult((uint)BitConverter.SingleToInt32Bits(value));

    // RISC-V FCLASS encoding (10-bit result).
    private static ulong FClass(uint bits) {
        bool sign = bits >> 31 != 0;
        uint exp = (bits >> 23) & 0xFF;
        uint frac = bits & 0x7FFFFF;
        return exp switch {
            0xFF when frac == 0 => sign ? 1UL << 0 : 1UL << 7,
            0xFF                => frac >> 22 != 0 ? 1UL << 9 : 1UL << 8,
            0 => frac == 0 ? sign ? 1UL << 3 : 1UL << 4 // ±zero
                : sign     ? 1UL << 2 : 1UL << 5,
            _ => sign ? 1UL << 1 : 1UL << 6,
        };
    }

    // RISC-V FMIN: if one arg is NaN, return the other; -0.0 < +0.0.
    private static float FMin(float a, float b) {
        if (float.IsNaN(a)) return b;
        if (float.IsNaN(b)) return a;
        if (a == 0f && b == 0f)
            return BitConverter.SingleToInt32Bits(a) < 0 ||
                   BitConverter.SingleToInt32Bits(b) < 0
                ? -0f
                : 0f;
        return a < b ? a : b;
    }

    // RISC-V FMAX: if one arg is NaN, return the other; +0.0 > -0.0.
    private static float FMax(float a, float b) {
        if (float.IsNaN(a)) return b;
        if (float.IsNaN(b)) return a;
        if (a == 0f && b == 0f)
            return BitConverter.SingleToInt32Bits(a) >= 0 ||
                   BitConverter.SingleToInt32Bits(b) >= 0
                ? 0f
                : -0f;
        return a > b ? a : b;
    }

    private static ExecuteResult ExecuteCsrImm(
        IArchState state,
        uint zimm,
        uint csr,
        ulong pc,
        Func<ulong, ulong, ulong> combine,
        bool writeIfSrcZero = true
    ) {
        ISystemRegisters csrFile = state.SystemRegisters;
        try {
            ulong old = csrFile.Read(csr, state.PrivilegeLevel);
            // Per spec §2.8: CSRRSI/CSRRCI with zimm==0 must not write the CSR.
            if (writeIfSrcZero || zimm != 0) csrFile.Write(csr, combine(old, zimm), state.PrivilegeLevel);
            return ExecuteResult.WithResult(old & 0xFFFFFFFF);
        }
        catch (SystemRegisterAccessException) {
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));
        }
    }

    // ── V extension helpers ───────────────────────────────────────────────────

    private static RvArchState VState(IArchState state) => (RvArchState)state;

    private static ExecuteResult VectorWrite(int vd, byte[] data) {
        return new ExecuteResult { SideEffect = s => ((RvArchState)s).VectorRegisters.Write(vd, data), };
    }

    private static ulong VReadElem(IArchState state, int vreg, int idx, int ewBytes) {
        byte[] data = VState(state).VectorRegisters.Read(vreg);
        return ReadVElement(data, idx, ewBytes);
    }

    private static ulong ReadVElement(byte[] data, int idx, int ewBytes) {
        int off = idx * ewBytes;
        return ewBytes switch {
            1 => data[off],
            2 => (ulong)(data[off] | (data[off + 1] << 8)),
            _ => (ulong)(data[off] | (data[off + 1] << 8) | (data[off + 2] << 16) | (data[off + 3] << 24)),
        };
    }

    private static void WriteVElement(byte[] data, int idx, int ewBytes, ulong value) {
        int off = idx * ewBytes;
        for (var b = 0; b < ewBytes; b++) data[off + b] = (byte)(value >> (b * 8));
    }

    private static (uint vl, int ewBytes) VGetVlEw(IArchState state) {
        CsrFile csrs = VState(state).CsrFile;
        uint vl = csrs.DirectRead(CsrFile.Vl);
        uint vsew = csrs.DirectRead(CsrFile.Vtype) & 0x7;
        var ewBytes = (int)(1u << (int)vsew); // 1, 2, 4, 8
        return (vl, ewBytes);
    }

    private static uint ComputeVlmax(int vtypei) {
        var vsew = (uint)(vtypei & 0x7);
        var ewBits = (int)(8u << (int)vsew); // 8, 16, 32, 64
        int vlmulField = (vtypei >> 3) & 0x7;
        // LMUL: fields 0-3 = 1,2,4,8; fields 5-7 = 1/8,1/4,1/2 (fractional)
        uint vlmax;
        if (vlmulField <= 3)
            vlmax = ((uint)VectorRegisterFile.VLen << vlmulField) / (uint)ewBits;
        else
            vlmax = ((uint)VectorRegisterFile.VLen >> (8 - vlmulField)) / (uint)ewBits;
        return vlmax == 0 ? 1 : vlmax;
    }

    private static ExecuteResult ExecuteVsetvli(IArchState state, int rd, int rs1, int vtypei) {
        CsrFile csrs = VState(state).CsrFile;
        uint vlmax = ComputeVlmax(vtypei);
        uint currentVl = csrs.DirectRead(CsrFile.Vl);

        uint newVl = rd == 0 && rs1 == 0
            ? currentVl // preserve vl
            : rs1 == 0
                ? vlmax // set vl = vlmax
                : Math.Min((uint)state.IntegerRegisters.Read(rs1), vlmax);

        csrs.DirectWrite(CsrFile.Vl, newVl);
        csrs.DirectWrite(CsrFile.Vtype, (uint)vtypei);
        return rd == 0 ? ExecuteResult.Clean : ExecuteResult.WithResult(newVl);
    }

    private static ExecuteResult ExecuteVsetivli(IArchState state, int rd, int zimm, int vtypei) {
        CsrFile csrs = VState(state).CsrFile;
        uint vlmax = ComputeVlmax(vtypei);
        uint newVl = Math.Min((uint)zimm, vlmax);
        csrs.DirectWrite(CsrFile.Vl, newVl);
        csrs.DirectWrite(CsrFile.Vtype, (uint)vtypei);
        return rd == 0 ? ExecuteResult.Clean : ExecuteResult.WithResult(newVl);
    }

    private static ExecuteResult ExecuteVsetvl(
        IArchState state,
        int rd,
        int rs1,
        int rs2,
        IRegisterFile regs
    ) {
        CsrFile csrs = VState(state).CsrFile;
        var vtypei = (int)(uint)regs.Read(rs2);
        uint vlmax = ComputeVlmax(vtypei);
        uint currentVl = csrs.DirectRead(CsrFile.Vl);

        uint newVl = rd == 0 && rs1 == 0
            ? currentVl
            : rs1 == 0
                ? vlmax
                : Math.Min((uint)regs.Read(rs1), vlmax);

        csrs.DirectWrite(CsrFile.Vl, newVl);
        csrs.DirectWrite(CsrFile.Vtype, (uint)vtypei);
        return rd == 0 ? ExecuteResult.Clean : ExecuteResult.WithResult(newVl);
    }

    private static ExecuteResult ExecuteVle(
        IArchState state,
        IMemory memory,
        int vd,
        int rs1,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        var result = new byte[VectorRegisterFile.VLenB];
        byte[] mask = VState(state).VectorRegisters.Read(0);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong val = memory.Read(baseAddr + (ulong)(i * ewBytes), ewBytes);
            WriteVElement(result, i, ewBytes, val);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVlm(IArchState state, IMemory memory, int vd, int rs1) {
        (uint vl, _) = VGetVlEw(state);
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        var result = new byte[VectorRegisterFile.VLenB];
        // VLM loads ceil(vl/8) bytes
        int byteCount = ((int)vl + 7) / 8;
        for (var b = 0; b < byteCount; b++) result[b] = (byte)memory.Read(baseAddr + (ulong)b, 1);
        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVse(
        IArchState state,
        IMemory memory,
        int vs3,
        int rs1,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] data = VState(state).VectorRegisters.Read(vs3);
        byte[] mask = VState(state).VectorRegisters.Read(0);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong val = ReadVElement(data, i, ewBytes);
            memory.Write(baseAddr + (ulong)(i * ewBytes), val, ewBytes);
        }

        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecuteVsm(IArchState state, IMemory memory, int vs3, int rs1) {
        (uint vl, _) = VGetVlEw(state);
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] data = VState(state).VectorRegisters.Read(vs3);
        int byteCount = ((int)vl + 7) / 8;
        for (var b = 0; b < byteCount; b++) memory.Write(baseAddr + (ulong)b, data[b], 1);
        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecuteVIntAlu(
        IArchState state,
        VIntOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getSource
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0); // snapshot before any writes
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getSource(i, ewBytes);
            WriteVElement(result, i, ewBytes, ApplyVIntOp(op, a, b, ewBytes));
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVMaskCmp(
        IArchState state,
        VMaskCmpOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getSource
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getSource(i, ewBytes);
            if (ApplyVMaskCmp(op, a, b, ewBytes)) result[i >> 3] |= (byte)(1 << (i & 7));
        }

        return VectorWrite(vd, result);
    }

    private static ulong ApplyVIntOp(VIntOp op, ulong a, ulong b, int ewBytes) {
        int bits = ewBytes * 8;
        ulong mask = (1UL << bits) - 1;
        int shiftMask = bits - 1;
        ulong r = op switch {
            VIntOp.Add => a + b,
            VIntOp.Sub => a - b,
            VIntOp.And => a & b,
            VIntOp.Or  => a | b,
            VIntOp.Xor => a ^ b,
            VIntOp.Sll => a << (int)(b & (uint)shiftMask),
            VIntOp.Srl => (a & mask) >> (int)(b & (uint)shiftMask),
            VIntOp.Sra => ewBytes switch {
                1 => (byte)((sbyte)(byte)(a & 0xFF) >> (int)(b & 7)),
                2 => (ushort)((short)(ushort)(a & 0xFFFF) >> (int)(b & 15)),
                _ => (ulong)(uint)((int)(uint)(a & 0xFFFFFFFF) >> (int)(b & 31)),
            },
            _ => 0,
        };
        return r & mask;
    }

    private static bool ApplyVMaskCmp(VMaskCmpOp op, ulong a, ulong b, int ewBytes) =>
        op switch {
            VMaskCmpOp.Eq  => a == b,
            VMaskCmpOp.Ne  => a != b,
            VMaskCmpOp.Ltu => a < b,
            VMaskCmpOp.Gtu => a > b,
            VMaskCmpOp.Lt => ewBytes switch {
                1 => (sbyte)(byte)a < (sbyte)(byte)b,
                2 => (short)(ushort)a < (short)(ushort)b,
                _ => (int)(uint)a < (int)(uint)b,
            },
            VMaskCmpOp.Gt => ewBytes switch {
                1 => (sbyte)(byte)a > (sbyte)(byte)b,
                2 => (short)(ushort)a > (short)(ushort)b,
                _ => (int)(uint)a > (int)(uint)b,
            },
            _ => false,
        };

    // ── UVE extension helpers ─────────────────────────────────────────────────

    private static RvArchState UState(IArchState state) => (RvArchState)state;

    // ss.ld.w ud, rs1_base, rs2_count, rs3_stride
    // Returns a StreamConfig so the pipeline can configure the streaming engine.
    private static ExecuteResult ExecuteUveSsLd(IRegisterFile regs, int ud, int rs1, int rs2, int rs3) {
        ulong baseAddr = regs.Read(rs1);
        long count = (long)regs.Read(rs2);
        long stride = (long)regs.Read(rs3);
        return new ExecuteResult {
            StreamConfig = (ud, new StreamDescriptor(baseAddr, 4, count, stride)),
            SideEffect = s => { UState(s).UveState.RegKind[ud] = UveRegKind.LoadStream; },
        };
    }

    // ss.st.w ud, rs1_base, rs2_count, rs3_stride
    // Configures a store-stream cursor in UveState; no StreamingEngine involvement.
    private static ExecuteResult ExecuteUveSsSt(IArchState state, IRegisterFile regs, int ud, int rs1, int rs2, int rs3) {
        ulong baseAddr = regs.Read(rs1);
        long count = (long)regs.Read(rs2);
        long stride = (long)regs.Read(rs3);
        return new ExecuteResult {
            SideEffect = s => {
                UveState uveState = UState(s).UveState;
                var ss = new UveStoreStream {
                    BaseAddress = baseAddr, ElementBytes = 4,
                    Dimensions = [new StreamDimension(count, stride)],
                    Indices = [0],
                };
                ss.Initialize();
                uveState.StoreStreams[ud] = ss;
                uveState.RegKind[ud] = UveRegKind.StoreStream;
            },
        };
    }

    // so.v.dp.w ud, rs1 — broadcast float32 bits from integer register into u-reg scalar slot
    private static ExecuteResult ExecuteUveSoVDpW(IArchState state, IRegisterFile regs, int ud, int rs1) {
        float value = BitConverter.Int32BitsToSingle((int)(uint)regs.Read(rs1));
        return new ExecuteResult {
            SideEffect = s => {
                UveState uveState = UState(s).UveState;
                uveState.Scalars[ud] = value;
                uveState.RegKind[ud] = UveRegKind.Scalar;
            },
        };
    }

    // so.a.fp ud, usrc1, usrc2 — element-wise FP arithmetic
    // Source values were injected into UveState.Scalars[usrc*] by the pipeline before this call.
    // If ud is a store stream, the result is written to memory and the store cursor advances.
    private static ExecuteResult ExecuteUveSoAFp(IArchState state, IMemory memory, UveFpOp op, int ud, int usrc1, int usrc2) {
        UveState uveState = UState(state).UveState;
        float a = uveState.Scalars[usrc1];
        float b = uveState.Scalars[usrc2];
        float result = op switch {
            UveFpOp.Mul => a * b,
            UveFpOp.Add => a + b,
            UveFpOp.Mac => uveState.Scalars[ud] + a * b,
            UveFpOp.Sub => a - b,
            _ => throw new InvalidOperationException($"Unknown UveFpOp {op}"),
        };
        uint resultBits = (uint)BitConverter.SingleToInt32Bits(result);

        if (uveState.RegKind[ud] == UveRegKind.StoreStream && uveState.StoreStreams[ud] is { } ss) {
            // Write result element to the store stream's current memory address.
            ulong addr = ss.CurrentAddress;
            int ewBytes = ss.ElementBytes;
            memory.Write(addr, resultBits, ewBytes);
            return new ExecuteResult {
                SideEffect = s => {
                    UveState uvs = UState(s).UveState;
                    uvs.StoreStreams[ud]?.Advance();
                    uvs.Scalars[ud] = result;
                },
            };
        }

        // Destination is a scalar/accumulator u-reg: just store the result.
        return new ExecuteResult {
            SideEffect = s => { UState(s).UveState.Scalars[ud] = result; },
        };
    }

    // so.b.nc urs, imm — branch (PC += imm) while stream urs is not exhausted
    // The pipeline has already synced the exhaustion state into UveState via IUveScalars.
    private static ExecuteResult ExecuteUveSoBNc(IArchState state, ulong pc, int urs, int imm) {
        bool done = UState(state).UveState.StreamDone[urs];
        bool taken = !done;
        return taken
            ? new ExecuteResult { BranchTaken = true, BranchTarget = pc + (ulong)(long)imm, }
            : new ExecuteResult { BranchTaken = false, BranchTarget = pc + 4, };
    }

    // ss.sta.ld.w / ss.sta.st.w — start multi-dim stream configuration.
    // Creates a pending config with the first (innermost) dimension and stores in UveState.
    private static ExecuteResult ExecuteUveSsSta(IArchState state, IRegisterFile regs,
        int ud, int rs1, int rs2, int rs3, bool isLoad) {
        ulong baseAddr = regs.Read(rs1);
        long count = (long)regs.Read(rs2);
        long stride = (long)regs.Read(rs3);
        return new ExecuteResult {
            SideEffect = s => {
                UveState uvs = UState(s).UveState;
                var cfg = new PendingStreamConfig {
                    BaseAddress  = baseAddr,
                    ElementBytes = 4,
                    IsLoad       = isLoad,
                };
                cfg.Dimensions.Add(new StreamDimension(count, stride));
                uvs.PendingConfig[ud] = cfg;
            },
        };
    }

    // ss.app ud, _, rs2_count, rs3_stride — append next outer dimension to pending config.
    private static ExecuteResult ExecuteUveSsApp(IArchState state, IRegisterFile regs, int ud, int rs2, int rs3) {
        long count = (long)regs.Read(rs2);
        long stride = (long)regs.Read(rs3);
        return new ExecuteResult {
            SideEffect = s => {
                UState(s).UveState.PendingConfig[ud]?.Dimensions.Add(new StreamDimension(count, stride));
            },
        };
    }

    // ss.end ud, _, rs2_count, rs3_stride — outermost dimension + activate stream.
    // For load streams: returns StreamConfig so the pipeline can configure StreamingEngine.
    // For store streams: configures a flattened UveStoreStream (multi-dim store TBD).
    private static ExecuteResult ExecuteUveSsEnd(IArchState state, IRegisterFile regs, int ud, int rs2, int rs3) {
        long count = (long)regs.Read(rs2);
        long stride = (long)regs.Read(rs3);
        UveState uveState = UState(state).UveState;
        PendingStreamConfig? pending = uveState.PendingConfig[ud];
        if (pending is null) return ExecuteResult.Clean;

        var dims = pending.Dimensions.Append(new StreamDimension(count, stride)).ToArray();
        var descriptor = new StreamDescriptor(pending.BaseAddress, pending.ElementBytes, dims);
        bool isLoad = pending.IsLoad;

        if (isLoad) {
            return new ExecuteResult {
                StreamConfig = (ud, descriptor),
                SideEffect = s => {
                    UveState uvs = UState(s).UveState;
                    uvs.PendingConfig[ud] = null;
                    uvs.RegKind[ud] = UveRegKind.LoadStream;
                },
            };
        }

        // Store stream: full multi-dim cursor, innermost dimension first.
        return new ExecuteResult {
            SideEffect = s => {
                UveState uvs = UState(s).UveState;
                uvs.PendingConfig[ud] = null;
                var ss = new UveStoreStream {
                    BaseAddress = descriptor.BaseAddress, ElementBytes = descriptor.ElementBytes,
                    Dimensions = dims, Indices = new long[dims.Length],
                };
                ss.Initialize();
                uvs.StoreStreams[ud] = ss;
                uvs.RegKind[ud] = UveRegKind.StoreStream;
            },
        };
    }

    // ss.cfg.vec ud — flag pending stream as vector-mode (no-op until vector streaming).
    private static ExecuteResult ExecuteUveSsCfgVec(IArchState state, int ud) =>
        new ExecuteResult {
            SideEffect = s => {
                PendingStreamConfig? cfg = UState(s).UveState.PendingConfig[ud];
                if (cfg is not null) cfg.IsVector = true;
            },
        };

    // so.b.ndc.D urs, imm — branch while dimension D of stream urs has not completed its pass.
    // The pipeline has already synced IsDimPassComplete into UveState.DimDone before this call.
    private static ExecuteResult ExecuteUveSoBNdc(IArchState state, ulong pc, int urs, int dim, int imm) {
        bool done = UState(state).UveState.DimDone[urs, dim];
        bool taken = !done;
        return taken
            ? new ExecuteResult { BranchTaken = true, BranchTarget = pc + (ulong)(long)imm, }
            : new ExecuteResult { BranchTaken = false, BranchTarget = pc + 4, };
    }
}