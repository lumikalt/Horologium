using System.Numerics;
using Mechanism;
using Orrery.Cache;
using Orrery.Devices;
using RiscV32.Decode;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

// ReSharper disable CompareOfFloatsByEqualityOperator

namespace RiscV32.Execute;

/// <summary>
/// Executes a single decoded RV32I instruction.
/// Reads from IArchState, returns an ExecuteResult — never writes back directly.
/// </summary>
public class Rv32Executor : IExecutor {
    /// <summary>
    /// Address of the HTIF <c>tohost</c> register, if this workload uses HTIF.
    /// A 4-byte store of an odd value here is a tohost exit code: the executor
    /// flags it with <see cref="ExecuteResult.RequestHalt"/> so the engine
    /// terminates at the exit write. Null disables the check.
    /// </summary>
    public ulong? HtifTohostAddress { get; init; }

    /// <summary>CLINT for WFI fast-forward: skips mtime to mtimecmp so the timer fires in one tick.</summary>
    public ClintDevice? Clint { get; init; }

    /// <summary>
    /// Shared reservation table for multi-hart LR/SC.  When set, LR.W registers
    /// this hart's reservation in the table, and SC.W consults it; a write from
    /// any other hart to the same granule will cancel the reservation before SC
    /// even executes.  Null = single-hart mode (private <see cref="_reservation"/>
    /// field is used instead, preserving backward compatibility).
    /// </summary>
    public ReservationTable? ReservationTable { get; init; }

    /// <summary>
    /// Hart identifier used as the key in <see cref="ReservationTable"/>.
    /// Ignored when <see cref="ReservationTable"/> is null.
    /// </summary>
    public int HartId { get; init; }

    // Single-hart fallback: used when ReservationTable is null.
    private ulong? _reservation;

    public virtual ExecuteResult Execute(ITooth instruction, IArchState state, IMemory memory) {
        if (instruction.Payload is not RvOp op)
            throw new InvalidOperationException(
                $"Rv32Executor received an instruction with an unexpected payload type: " +
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

            // If a trap handler is installed (mtvec != 0), generate a real breakpoint exception
            // so OpenSBI's semihosting probe (and similar) can recover via their mtvec handler.
            // If mtvec == 0, halt — matches Spike's non-interactive behavior for bare-metal tests.
            RvEbreak => state.SystemRegisters is CsrFile ebreakCsrs && ebreakCsrs.DirectRead(CsrFile.Mtvec) != 0
                ? ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.Breakpoint, pc, pc))
                : new ExecuteResult { IsHalt = true, },

            RvMret => state.PrivilegeLevel == RvPrivilege.Machine
                ? new ExecuteResult { IsReturnFromTrap = true, ReturnPrivilege = RvPrivilege.Machine, }
                : ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc)),

            RvSret => state.PrivilegeLevel >= RvPrivilege.Supervisor
                ? new ExecuteResult { IsReturnFromTrap = true, ReturnPrivilege = RvPrivilege.Supervisor, }
                : ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc)),

            RvWfi => WfiResult(state),

            // Architecturally a NOP: ordering is a timing concern, enforced by the
            // pipeline via ITooth.IsStoreLoadFence (OoO write-buffer drain + load gate).
            RvFence     => ExecuteResult.Clean,
            RvFenceI    => ExecuteResult.Clean, // I-cache invalidation not modeled
            RvSfenceVma => ExecuteResult.Clean, // TLB flush — no-op in NOMMU simulation

            // ── Zawrs extension (single-core: NOP) ────────────────────────────
            RvWrsNto => ExecuteResult.Clean,
            RvWrsSto => ExecuteResult.Clean,

            // ── Zicbom / Zicboz extensions ────────────────────────────────────
            RvCboInval(var rs1) => CboMaintain(memory, state, pc, regs.Read(rs1), static (m, a) => m.InvalidateLine(a)),
            RvCboClean(var rs1) => CboMaintain(memory, state, pc, regs.Read(rs1), static (m, a) => m.CleanLine(a)),
            RvCboFlush(var rs1) => CboMaintain(memory, state, pc, regs.Read(rs1), static (m, a) => m.FlushLine(a)),
            RvCboZero(var rs1)  => CboZero(memory, state, pc, regs.Read(rs1)),

            // ── Zimop extension (always return 0) ─────────────────────────────
            RvMopR _  => Reg(0),
            RvMopRr _ => Reg(0),

            // ── Zcmop extension (compressed NOPs, no effect) ──────────────────
            RvCMopN _ => ExecuteResult.Clean,

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

            // ── Zbc extension (carry-less multiplication) ─────────────────────────────
            RvClmul (_, var rs1, var rs2) => Reg(Clmul((uint)regs.Read(rs1), (uint)regs.Read(rs2))),
            RvClmulh(_, var rs1, var rs2) => Reg(Clmul((uint)regs.Read(rs1), (uint)regs.Read(rs2)) >> 32),
            RvClmulr(_, var rs1, var rs2) => Reg(Clmul((uint)regs.Read(rs1), (uint)regs.Read(rs2)) >> 31),

            // ── Zba extension (address generation) ───────────────────────────────────
            RvSh1Add(_, var rs1, var rs2) => Reg(regs.Read(rs2) + (regs.Read(rs1) << 1)),
            RvSh2Add(_, var rs1, var rs2) => Reg(regs.Read(rs2) + (regs.Read(rs1) << 2)),
            RvSh3Add(_, var rs1, var rs2) => Reg(regs.Read(rs2) + (regs.Read(rs1) << 3)),

            // ── Zbs extension (single-bit ops) ────────────────────────────────────────
            RvBclr(_, var rs1, var rs2) => Reg((uint)regs.Read(rs1) & ~(1u << (int)(regs.Read(rs2) & 31))),
            RvBext(_, var rs1, var rs2) => Reg(((uint)regs.Read(rs1) >> (int)(regs.Read(rs2) & 31)) & 1),
            RvBinv(_, var rs1, var rs2) => Reg((uint)regs.Read(rs1) ^ (1u << (int)(regs.Read(rs2) & 31))),
            RvBset(_, var rs1, var rs2) => Reg((uint)regs.Read(rs1) | (1u << (int)(regs.Read(rs2) & 31))),
            RvBclri(_, var rs1, var sh) => Reg((uint)regs.Read(rs1) & ~(1u << sh)),
            RvBexti(_, var rs1, var sh) => Reg(((uint)regs.Read(rs1) >> sh) & 1),
            RvBinvi(_, var rs1, var sh) => Reg((uint)regs.Read(rs1) ^ (1u << sh)),
            RvBseti(_, var rs1, var sh) => Reg((uint)regs.Read(rs1) | (1u << sh)),

            // ── Zicond extension (integer conditional ops) ────────────────────────────
            RvCzeroEqz(_, var rs1, var rs2) => Reg(regs.Read(rs2) == 0 ? 0UL : regs.Read(rs1)),
            RvCzeroNez(_, var rs1, var rs2) => Reg(regs.Read(rs2) != 0 ? 0UL : regs.Read(rs1)),

            // ── Zbb extension (basic bit manipulation) ────────────────────────────────
            RvAndn(_, var rs1, var rs2) => Reg((uint)regs.Read(rs1) & ~(uint)regs.Read(rs2)),
            RvOrn (_, var rs1, var rs2) => Reg((uint)regs.Read(rs1) | ~(uint)regs.Read(rs2)),
            RvXnor(_, var rs1, var rs2) => Reg(~((uint)regs.Read(rs1) ^ (uint)regs.Read(rs2))),
            RvMin (_, var rs1, var rs2) => Reg(
                (int)regs.Read(rs1) < (int)regs.Read(rs2) ? regs.Read(rs1) : regs.Read(rs2)
            ),
            RvMinu(_, var rs1, var rs2) => Reg(regs.Read(rs1) < regs.Read(rs2) ? regs.Read(rs1) : regs.Read(rs2)),
            RvMax (_, var rs1, var rs2) => Reg(
                (int)regs.Read(rs1) > (int)regs.Read(rs2) ? regs.Read(rs1) : regs.Read(rs2)
            ),
            RvMaxu(_, var rs1, var rs2) => Reg(regs.Read(rs1) > regs.Read(rs2) ? regs.Read(rs1) : regs.Read(rs2)),
            RvRol (_, var rs1, var rs2) => Reg(
                BitOperations.RotateLeft((uint)regs.Read(rs1), (int)(regs.Read(rs2) & 31))
            ),
            RvRor (_, var rs1, var rs2) => Reg(
                BitOperations.RotateRight((uint)regs.Read(rs1), (int)(regs.Read(rs2) & 31))
            ),
            RvZextH(_, var rs1)         => Reg((uint)regs.Read(rs1) & 0xFFFF),
            RvClz (_, var rs1)          => Reg((ulong)BitOperations.LeadingZeroCount((uint)regs.Read(rs1))),
            RvCtz (_, var rs1)          => Reg((ulong)BitOperations.TrailingZeroCount((uint)regs.Read(rs1))),
            RvCpop (_, var rs1)         => Reg((ulong)BitOperations.PopCount((uint)regs.Read(rs1))),
            RvSextB(_, var rs1)         => Reg((ulong)(sbyte)regs.Read(rs1)),
            RvSextH(_, var rs1)         => Reg((ulong)(short)regs.Read(rs1)),
            RvRori (_, var rs1, var sh) => Reg(BitOperations.RotateRight((uint)regs.Read(rs1), sh)),
            RvOrcB (_, var rs1)         => OrcB(regs, rs1),
            RvRev8 (_, var rs1)         => Rev8(regs, rs1),

            // ── An extension ──────────────────────────────────────────────────────────
            RvLrW(_, var rs1) => AmoLr(memory, state, pc, regs, rs1),

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

            // ── Zacas extension ───────────────────────────────────────────────
            RvAmocasW(var rd, var rs1, var rs2) => AmoCasW(memory, state, pc, regs, rd, rs1, rs2),

            // ── Zabha extension (byte and halfword AMOs) ──────────────────────
            RvAmoswapB(_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 1, (_, v) => v),
            RvAmoaddB (_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 1, (a, v) => a + v),
            RvAmoxorB (_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 1, (a, v) => a ^ v),
            RvAmoandB (_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 1, (a, v) => a & v),
            RvAmoorB (_, var rs1, var rs2)  => AmoNarrow(memory, state, pc, regs, rs1, rs2, 1, (a, v) => a | v),
            RvAmominB (_, var rs1, var rs2) => AmoNarrow(
                memory, state, pc, regs, rs1, rs2, 1,
                (a, v) => (uint)Math.Min((sbyte)(byte)a, (int)(sbyte)(byte)v)
            ),
            RvAmomaxB (_, var rs1, var rs2) => AmoNarrow(
                memory, state, pc, regs, rs1, rs2, 1,
                (a, v) => (uint)Math.Max((sbyte)(byte)a, (int)(sbyte)(byte)v)
            ),
            RvAmominuB(_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 1, Math.Min),
            RvAmomaxuB(_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 1, Math.Max),

            RvAmoswapH(_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 2, (_, v) => v),
            RvAmoaddH (_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 2, (a, v) => a + v),
            RvAmoxorH (_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 2, (a, v) => a ^ v),
            RvAmoandH (_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 2, (a, v) => a & v),
            RvAmoorH (_, var rs1, var rs2)  => AmoNarrow(memory, state, pc, regs, rs1, rs2, 2, (a, v) => a | v),
            RvAmominH (_, var rs1, var rs2) => AmoNarrow(
                memory, state, pc, regs, rs1, rs2, 2,
                (a, v) => (uint)Math.Min((short)(ushort)a, (int)(short)(ushort)v)
            ),
            RvAmomaxH (_, var rs1, var rs2) => AmoNarrow(
                memory, state, pc, regs, rs1, rs2, 2,
                (a, v) => (uint)Math.Max((short)(ushort)a, (int)(short)(ushort)v)
            ),
            RvAmominuH(_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 2, Math.Min),
            RvAmomaxuH(_, var rs1, var rs2) => AmoNarrow(memory, state, pc, regs, rs1, rs2, 2, Math.Max),

            // ── F extension ───────────────────────────────────────────────────
            // FLW: load 32-bit float, NaN-box it before writing to fp register.
            RvFlw(_, var rs1, var imm) => NanBoxF(Load(memory, state, pc, regs.Read(rs1), imm, 4, false, 32)),

            // FSW: rs1 = int base address, rs2 = fp data register (unified index).
            RvFsw(var rs1, var rs2, var imm) =>
                Store(memory, state, pc, regs.Read(rs1), imm, regs.Read(rs2), 4),

            RvFaddS (_, var rs1, var rs2) => FpBin(FBits(regs, rs1), FBits(regs, rs2), 0),
            RvFsubS (_, var rs1, var rs2) => FpBin(FBits(regs, rs1), FBits(regs, rs2), 1),
            RvFmulS (_, var rs1, var rs2) => FpBin(FBits(regs, rs1), FBits(regs, rs2), 2),
            RvFdivS (_, var rs1, var rs2) => FpBin(FBits(regs, rs1), FBits(regs, rs2), 3),
            RvFsqrtS(_, var rs1)          => FpSqrt(FBits(regs, rs1)),

            RvFsgnjS (_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    0xFFFFFFFF00000000UL |
                    (((uint)regs.Read(rs1) & 0x7FFFFFFFu) | ((uint)regs.Read(rs2) & 0x80000000u))
                ),
            RvFsgnjnS(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    0xFFFFFFFF00000000UL |
                    (((uint)regs.Read(rs1) & 0x7FFFFFFFu) | (~(uint)regs.Read(rs2) & 0x80000000u))
                ),
            RvFsgnjxS(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    0xFFFFFFFF00000000UL |
                    (((uint)regs.Read(rs1) & 0x7FFFFFFFu) |
                     (((uint)regs.Read(rs1) ^ (uint)regs.Read(rs2)) & 0x80000000u))
                ),

            RvFminS(_, var rs1, var rs2) => FpMinMax(regs, rs1, rs2, true),
            RvFmaxS(_, var rs1, var rs2) => FpMinMax(regs, rs1, rs2, false),

            // IEEE 754: NaN comparisons return false; sNaN inputs set NV flag
            RvFeqS(_, var rs1, var rs2) => FpCmp(regs, rs1, rs2, 0),
            RvFltS(_, var rs1, var rs2) => FpCmp(regs, rs1, rs2, 1),
            RvFleS(_, var rs1, var rs2) => FpCmp(regs, rs1, rs2, 2),

            RvFclassS(_, var rs1) => Reg(FClass((uint)regs.Read(rs1))),

            RvFcvtWs (_, var rs1, var rm) => FcvtWsResult(FBits(regs, rs1), rm, state),
            RvFcvtWuS(_, var rs1, var rm) => FcvtWuSResult(FBits(regs, rs1), rm, state),
            RvFcvtSw (_, var rs1, _)      => FpIntToFloat((int)(uint)regs.Read(rs1)),
            RvFcvtSWu(_, var rs1, _)      => FpIntToFloat((uint)regs.Read(rs1)),

            RvFmvXw(_, var rs1) => Reg(regs.Read(rs1) & 0xFFFFFFFF), // lower 32 fp bits → int reg
            RvFmvWx(_, var rs1) => ExecuteResult.WithResult(0xFFFFFFFF00000000UL | (regs.Read(rs1) & 0xFFFFFFFF)),

            RvFmaddS (_, var rs1, var rs2, var rs3) =>
                FpFma(FBits(regs, rs1), FBits(regs, rs2), FBits(regs, rs3)),
            RvFmsubS (_, var rs1, var rs2, var rs3) =>
                FpFma(FBits(regs, rs1), FBits(regs, rs2), -FBits(regs, rs3)),
            RvFnmsubS(_, var rs1, var rs2, var rs3) =>
                FpFma(-FBits(regs, rs1), FBits(regs, rs2), FBits(regs, rs3)),
            RvFnmaddS(_, var rs1, var rs2, var rs3) =>
                FpFma(-FBits(regs, rs1), FBits(regs, rs2), -FBits(regs, rs3)),

            // ── D extension ───────────────────────────────────────────────────
            RvFld(_, var rs1, var imm) => LoadD(memory, state, pc, regs.Read(rs1), imm),
            RvFsd(var rs1, var rs2, var imm) =>
                Store(memory, state, pc, regs.Read(rs1), imm, regs.Read(rs2), 8),

            RvFaddD (_, var rs1, var rs2) => DpBin(DBits(regs, rs1), DBits(regs, rs2), 0),
            RvFsubD (_, var rs1, var rs2) => DpBin(DBits(regs, rs1), DBits(regs, rs2), 1),
            RvFmulD (_, var rs1, var rs2) => DpBin(DBits(regs, rs1), DBits(regs, rs2), 2),
            RvFdivD (_, var rs1, var rs2) => DpBin(DBits(regs, rs1), DBits(regs, rs2), 3),
            RvFsqrtD(_, var rs1)          => DpSqrt(DBits(regs, rs1)),

            RvFsgnjD (_, var rs1, var rs2) =>
                FloatRegD(
                    BitConverter.Int64BitsToDouble(
                        (long)(((ulong)BitConverter.DoubleToInt64Bits(DBits(regs, rs1)) & 0x7FFFFFFFFFFFFFFFUL) |
                               ((ulong)BitConverter.DoubleToInt64Bits(DBits(regs, rs2)) & 0x8000000000000000UL))
                    ), 0
                ),
            RvFsgnjnD(_, var rs1, var rs2) =>
                FloatRegD(
                    BitConverter.Int64BitsToDouble(
                        (long)(((ulong)BitConverter.DoubleToInt64Bits(DBits(regs, rs1)) & 0x7FFFFFFFFFFFFFFFUL) |
                               (~(ulong)BitConverter.DoubleToInt64Bits(DBits(regs, rs2)) & 0x8000000000000000UL))
                    ), 0
                ),
            RvFsgnjxD(_, var rs1, var rs2) =>
                FloatRegD(
                    BitConverter.Int64BitsToDouble(
                        (long)(((ulong)BitConverter.DoubleToInt64Bits(DBits(regs, rs1)) & 0x7FFFFFFFFFFFFFFFUL) |
                               (((ulong)BitConverter.DoubleToInt64Bits(DBits(regs, rs1)) ^
                                 (ulong)BitConverter.DoubleToInt64Bits(DBits(regs, rs2))) & 0x8000000000000000UL))
                    ), 0
                ),

            RvFminD(_, var rs1, var rs2) => DpMinMax(regs, rs1, rs2, true),
            RvFmaxD(_, var rs1, var rs2) => DpMinMax(regs, rs1, rs2, false),

            RvFeqD(_, var rs1, var rs2) => DpCmp(regs, rs1, rs2, 0),
            RvFltD(_, var rs1, var rs2) => DpCmp(regs, rs1, rs2, 1),
            RvFleD(_, var rs1, var rs2) => DpCmp(regs, rs1, rs2, 2),

            RvFclassD(_, var rs1) => Reg(DClass(regs.Read(rs1))),

            RvFcvtWd (_, var rs1, var rm) => FcvtWdResult(DBits(regs, rs1), rm, state),
            RvFcvtWuD(_, var rs1, var rm) => FcvtWudResult(DBits(regs, rs1), rm, state),
            RvFcvtDw (_, var rs1, _)      => DpIntToDouble((int)(uint)regs.Read(rs1)),
            RvFcvtDWu(_, var rs1, _)      => DpIntToDouble((uint)regs.Read(rs1)),

            RvFcvtSd(_, var rs1, var rm) => FpFcvtSd(DBits(regs, rs1), rm, state),
            RvFcvtDs(_, var rs1, _)      => FpFcvtDs(FBits(regs, rs1)),

            RvFmaddD (_, var rs1, var rs2, var rs3) =>
                DpFma(DBits(regs, rs1), DBits(regs, rs2), DBits(regs, rs3)),
            RvFmsubD (_, var rs1, var rs2, var rs3) =>
                DpFma(DBits(regs, rs1), DBits(regs, rs2), -DBits(regs, rs3)),
            RvFnmsubD(_, var rs1, var rs2, var rs3) =>
                DpFma(-DBits(regs, rs1), DBits(regs, rs2), DBits(regs, rs3)),
            RvFnmaddD(_, var rs1, var rs2, var rs3) =>
                DpFma(-DBits(regs, rs1), DBits(regs, rs2), -DBits(regs, rs3)),

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
            RvVlsegVv (var numFields, var vd, var rs1, var sew, var masked) =>
                ExecuteVlseg(state, memory, numFields, vd, rs1, sew, masked),
            RvVlseVv (var vd, var rs1, var rs2, var sew, var masked) =>
                ExecuteVlse(state, memory, vd, rs1, rs2, sew, masked),
            RvVlxeiVv (var vd, var rs1, var vs2, var idxSew, var masked, _) =>
                ExecuteVlxei(state, memory, vd, rs1, vs2, idxSew, masked),
            RvVseVv (var vs3, var rs1, var sew, var masked) =>
                ExecuteVse(state, memory, vs3, rs1, sew, masked),
            RvVsm (var vs3, var rs1) =>
                ExecuteVsm(state, memory, vs3, rs1),
            RvVssegVv (var numFields, var vs3, var rs1, var sew, var masked) =>
                ExecuteVsseg(state, memory, numFields, vs3, rs1, sew, masked),
            RvVlrV (var numRegs, var vd, var rs1) =>
                ExecuteVlr(state, memory, numRegs, vd, rs1),
            RvVsrV (var numRegs, var vs3, var rs1) =>
                ExecuteVsr(state, memory, numRegs, vs3, rs1),
            RvVsseVv (var vs3, var rs1, var rs2, var sew, var masked) =>
                ExecuteVsse(state, memory, vs3, rs1, rs2, sew, masked),
            RvVsxeiVv (var vs3, var rs1, var vs2, var idxSew, var masked, _) =>
                ExecuteVsxei(state, memory, vs3, rs1, vs2, idxSew, masked),

            RvVWideVv (var op2, var vd, var vs2, var vs1, var masked, var vs2Wide) =>
                ExecuteVWide(state, op2, vd, vs2, masked, vs2Wide, (i, ew) => VReadElem(state, vs1, i, ew)),
            RvVWideVx (var op2, var vd, var vs2, var rs1, var masked, var vs2Wide) =>
                ExecuteVWide(state, op2, vd, vs2, masked, vs2Wide, (_, _) => regs.Read(rs1)),
            RvVwMacVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVwMac(state, op2, vd, vs2, masked, (i, ew) => VReadElem(state, vs1, i, ew)),
            RvVwMacVx (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVwMac(state, op2, vd, vs2, masked, (_, _) => regs.Read(rs1)),

            RvVNarrVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVNarr(state, op2, vd, vs2, masked, (i, ew) => VReadElem(state, vs1, i, ew)),
            RvVNarrVx (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVNarr(state, op2, vd, vs2, masked, (_, _) => regs.Read(rs1)),
            RvVNarrVi (var op2, var vd, var vs2, var imm, var masked) =>
                ExecuteVNarr(state, op2, vd, vs2, masked, (_, _) => (uint)imm),

            RvVSlideVx (var dir, var is1, var vd, var vs2, var rs1, var masked) =>
                ExecuteVSlide(state, dir, is1, vd, vs2, masked, regs.Read(rs1)),
            RvVFpSlide1Vf (var dir, var vd, var vs2, var fpRs1, var masked) =>
                ExecuteVSlide(state, dir, true, vd, vs2, masked, regs.Read(fpRs1)),
            RvVSlideVi (var dir, var vd, var vs2, var imm, var masked) =>
                ExecuteVSlide(state, dir, false, vd, vs2, masked, (uint)imm),

            RvVRgatherVv (var vd, var vs2, var vs1, var masked) =>
                ExecuteVRgather(state, vd, vs2, masked, (i, ew) => VReadElem(state, vs1, i, ew)),
            RvVRgatherEi16Vv (var vd, var vs2, var vs1, var masked) =>
                ExecuteVRgatherEi16(state, vd, vs2, vs1, masked),
            RvVRgatherVx (var vd, var vs2, var rs1, var masked) =>
                ExecuteVRgather(state, vd, vs2, masked, (_, _) => regs.Read(rs1)),
            RvVRgatherVi (var vd, var vs2, var imm, var masked) =>
                ExecuteVRgather(state, vd, vs2, masked, (_, _) => (uint)imm),

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

            RvVMvXs (var rd, var vs2) => ExecuteVMvXs(state, rd, vs2),
            RvVMvSx (var vd, var rs1) => ExecuteVMvSx(state, vd, regs.Read(rs1)),

            RvVIntMacVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVIntMac(state, op2, vd, vs2, masked, (i, ew) => VReadElem(state, vs1, i, ew)),
            RvVIntMacVx (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVIntMac(state, op2, vd, vs2, masked, (_, _) => regs.Read(rs1)),

            RvVMergeVv (var vd, var vs2, var vs1) =>
                ExecuteVMerge(state, vd, vs2, (i, ew) => VReadElem(state, vs1, i, ew)),
            RvVMergeVx (var vd, var vs2, var rs1) =>
                ExecuteVMerge(state, vd, vs2, (_, _) => regs.Read(rs1)),
            RvVMergeVi (var vd, var vs2, var imm) =>
                ExecuteVMerge(state, vd, vs2, (_, _) => (ulong)imm),
            RvVFpMergeVf (var vd, var vs2, var fpRs1) =>
                ExecuteVMerge(state, vd, vs2, (_, _) => regs.Read(fpRs1)),

            RvVMaskLogMm (var op2, var vd, var vs2, var vs1) =>
                ExecuteVMaskLog(state, op2, vd, vs2, vs1),
            RvVcpop (_, var vs2, var masked) =>
                ExecuteVcpop(state, vs2, masked),
            RvVfirst (_, var vs2, var masked) =>
                ExecuteVfirst(state, vs2, masked),
            RvVMaskUnary (var op2, var vd, var vs2, var masked) =>
                ExecuteVMaskUnary(state, op2, vd, vs2, masked),
            RvVCompress (var vd, var vs2, var vs1) =>
                ExecuteVCompress(state, vd, vs2, vs1),
            RvVMvNr (var n, var vd, var vs2) =>
                ExecuteVMvNr(state, n, vd, vs2),

            RvVMulVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVMul(
                    state, op2, vd, vs2, masked,
                    (i, ew) => VReadElem(state, vs1, i, ew)
                ),
            RvVMulVx (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVMul(
                    state, op2, vd, vs2, masked,
                    (_, _) => regs.Read(rs1)
                ),

            RvVRedVs (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVRed(state, op2, vd, vs2, vs1, masked),
            RvVWideRedVs (var signed, var vd, var vs2, var vs1, var masked) =>
                ExecuteVWideRed(state, signed, vd, vs2, vs1, masked),

            // ── FP vector ops (V 1.0 OPFVV/OPFVF) ────────────────────────────
            RvVFpBinVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVFpBin(state, op2, vd, vs2, masked, i => VFpElem(state, vs1, i)),
            RvVFpBinVf (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVFpBin(state, op2, vd, vs2, masked, _ => FBits(regs, rs1)),

            RvVFpFmaVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVFpFma(state, op2, vd, vs2, masked, i => VFpElem(state, vs1, i)),
            RvVFpFmaVf (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVFpFma(state, op2, vd, vs2, masked, _ => FBits(regs, rs1)),

            RvVmFpCmpVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVFpCmp(state, op2, vd, vs2, masked, i => VFpElem(state, vs1, i)),
            RvVmFpCmpVf (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVFpCmp(state, op2, vd, vs2, masked, _ => FBits(regs, rs1)),

            RvVFpSqrt (var vd, var vs2, var masked) =>
                ExecuteVFpUnary(state, vd, vs2, masked, a => (float)Math.Sqrt(a)),
            RvVFpClass (var vd, var vs2, var masked) =>
                ExecuteVFpClassOp(state, vd, vs2, masked),
            RvVFpCvt (var op2, var vd, var vs2, var masked) =>
                ExecuteVFpCvt(state, op2, vd, vs2, masked),

            RvVFpMvFs (_, var vs2)      => ExecuteVFpMvFs(state, vs2),
            RvVFpMvSf (var vd, var rs1) => ExecuteVFpMvSf(state, regs, vd, rs1),
            RvVFpMvVf (var vd, var rs1, var masked) =>
                ExecuteVFpMvVf(state, vd, FBits(regs, rs1), masked),
            RvVFpRedVs (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVFpRed(state, op2, vd, vs2, vs1, masked),

            RvVSatIntVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVSatInt(state, op2, vd, vs2, masked, (i, ew) => VReadElem(state, vs1, i, ew)),
            RvVSatIntVx (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVSatInt(state, op2, vd, vs2, masked, (_, _) => regs.Read(rs1)),
            RvVSatIntVi (var op2, var vd, var vs2, var imm, var masked) =>
                ExecuteVSatInt(state, op2, vd, vs2, masked, (_, _) => (ulong)imm),

            RvVnClipVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVnClip(state, op2, vd, vs2, masked, (i, ew) => VReadElem(state, vs1, i, ew)),
            RvVnClipVx (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVnClip(state, op2, vd, vs2, masked, (_, _) => regs.Read(rs1)),
            RvVnClipVi (var op2, var vd, var vs2, var imm, var masked) =>
                ExecuteVnClip(state, op2, vd, vs2, masked, (_, _) => (uint)imm),

            // ── V widening FP arithmetic / MAC / converts ──────────────────────
            RvVFpWArithVv (var op2, var vd, var vs2, var vs1, var vs2Wide, var masked) =>
                ExecuteVFpWArith(state, op2, vd, vs2, vs2Wide, masked, i => VFpElem(state, vs1, i)),
            RvVFpWArithVf (var op2, var vd, var vs2, var rs1, var vs2Wide, var masked) =>
                ExecuteVFpWArith(state, op2, vd, vs2, vs2Wide, masked, _ => FBits(regs, rs1)),
            RvVFpWMacVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVFpWMac(state, op2, vd, vs2, masked, i => VFpElem(state, vs1, i)),
            RvVFpWMacVf (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVFpWMac(state, op2, vd, vs2, masked, _ => FBits(regs, rs1)),
            RvVFpWCvt (var op2, var vd, var vs2, var masked) =>
                ExecuteVFpWCvt(state, op2, vd, vs2, masked),
            RvVFpNCvt (var op2, var vd, var vs2, var masked) =>
                ExecuteVFpNCvt(state, op2, vd, vs2, masked),

            // ── V extension new ops ───────────────────────────────────────────
            RvVleFf (var vd, var rs1, var sew, var masked) =>
                ExecuteVle(state, memory, vd, rs1, sew, masked),
            RvVExt (var signed, var factor, var vd, var vs2, var masked) =>
                ExecuteVExt(state, signed, factor, vd, vs2, masked),
            RvVAvgVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVAvg(state, op2, vd, vs2, masked, (i, ew) => VReadElem(state, vs1, i, ew)),
            RvVAvgVx (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVAvg(state, op2, vd, vs2, masked, (_, _) => regs.Read(rs1)),
            RvVFpWideRedVs (_, var vd, var vs2, var vs1, var masked) =>
                ExecuteVFpWideRed(state, vd, vs2, vs1, masked),
            RvVlssegVv (var numFields, var vd, var rs1, var rs2, var sew, var masked) =>
                ExecuteVlsseg(state, memory, numFields, vd, rs1, rs2, sew, masked),
            RvVsssegVv (var numFields, var vs3, var rs1, var rs2, var sew, var masked) =>
                ExecuteVssseg(state, memory, numFields, vs3, rs1, rs2, sew, masked),
            RvVlxsegVv (var numFields, var vd, var rs1, var vs2, var idxSew, var masked, _) =>
                ExecuteVlxseg(state, memory, numFields, vd, rs1, vs2, idxSew, masked),
            RvVsxsegVv (var numFields, var vs3, var rs1, var vs2, var idxSew, var masked, _) =>
                ExecuteVsxseg(state, memory, numFields, vs3, rs1, vs2, idxSew, masked),

            // ── UVE extension ─────────────────────────────────────────────────
            RvUveSsStaLdW (var ud, var rs1, var ew, var isVec, var vecDim, var pm, _) => ExecuteUveSsSta(
                regs, ud, rs1, true, ew, isVec, vecDim, pm
            ),
            RvUveSsStaStW (var ud, var rs1, var ew, var isVec, var vecDim, var pm, _) => ExecuteUveSsSta(
                regs, ud, rs1, false, ew, isVec, vecDim, pm
            ),
            RvUveSsStaLdWInds (var ud, var rs1, var ew, _) => ExecuteUveSsStaLdWInds(regs, ud, rs1, ew),
            RvUveSsApp (var ud, var rs1, var rs2, var rs3) => ExecuteUveSsApp(regs, ud, rs1, rs2, rs3),
            RvUveSsAppInd (var ud, var tdim, var target, var behavior, var srcId) =>
                ExecuteUveSsAppInd(ud, tdim, target, behavior, srcId),
            RvUveSsAppSgi (var ud, var srcId, var behavior) => ExecuteUveSsAppSgi(ud, srcId, behavior),
            RvUveSsEndSgi (var ud, var srcId, var behavior) => ExecuteUveSsEndSgi(state, ud, srcId, behavior),
            RvUveSsEnd (var ud, var rs1, var rs2, var rs3)  => ExecuteUveSsEnd(state, regs, ud, rs1, rs2, rs3),
            RvUveSsAppMod (var ud, var tdim, var target, var behavior, var rs3Disp) =>
                ExecuteUveSsAppMod(regs, ud, tdim, target, behavior, rs3Disp),
            RvUveSoVDp (var ud, var rs1, var elemBytes)   => ExecuteUveSoVDp(regs, ud, rs1, elemBytes),
            RvUveSoVMvvs (var us1, var rd)                => ExecuteUveSoVMvvs(state, rd, us1),
            RvUveSoVMvsv (var ud, var rs1, var elemBytes) => ExecuteUveSoVMvsv(regs, ud, rs1, elemBytes),
            RvUveSoAFp (var fpOp, var ud, var usrc1, var usrc2, var ps3) => ExecuteUveSoAFp(
                state, memory, fpOp, ud, usrc1, usrc2, ps3
            ),
            RvUveSoAInt (var intOp, var signed, var ud, var usrc1, var usrc2, var ps3) => ExecuteUveSoAInt(
                state, memory, intOp, signed, ud, usrc1, usrc2, ps3
            ),
            RvUveSoALogic (var logicOp, var ud, var usrc1, var usrc2, var ps3) => ExecuteUveSoALogic(
                state, memory, logicOp, ud, usrc1, usrc2, ps3
            ),
            RvUveSoAShiftV (var shiftOp, var ud, var usrc1, var usrc2, var ps3) => ExecuteUveSoAShiftV(
                state, memory, shiftOp, ud, usrc1, usrc2, ps3
            ),
            RvUveSoAShiftS (var shiftOp, var ud, var usrc1, var rs2, var ps3) => ExecuteUveSoAShiftS(
                state, memory, regs, shiftOp, ud, usrc1, rs2, ps3
            ),
            RvUveSoASadde (var isFp, var acc, var rd, var usrc1, var ps3) => ExecuteUveSoASadde(
                state, regs, isFp, acc, rd, usrc1, ps3
            ),
            RvUveSoCBreak (var ud)                  => ExecuteUveSoCBreak(ud),
            RvUveSoCSuspd (var ud)                  => ExecuteUveSoCSuspd(ud),
            RvUveSoCResum (var ud)                  => ExecuteUveSoCResum(ud),
            RvUveSoCGetvl (var rd)                  => ExecuteUveSoCGetvl(state, rd),
            RvUveSoCSetvl (var rd, var rs1)         => ExecuteUveSoCSetvl(state, regs, rd, rs1),
            RvUveSoBNc (var urs, var imm)           => ExecuteUveSoBNc(state, pc, urs, imm),
            RvUveSoBNdc (var urs, var dim, var imm) => ExecuteUveSoBNdc(state, pc, urs, dim, imm),
            RvUveSoBc (var urs, var imm)            => ExecuteUveSoBc(state, pc, urs, imm),
            RvUveSoBdc (var urs, var dim, var imm)  => ExecuteUveSoBdc(state, pc, urs, dim, imm),
            RvUveSoPSimple (var sop, var pd, var govPred, var zeroing, var ps1, _)
                => ExecuteUveSoPSimple(state, sop, pd, govPred, zeroing, ps1),
            RvUveSoPCmp (var cop, var cmpType, var pd, var govPred, var vs1, var vs2, var cmpZeroing)
                => ExecuteUveSoPCmp(state, cop, cmpType, pd, govPred, vs1, vs2, cmpZeroing),
            RvUveSoVMv (var transpose, var vd, var vs1, var predIdx)
                => ExecuteUveSoVMv(state, transpose, vd, vs1, predIdx),
            RvUveSoPCv (var pd, var ps1, var srcBytes, var destBytes, var zeroing)
                => ExecuteUveSoPCv(state, pd, ps1, srcBytes, destBytes, zeroing),
            RvUveSoVCv (var vd, var vs1, var destBytes, var isFp, var isSigned)
                => ExecuteUveSoVCv(state, vd, vs1, destBytes, isFp, isSigned),

            _ => throw new InvalidOperationException(
                $"Unhandled RvOp: {op.GetType().Name}"
            ),
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Carry-less multiply: XOR-sum of (a << i) for each set bit i in b.
    // Returns the full 63-bit product as ulong; callers slice the desired half.
    private static ulong Clmul(uint a, uint b) {
        ulong result = 0;
        for (var i = 0; i < 32; i++)
            if (((b >> i) & 1u) != 0)
                result ^= (ulong)a << i;
        return result;
    }

    // orc.b: per-byte OR-combine — nonzero byte → 0xFF, zero byte → 0x00.
    private ExecuteResult OrcB(IRegisterFile regs, int rs1) {
        var v = (uint)regs.Read(rs1);
        uint r = ((v & 0x000000FFu) != 0 ? 0x000000FFu : 0u)
               | ((v & 0x0000FF00u) != 0 ? 0x0000FF00u : 0u)
               | ((v & 0x00FF0000u) != 0 ? 0x00FF0000u : 0u)
               | ((v & 0xFF000000u) != 0 ? 0xFF000000u : 0u);
        return Reg(r);
    }

    // rev8: reverse byte order of a 32-bit word.
    private ExecuteResult Rev8(IRegisterFile regs, int rs1) {
        var v = (uint)regs.Read(rs1);
        return Reg((v >> 24) | ((v >> 8) & 0xFF00u) | ((v << 8) & 0xFF0000u) | (v << 24));
    }

    private static int EcallCause(PrivilegeLevel priv) => (int)priv switch {
        0 => RvTrapCause.EnvironmentCallFromU,
        1 => RvTrapCause.EnvironmentCallFromS,
        _ => RvTrapCause.EnvironmentCallFromM,
    };

    private ExecuteResult WfiResult(IArchState state) {
        if (state.SystemRegisters is CsrFile csrs) {
            if (state.PrivilegeLevel == RvPrivilege.Machine) {
                // M-mode WFI: fast-forward CLINT to the next timer event, so the timer fires
                // in one tick instead of burning millions of ticks in the idle loop.
                uint mip = csrs.DirectRead(CsrFile.Mip) & csrs.DirectRead(CsrFile.Mie);
                if (mip != 0) return ExecuteResult.Clean; // interrupt already pending
                Clint?.SkipToTimer();
                return Clint?.TimerPending() == true
                    ? ExecuteResult.Clean // timer now set; PeekInterrupt will catch it
                    : new ExecuteResult { IsHalt = true, };
            }

            // S/U-mode WFI: halt unless an S-mode interrupt is already pending.
            uint sip = csrs.DirectRead(CsrFile.Sip) & csrs.DirectRead(CsrFile.Sie);
            if (sip != 0) return ExecuteResult.Clean;
        }

        return new ExecuteResult { IsHalt = true, };
    }

    protected virtual ExecuteResult Reg(ulong value) =>
        ExecuteResult.WithResult(value & 0xFFFFFFFF); // truncate to 32 bits

    private ExecuteResult DivSigned(IRegisterFile regs, int rs1, int rs2) {
        var a = (int)regs.Read(rs1);
        var b = (int)regs.Read(rs2);
        if (b == 0) return Reg(0xFFFF_FFFF);                                         // div-by-zero → -1
        if (a == int.MinValue && b == -1) return Reg(unchecked((uint)int.MinValue)); // overflow
        return Reg(unchecked((uint)(a / b)));
    }

    private ExecuteResult RemSigned(IRegisterFile regs, int rs1, int rs2) {
        var a = (int)regs.Read(rs1);
        var b = (int)regs.Read(rs2);
        if (b == 0) return Reg(regs.Read(rs1));          // div-by-zero → rs1
        if (a == int.MinValue && b == -1) return Reg(0); // overflow → 0
        return Reg(unchecked((uint)(a % b)));
    }

    // Atomic read-modify-write. Returns original value; combines with rs2 and stores.
    private ExecuteResult Amo(
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

    private ExecuteResult AmoLr(
        IMemory memory,
        IArchState state,
        ulong pc,
        IRegisterFile regs,
        int rs1
    ) {
        ulong vaddr = regs.Read(rs1);
        (ulong paddr, int fault) = Translate(memory, state, vaddr, false, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        if (ReservationTable is not null)
            ReservationTable.Set(HartId, paddr);
        else
            _reservation = paddr;
        return ExecuteResult.WithResult(memory.Read(paddr, 4) & 0xFFFFFFFF);
    }

    private ExecuteResult AmoSc(
        IMemory memory,
        IArchState state,
        ulong pc,
        IRegisterFile regs,
        int rs1,
        int rs2
    ) {
        ulong vaddr = regs.Read(rs1);
        (ulong paddr, int fault) = Translate(memory, state, vaddr, true, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        bool success = ReservationTable?.TryConsume(HartId, paddr) ?? ConsumePrivateReservation(paddr);
        if (!success) return Reg(1); // reservation is absent or invalidated → fail
        memory.Write(paddr, regs.Read(rs2), 4);
        return Reg(0); // 0 = success
    }

    // Single-hart reservation consume: clears _reservation regardless of match (per spec).
    private bool ConsumePrivateReservation(ulong paddr) {
        bool matched = _reservation == paddr;
        _reservation = null; // SC always releases the reservation
        return matched;
    }

    // Zabha: narrow (byte or halfword) atomic RMW. rd receives the sign-extended old value.
    private ExecuteResult AmoNarrow(
        IMemory memory,
        IArchState state,
        ulong pc,
        IRegisterFile regs,
        int rs1,
        int rs2,
        int bytes,
        Func<uint, uint, uint> combine
    ) {
        ulong vaddr = regs.Read(rs1);
        (ulong addr, int fault) = Translate(memory, state, vaddr, true, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        var old = (uint)memory.Read(addr, bytes);
        memory.Write(addr, combine(old, (uint)regs.Read(rs2)), bytes);
        uint rd = bytes == 1
            ? (uint)(sbyte)(byte)old
            : (uint)(short)(ushort)old;
        return Reg(rd);
    }

    // Zacas: compare-and-swap word. rdReg is both comparand (source) and destination.
    private ExecuteResult AmoCasW(
        IMemory memory,
        IArchState state,
        ulong pc,
        IRegisterFile regs,
        int rdReg,
        int rs1,
        int rs2
    ) {
        ulong vaddr = regs.Read(rs1);
        (ulong addr, int fault) = Translate(memory, state, vaddr, true, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        var old = (uint)memory.Read(addr, 4);
        if (old == (uint)regs.Read(rdReg)) memory.Write(addr, regs.Read(rs2), 4);
        return Reg(old);
    }

    protected virtual (ulong paddr, int faultCause) Translate(
        IMemory memory,
        IArchState state,
        ulong vaddr,
        bool isWrite,
        bool isExec
    ) {
        if (state.SystemRegisters is not CsrFile csrs) return (vaddr, 0);
        bool sum = (csrs.DirectRead(CsrFile.Sstatus) & CsrFile.SstatusSum) != 0;
        return Sv32Walker.Translate(
            memory, csrs.DirectRead(CsrFile.Satp), vaddr, isWrite, isExec, state.PrivilegeLevel, sum
        );
    }

    protected virtual ExecuteResult Load(
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

    protected virtual ExecuteResult Store(
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

        // HTIF tohost exit: a word store of an odd value to the tohost register
        // is an exit code ((code << 1) | 1). Request a post-commit halt so the
        // engine stops at the exit write rather than the spin-loop after it.
        // (Even values are syscall pointers — e.g., printstr — and are ignored.)
        if (HtifTohostAddress is { } t && addr == t && bytes == 4 && (value & 1) == 1)
            return new ExecuteResult { RequestHalt = true, };

        return ExecuteResult.Clean;
    }

    private ExecuteResult CboMaintain(
        IMemory memory,
        IArchState state,
        ulong pc,
        ulong rs1Val,
        Action<IMemory, ulong> op
    ) {
        (ulong paddr, int fault) = Translate(memory, state, rs1Val, false, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, rs1Val, pc));
        op(memory, paddr);
        return ExecuteResult.Clean;
    }

    private ExecuteResult CboZero(IMemory memory, IArchState state, ulong pc, ulong addr) {
        ulong lineAddr = addr & ~63UL; // align down to 64-byte cache line
        (ulong physAddr, int fault) = Translate(memory, state, lineAddr, true, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, lineAddr, pc));
        for (var i = 0; i < 16; i++) memory.Write(physAddr + (ulong)(i * 4), 0, 4);
        return ExecuteResult.Clean;
    }

    protected static ExecuteResult Branch(bool taken, ulong pc, int imm, int instrSize) =>
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

    // Wrap an ExecuteResult (e.g., from Load) to NaN-box the 32-bit float value.
    private static ExecuteResult NanBoxF(ExecuteResult r) =>
        r.RegisterResult.HasValue
            ? ExecuteResult.WithResult(0xFFFFFFFF00000000UL | r.RegisterResult.Value)
            : r;

    // Read a float register as a C# float (bit-exact reinterpret).
    // NaN-boxing (§11.3): upper 32 bits must be all 1s; otherwise canonical NaN.
    private static float FBits(IRegisterFile regs, int rs) {
        ulong raw = regs.Read(rs);
        return raw >> 32 == 0xFFFFFFFFu
            ? BitConverter.Int32BitsToSingle((int)(uint)raw)
            : BitConverter.Int32BitsToSingle((int)Rv32Executor.RvCanonicalNaN);
    }

    // Read a double register as a C# double (bit-exact reinterpret).
    private static double DBits(IRegisterFile regs, int rs) =>
        BitConverter.Int64BitsToDouble((long)regs.Read(rs));

    // RISC-V canonical NaN for float32 (positive, quiet NaN with one mantissa bit set).
    private const uint RvCanonicalNaN = 0x7FC00000u;

    // Float result + OR flags into fflags via SideEffect.
    // Writes NaN-boxed value (upper 32 bits = 0xFFFFFFFF) per spec §11.3.
    private static ExecuteResult FloatRegF(float value, uint flags) {
        uint bits = float.IsNaN(value) ? Rv32Executor.RvCanonicalNaN : (uint)BitConverter.SingleToInt32Bits(value);
        ulong nanBoxed = 0xFFFFFFFF00000000UL | bits;
        if (flags == 0) return ExecuteResult.WithResult(nanBoxed);
        return new ExecuteResult
            { RegisterResult = (nanBoxed, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), };
    }

    // Double result + OR flags into fflags via SideEffect.
    private const ulong RvCanonicalNaNd = 0x7FF8000000000000UL;

    private static ExecuteResult FloatRegD(double value, uint flags) {
        ulong bits = double.IsNaN(value) ? Rv32Executor.RvCanonicalNaNd : (ulong)BitConverter.DoubleToInt64Bits(value);
        if (flags == 0) return ExecuteResult.WithResult(bits);
        return new ExecuteResult
            { RegisterResult = (bits, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), };
    }

    // Integer result + OR flags into fflags via SideEffect.
    private static ExecuteResult IntRegF(uint value, uint flags) {
        if (flags == 0) return ExecuteResult.WithResult(value);
        return new ExecuteResult
            { RegisterResult = (value, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), };
    }

    // ── Float32 minimum normal magnitude (2^-126) ─────────────────────────────
    private const float MinNormalF = 1.1754944e-38f;

    // Detect FP exception flags for binary arithmetic op by comparing the float
    // result against double-precision arithmetic (which is exact for 24-bit mantissa ops).
    // op: 0=add, 1=sub, 2=mul, 3=div
    private static uint FpArithFlags(float a, float b, float r, int op) {
        uint rawA = BitConverter.SingleToUInt32Bits(a);
        uint rawB = BitConverter.SingleToUInt32Bits(b);
        bool aNaN = float.IsNaN(a), bNaN = float.IsNaN(b);
        uint flags = 0;

        // NV: sNaN input, or NaN result from non-NaN inputs (e.g., Inf-Inf, 0*Inf)
        if (IsSNan(rawA) || IsSNan(rawB) || (float.IsNaN(r) && !aNaN && !bNaN)) flags |= 0x10;
        if (float.IsNaN(r)) return flags;

        double da = a, db = b;
        double exact = op switch { 0 => da + db, 1 => da - db, 2 => da * db, _ => da / db, };

        // DZ: finite non-zero / 0
        if (op == 3 && b == 0f && !aNaN && !float.IsInfinity(a)) flags |= 0x08;
        // OF: float overflowed to Inf but math didn't
        if (float.IsInfinity(r) && !double.IsInfinity(exact)) flags |= 0x04;
        if (float.IsInfinity(r)) return flags;

        bool nx = r != exact;
        if (nx) flags |= 0x01;
        // UF: subnormal result that is also inexact
        if (nx && r != 0f && MathF.Abs(r) < Rv32Executor.MinNormalF) flags |= 0x02;
        return flags;
    }

    // FMA version: r = a*b + c, exact via double FMA.
    private static uint FpFmaFlags(float a, float b, float c, float r) {
        uint rawA = BitConverter.SingleToUInt32Bits(a);
        uint rawB = BitConverter.SingleToUInt32Bits(b);
        uint rawC = BitConverter.SingleToUInt32Bits(c);
        bool aNaN = float.IsNaN(a), bNaN = float.IsNaN(b), cNaN = float.IsNaN(c);
        uint flags = 0;
        if (IsSNan(rawA) || IsSNan(rawB) || IsSNan(rawC) ||
            (float.IsNaN(r) && !aNaN && !bNaN && !cNaN))
            flags |= 0x10;
        if (float.IsNaN(r)) return flags;

        double exact = Math.FusedMultiplyAdd(a, b, c);
        if (float.IsInfinity(r) && !double.IsInfinity(exact)) flags |= 0x04;
        if (float.IsInfinity(r)) return flags;

        bool nx = r != exact;
        if (nx) flags |= 0x01;
        if (nx && r != 0f && MathF.Abs(r) < Rv32Executor.MinNormalF) flags |= 0x02;
        return flags;
    }

    // FSQRT flags: NV if a < 0, otherwise same NX/UF/OF logic.
    private static uint FpSqrtFlags(float a, float r) {
        uint rawA = BitConverter.SingleToUInt32Bits(a);
        if (IsSNan(rawA)) return 0x10;
        if (a < 0f) return 0x10; // sqrt of negative
        if (float.IsNaN(r)) return 0;
        double exact = Math.Sqrt(a);
        if (float.IsInfinity(r) && !double.IsInfinity(exact)) return 0x04;
        if (float.IsInfinity(r)) return 0;
        bool nx = r != exact;
        uint flags = nx ? 0x01u : 0u;
        if (nx && r != 0f && MathF.Abs(r) < Rv32Executor.MinNormalF) flags |= 0x02;
        return flags;
    }

    // Signaling NaN check: exponent=0xFF, fraction!=0, quiet bit (bit 22) = 0.
    private static bool IsSNan(uint bits) =>
        (bits & 0x7F800000u) == 0x7F800000u && (bits & 0x7FFFFFu) != 0 && (bits & 0x400000u) == 0;

    // ── MXCSR-tracked FP arithmetic ───────────────────────────────────────────

    private static ExecuteResult FpBin(float a, float b, int op) {
        float r = op switch { 0 => a + b, 1 => a - b, 2 => a * b, _ => a / b, };
        return FloatRegF(r, FpArithFlags(a, b, r, op));
    }

    private static ExecuteResult FpSqrt(float a) {
        float r = MathF.Sqrt(a);
        return FloatRegF(r, FpSqrtFlags(a, r));
    }

    private static ExecuteResult FpFma(float a, float b, float c) {
        float r = MathF.FusedMultiplyAdd(a, b, c);
        return FloatRegF(r, FpFmaFlags(a, b, c, r));
    }

    // FCVT.S.W / FCVT.S.WU: integer → float (may set NX if inexact).
    // Note: rounding mode affects which float is chosen; C# uses RNE by default.
    // We use the hardware default (RNE) since .NET doesn't expose per-op rounding.
    private static ExecuteResult FpIntToFloat(long intVal) {
        var r = (float)intVal;
        // NX if the integer can't be exactly represented in float32 (24-bit mantissa)
        bool nx = (long)r != intVal;
        return FloatRegF(r, nx ? 0x01u : 0u);
    }

    // FCVT.W.S with rounding mode and NV/NX flags.
    private static ExecuteResult FcvtWsResult(float f, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (float.IsNaN(f)) return IntRegF(0x7FFFFFFFu, 0x10u); // NaN → INT_MAX + NV
        float rounded = ApplyRm(f, rm);
        switch (rounded) {
            // Check if rounded value is out of int32 range
            case >= 2147483648f: return IntRegF(0x7FFFFFFFu, 0x10u); // > INT_MAX → NV
            case < -2147483648f: return IntRegF(0x80000000u, 0x10u); // < INT_MIN → NV
        }

        var result = (uint)(int)rounded;
        uint flags = f != (int)result ? 0x01u : 0u; // NX if original was not exact integer
        return IntRegF(result, flags);
    }

    // FCVT.WU.S with rounding mode and NV/NX flags.
    private static ExecuteResult FcvtWuSResult(float f, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (float.IsNaN(f)) return IntRegF(0xFFFFFFFFu, 0x10u); // NaN → UINT_MAX + NV
        float rounded = ApplyRm(f, rm);
        switch (rounded) {
            // Check if rounded value is out of uint32 range
            case >= 4294967296f: return IntRegF(0xFFFFFFFFu, 0x10u); // > UINT_MAX → NV
            case < 0f:           return IntRegF(0u, 0x10u);          // < 0 → NV
        }

        var result = (uint)rounded;
        uint flags = f != result ? 0x01u : 0u; // NX if original was not exact integer
        return IntRegF(result, flags);
    }

    // FMIN/FMAX: set NV if either input is a signaling NaN.
    private static ExecuteResult FpMinMax(IRegisterFile regs, int rs1, int rs2, bool isMin) {
        uint raw1 = (uint)regs.Read(rs1), raw2 = (uint)regs.Read(rs2);
        float a = BitConverter.Int32BitsToSingle((int)raw1);
        float b = BitConverter.Int32BitsToSingle((int)raw2);
        uint flags = IsSNan(raw1) || IsSNan(raw2) ? 0x10u : 0u;
        float result = isMin ? FMin(a, b) : FMax(a, b);
        return FloatRegF(result, flags);
    }

    // FEQ/FLT/FLE: NV flag for sNaN (FEQ) or any NaN (FLT/FLE).
    private static ExecuteResult FpCmp(IRegisterFile regs, int rs1, int rs2, int op) {
        uint raw1 = (uint)regs.Read(rs1), raw2 = (uint)regs.Read(rs2);
        float a = BitConverter.Int32BitsToSingle((int)raw1);
        float b = BitConverter.Int32BitsToSingle((int)raw2);
        // FLT/FLE: NV if either operand is NaN; FEQ: NV only for sNaN
        bool nvFlt = float.IsNaN(a) || float.IsNaN(b);
        bool nvFeq = IsSNan(raw1) || IsSNan(raw2);
        uint flags = op switch { 0 => nvFeq ? 0x10u : 0u, _ => nvFlt ? 0x10u : 0u, };
        ulong cmp = op switch { 0  => a == b ? 1UL : 0UL, 1 => a < b ? 1UL : 0UL, _ => a <= b ? 1UL : 0UL, };
        return flags != 0
            ? new ExecuteResult { RegisterResult = (cmp, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), }
            : ExecuteResult.WithResult(cmp);
    }

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

    // Apply rounding mode to a float before converting to integer.
    // rm: 0=RNE, 1=RTZ, 2=RDN, 3=RUP, 4=RMM, 7=DYN (resolved before call).
    private static float ApplyRm(float f, int rm) => rm switch {
        0 => MathF.Round(f, MidpointRounding.ToEven),
        2 => MathF.Floor(f),
        3 => MathF.Ceiling(f),
        4 => MathF.Round(f, MidpointRounding.AwayFromZero),
        _ => f >= 0f ? MathF.Floor(f) : MathF.Ceiling(f), // RTZ (truncate toward zero)
    };

    // Resolve DYN rounding mode (rm=7) from fcsr.frm.
    private static int ResolveRm(int rm, IArchState state) =>
        rm == 7 ? (int)((Rv32ArchState)state).CsrFile.DirectRead(CsrFile.Frm) : rm;

    // RISC-V FCVT.W.S: float → signed int with rounding mode and saturating clamp.
    private static uint FcvtWs(float f) =>
        f switch {
            float.NaN or >= 2147483648f => 0x7FFFFFFF // INT_MAX
           ,
            < -2147483648f => 0x80000000u // INT_MIN
           ,
            _ => (uint)(int)f,
        };

    // RISC-V FCVT.WU.S: float → unsigned int with saturating clamp.
    private static uint FcvtWuS(float f) =>
        f switch {
            float.NaN or >= 4294967296f => 0xFFFFFFFF,
            < 0f                        => 0,
            _                           => (uint)f,
        };

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

    // ── Double precision helpers ───────────────────────────────────────────────

    // FLD: reads 8 bytes from memory; returns all 64 bits (no truncation).
    private ExecuteResult LoadD(IMemory memory, IArchState state, ulong pc, ulong @base, int imm) {
        ulong vaddr = @base + (ulong)imm;
        (ulong addr, int fault) = Translate(memory, state, vaddr, false, false);
        return fault != 0
            ? ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc))
            : ExecuteResult.WithResult(memory.Read(addr, 8));
    }

    // sNaN detection for double (quiet bit = bit 51 is 0, mantissa != 0).
    private static bool IsDsNan(ulong raw) =>
        (raw & 0x7FF0000000000000UL) == 0x7FF0000000000000UL &&
        (raw & 0x000FFFFFFFFFFFFFUL) != 0 &&
        (raw & 0x0008000000000000UL) == 0;

    // Detect flags for D-precision binary op. NX/UF are best-effort (no 128-bit ref).
    private static uint DpArithFlags(double a, double b, double r, int op) {
        var rawA = (ulong)BitConverter.DoubleToInt64Bits(a);
        var rawB = (ulong)BitConverter.DoubleToInt64Bits(b);
        bool aNaN = double.IsNaN(a), bNaN = double.IsNaN(b);
        uint flags = 0;
        if (IsDsNan(rawA) || IsDsNan(rawB) || (double.IsNaN(r) && !aNaN && !bNaN)) flags |= 0x10;
        if (double.IsNaN(r)) return flags;
        if (op == 3 && b == 0.0 && !aNaN && !double.IsInfinity(a)) flags |= 0x08;
        if (double.IsInfinity(r) && !double.IsInfinity(a) && !double.IsInfinity(b)) flags |= 0x04;
        return flags;
    }

    private static uint DpFmaFlags(double a, double b, double c, double r) {
        var rawA = (ulong)BitConverter.DoubleToInt64Bits(a);
        var rawB = (ulong)BitConverter.DoubleToInt64Bits(b);
        var rawC = (ulong)BitConverter.DoubleToInt64Bits(c);
        bool aNaN = double.IsNaN(a), bNaN = double.IsNaN(b), cNaN = double.IsNaN(c);
        uint flags = 0;
        if (IsDsNan(rawA) || IsDsNan(rawB) || IsDsNan(rawC) ||
            (double.IsNaN(r) && !aNaN && !bNaN && !cNaN))
            flags |= 0x10;
        if (double.IsNaN(r)) return flags;
        if (double.IsInfinity(r) &&
            !double.IsInfinity(a) && !double.IsInfinity(b) && !double.IsInfinity(c))
            flags |= 0x04;
        return flags;
    }

    private static uint DpSqrtFlags(double a) {
        if (!double.IsNaN(a) && a < 0.0) return 0x10; // NV: sqrt of negative
        return 0;
    }

    private static ExecuteResult DpBin(double a, double b, int op) {
        double r = op switch { 0 => a + b, 1 => a - b, 2 => a * b, _ => a / b, };
        return FloatRegD(r, DpArithFlags(a, b, r, op));
    }

    private static ExecuteResult DpSqrt(double a) {
        double r = Math.Sqrt(a);
        return FloatRegD(r, DpSqrtFlags(a));
    }

    private static ExecuteResult DpFma(double a, double b, double c) {
        double r = Math.FusedMultiplyAdd(a, b, c);
        return FloatRegD(r, DpFmaFlags(a, b, c, r));
    }

    private static ExecuteResult DpMinMax(IRegisterFile regs, int rs1, int rs2, bool isMin) {
        ulong raw1 = regs.Read(rs1), raw2 = regs.Read(rs2);
        double a = BitConverter.Int64BitsToDouble((long)raw1);
        double b = BitConverter.Int64BitsToDouble((long)raw2);
        uint flags = IsDsNan(raw1) || IsDsNan(raw2) ? 0x10u : 0u;
        double result = isMin ? DMin(a, b) : DMax(a, b);
        return FloatRegD(result, flags);
    }

    private static ExecuteResult DpCmp(IRegisterFile regs, int rs1, int rs2, int op) {
        ulong raw1 = regs.Read(rs1), raw2 = regs.Read(rs2);
        double a = BitConverter.Int64BitsToDouble((long)raw1);
        double b = BitConverter.Int64BitsToDouble((long)raw2);
        bool nvFlt = double.IsNaN(a) || double.IsNaN(b);
        bool nvFeq = IsDsNan(raw1) || IsDsNan(raw2);
        uint flags = op switch { 0 => nvFeq ? 0x10u : 0u, _ => nvFlt ? 0x10u : 0u, };
        ulong cmp = op switch { 0  => a == b ? 1UL : 0UL, 1 => a < b ? 1UL : 0UL, _ => a <= b ? 1UL : 0UL, };
        return flags != 0
            ? new ExecuteResult { RegisterResult = (cmp, true), SideEffect = s => VState(s).CsrFile.OrFflags(flags), }
            : ExecuteResult.WithResult(cmp);
    }

    // FCLASS.D encoding (10-bit result, same bit semantics as FCLASS.S).
    private static ulong DClass(ulong bits) {
        bool sign = bits >> 63 != 0;
        var exp = (uint)((bits >> 52) & 0x7FF);
        ulong frac = bits & 0x000FFFFFFFFFFFFFUL;
        return exp switch {
            0x7FF when frac == 0 => sign ? 1UL << 0 : 1UL << 7,
            0x7FF                => frac >> 51 != 0 ? 1UL << 9 : 1UL << 8,
            0 => frac == 0 ? sign ? 1UL << 3 : 1UL << 4
                : sign     ? 1UL << 2 : 1UL << 5,
            _ => sign ? 1UL << 1 : 1UL << 6,
        };
    }

    private static double ApplyRmD(double d, int rm) => rm switch {
        0 => Math.Round(d, MidpointRounding.ToEven),
        2 => Math.Floor(d),
        3 => Math.Ceiling(d),
        4 => Math.Round(d, MidpointRounding.AwayFromZero),
        _ => d >= 0.0 ? Math.Floor(d) : Math.Ceiling(d), // RTZ
    };

    private static ExecuteResult FcvtWdResult(double d, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (double.IsNaN(d)) return IntRegF(0x7FFFFFFFu, 0x10u);
        double rounded = ApplyRmD(d, rm);
        switch (rounded) {
            case >= 2147483648.0: return IntRegF(0x7FFFFFFFu, 0x10u);
            case < -2147483648.0: return IntRegF(0x80000000u, 0x10u);
        }

        var result = (uint)(int)rounded;
        uint flags = d != (int)result ? 0x01u : 0u;
        return IntRegF(result, flags);
    }

    private static ExecuteResult FcvtWudResult(double d, int rm, IArchState state) {
        rm = ResolveRm(rm, state);
        if (double.IsNaN(d)) return IntRegF(0xFFFFFFFFu, 0x10u);
        double rounded = ApplyRmD(d, rm);
        switch (rounded) {
            case >= 4294967296.0: return IntRegF(0xFFFFFFFFu, 0x10u);
            case < 0.0:           return IntRegF(0u, 0x10u);
        }

        var result = (uint)rounded;
        uint flags = d != result ? 0x01u : 0u;
        return IntRegF(result, flags);
    }

    // FCVT.D.W / FCVT.D.WU: integer → double (always exact for 32-bit integers).
    private static ExecuteResult DpIntToDouble(long intVal) =>
        FloatRegD(intVal, 0);

    // FCVT.S.D: narrow double → single (may set NX, OF).
    private static ExecuteResult FpFcvtSd(double d, int rm, IArchState state) {
        ResolveRm(rm, state);
        // C# always rounds to nearest-even; other rounding modes are best-effort.
        var r = (float)d;
        uint flags = 0;
        var rawD = (ulong)BitConverter.DoubleToInt64Bits(d);
        if (IsDsNan(rawD))
            flags |= 0x10;
        else
            switch (double.IsNaN(d)) {
                case false when float.IsInfinity(r) && !double.IsInfinity(d):
                    flags |= 0x04; // OF
                    break;
                case false when !double.IsInfinity(d) && r != d:
                    flags |= 0x01; // NX
                    break;
            }

        return FloatRegF(r, flags);
    }

    // FCVT.D.S: widen single → double (exact; no flags except for sNaN input).
    private static ExecuteResult FpFcvtDs(float f) {
        uint rawF = BitConverter.SingleToUInt32Bits(f);
        uint flags = IsSNan(rawF) ? 0x10u : 0u;
        // Canonical NaN on sNaN input; otherwise exact widening.
        double result = IsSNan(rawF) ? BitConverter.Int64BitsToDouble((long)Rv32Executor.RvCanonicalNaNd) : f;
        return FloatRegD(result, flags);
    }

    // RISC-V DMIN: if one arg is NaN return the other; -0.0 < +0.0.
    private static double DMin(double a, double b) {
        if (double.IsNaN(a)) return b;
        if (double.IsNaN(b)) return a;
        if (a == 0.0 && b == 0.0)
            return BitConverter.DoubleToInt64Bits(a) < 0 || BitConverter.DoubleToInt64Bits(b) < 0 ? -0.0 : 0.0;
        return a < b ? a : b;
    }

    // RISC-V DMAX: if one arg is NaN return the other; +0.0 > -0.0.
    private static double DMax(double a, double b) {
        if (double.IsNaN(a)) return b;
        if (double.IsNaN(b)) return a;
        if (a == 0.0 && b == 0.0)
            return BitConverter.DoubleToInt64Bits(a) >= 0 || BitConverter.DoubleToInt64Bits(b) >= 0 ? 0.0 : -0.0;
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

    private static Rv32ArchState VState(IArchState state) => (Rv32ArchState)state;

    private static ExecuteResult VectorWrite(int vd, byte[] data) {
        return new ExecuteResult { SideEffect = s => ((Rv32ArchState)s).VectorRegisters.Write(vd, data), };
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
            8 => BitConverter.ToUInt64(data, off),
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
        uint vsew = (csrs.DirectRead(CsrFile.Vtype) >> 3) & 0x7; // vtype bits [5:3]
        var ewBytes = (int)(1u << (int)vsew);                    // 1, 2, 4, 8
        return (vl, ewBytes);
    }

    private static uint ComputeVlmax(int vtypei) {
        var vsew = (uint)((vtypei >> 3) & 0x7); // vtypei bits [5:3] = vsew
        var ewBits = (int)(8u << (int)vsew);    // 8, 16, 32, 64
        int vlmulField = vtypei & 0x7;          // vtypei bits [2:0] = vlmul
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

    private static ExecuteResult ExecuteVlr(IArchState state, IMemory memory, int numRegs, int vd, int rs1) {
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        return new ExecuteResult {
            SideEffect = s => {
                var rv = (Rv32ArchState)s;
                for (var r = 0; r < numRegs; r++) {
                    var buf = new byte[VectorRegisterFile.VLenB];
                    for (var b = 0; b < VectorRegisterFile.VLenB; b++)
                        buf[b] = (byte)memory.Read(baseAddr + (ulong)(r * VectorRegisterFile.VLenB + b), 1);
                    rv.VectorRegisters.Write(vd + r, buf);
                }
            },
        };
    }

    private static ExecuteResult ExecuteVsr(IArchState state, IMemory memory, int numRegs, int vs3, int rs1) {
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        for (var r = 0; r < numRegs; r++) {
            byte[] data = vregs.Read(vs3 + r);
            for (var b = 0; b < VectorRegisterFile.VLenB; b++)
                memory.Write(baseAddr + (ulong)(r * VectorRegisterFile.VLenB + b), data[b], 1);
        }

        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecuteVlseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vd,
        int rs1,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] mask = VState(state).VectorRegisters.Read(0);

        var results = new byte[numFields][];
        for (var f = 0; f < numFields; f++) results[f] = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            for (var f = 0; f < numFields; f++) {
                ulong addr = baseAddr + (ulong)((i * numFields + f) * ewBytes);
                WriteVElement(results[f], i, ewBytes, memory.Read(addr, ewBytes));
            }
        }

        return new ExecuteResult {
            SideEffect = s => {
                for (var f = 0; f < numFields; f++) VState(s).VectorRegisters.Write(vd + f, results[f]);
            },
        };
    }

    private static ExecuteResult ExecuteVsseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vs3,
        int rs1,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] mask = VState(state).VectorRegisters.Read(0);

        var srcs = new byte[numFields][];
        for (var f = 0; f < numFields; f++) srcs[f] = VState(state).VectorRegisters.Read(vs3 + f);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            for (var f = 0; f < numFields; f++) {
                ulong addr = baseAddr + (ulong)((i * numFields + f) * ewBytes);
                memory.Write(addr, ReadVElement(srcs[f], i, ewBytes), ewBytes);
            }
        }

        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecuteVlse(
        IArchState state,
        IMemory memory,
        int vd,
        int rs1,
        int rs2,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        long stride = (int)(uint)state.IntegerRegisters.Read(rs2); // sign-extend 32→64
        var result = new byte[VectorRegisterFile.VLenB];
        byte[] mask = VState(state).VectorRegisters.Read(0);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            var addr = (ulong)((long)baseAddr + stride * i);
            WriteVElement(result, i, ewBytes, memory.Read(addr, ewBytes));
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVsse(
        IArchState state,
        IMemory memory,
        int vs3,
        int rs1,
        int rs2,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        long stride = (int)(uint)state.IntegerRegisters.Read(rs2); // sign-extend 32→64
        byte[] data = VState(state).VectorRegisters.Read(vs3);
        byte[] mask = VState(state).VectorRegisters.Read(0);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            var addr = (ulong)((long)baseAddr + stride * i);
            memory.Write(addr, ReadVElement(data, i, ewBytes), ewBytes);
        }

        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecuteVlxei(
        IArchState state,
        IMemory memory,
        int vd,
        int rs1,
        int vs2,
        int indexSew,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int idxBytes = indexSew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] idxData = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong offset = ReadVElement(idxData, i, idxBytes);
            WriteVElement(result, i, ewBytes, memory.Read(baseAddr + offset, ewBytes));
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVsxei(
        IArchState state,
        IMemory memory,
        int vs3,
        int rs1,
        int vs2,
        int indexSew,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int idxBytes = indexSew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] idxData = VState(state).VectorRegisters.Read(vs2);
        byte[] data = VState(state).VectorRegisters.Read(vs3);
        byte[] mask = VState(state).VectorRegisters.Read(0);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong offset = ReadVElement(idxData, i, idxBytes);
            memory.Write(baseAddr + offset, ReadVElement(data, i, ewBytes), ewBytes);
        }

        return ExecuteResult.Clean;
    }

    private static ExecuteResult ExecuteVWide(
        IArchState state,
        VWideOp op,
        int vd,
        int vs2,
        bool masked,
        bool vs2IsWide,
        Func<int, int, ulong> getSource
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int outEwBytes = ewBytes * 2;
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / outEwBytes);
        int vs2EwBytes = vs2IsWide ? outEwBytes : ewBytes;
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, vs2EwBytes);
            ulong b = getSource(i, ewBytes);
            WriteVElement(result, i, outEwBytes, ApplyVWideOp(op, a, b, ewBytes, vs2IsWide));
        }

        return VectorWrite(vd, result);
    }

    private static ulong ApplyVWideOp(VWideOp op, ulong a, ulong b, int ewBytes, bool vs2IsWide) {
        return op switch {
            VWideOp.AddU  => (vs2IsWide ? a : Zx(a)) + Zx(b),
            VWideOp.Add   => (ulong)((vs2IsWide ? Sx2(a) : Sx(a)) + Sx(b)),
            VWideOp.SubU  => (vs2IsWide ? a : Zx(a)) - Zx(b),
            VWideOp.Sub   => (ulong)((vs2IsWide ? Sx2(a) : Sx(a)) - Sx(b)),
            VWideOp.MulU  => Zx(a) * Zx(b),
            VWideOp.MulSu => (ulong)(Sx(a) * (long)Zx(b)),
            VWideOp.Mul   => (ulong)(Sx(a) * Sx(b)),
            _             => throw new InvalidOperationException($"Unknown VWideOp {op}"),
        };

        long Sx(ulong v) => ewBytes switch {
            1 => (sbyte)(byte)v, 2 => (short)(ushort)v, 4 => (int)(uint)v, _ => (long)v,
        };

        long Sx2(ulong v) => (ewBytes * 2) switch {
            2 => (short)(ushort)v, 4 => (int)(uint)v, _ => (long)v,
        };

        ulong Zx(ulong v) => ewBytes switch {
            1 => (byte)v, 2 => (ushort)v, 4 => (uint)v, _ => v,
        };
    }

    // vwmacc/vwmaccu/vwmaccsu/vwmaccus: vd[i] (2×SEW) += product of two SEW operands.
    private static ExecuteResult ExecuteVwMac(
        IArchState state,
        VwMacOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int outEwBytes = ewBytes * 2;
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / outEwBytes);
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] vs2Data = vregs.Read(vs2);
        byte[] vdData = vregs.Read(vd);
        byte[] mask = vregs.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(vdData, result, vdData.Length);

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getB(i, ewBytes);
            ulong acc = ReadVElement(result, i, outEwBytes);
            ulong product = op switch {
                VwMacOp.Maccu  => Zx(a) * Zx(b),
                VwMacOp.Macc   => (ulong)(Sx(a) * Sx(b)),
                VwMacOp.Maccsu => (ulong)(Sx(a) * (long)Zx(b)),
                VwMacOp.Maccus => (ulong)((long)Zx(a) * Sx(b)),
                _              => 0UL,
            };
            WriteVElement(result, i, outEwBytes, acc + product);
        }

        return VectorWrite(vd, result);

        long Sx(ulong v) => ewBytes switch {
            1 => (sbyte)(byte)v, 2 => (short)(ushort)v, 4 => (int)(uint)v, _ => (long)v,
        };

        ulong Zx(ulong v) => ewBytes switch {
            1 => (byte)v, 2 => (ushort)v, 4 => (uint)v, _ => v,
        };
    }

    private static ExecuteResult ExecuteVNarr(
        IArchState state,
        VNarrOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getShift
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state); // output element width
        int srcEwBytes = ewBytes * 2;             // vs2 source element width (2*SEW)
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / srcEwBytes);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        int shamtMask = srcEwBytes * 8 - 1; // log2(2*SEW) bits

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong src = ReadVElement(vs2Data, i, srcEwBytes);
            var shamt = (int)(getShift(i, ewBytes) & (ulong)shamtMask);
            ulong res = op == VNarrOp.Sra
                ? (ulong)(srcEwBytes switch {
                    2 => (short)(ushort)src >> shamt,
                    4 => (int)(uint)src >> shamt,
                    _ => (long)src >> shamt,
                })
                : src >> shamt;
            WriteVElement(result, i, ewBytes, res);
        }

        return VectorWrite(vd, result);
    }

    // ── V new ops ─────────────────────────────────────────────────────────────

    // vzext/vsext: zero/sign extend each element from SEW/factor bits to SEW bits.
    private static ExecuteResult ExecuteVExt(IArchState state, bool signed, int factor, int vd, int vs2, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int srcBytes = ewBytes / factor;
        if (srcBytes < 1) throw new InvalidOperationException("VExt: factor exceeds SEW");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong raw = ReadVElement(vs2Data, i, srcBytes);
            ulong val = signed
                ? srcBytes switch {
                    1 => (ulong)(sbyte)(byte)raw,
                    2 => (ulong)(short)(ushort)raw,
                    _ => (ulong)(int)(uint)raw,
                }
                : raw;
            ulong mask = ewBytes == 8 ? ulong.MaxValue : (1UL << (ewBytes * 8)) - 1;
            WriteVElement(result, i, ewBytes, val & mask);
        }

        return VectorWrite(vd, result);
    }

    // vaaddu/vaadd/vasubu/vasub: fixed-point averaging with vxrm rounding.
    private static ExecuteResult ExecuteVAvg(
        IArchState state,
        VAvgOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        uint vxrm = VState(state).CsrFile.DirectRead(CsrFile.Vxrm) & 3;
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        ulong uMax = ewBytes switch { 1 => 0xFFUL, 2 => 0xFFFFUL, 4 => 0xFFFFFFFFUL, _ => ulong.MaxValue, };

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getB(i, ewBytes);
            ulong sum;
            ulong shifted;
            switch (op) {
                case VAvgOp.Addu:
                    sum = (a & uMax) + (b & uMax);
                    shifted = sum >> 1;
                    break;
                case VAvgOp.Add: {
                    long ssum = Sx(a) + Sx(b);
                    sum = (ulong)ssum;
                    shifted = (ulong)(ssum >> 1);
                    break;
                }
                case VAvgOp.Subu:
                    sum = (a & uMax) - (b & uMax);
                    shifted = sum >> 1;
                    break;
                case VAvgOp.Sub:
                default: {
                    // Sub (signed)
                    long sdiff = Sx(a) - Sx(b);
                    sum = (ulong)sdiff;
                    shifted = (ulong)(sdiff >> 1);
                    break;
                }
            }

            WriteVElement(result, i, ewBytes, (shifted + ComputeRoundBit(sum, 1, shifted, vxrm)) & uMax);
        }

        return VectorWrite(vd, result);

        long Sx(ulong v) => ewBytes switch {
            1 => (sbyte)(byte)v, 2 => (short)(ushort)v, 4 => (int)(uint)v, _ => (long)v,
        };
    }

    // vfwredusum.vs / vfwredosum.vs: f32 elements summed into f64 accumulator in vd[0].
    private static ExecuteResult ExecuteVFpWideRed(
        IArchState state,
        int vd,
        int vs2,
        int vs1,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("vfwred: only SEW=32 supported");
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] vs2Data = vregs.Read(vs2);
        byte[] vs1Data = vregs.Read(vs1);
        byte[] maskData = vregs.Read(0);
        double acc = BitConverter.Int64BitsToDouble((long)ReadVElement(vs1Data, 0, 8));

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            acc += BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
        }

        var result = new byte[VectorRegisterFile.VLenB];
        WriteVElement(result, 0, 8, (ulong)BitConverter.DoubleToInt64Bits(acc));
        return VectorWrite(vd, result);
    }

    // vlsseg: strided segment load — element i field f at base + stride*i + f*ewBytes.
    private static ExecuteResult ExecuteVlsseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vd,
        int rs1,
        int rs2,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        long stride = (int)(uint)state.IntegerRegisters.Read(rs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var results = new byte[numFields][];
        for (var f = 0; f < numFields; f++) results[f] = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            for (var f = 0; f < numFields; f++) {
                ulong addr = (ulong)((long)baseAddr + stride * i) + (ulong)(f * ewBytes);
                WriteVElement(results[f], i, ewBytes, memory.Read(addr, ewBytes));
            }
        }

        return new ExecuteResult {
            SideEffect = s => {
                for (var f = 0; f < numFields; f++) VState(s).VectorRegisters.Write(vd + f, results[f]);
            },
        };
    }

    // vsseg (strided): element i field f at base + stride*i + f*ewBytes.
    private static ExecuteResult ExecuteVssseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vs3,
        int rs1,
        int rs2,
        int sew,
        bool masked
    ) {
        (uint vl, _) = VGetVlEw(state);
        int ewBytes = sew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        long stride = (int)(uint)state.IntegerRegisters.Read(rs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var srcs = new byte[numFields][];
        for (var f = 0; f < numFields; f++) srcs[f] = VState(state).VectorRegisters.Read(vs3 + f);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            for (var f = 0; f < numFields; f++) {
                ulong addr = (ulong)((long)baseAddr + stride * i) + (ulong)(f * ewBytes);
                memory.Write(addr, ReadVElement(srcs[f], i, ewBytes), ewBytes);
            }
        }

        return ExecuteResult.Clean;
    }

    // vlxseg: indexed segment load — element i field f at base + index[i] + f*dataSew/8.
    private static ExecuteResult ExecuteVlxseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vd,
        int rs1,
        int vs2,
        int indexSew,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int idxBytes = indexSew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] idxData = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var results = new byte[numFields][];
        for (var f = 0; f < numFields; f++) results[f] = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong offset = ReadVElement(idxData, i, idxBytes);
            for (var f = 0; f < numFields; f++) {
                ulong addr = baseAddr + offset + (ulong)(f * ewBytes);
                WriteVElement(results[f], i, ewBytes, memory.Read(addr, ewBytes));
            }
        }

        return new ExecuteResult {
            SideEffect = s => {
                for (var f = 0; f < numFields; f++) VState(s).VectorRegisters.Write(vd + f, results[f]);
            },
        };
    }

    // vsxseg: indexed segment store — element i field f at base + index[i] + f*dataSew/8.
    private static ExecuteResult ExecuteVsxseg(
        IArchState state,
        IMemory memory,
        int numFields,
        int vs3,
        int rs1,
        int vs2,
        int indexSew,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int idxBytes = indexSew / 8;
        ulong baseAddr = state.IntegerRegisters.Read(rs1);
        byte[] idxData = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var srcs = new byte[numFields][];
        for (var f = 0; f < numFields; f++) srcs[f] = VState(state).VectorRegisters.Read(vs3 + f);

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong offset = ReadVElement(idxData, i, idxBytes);
            for (var f = 0; f < numFields; f++) {
                ulong addr = baseAddr + offset + (ulong)(f * ewBytes);
                memory.Write(addr, ReadVElement(srcs[f], i, ewBytes), ewBytes);
            }
        }

        return ExecuteResult.Clean;
    }

    // ── Saturating integer helpers (vxrm rounding modes) ─────────────────────

    // Round bit for a right-shift of shiftAmt bits on value, given already-shifted result.
    private static ulong ComputeRoundBit(ulong value, int shiftAmt, ulong shifted, uint vxrm) {
        switch (vxrm) {
            case 0: // rnu: round-nearest-up (add guard bit)
                return (value >> (shiftAmt - 1)) & 1;
            case 1: {
                // rne: round-nearest-even (round to even if exactly halfway)
                ulong g = (value >> (shiftAmt - 1)) & 1;
                ulong sticky = shiftAmt >= 2 ? value & ((1UL << (shiftAmt - 1)) - 1) : 0;
                return g & ((sticky != 0 ? 1UL : 0) | (shifted & 1));
            }
            case 2: // rdn: round-down (truncate)
                return 0;
            default: {
                // rod: round-to-odd (force result LSB = 1 if any truncated bits are nonzero)
                ulong lost = shiftAmt < 64 ? value & ((1UL << shiftAmt) - 1) : value != 0 ? 1UL : 0;
                return ~shifted & 1 & (lost != 0 ? 1UL : 0);
            }
        }
    }

    private static ulong VRoundShiftU(ulong value, int shiftAmt, uint vxrm) {
        if (shiftAmt == 0) return value;
        ulong shifted = value >> shiftAmt;
        return shifted + ComputeRoundBit(value, shiftAmt, shifted, vxrm);
    }

    // Arithmetic rounded shift; value is sign-extended from ewBytes before shifting.
    private static ulong VRoundShiftS(ulong value, int shiftAmt, uint vxrm, int ewBytes) {
        long svalue = ewBytes switch {
            1 => (sbyte)(byte)value,
            2 => (short)(ushort)value,
            4 => (int)(uint)value,
            _ => (long)value,
        };
        if (shiftAmt == 0) return (ulong)svalue;
        var uvalue = (ulong)svalue;
        var shifted = (ulong)(svalue >> shiftAmt);
        return shifted + ComputeRoundBit(uvalue, shiftAmt, shifted, vxrm);
    }

    private static ExecuteResult ExecuteVSatInt(
        IArchState state,
        VSatIntOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        uint vxrm = VState(state).CsrFile.DirectRead(CsrFile.Vxrm) & 3;
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        int sew = ewBytes * 8;
        int shiftMask = sew - 1;
        ulong uMax = ewBytes switch { 1 => 0xFFUL, 2 => 0xFFFFUL, 4 => 0xFFFFFFFFUL, _ => ulong.MaxValue, };
        long sMin = ewBytes switch { 1 => sbyte.MinValue, 2 => short.MinValue, 4 => int.MinValue, _ => long.MinValue, };
        long sMax = ewBytes switch { 1 => sbyte.MaxValue, 2 => short.MaxValue, 4 => int.MaxValue, _ => long.MaxValue, };

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getB(i, ewBytes);
            ulong elem;
            switch (op) {
                case VSatIntOp.Sadd: elem = (ulong)Math.Clamp(Sx(a) + Sx(b), sMin, sMax); break;
                case VSatIntOp.Saddu: {
                    ulong sum = (a & uMax) + (b & uMax);
                    elem = sum > uMax ? uMax : sum;
                    break;
                }
                case VSatIntOp.Ssub: elem = (ulong)Math.Clamp(Sx(a) - Sx(b), sMin, sMax); break;
                case VSatIntOp.Ssubu: {
                    ulong ua = a & uMax, ub = b & uMax;
                    elem = ua >= ub ? ua - ub : 0;
                    break;
                }
                case VSatIntOp.Smul: {
                    // 2*SEW product, round right by (SEW-1), saturate
                    long sa = Sx(a), sb = Sx(b);
                    long product = sa * sb;
                    int shift = sew - 1;
                    ulong rounded = VRoundShiftS((ulong)product, shift, vxrm, ewBytes * 2);
                    elem = (ulong)Math.Clamp((long)rounded, sMin, sMax);
                    break;
                }
                case VSatIntOp.Ssrl: {
                    var shamt = (int)(b & (ulong)shiftMask);
                    elem = VRoundShiftU(a & uMax, shamt, vxrm) & uMax;
                    break;
                }
                case VSatIntOp.Ssra: {
                    var shamt = (int)(b & (ulong)shiftMask);
                    elem = VRoundShiftS(a, shamt, vxrm, ewBytes) & uMax;
                    break;
                }
                default: elem = 0; break;
            }

            WriteVElement(result, i, ewBytes, elem);
        }

        return VectorWrite(vd, result);

        long Sx(ulong v) => ewBytes switch {
            1 => (sbyte)(byte)v, 2 => (short)(ushort)v, 4 => (int)(uint)v, _ => (long)v,
        };
    }

    private static ExecuteResult ExecuteVnClip(
        IArchState state,
        VnClipOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state); // output SEW
        int inEwBytes = ewBytes * 2;              // input is 2*SEW
        uint vxrm = VState(state).CsrFile.DirectRead(CsrFile.Vxrm) & 3;
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / inEwBytes);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        int shiftMask = inEwBytes * 8 - 1; // log2(2*SEW) bits
        ulong uMax = ewBytes switch { 1 => 0xFFUL, 2 => 0xFFFFUL, 4 => 0xFFFFFFFFUL, _ => ulong.MaxValue, };
        long sMin = ewBytes switch { 1 => sbyte.MinValue, 2 => short.MinValue, 4 => int.MinValue, _ => long.MinValue, };
        long sMax = ewBytes switch { 1 => sbyte.MaxValue, 2 => short.MaxValue, 4 => int.MaxValue, _ => long.MaxValue, };
        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, inEwBytes);
            ulong b = getB(i, ewBytes);
            var shamt = (int)(b & (ulong)shiftMask);
            ulong elem;
            if (op == VnClipOp.Clipu) {
                ulong shifted = VRoundShiftU(a, shamt, vxrm);
                elem = Math.Min(shifted, uMax);
            }
            else {
                ulong shifted = VRoundShiftS(a, shamt, vxrm, inEwBytes);
                elem = (ulong)Math.Clamp((long)shifted, sMin, sMax);
            }

            WriteVElement(result, i, ewBytes, elem);
        }

        return VectorWrite(vd, result);
    }

    // offsetOrVal interpretation:
    //   is1=false → unsigned offset count (elements to shift by)
    //   is1=true  → scalar value to insert at the slide boundary (position 0 for Up, vl-1 for Down)
    private static ExecuteResult ExecuteVSlide(
        IArchState state,
        VSlideDir dir,
        bool is1,
        int vd,
        int vs2,
        bool masked,
        ulong offsetOrVal
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] src = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        // Start with current vd content so unaffected elements are preserved (TU semantics).
        var result = (byte[])VState(state).VectorRegisters.Read(vd).Clone();

        if (dir == VSlideDir.Up) {
            ulong offset = is1 ? 1UL : offsetOrVal;
            for (var i = (int)offset; i < (int)vl; i++) {
                if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
                ulong val = i == 0 && is1
                    ? offsetOrVal // insert scalar at vd[0]
                    : ReadVElement(src, i - (int)offset, ewBytes);
                WriteVElement(result, i, ewBytes, val);
            }

            // vslide1up: also write scalar at position 0
            if (is1 && vl > 0 && (!masked || ((mask[0] >> 0) & 1) != 0)) WriteVElement(result, 0, ewBytes, offsetOrVal);
        }
        else {
            ulong offset = is1 ? 1UL : offsetOrVal;
            for (var i = 0; i < (int)vl; i++) {
                if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
                ulong srcIdx = (ulong)i + offset;
                ulong val = is1 && i == (int)vl - 1
                    ? offsetOrVal // insert scalar at last position
                    : srcIdx < vl
                        ? ReadVElement(src, (int)srcIdx, ewBytes)
                        : 0;
                WriteVElement(result, i, ewBytes, val);
            }
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVRgatherEi16(
        IArchState state,
        int vd,
        int vs2,
        int vs1,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] src = VState(state).VectorRegisters.Read(vs2);
        byte[] idxData = VState(state).VectorRegisters.Read(vs1);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong index = ReadVElement(idxData, i, 2); // always u16 regardless of SEW
            WriteVElement(result, i, ewBytes, index < vl ? ReadVElement(src, (int)index, ewBytes) : 0);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVRgather(
        IArchState state,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getIndex
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] src = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong index = getIndex(i, ewBytes);
            WriteVElement(result, i, ewBytes, index < vl ? ReadVElement(src, (int)index, ewBytes) : 0);
        }

        return VectorWrite(vd, result);
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

    private static ExecuteResult ExecuteVMaskLog(IArchState state, VMaskLogOp op, int vd, int vs2, int vs1) {
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] a = vregs.Read(vs2);
        byte[] b = vregs.Read(vs1);
        var result = new byte[VectorRegisterFile.VLenB];
        for (var i = 0; i < VectorRegisterFile.VLenB; i++)
            result[i] = op switch {
                VMaskLogOp.Andn => (byte)(a[i] & ~b[i]),
                VMaskLogOp.And  => (byte)(a[i] & b[i]),
                VMaskLogOp.Or   => (byte)(a[i] | b[i]),
                VMaskLogOp.Xor  => (byte)(a[i] ^ b[i]),
                VMaskLogOp.Orn  => (byte)(a[i] | ~b[i]),
                VMaskLogOp.Nand => (byte)~(a[i] & b[i]),
                VMaskLogOp.Nor  => (byte)~(a[i] | b[i]),
                VMaskLogOp.Xnor => (byte)~(a[i] ^ b[i]),
                _               => 0,
            };
        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVcpop(IArchState state, int vs2, bool masked) {
        (uint vl, _) = VGetVlEw(state);
        byte[] src = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = masked ? VState(state).VectorRegisters.Read(0) : [];
        ulong count = 0;
        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            if (((src[i >> 3] >> (i & 7)) & 1) != 0) count++;
        }

        return ExecuteResult.WithResult(count);
    }

    private static ExecuteResult ExecuteVfirst(IArchState state, int vs2, bool masked) {
        (uint vl, _) = VGetVlEw(state);
        byte[] src = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = masked ? VState(state).VectorRegisters.Read(0) : [];
        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            if (((src[i >> 3] >> (i & 7)) & 1) != 0) return ExecuteResult.WithResult((uint)i);
        }

        return ExecuteResult.WithResult(ulong.MaxValue); // -1 sign-extended to XLEN
    }

    private static ExecuteResult ExecuteVMaskUnary(
        IArchState state,
        VMaskUnaryOp op,
        int vd,
        int vs2,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] maskReg = masked ? vregs.Read(0) : [];
        byte[] vdOld = vregs.Read(vd);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(vdOld, result, VectorRegisterFile.VLenB);

        switch (op) {
            case VMaskUnaryOp.Msbf or VMaskUnaryOp.Msof or VMaskUnaryOp.Msif: {
                byte[] src2 = vregs.Read(vs2);
                int firstSet = -1;
                for (var i = 0; i < (int)vl; i++)
                    if (((src2[i >> 3] >> (i & 7)) & 1) != 0) {
                        firstSet = i;
                        break;
                    }

                for (var i = 0; i < (int)vl; i++) {
                    if (masked && ((maskReg[i >> 3] >> (i & 7)) & 1) == 0) continue;
                    bool val = op switch {
                        VMaskUnaryOp.Msbf => firstSet < 0 || i < firstSet,
                        VMaskUnaryOp.Msof => i == firstSet,
                        _                 => firstSet < 0 || i <= firstSet, // Msif
                    };
                    if (val)
                        result[i >> 3] |= (byte)(1 << (i & 7));
                    else
                        result[i >> 3] &= (byte)~(1 << (i & 7));
                }

                return VectorWrite(vd, result);
            }
            case VMaskUnaryOp.Iota: {
                byte[] src2 = vregs.Read(vs2);
                ulong prefix = 0;
                for (var i = 0; i < (int)vl; i++) {
                    bool isSet = ((src2[i >> 3] >> (i & 7)) & 1) != 0;
                    if (masked && ((maskReg[i >> 3] >> (i & 7)) & 1) == 0) {
                        if (isSet) prefix++;
                        continue;
                    }

                    WriteVElement(result, i, ewBytes, prefix);
                    if (isSet) prefix++;
                }

                return VectorWrite(vd, result);
            }
        }

        // VMaskUnaryOp.Id: write element index i into each active vd[i]
        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskReg[i >> 3] >> (i & 7)) & 1) == 0) continue;
            WriteVElement(result, i, ewBytes, (uint)i);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVCompress(IArchState state, int vd, int vs2, int vs1) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] srcData = vregs.Read(vs2);
        byte[] maskReg = vregs.Read(vs1); // vs1 is the explicit mask register (not v0)
        byte[] vdOld = vregs.Read(vd);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(vdOld, result, VectorRegisterFile.VLenB); // tail elements undisturbed
        var destIdx = 0;
        for (var i = 0; i < (int)vl; i++) {
            if (((maskReg[i >> 3] >> (i & 7)) & 1) == 0) continue;
            WriteVElement(result, destIdx, ewBytes, ReadVElement(srcData, i, ewBytes));
            destIdx++;
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVMvNr(IArchState state, int numRegs, int vd, int vs2) {
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        var snapshots = new byte[numRegs][];
        for (var i = 0; i < numRegs; i++) snapshots[i] = vregs.Read(vs2 + i);
        return new ExecuteResult {
            SideEffect = s => {
                for (var i = 0; i < numRegs; i++) ((Rv32ArchState)s).VectorRegisters.Write(vd + i, snapshots[i]);
            },
        };
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
            VIntOp.Add  => a + b,
            VIntOp.Sub  => a - b,
            VIntOp.Rsub => b - a, // vrsub: scalar/imm (b) minus vector element (a)
            VIntOp.And  => a & b,
            VIntOp.Or   => a | b,
            VIntOp.Xor  => a ^ b,
            VIntOp.Mov  => b, // vmv.v.v/x/i: broadcast second operand (vs1 or scalar or imm)
            VIntOp.Minu => a < b ? a : b,
            VIntOp.Maxu => a > b ? a : b,
            VIntOp.Min => ewBytes switch {
                1 => (sbyte)(byte)a < (sbyte)(byte)b ? a : b,
                2 => (short)(ushort)a < (short)(ushort)b ? a : b,
                _ => (int)(uint)a < (int)(uint)b ? a : b,
            },
            VIntOp.Max => ewBytes switch {
                1 => (sbyte)(byte)a > (sbyte)(byte)b ? a : b,
                2 => (short)(ushort)a > (short)(ushort)b ? a : b,
                _ => (int)(uint)a > (int)(uint)b ? a : b,
            },
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

    private static ExecuteResult ExecuteVMul(
        IArchState state,
        VMulOp op,
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
            WriteVElement(result, i, ewBytes, ApplyVMulOp(op, a, b, ewBytes));
        }

        return VectorWrite(vd, result);
    }

    private static ulong ApplyVMulOp(VMulOp op, ulong a, ulong b, int ewBytes) {
        ulong mask = (1UL << (ewBytes * 8)) - 1;
        ulong r = op switch {
            VMulOp.Mul => a * b,
            VMulOp.MulH => ewBytes switch {
                1 => (byte)((short)((sbyte)(byte)a * (sbyte)(byte)b) >> 8),
                2 => (ushort)(((short)(ushort)a * (short)(ushort)b) >> 16),
                _ => (ulong)(uint)(((int)(uint)a * (long)(int)(uint)b) >> 32),
            },
            VMulOp.MulHu => ewBytes switch {
                1 => (byte)((ushort)((byte)a * (byte)b) >> 8),
                2 => (ushort)((uint)((ushort)a * (ushort)b) >> 16),
                _ => (ulong)(uint)(((uint)a * (ulong)(uint)b) >> 32),
            },
            // vs2 signed × vs1/rs1 unsigned, high half
            VMulOp.MulHsu => ewBytes switch {
                1 => (byte)((short)((sbyte)(byte)a * (byte)b) >> 8),
                2 => (ushort)(((short)(ushort)a * (ushort)b) >> 16),
                _ => (ulong)(uint)(((int)(uint)a * (uint)b) >> 32),
            },
            VMulOp.Div => VDivSigned(a, b, ewBytes),
            VMulOp.Divu => ewBytes switch {
                1 => (byte)b == 0 ? 0xFFUL : (ulong)((byte)a / (byte)b),
                2 => (ushort)b == 0 ? 0xFFFFUL : (ulong)((ushort)a / (ushort)b),
                _ => (uint)b == 0 ? 0xFFFFFFFFUL : (uint)a / (uint)b,
            },
            VMulOp.Rem => VRemSigned(a, b, ewBytes),
            VMulOp.Remu => ewBytes switch {
                1 => (byte)b == 0 ? a & 0xFFUL : (ulong)((byte)a % (byte)b),
                2 => (ushort)b == 0 ? a & 0xFFFFUL : (ulong)((ushort)a % (ushort)b),
                _ => (uint)b == 0 ? a & 0xFFFFFFFFUL : (uint)a % (uint)b,
            },
            _ => 0,
        };
        return r & mask;
    }

    private static ulong VDivSigned(ulong a, ulong b, int ewBytes) {
        switch (ewBytes) {
            case 1: {
                var sa = (sbyte)(byte)a;
                var sb = (sbyte)(byte)b;
                if (sb == 0) return 0xFFUL;
                if (sa == sbyte.MinValue && sb == -1) return unchecked((byte)sbyte.MinValue);
                return (byte)(sa / sb);
            }
            case 2: {
                var sa = (short)(ushort)a;
                var sb = (short)(ushort)b;
                if (sb == 0) return 0xFFFFUL;
                if (sa == short.MinValue && sb == -1) return unchecked((ushort)short.MinValue);
                return (ushort)(sa / sb);
            }
            default: {
                var sa = (int)(uint)a;
                var sb = (int)(uint)b;
                if (sb == 0) return 0xFFFFFFFFUL;
                if (sa == int.MinValue && sb == -1) return unchecked((uint)int.MinValue);
                return (uint)(sa / sb);
            }
        }
    }

    private static ulong VRemSigned(ulong a, ulong b, int ewBytes) {
        switch (ewBytes) {
            case 1: {
                var sa = (sbyte)(byte)a;
                var sb = (sbyte)(byte)b;
                if (sb == 0) return a & 0xFFUL;
                if (sa == sbyte.MinValue && sb == -1) return 0;
                return (byte)(sa % sb);
            }
            case 2: {
                var sa = (short)(ushort)a;
                var sb = (short)(ushort)b;
                if (sb == 0) return a & 0xFFFFUL;
                if (sa == short.MinValue && sb == -1) return 0;
                return (ushort)(sa % sb);
            }
            default: {
                var sa = (int)(uint)a;
                var sb = (int)(uint)b;
                if (sb == 0) return a & 0xFFFFFFFFUL;
                if (sa == int.MinValue && sb == -1) return 0;
                return (uint)(sa % sb);
            }
        }
    }

    private static ExecuteResult ExecuteVRed(
        IArchState state,
        VRedOp op,
        int vd,
        int vs2,
        int vs1,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] vs1Data = VState(state).VectorRegisters.Read(vs1);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        ulong acc = ReadVElement(vs1Data, 0, ewBytes); // seed from vs1[0]

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            acc = ApplyVRedOp(op, acc, ReadVElement(vs2Data, i, ewBytes), ewBytes);
        }

        // Write result to element 0 of vd; all other elements are undefined (left zero).
        var result = new byte[VectorRegisterFile.VLenB];
        WriteVElement(result, 0, ewBytes, acc);
        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVWideRed(
        IArchState state,
        bool signed,
        int vd,
        int vs2,
        int vs1,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        int wBytes = ewBytes * 2;
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] vs1Data = VState(state).VectorRegisters.Read(vs1);
        byte[] maskData = VState(state).VectorRegisters.Read(0);
        ulong acc = ReadVElement(vs1Data, 0, wBytes); // seed from vs1[0] at 2×SEW

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((maskData[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong elem = ReadVElement(vs2Data, i, ewBytes);
            acc += signed ? (ulong)Sx(elem) : elem;
        }

        var result = new byte[VectorRegisterFile.VLenB];
        WriteVElement(result, 0, wBytes, acc);
        return VectorWrite(vd, result);

        long Sx(ulong v) => ewBytes switch {
            1 => (sbyte)(byte)v, 2 => (short)(ushort)v, _ => (int)(uint)v,
        };
    }

    private static ulong ApplyVRedOp(VRedOp op, ulong acc, ulong elem, int ewBytes) {
        ulong mask = (1UL << (ewBytes * 8)) - 1;
        ulong r = op switch {
            VRedOp.Sum  => acc + elem,
            VRedOp.And  => acc & elem,
            VRedOp.Or   => acc | elem,
            VRedOp.Xor  => acc ^ elem,
            VRedOp.Minu => (acc & mask) < (elem & mask) ? acc : elem,
            VRedOp.Maxu => (acc & mask) > (elem & mask) ? acc : elem,
            VRedOp.Min => ewBytes switch {
                1 => (sbyte)(byte)acc < (sbyte)(byte)elem ? acc : elem,
                2 => (short)(ushort)acc < (short)(ushort)elem ? acc : elem,
                _ => (int)(uint)acc < (int)(uint)elem ? acc : elem,
            },
            VRedOp.Max => ewBytes switch {
                1 => (sbyte)(byte)acc > (sbyte)(byte)elem ? acc : elem,
                2 => (short)(ushort)acc > (short)(ushort)elem ? acc : elem,
                _ => (int)(uint)acc > (int)(uint)elem ? acc : elem,
            },
            _ => acc,
        };
        return r & mask;
    }

    // vmv.x.s rd, vs2 — read element 0 of vs2 into integer rd (sign-extended to XLEN).
    private static ExecuteResult ExecuteVMvXs(IArchState state, int rd, int vs2) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (vl == 0 || rd == 0) return ExecuteResult.Clean;
        ulong elem = VReadElem(state, vs2, 0, ewBytes);
        ulong result = ewBytes switch {
            1 => (ulong)(sbyte)(byte)elem,
            2 => (ulong)(short)(ushort)elem,
            _ => elem,
        };
        return ExecuteResult.WithResult(result);
    }

    // vmacc/vnmsac: vd[i] = vd[i] ± vs2[i]*vs1[i]; vd is both source and destination.
    private static ExecuteResult ExecuteVIntMac(
        IArchState state,
        VIntMacOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, int, ulong> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] vdData = VState(state).VectorRegisters.Read(vd);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(vdData, result, vdData.Length);

        ulong elemMask = (1UL << (ewBytes * 8)) - 1;
        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong a = ReadVElement(vs2Data, i, ewBytes);
            ulong b = getB(i, ewBytes);
            ulong acc = ReadVElement(vdData, i, ewBytes);
            ulong r = op switch {
                VIntMacOp.Macc  => acc + a * b,
                VIntMacOp.Nmsac => acc - a * b,
                VIntMacOp.Madd  => a + acc * b,
                VIntMacOp.Nmsub => a - acc * b,
                _               => acc,
            };
            WriteVElement(result, i, ewBytes, r & elemMask);
        }

        return VectorWrite(vd, result);
    }

    // vmv.s.x: write integer register value into element 0 of vector register vd.
    private static ExecuteResult ExecuteVMvSx(IArchState state, int vd, ulong rs1Val) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (vl == 0) return ExecuteResult.Clean;
        byte[] current = VState(state).VectorRegisters.Read(vd);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(current, result, current.Length);
        WriteVElement(result, 0, ewBytes, rs1Val);
        return VectorWrite(vd, result);
    }

    // vmerge: for each active element, mask=1 → active source, mask=0 → vs2[i].
    private static ExecuteResult ExecuteVMerge(
        IArchState state,
        int vd,
        int vs2,
        Func<int, int, ulong> getActiveSrc
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            bool bit = ((mask[i >> 3] >> (i & 7)) & 1) == 1;
            ulong src = bit ? getActiveSrc(i, ewBytes) : ReadVElement(vs2Data, i, ewBytes);
            WriteVElement(result, i, ewBytes, src);
        }

        return VectorWrite(vd, result);
    }

    // ── UVE extension helpers ─────────────────────────────────────────────────

    private static Rv32ArchState UState(IArchState state) => (Rv32ArchState)state;

    // so.v.dp.(b/h/w) ud, rs1 — data pack: broadcast masked register value into u-reg lanes.
    // 32-bit width: broadcast to all 4 lanes, Vector mode. Sub-word: lane 0 only, Scalar mode.
    private static ExecuteResult ExecuteUveSoVDp(IRegisterFile regs, int ud, int rs1, int elemBytes) {
        uint mask = elemBytes switch { 1 => 0xFFu, 2 => 0xFFFFu, _ => 0xFFFFFFFFu, };
        uint bits = (uint)regs.Read(rs1) & mask;
        int lanes = elemBytes >= 4 ? 4 : 1;
        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                for (var i = 0; i < lanes; i++) u.SetLane32(ud, i, bits);
                u.RegMode[ud] = lanes > 1 ? UveRegMode.Vector : UveRegMode.Scalar;
                u.ValidElements[ud] = lanes;
                u.RegKind[ud] = UveRegKind.Scalar;
            },
        };
    }

    private static ExecuteResult ExecuteUveSoVMvvs(IArchState state, int rd, int us1) {
        uint bits = UState(state).UveState.GetLane32(us1, 0);
        return new ExecuteResult {
            SideEffect = s => UState(s).IntegerRegisters.Write(rd, bits),
        };
    }

    private static ExecuteResult ExecuteUveSoVMvsv(IRegisterFile regs, int ud, int rs1, int elemBytes) {
        uint mask = elemBytes switch { 1 => 0xFFu, 2 => 0xFFFFu, _ => 0xFFFFFFFFu, };
        uint bits = (uint)regs.Read(rs1) & mask;
        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                u.SetLane32(ud, 0, bits);
                u.RegMode[ud] = UveRegMode.Scalar;
                u.ValidElements[ud] = 1;
                u.RegKind[ud] = UveRegKind.Scalar;
            },
        };
    }

    // so.a.fp ud, usrc1, usrc2 — element-wise FP arithmetic, per lane.
    // Both scalar sources → vLen=1 (scalar result). Both vector → vLen=4.
    private static ExecuteResult ExecuteUveSoAFp(
        IArchState state,
        IMemory memory,
        UveFpOp op,
        int ud,
        int usrc1,
        int usrc2,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        (int vLen, bool zeroing) = UveLaneParams(uveState, usrc1, usrc2);
        var results = new uint[vLen];
        for (var i = 0; i < vLen; i++) {
            float a = BitConverter.Int32BitsToSingle((int)uveState.GetLane32(usrc1, i));
            float b = usrc2 >= 0 ? BitConverter.Int32BitsToSingle((int)uveState.GetLane32(usrc2, i)) : 0f;
            float acc = BitConverter.Int32BitsToSingle((int)uveState.GetLane32(ud, i));
            float r = op switch {
                UveFpOp.Mul     => a * b,
                UveFpOp.Add     => a + b,
                UveFpOp.Mac     => acc + a * b,
                UveFpOp.Sub     => a - b,
                UveFpOp.Div     => a / b,
                UveFpOp.Min     => MathF.Min(a, b),
                UveFpOp.Max     => MathF.Max(a, b),
                UveFpOp.Abs     => MathF.Abs(a),
                UveFpOp.Inc     => a + 1f,
                UveFpOp.Dec     => a - 1f,
                UveFpOp.Sqrt    => MathF.Sqrt(a),
                UveFpOp.Adde    => a,
                UveFpOp.AddeAcc => acc + a,
                UveFpOp.Mine    => MathF.Min(acc, a),
                UveFpOp.Maxe    => MathF.Max(acc, a),
                _               => throw new InvalidOperationException($"Unknown UveFpOp {op}"),
            };
            results[i] = (uint)BitConverter.SingleToInt32Bits(r);
        }

        return UveWriteResult(state, memory, ud, results, vLen, zeroing, ps3);
    }

    private static ExecuteResult ExecuteUveSoAInt(
        IArchState state,
        IMemory memory,
        UveIntOp op,
        bool signed,
        int ud,
        int usrc1,
        int usrc2,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        (int vLen, bool zeroing) = UveLaneParams(uveState, usrc1, usrc2);
        var results = new uint[vLen];
        for (var i = 0; i < vLen; i++) {
            uint rawA = uveState.GetLane32(usrc1, i);
            uint rawB = usrc2 >= 0 ? uveState.GetLane32(usrc2, i) : 0u;
            uint rawAcc = uveState.GetLane32(ud, i);
            uint r;
            if (signed) {
                int a = (int)rawA, b = (int)rawB, acc = (int)rawAcc;
                r = (uint)(op switch {
                    UveIntOp.Add     => a + b,
                    UveIntOp.Sub     => a - b,
                    UveIntOp.Mul     => a * b,
                    UveIntOp.Div     => a / b,
                    UveIntOp.Mac     => acc + a * b,
                    UveIntOp.Min     => Math.Min(a, b),
                    UveIntOp.Max     => Math.Max(a, b),
                    UveIntOp.Abs     => Math.Abs(a),
                    UveIntOp.Inc     => a + 1,
                    UveIntOp.Dec     => a - 1,
                    UveIntOp.Adde    => a,
                    UveIntOp.AddeAcc => acc + a,
                    UveIntOp.Mine    => Math.Min(acc, a),
                    UveIntOp.Maxe    => Math.Max(acc, a),
                    _                => throw new InvalidOperationException($"Unknown UveIntOp {op}"),
                });
            }
            else {
                r = op switch {
                    UveIntOp.Add     => rawA + rawB,
                    UveIntOp.Sub     => rawA - rawB,
                    UveIntOp.Mul     => rawA * rawB,
                    UveIntOp.Div     => rawA / rawB,
                    UveIntOp.Mac     => rawAcc + rawA * rawB,
                    UveIntOp.Min     => Math.Min(rawA, rawB),
                    UveIntOp.Max     => Math.Max(rawA, rawB),
                    UveIntOp.Abs     => rawA,
                    UveIntOp.Inc     => rawA + 1u,
                    UveIntOp.Dec     => rawA - 1u,
                    UveIntOp.Adde    => rawA,
                    UveIntOp.AddeAcc => rawAcc + rawA,
                    UveIntOp.Mine    => Math.Min(rawAcc, rawA),
                    UveIntOp.Maxe    => Math.Max(rawAcc, rawA),
                    _                => throw new InvalidOperationException($"Unknown UveIntOp {op}"),
                };
            }

            results[i] = r;
        }

        return UveWriteResult(state, memory, ud, results, vLen, zeroing, ps3);
    }

    private static ExecuteResult ExecuteUveSoALogic(
        IArchState state,
        IMemory memory,
        UveLogicOp op,
        int ud,
        int usrc1,
        int usrc2,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        (int vLen, bool zeroing) = UveLaneParams(uveState, usrc1, usrc2);
        var results = new uint[vLen];
        for (var i = 0; i < vLen; i++) {
            uint a = uveState.GetLane32(usrc1, i);
            uint b = usrc2 >= 0 ? uveState.GetLane32(usrc2, i) : 0u;
            results[i] = op switch {
                UveLogicOp.Nand => ~(a & b),
                UveLogicOp.And  => a & b,
                UveLogicOp.Nor  => ~(a | b),
                UveLogicOp.Or   => a | b,
                UveLogicOp.Not  => ~a,
                UveLogicOp.Xor  => a ^ b,
                _               => throw new InvalidOperationException($"Unknown UveLogicOp {op}"),
            };
        }

        return UveWriteResult(state, memory, ud, results, vLen, zeroing, ps3);
    }

    private static ExecuteResult ExecuteUveSoAShiftV(
        IArchState state,
        IMemory memory,
        UveShiftOp op,
        int ud,
        int usrc1,
        int usrc2,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        (int vLen, bool zeroing) = UveLaneParams(uveState, usrc1, usrc2);
        var results = new uint[vLen];
        for (var i = 0; i < vLen; i++) {
            uint a = uveState.GetLane32(usrc1, i);
            var shamt = (int)(uveState.GetLane32(usrc2, i) & 0x1F);
            results[i] = op switch {
                UveShiftOp.Sll => a << shamt,
                UveShiftOp.Srl => a >> shamt,
                UveShiftOp.Sra => (uint)((int)a >> shamt),
                _              => throw new InvalidOperationException($"Unknown UveShiftOp {op}"),
            };
        }

        return UveWriteResult(state, memory, ud, results, vLen, zeroing, ps3);
    }

    private static ExecuteResult ExecuteUveSoAShiftS(
        IArchState state,
        IMemory memory,
        IRegisterFile regs,
        UveShiftOp op,
        int ud,
        int usrc1,
        int rs2,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        (int vLen, bool zeroing) = UveLaneParams(uveState, usrc1, -1);
        var shamt = (int)(regs.Read(rs2) & 0x1F);
        var results = new uint[vLen];
        for (var i = 0; i < vLen; i++) {
            uint a = uveState.GetLane32(usrc1, i);
            results[i] = op switch {
                UveShiftOp.Sll => a << shamt,
                UveShiftOp.Srl => a >> shamt,
                UveShiftOp.Sra => (uint)((int)a >> shamt),
                _              => throw new InvalidOperationException($"Unknown UveShiftOp {op}"),
            };
        }

        return UveWriteResult(state, memory, ud, results, vLen, zeroing, ps3);
    }

    private static ExecuteResult ExecuteUveSoASadde(
        IArchState state,
        IRegisterFile regs,
        bool isFp,
        bool acc,
        int rd,
        int usrc1,
        int ps3
    ) {
        UveState uveState = UState(state).UveState;
        int vLen = uveState.ValidElements[usrc1] > 0 ? uveState.ValidElements[usrc1] : 1;
        bool[] predReg = uveState.PredicateRegs[ps3];
        // predicate byte for element i (float32 = 4 bytes/element): (i+1)*4-1
        const int elemBytesInPred = 4;
        if (isFp) {
            var sum = 0f;
            for (var i = 0; i < vLen; i++)
                if (predReg[(i + 1) * elemBytesInPred - 1])
                    sum += BitConverter.Int32BitsToSingle((int)uveState.GetLane32(usrc1, i));
            float result = acc ? FBits(regs, rd) + sum : sum;
            ulong nanBoxed = 0xFFFFFFFF00000000UL | (uint)BitConverter.SingleToInt32Bits(result);
            return new ExecuteResult { SideEffect = s => { UState(s).IntegerRegisters.Write(rd, nanBoxed); }, };
        }
        else {
            var sum = 0;
            for (var i = 0; i < vLen; i++)
                if (predReg[(i + 1) * elemBytesInPred - 1])
                    sum += (int)uveState.GetLane32(usrc1, i);
            int result = acc ? (int)(uint)regs.Read(rd) + sum : sum;
            return new ExecuteResult { SideEffect = s => { UState(s).IntegerRegisters.Write(rd, (uint)result); }, };
        }
    }

    // SO_C: stream lifecycle — stop / suspend / resume.
    private static ExecuteResult ExecuteUveSoCBreak(int ud) =>
        new() {
            SideEffect = s => {
                UveState uvs = UState(s).UveState;
                uvs.StoreStreams[ud] = null;
                uvs.RegKind[ud] = UveRegKind.None;
                uvs.StreamDone[ud] = true;
                uvs.Suspended[ud] = false;
            },
        };

    private static ExecuteResult ExecuteUveSoCSuspd(int ud) =>
        new() { SideEffect = s => { UState(s).UveState.Suspended[ud] = true; }, };

    private static ExecuteResult ExecuteUveSoCResum(int ud) =>
        new() { SideEffect = s => { UState(s).UveState.Suspended[ud] = false; }, };

    // SO_C: vector-length control — getvl / setvl.
    private static ExecuteResult ExecuteUveSoCGetvl(IArchState state, int rd) {
        int vl = UState(state).UveState.VectorLength;
        return new ExecuteResult { SideEffect = s => { UState(s).IntegerRegisters.Write(rd, (uint)vl); }, };
    }

    private static ExecuteResult ExecuteUveSoCSetvl(IArchState state, IRegisterFile regs, int rd, int rs1) {
        int oldVl = UState(state).UveState.VectorLength;
        var newVl = (int)(uint)regs.Read(rs1);
        return new ExecuteResult {
            SideEffect = s => {
                UState(s).UveState.VectorLength = newVl;
                UState(s).IntegerRegisters.Write(rd, (uint)oldVl);
            },
        };
    }

    // Returns (vLen, zeroing).
    // vLen: 1 if either source is Scalar; otherwise the min of sources' ValidElements counts
    //       (0 → 4 as safe default). This makes the out-of-range zeroing/merging loop in
    //       UveWriteResult actually fire when VL < VLEN/ew (e.g., after ss.setvl).
    // zeroing: true when no source register has pm=1 (merging), so lanes beyond vLen are zeroed.
    private static (int vLen, bool zeroing) UveLaneParams(UveState u, int usrc1, int usrc2) {
        bool s1Scalar = u.RegMode[usrc1] == UveRegMode.Scalar;
        bool s2Scalar = usrc2 < 0 || u.RegMode[usrc2] == UveRegMode.Scalar;
        int vLen;
        if (s1Scalar || s2Scalar) { vLen = 1; }
        else {
            int v1 = u.ValidElements[usrc1] > 0 ? u.ValidElements[usrc1] : 4;
            int v2 = usrc2 < 0 ? v1 : u.ValidElements[usrc2] > 0 ? u.ValidElements[usrc2] : 4;
            vLen = Math.Min(v1, v2);
        }

        bool zeroing = !u.RegMerging[usrc1] && (usrc2 < 0 || !u.RegMerging[usrc2]);
        return (vLen, zeroing);
    }

    // Shared write-back for so.a.* ops: writes vLen lane results to store stream or u-register.
    // ps3 selects the governing predicate register (p0 = all-ones = all lanes active).
    // Per Spike semantics: predicate-inactive lanes always merge (keep existing dest value);
    // zeroing the flag only applies to lanes beyond vLen (controlled by stream pm).
    private static ExecuteResult UveWriteResult(
        IArchState state,
        IMemory memory,
        int ud,
        uint[] results,
        int vLen,
        bool zeroing,
        int ps3
    ) {
        // Predicate byte for lane i (float32 = 4 bytes/element): (i+1)*4-1 = i*4+3
        const int elemBytesInPred = 4;
        UveState uveState = UState(state).UveState;
        if (uveState.RegKind[ud] == UveRegKind.StoreStream && uveState.StoreStreams[ud] is { } ss) {
            int ewBytes = ss.ElementBytes;
            bool[] predReg = uveState.PredicateRegs[ps3];
            for (var i = 0; i < vLen; i++) {
                if (predReg[(i + 1) * elemBytesInPred - 1]) memory.Write(ss.CurrentAddress, results[i], ewBytes);
                ss.Advance(); // always advance stream position, even for inactive lanes
            }

            return new ExecuteResult {
                SideEffect = s => {
                    UveState uvs = UState(s).UveState;
                    bool[] pr = uvs.PredicateRegs[ps3];
                    for (var i = 0; i < vLen; i++)
                        if (pr[(i + 1) * elemBytesInPred - 1])
                            uvs.SetLane32(ud, i, results[i]);
                    uvs.RegMode[ud] = vLen == 1 ? UveRegMode.Scalar : UveRegMode.Vector;
                    uvs.ValidElements[ud] = vLen;
                },
            };
        }

        const int maxLanes = 4;
        return new ExecuteResult {
            SideEffect = s => {
                UveState uvs = UState(s).UveState;
                bool[] pr = uvs.PredicateRegs[ps3];
                for (var i = 0; i < vLen; i++)
                    if (pr[(i + 1) * elemBytesInPred - 1])
                        uvs.SetLane32(ud, i, results[i]);
                // predicate-inactive lanes: merging — no write, existing value preserved
                if (zeroing)
                    for (int i = vLen; i < maxLanes; i++)
                        uvs.SetLane32(ud, i, 0);
                uvs.RegMode[ud] = vLen == 1 ? UveRegMode.Scalar : UveRegMode.Vector;
                uvs.ValidElements[ud] = vLen;
            },
        };
    }

    // sb.nc urs, imm — branch (PC += imm) while stream urs is not exhausted
    // The pipeline has already synced the exhaustion state into UveState via IUveScalars.
    private static ExecuteResult ExecuteUveSoBNc(IArchState state, ulong pc, int urs, int imm) {
        bool done = UState(state).UveState.StreamDone[urs];
        return !done
            ? new ExecuteResult { BranchTaken = true, BranchTarget = pc + (ulong)imm, }
            : new ExecuteResult { BranchTaken = false, BranchTarget = pc + 4, };
    }

    // sb.c urs, imm — branch when stream urs IS exhausted (complete polarity of sb.nc)
    private static ExecuteResult ExecuteUveSoBc(IArchState state, ulong pc, int urs, int imm) {
        bool done = UState(state).UveState.StreamDone[urs];
        return done
            ? new ExecuteResult { BranchTaken = true, BranchTarget = pc + (ulong)imm, }
            : new ExecuteResult { BranchTaken = false, BranchTarget = pc + 4, };
    }

    // ss.sta.{ld|st}.* — start multi-dim stream configuration. Sets base and element width; no dimension added.
    private static ExecuteResult ExecuteUveSsSta(
        IRegisterFile regs,
        int ud,
        int rs1,
        bool isLoad,
        int ew,
        bool isVec = false,
        int vecCfgDim = -1,
        bool mergingPredication = false
    ) {
        ulong baseAddr = regs.Read(rs1);
        return new ExecuteResult {
            SideEffect = s => {
                UveState uvs = UState(s).UveState;
                uvs.PendingConfig[ud] = new PendingStreamConfig {
                    BaseAddress = baseAddr, ElementBytes = ew, IsLoad = isLoad,
                    IsVector = isVec, VecCfgDim = vecCfgDim, MergingPredication = mergingPredication,
                };
            },
        };
    }

    // ss.sta.ld.*_inds ud, rs1 — begin IndSource stream configuration (provides values for indirect modifiers).
    private static ExecuteResult ExecuteUveSsStaLdWInds(IRegisterFile regs, int ud, int rs1, int ew) {
        ulong baseAddr = regs.Read(rs1);
        return new ExecuteResult {
            SideEffect = s => {
                UveState uvs = UState(s).UveState;
                uvs.PendingConfig[ud] = new PendingStreamConfig {
                    BaseAddress = baseAddr, ElementBytes = ew, IsLoad = true, IsIndSource = true,
                };
            },
        };
    }

    // ss.app.ind ud, rs1_indsrc — append one indirect modifier to the pending stream config.
    // The trigger is positional: the most recently appended dimension at execute time (Spike
    // keys modifiers to dimensions.size()-1). Pending modifiers hold SPIKE (outermost-first)
    // indices in TriggerDim/TargetDim; ExecuteUveSsEnd remaps both to engine order.
    private static ExecuteResult ExecuteUveSsAppInd(
        int ud,
        int targetDimRaw,
        StreamModifierTarget target,
        StreamModifierBehavior behavior,
        int sourceStreamId
    ) {
        return new ExecuteResult {
            SideEffect = s => {
                PendingStreamConfig? cfg = UState(s).UveState.PendingConfig[ud];
                if (cfg is null || cfg.Dimensions.Count == 0) return;
                int spikeTrigger = cfg.Dimensions.Count - 1;
                int spikeTarget = targetDimRaw == 7 ? spikeTrigger + 1 : targetDimRaw;
                cfg.Modifiers.Add(new StreamModifier(spikeTrigger, spikeTarget, target, behavior, 0, sourceStreamId));
            },
        };
    }

    // ss.app ud, rs1_offset, rs2_count, rs3_stride — append the next dimension to the pending config.
    // Dimensions are configured OUTERMOST-FIRST (Spike: dimensions.push_back, dimensions.back() = innermost);
    // ss.end appends the innermost dimension. The list is reversed into the engine's innermost-first
    // order when the stream is activated.
    // rs1_offset adds offset*ew to the stream base address (accumulated into PendingStreamConfig.OffsetBytes).
    // rs3_stride is an element count (Spike/RTL convention); scaled to bytes here before storing.
    private static ExecuteResult ExecuteUveSsApp(IRegisterFile regs, int ud, int rs1, int rs2, int rs3) {
        var offset = (long)regs.Read(rs1);
        var count = (long)regs.Read(rs2);
        var stride = (long)regs.Read(rs3);
        return new ExecuteResult {
            SideEffect = s => {
                PendingStreamConfig? cfg = UState(s).UveState.PendingConfig[ud];
                if (cfg is null) return;
                cfg.OffsetBytes += offset * cfg.ElementBytes;
                cfg.Dimensions.Add(new StreamDimension(count, stride * cfg.ElementBytes));
            },
        };
    }

    // ss.end ud, rs1_offset, rs2_count, rs3_stride — innermost dimension + activate stream.
    // Config order is outermost-first (Spike convention), so the accumulated dimension list is
    // reversed into the engine's innermost-first order here. Modifier DimIndex values (ss.app.mod,
    // ss.app.ind) and explicit vecCfgDim are encoded outermost-first and remapped the same way.
    // rs1_offset adds offset*ew to the stream base address (combined with any prior ss.app offsets).
    // rs3_stride is an element count (Spike/RTL convention); scaled to bytes here before storing.
    private static ExecuteResult ExecuteUveSsEnd(
        IArchState state,
        IRegisterFile regs,
        int ud,
        int rs1,
        int rs2,
        int rs3
    ) {
        var offset = (long)regs.Read(rs1);
        var count = (long)regs.Read(rs2);
        var stride = (long)regs.Read(rs3);
        UveState uveState = UState(state).UveState;
        PendingStreamConfig? pending = uveState.PendingConfig[ud];
        if (pending is null) return ExecuteResult.Clean;

        long totalOffsetBytes = pending.OffsetBytes + offset * pending.ElementBytes;
        var baseAddr = (ulong)((long)pending.BaseAddress + totalOffsetBytes);
        StreamDimension[] dims = pending.Dimensions.Append(new StreamDimension(count, stride * pending.ElementBytes))
                                        .Reverse().ToArray();
        return BuildAndActivatePendingStream(ud, pending, baseAddr, dims);
    }

    // ss.app.sgi ud, rs1_indsrc — attach scatter-gather modifier to the pending config.
    // Fires per element, always targeting Offset of dimension 0; encoded in PendingStreamConfig.SgiMod.
    private static ExecuteResult ExecuteUveSsAppSgi(int ud, int srcId, StreamModifierBehavior behavior) {
        return new ExecuteResult {
            SideEffect = s => {
                PendingStreamConfig? cfg = UState(s).UveState.PendingConfig[ud];
                if (cfg is null) return;
                cfg.SgiMod = (srcId, behavior);
            },
        };
    }

    // ss.end.sgi ud, rs1_indsrc — attach scatter-gather modifier + activate stream.
    // Does NOT add a new dimension; existing dimensions from prior ss.app instructions are used.
    private static ExecuteResult ExecuteUveSsEndSgi(
        IArchState state,
        int ud,
        int srcId,
        StreamModifierBehavior behavior
    ) {
        UveState uveState = UState(state).UveState;
        PendingStreamConfig? pending = uveState.PendingConfig[ud];
        if (pending is null || pending.Dimensions.Count == 0) return ExecuteResult.Clean;

        pending.SgiMod = (srcId, behavior);
        var baseAddr = (ulong)((long)pending.BaseAddress + pending.OffsetBytes);
        StreamDimension[] dims = ((IEnumerable<StreamDimension>)pending.Dimensions).Reverse().ToArray();
        return BuildAndActivatePendingStream(ud, pending, baseAddr, dims);
    }

    // Shared activation helper: builds a StreamDescriptor from pending config + already-reversed dims
    // and returns the appropriate ExecuteResult (load/IndSource → StreamConfig; store → SideEffect).
    private static ExecuteResult BuildAndActivatePendingStream(
        int ud,
        PendingStreamConfig pending,
        ulong baseAddr,
        StreamDimension[] dims
    ) {
        int ndim = dims.Length;

        // Remap modifier dims and explicit vecCfgDim from Spike (outermost=0) to engine (innermost=0).
        // Pending TriggerDim holds the Spike deque index K of the dimension the modifier was appended
        // after; that dimension ADVANCES when its inner neighbor (deque K+1) wraps, so the engine
        // trigger is ndim-2-K. TargetDim is a plain index remapping.
        StreamModifier[]? mods = null;
        if (pending.Modifiers.Count > 0)
            mods = pending.Modifiers
                          .Select(m => m with {
                                   TriggerDim = ndim - 2 - m.TriggerDim, TargetDim = ndim - 1 - m.TargetDim,
                               }
                           )
                          .ToArray();
        int vecCfgDim = pending.VecCfgDim >= 0 ? ndim - 1 - pending.VecCfgDim : -1;

        var descriptor = new StreamDescriptor(
            baseAddr, pending.ElementBytes, dims, mods, pending.IsVector, vecCfgDim, pending.MergingPredication,
            pending.SgiMod
        );
        bool isLoad = pending.IsLoad;
        bool isIndSource = pending.IsIndSource;

        if (isLoad || isIndSource) {
            UveRegKind regKind = isIndSource ? UveRegKind.IndSource : UveRegKind.LoadStream;
            return new ExecuteResult {
                StreamConfig = (ud, descriptor),
                SideEffect = s => {
                    UveState uvs = UState(s).UveState;
                    uvs.PendingConfig[ud] = null;
                    uvs.RegKind[ud] = regKind;
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

    // ss.app.mod ud, tdim, target, behavior, rs3Disp — append static modifier (UVE2).
    // Trigger is positional (the most recently appended dimension); tdim is the target dimension
    // in Spike outermost-first order (7 = "linked" → the dimension configured right after the
    // trigger). Pending modifiers hold Spike indices; ExecuteUveSsEnd remaps to engine order.
    private static ExecuteResult ExecuteUveSsAppMod(
        IRegisterFile regs,
        int ud,
        int targetDimRaw,
        StreamModifierTarget target,
        StreamModifierBehavior behavior,
        int rs3Disp
    ) {
        var disp = (long)regs.Read(rs3Disp);
        return new ExecuteResult {
            SideEffect = s => {
                PendingStreamConfig? cfg = UState(s).UveState.PendingConfig[ud];
                if (cfg is null || cfg.Dimensions.Count == 0) return;
                int spikeTrigger = cfg.Dimensions.Count - 1;
                int spikeTarget = targetDimRaw == 7 ? spikeTrigger + 1 : targetDimRaw;
                // Stride displacement is an element count; scale to bytes for the engine.
                // Offset displacement stays as element count (engine scales internally).
                // Size displacement is already a count; no scaling.
                long scaledDisp = target == StreamModifierTarget.Stride ? disp * cfg.ElementBytes : disp;
                cfg.Modifiers.Add(new StreamModifier(spikeTrigger, spikeTarget, target, behavior, scaledDisp));
            },
        };
    }

    // sb.ndc.D urs, imm — branch while the dimension has not completed its pass.
    // dim = funct3 = D-1, counting from the OUTERMOST dimension (Spike convention);
    // the pipeline has already remapped and synced the flag into UveState.DimDone[urs, dim].
    private static ExecuteResult ExecuteUveSoBNdc(IArchState state, ulong pc, int urs, int dim, int imm) {
        bool done = UState(state).UveState.DimDone[urs, dim];
        return !done
            ? new ExecuteResult { BranchTaken = true, BranchTarget = pc + (ulong)imm, }
            : new ExecuteResult { BranchTaken = false, BranchTarget = pc + 4, };
    }

    // sb.dc.D urs, imm — branch when dimension D IS complete (complete polarity of sb.ndc)
    private static ExecuteResult ExecuteUveSoBdc(IArchState state, ulong pc, int urs, int dim, int imm) {
        bool done = UState(state).UveState.DimDone[urs, dim];
        return done
            ? new ExecuteResult { BranchTaken = true, BranchTarget = pc + (ulong)imm, }
            : new ExecuteResult { BranchTaken = false, BranchTarget = pc + 4, };
    }

    // ── SO_P predicate register operations ───────────────────────────────────

    // so.p.{zero,one,vr,not,mv,mvt} pd, ... — manipulate predicate register pd.
    // GovPred[i]=true → apply operation; GovPred[i]=false → zeroing ? 0 : keep old.
    private static ExecuteResult ExecuteUveSoPSimple(
        IArchState state,
        UveSoPSimpleOp op,
        int pd,
        int govPred,
        bool zeroing,
        int ps1
    ) {
        UveState uvs = UState(state).UveState;
        // Capture the valid element count for Vr before the closure.
        int validCount = uvs.VectorLength > 0 ? uvs.VectorLength : UveState.PredBytes;
        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                bool[] gov = u.PredicateRegs[govPred];
                bool[] dst = u.PredicateRegs[pd];
                bool[] src = ps1 >= 0 ? u.PredicateRegs[ps1] : [];
                for (var i = 0; i < UveState.PredBytes; i++) {
                    if (!gov[i]) {
                        if (zeroing) dst[i] = false;
                        continue;
                    }

                    dst[i] = op switch {
                        UveSoPSimpleOp.Zero => false,
                        UveSoPSimpleOp.One  => true,
                        UveSoPSimpleOp.Vr   => i < validCount,
                        UveSoPSimpleOp.Not  => !src[i],
                        UveSoPSimpleOp.Mv   => src[i],
                        UveSoPSimpleOp.Mvt  => src[UveState.PredBytes - 1 - i],
                        _                   => throw new InvalidOperationException($"Unknown UveSoPSimpleOp {op}"),
                    };
                }
            },
        };
    }

    // so.p.{ge,eq,lt}.{us,fp,sg}[.z] pd, vs1, vs2 — element-wise comparison into predicate register.
    // Scalar sources → uniform result broadcast to all governed bytes.
    // Both vector → per-lane result (4 bytes per lane for float32 width).
    // zeroing=true (_z variant): sets PredZeroing[pd]=true — tags the output register Zeroing.
    private static ExecuteResult ExecuteUveSoPCmp(
        IArchState state,
        UveSoPCmpOp op,
        UveSoPCmpType cmpType,
        int pd,
        int govPred,
        int vs1,
        int vs2,
        bool zeroing
    ) {
        UveState uvs = UState(state).UveState;
        bool isVector = uvs.RegMode[vs1] == UveRegMode.Vector && uvs.RegMode[vs2] == UveRegMode.Vector;
        int vLen = isVector
            ? Math.Max(
                uvs.ValidElements[vs1] > 0 ? uvs.ValidElements[vs1] : 1,
                uvs.ValidElements[vs2] > 0 ? uvs.ValidElements[vs2] : 1
            )
            : 1;
        var laneResults = new bool[vLen];
        for (var i = 0; i < vLen; i++) {
            uint rawA = uvs.GetLane32(vs1, i);
            uint rawB = uvs.GetLane32(vs2, i);
            float fa = BitConverter.Int32BitsToSingle((int)rawA);
            float fb = BitConverter.Int32BitsToSingle((int)rawB);
            laneResults[i] = (op, cmpType) switch {
                (UveSoPCmpOp.Ge, UveSoPCmpType.Us) => rawA >= rawB,
                (UveSoPCmpOp.Ge, UveSoPCmpType.Sg) => (int)rawA >= (int)rawB,
                (UveSoPCmpOp.Ge, UveSoPCmpType.Fp) => fa >= fb,
                (UveSoPCmpOp.Eq, UveSoPCmpType.Us) => rawA == rawB,
                (UveSoPCmpOp.Eq, UveSoPCmpType.Sg) => (int)rawA == (int)rawB,
                (UveSoPCmpOp.Eq, UveSoPCmpType.Fp) => fa == fb,
                (UveSoPCmpOp.Lt, UveSoPCmpType.Us) => rawA < rawB,
                (UveSoPCmpOp.Lt, UveSoPCmpType.Sg) => (int)rawA < (int)rawB,
                (UveSoPCmpOp.Lt, UveSoPCmpType.Fp) => fa < fb,
                _ => throw new InvalidOperationException($"Unknown SO_P comparison {op}/{cmpType}"),
            };
        }

        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                bool[] gov = u.PredicateRegs[govPred];
                bool[] dst = u.PredicateRegs[pd];
                if (!isVector) {
                    bool r = laneResults[0];
                    for (var i = 0; i < UveState.PredBytes; i++)
                        if (gov[i])
                            dst[i] = r;
                }
                else {
                    for (var i = 0; i < vLen; i++) {
                        int predBase = i * 4;
                        int repIdx = predBase + 3;
                        if (repIdx < UveState.PredBytes && gov[repIdx]) {
                            bool r = laneResults[i];
                            for (int k = predBase; k <= repIdx; k++) dst[k] = r;
                        }
                    }
                }

                u.PredZeroing[pd] = zeroing;
            },
        };
    }

    // so.v.mv/mvt vd, vs1, pred — copy vs1 into vd where predicate PredIdx is active (merging).
    // Transpose variant (mvt) reverses the active range.
    private static ExecuteResult ExecuteUveSoVMv(IArchState state, bool transpose, int vd, int vs1, int predIdx) {
        UveState uvs = UState(state).UveState;
        bool isVector = uvs.RegMode[vs1] == UveRegMode.Vector;
        int vLen = isVector ? uvs.ValidElements[vs1] > 0 ? uvs.ValidElements[vs1] : 1 : 1;
        var srcLanes = new uint[vLen];
        for (var i = 0; i < vLen; i++) srcLanes[i] = uvs.GetLane32(vs1, i);
        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                bool[] pred = u.PredicateRegs[predIdx];
                if (!isVector) {
                    int checkIdx = transpose ? UveState.PredBytes - 1 : 0;
                    if (pred[checkIdx]) {
                        u.SetLane32(vd, 0, srcLanes[0]);
                        u.RegMode[vd] = UveRegMode.Scalar;
                        u.ValidElements[vd] = 1;
                        u.RegKind[vd] = UveRegKind.Scalar;
                    }
                }
                else {
                    for (var i = 0; i < vLen; i++) {
                        int predByte = transpose ? UveState.PredBytes - 1 - (i * 4 + 3) : i * 4 + 3;
                        if (predByte is >= 0 and < UveState.PredBytes && pred[predByte])
                            u.SetLane32(vd, i, srcLanes[i]);
                    }
                }
            },
        };
    }

    // so.p.cv.<srcW>.<destW>[.z] pd, ps1 — predicate register width conversion.
    // Maps the active bit for each element from the source width slot to the destination width slot.
    // Horologium active-bit position for element i of width W: (i+1)*W - 1 (MSByte).
    // nElems = PredBytes / max(srcBytes, destBytes): elements that fit in both widths.
    private static ExecuteResult ExecuteUveSoPCv(
        IArchState state,
        int pd,
        int ps1,
        int srcBytes,
        int destBytes,
        bool zeroing
    ) {
        UveState uvs = UState(state).UveState;
        bool[] src = uvs.PredicateRegs[ps1];
        int nElems = UveState.PredBytes / Math.Max(srcBytes, destBytes);
        var destPred = new bool[UveState.PredBytes];
        for (var i = 0; i < nElems; i++) destPred[(i + 1) * destBytes - 1] = src[(i + 1) * srcBytes - 1];
        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                Array.Copy(destPred, u.PredicateRegs[pd], UveState.PredBytes);
                u.PredZeroing[pd] = zeroing;
            },
        };
    }

    // so.v.cv.{fp,sg,us}.<destW> vd, vs1 — vector u-register element type conversion.
    // Reads ValidElements[vs1] lanes from vs1 (interpreting each as RegElemBytes[vs1]-wide),
    // converts to destBytes width, and writes to vd.
    // isFp=true: floating-point cast; isSigned=true: sign-extend; else zero-extend.
    private static ExecuteResult ExecuteUveSoVCv(
        IArchState state,
        int vd,
        int vs1,
        int destBytes,
        bool isFp,
        bool isSigned
    ) {
        UveState uvs = UState(state).UveState;
        int srcBytes = uvs.RegElemBytes[vs1] > 0 ? uvs.RegElemBytes[vs1] : 4;
        int srcValid = Math.Max(1, uvs.ValidElements[vs1]);
        int finalCount = Math.Min(4, srcValid);
        var converted = new uint[finalCount];
        for (var i = 0; i < finalCount; i++) {
            uint raw = uvs.GetLane32(vs1, i);
            converted[i] = isFp
                ? ConvertFpLane(raw, srcBytes, destBytes)
                : ConvertIntLane(raw, srcBytes, destBytes, isSigned);
        }

        return new ExecuteResult {
            SideEffect = s => {
                UveState u = UState(s).UveState;
                for (var i = 0; i < finalCount; i++) u.SetLane32(vd, i, converted[i]);
                u.RegMode[vd] = uvs.RegMode[vs1];
                u.ValidElements[vd] = finalCount;
                u.RegElemBytes[vd] = destBytes;
            },
        };
    }

    // Mask raw to srcBytes width, then sign- or zero-extend to destBytes width stored as uint32.
    private static uint ConvertIntLane(uint raw, int srcBytes, int destBytes, bool signed) {
        uint mask = srcBytes >= 4 ? uint.MaxValue : (1u << (srcBytes * 8)) - 1u;
        uint narrow = raw & mask;
        if (signed && srcBytes < 4) {
            uint signBit = 1u << (srcBytes * 8 - 1);
            if ((narrow & signBit) != 0) narrow |= ~mask;
        }

        // Truncate to destBytes width before storing as uint32.
        uint destMask = destBytes >= 4 ? uint.MaxValue : (1u << (destBytes * 8)) - 1u;
        return narrow & destMask;
    }

    // Floating-point conversion between lane widths (stored as uint32 bits).
    // Only the combinations meaningful for a 32-bit lane model are handled:
    // fp.h (float32→float16), fp.w (float32→float32 = identity), others default to identity.
    private static uint ConvertFpLane(uint raw, int srcBytes, int destBytes) {
        switch (srcBytes) {
            case 4 when destBytes == 2: {
                float f = BitConverter.Int32BitsToSingle((int)raw);
                var h = (Half)f;
                return BitConverter.HalfToUInt16Bits(h);
            }
            case 2 when destBytes == 4: {
                Half h = BitConverter.UInt16BitsToHalf((ushort)raw);
                return (uint)BitConverter.SingleToInt32Bits((float)h);
            }
            default: return raw; // identity for matching widths or unsupported combos
        }
    }

    // ── FP vector helpers ─────────────────────────────────────────────────────

    // Read element i of a vector register as a float32 (bit-exact).
    private static float VFpElem(IArchState state, int vreg, int i) {
        byte[] data = VState(state).VectorRegisters.Read(vreg);
        var bits = (uint)(data[i * 4] | (data[i * 4 + 1] << 8) | (data[i * 4 + 2] << 16) | (data[i * 4 + 3] << 24));
        return BitConverter.Int32BitsToSingle((int)bits);
    }

    private static void WriteVFpElem(byte[] data, int i, float value) {
        uint bits = float.IsNaN(value) ? Rv32Executor.RvCanonicalNaN : (uint)BitConverter.SingleToInt32Bits(value);
        int off = i * 4;
        data[off] = (byte)bits;
        data[off + 1] = (byte)(bits >> 8);
        data[off + 2] = (byte)(bits >> 16);
        data[off + 3] = (byte)(bits >> 24);
    }

    private static float ApplyVFpBin(VFpBinOp op, float a, float b) => op switch {
        VFpBinOp.Add => a + b,
        VFpBinOp.Sub => a - b,
        VFpBinOp.Mul => a * b,
        VFpBinOp.Div => a / b,
        VFpBinOp.Min => FMin(a, b),
        VFpBinOp.Max => FMax(a, b),
        VFpBinOp.Sgnj => BitConverter.Int32BitsToSingle(
            (BitConverter.SingleToInt32Bits(a) & 0x7FFFFFFF)
          | (BitConverter.SingleToInt32Bits(b) & unchecked((int)0x80000000))
        ),
        VFpBinOp.Sgnjn => BitConverter.Int32BitsToSingle(
            (BitConverter.SingleToInt32Bits(a) & 0x7FFFFFFF)
          | (~BitConverter.SingleToInt32Bits(b) & unchecked((int)0x80000000))
        ),
        VFpBinOp.Sgnjx => BitConverter.Int32BitsToSingle(
            (BitConverter.SingleToInt32Bits(a) & 0x7FFFFFFF) |
            ((BitConverter.SingleToInt32Bits(a) ^ BitConverter.SingleToInt32Bits(b)) & unchecked((int)0x80000000))
        ),
        _ => float.NaN,
    };

    private static ExecuteResult ExecuteVFpBin(
        IArchState state,
        VFpBinOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, float> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            float a = BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            float b = getB(i);
            WriteVFpElem(result, i, ApplyVFpBin(op, a, b));
        }

        return VectorWrite(vd, result);
    }

    // FMA semantics for each op variant.
    private static float ApplyVFpFma(VFpFmaOp op, float vdElem, float a, float b) => op switch {
        // vfmacc:  vd = vd + a*b       vfmadd:  vd = vd*a + b
        VFpFmaOp.Macc  => vdElem + a * b,
        VFpFmaOp.Nmacc => -vdElem - a * b,
        VFpFmaOp.Msac  => vdElem - a * b,
        VFpFmaOp.Nmsac => -vdElem + a * b,
        VFpFmaOp.Madd  => vdElem * a + b,
        VFpFmaOp.Nmadd => -(vdElem * a) - b,
        VFpFmaOp.Msub  => vdElem * a - b,
        VFpFmaOp.Nmsub => -(vdElem * a) + b,
        _              => float.NaN,
    };

    private static ExecuteResult ExecuteVFpFma(
        IArchState state,
        VFpFmaOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, float> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] vdData = VState(state).VectorRegisters.Read(vd);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            float acc = BitConverter.Int32BitsToSingle((int)ReadVElement(vdData, i, 4));
            float a = BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            float b = getB(i);
            WriteVFpElem(result, i, ApplyVFpFma(op, acc, a, b));
        }

        return VectorWrite(vd, result);
    }

    private static bool ApplyVFpCmp(VFpCmpOp op, float a, float b) => op switch {
        VFpCmpOp.Eq => !float.IsNaN(a) && !float.IsNaN(b) && a == b,
        VFpCmpOp.Le => !float.IsNaN(a) && !float.IsNaN(b) && a <= b,
        VFpCmpOp.Lt => !float.IsNaN(a) && !float.IsNaN(b) && a < b,
        VFpCmpOp.Ne => float.IsNaN(a) || float.IsNaN(b) || a != b,
        VFpCmpOp.Gt => !float.IsNaN(a) && !float.IsNaN(b) && a > b,
        VFpCmpOp.Ge => !float.IsNaN(a) && !float.IsNaN(b) && a >= b,
        _           => false,
    };

    // FP compare: result is 1 mask bit per element packed in vd.
    private static ExecuteResult ExecuteVFpCmp(
        IArchState state,
        VFpCmpOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, float> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            float a = BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            float b = getB(i);
            if (ApplyVFpCmp(op, a, b)) result[i >> 3] |= (byte)(1 << (i & 7));
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVFpUnary(
        IArchState state,
        int vd,
        int vs2,
        bool masked,
        Func<float, float> f
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            float a = BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            WriteVFpElem(result, i, f(a));
        }

        return VectorWrite(vd, result);
    }

    // vfclass.v: classify each element, write the 10-bit mask as a float-width integer.
    private static ExecuteResult ExecuteVFpClassOp(IArchState state, int vd, int vs2, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            var bits = (uint)ReadVElement(vs2Data, i, 4);
            WriteVElement(result, i, 4, FClass(bits));
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVFpCvt(IArchState state, VFpCvtOp op, int vd, int vs2, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            var src = (uint)ReadVElement(vs2Data, i, 4);
            ulong val = op switch {
                VFpCvtOp.XuFromF    => FcvtWuS(BitConverter.Int32BitsToSingle((int)src)),
                VFpCvtOp.XFromF     => FcvtWs(BitConverter.Int32BitsToSingle((int)src)),
                VFpCvtOp.FFromXu    => (uint)BitConverter.SingleToInt32Bits(src),
                VFpCvtOp.FFromX     => (uint)BitConverter.SingleToInt32Bits((int)src),
                VFpCvtOp.RtzXuFromF => FcvtWuS(BitConverter.Int32BitsToSingle((int)src)),
                VFpCvtOp.RtzXFromF  => FcvtWs(BitConverter.Int32BitsToSingle((int)src)),
                _                   => 0,
            };
            WriteVElement(result, i, 4, val);
        }

        return VectorWrite(vd, result);
    }

    // vfmv.f.s: scalar float rd ← vs2[0]
    private static ExecuteResult ExecuteVFpMvFs(IArchState state, int vs2) {
        byte[] data = VState(state).VectorRegisters.Read(vs2);
        var elem0 = (uint)ReadVElement(data, 0, 4);
        return ExecuteResult.WithResult(elem0);
    }

    // vfmv.s.f: vd[0] ← scalar float rs1; other elements undisturbed
    private static ExecuteResult ExecuteVFpMvSf(IArchState state, IRegisterFile regs, int vd, int rs1) {
        float scalar = FBits(regs, rs1);
        var current = (byte[])VState(state).VectorRegisters.Read(vd).Clone();
        WriteVFpElem(current, 0, scalar);
        return VectorWrite(vd, current);
    }

    // vfmv.v.f: broadcast scalar float to all active elements
    private static ExecuteResult ExecuteVFpMvVf(IArchState state, int vd, float scalar, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            WriteVFpElem(result, i, scalar);
        }

        return VectorWrite(vd, result);
    }

    // vfredusum/vfredosum/vfredmin/vfredmax: reduce vs2 into scalar vd[0]; vs1[0] seeds the accumulator.
    private static ExecuteResult ExecuteVFpRed(
        IArchState state,
        VFpRedOp op,
        int vd,
        int vs2,
        int vs1,
        bool masked
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("VFP: only SEW=32 supported");
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] vs2Data = vregs.Read(vs2);
        byte[] vs1Data = vregs.Read(vs1);
        byte[] mask = vregs.Read(0);
        float acc = BitConverter.Int32BitsToSingle((int)ReadVElement(vs1Data, 0, 4));

        for (var i = 0; i < (int)vl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            float elem = BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            acc = op switch {
                VFpRedOp.Usum or VFpRedOp.Osum => acc + elem,
                VFpRedOp.Min                   => FMin(acc, elem),
                VFpRedOp.Max                   => FMax(acc, elem),
                _                              => acc,
            };
        }

        var result = new byte[VectorRegisterFile.VLenB];
        WriteVFpElem(result, 0, acc);
        return VectorWrite(vd, result);
    }

    // ── V widening FP helpers ─────────────────────────────────────────────────

    private const ulong RvCanonicalNaN64 = 0x7FF8_0000_0000_0000UL;

    private static void WriteVFpElemD(byte[] data, int i, double value) {
        ulong bits = double.IsNaN(value) ? Rv32Executor.RvCanonicalNaN64 : (ulong)BitConverter.DoubleToInt64Bits(value);
        WriteVElement(data, i, 8, bits);
    }

    // f32 → u64 saturating (NaN or negative → 0; overflow → MaxValue)
    private static ulong VFcvtXuFromF32(float f) =>
        f switch {
            float.NaN or < 0f          => 0,
            >= 1.8446744073709552E+19f => ulong.MaxValue,
            _                          => (ulong)f,
        };

    // f32 → i64 saturating
    private static ulong VFcvtXFromF32(float f) =>
        f switch {
            float.NaN or >= 9.2233720368547758E+18f => long.MaxValue,
            < -9.2233720368547758E+18f              => unchecked((ulong)long.MinValue),
            _                                       => (ulong)(long)f,
        };

    // f64 → u32 saturating
    private static ulong VFcvtXuFromF64(double d) =>
        d switch {
            double.NaN or < 0.0 => 0,
            >= 4294967296.0     => uint.MaxValue,
            _                   => (uint)d,
        };

    // f64 → i32 saturating
    private static ulong VFcvtXFromF64(double d) =>
        d switch {
            double.NaN or >= 2147483648.0 => int.MaxValue,
            < -2147483648.0               => unchecked((uint)int.MinValue),
            _                             => (uint)(int)d,
        };

    // vfncvt.rod.f.f.w: f64 → f32, round-to-odd (if inexact, force mantissa LSB=1)
    private static float VFcvtRodF32FromF64(double d) {
        if (double.IsNaN(d)) return BitConverter.Int32BitsToSingle(2143289344);
        var f = (float)d;
        if (float.IsInfinity(f) || f == d) return f;
        return BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(f) | 1);
    }

    private static ExecuteResult ExecuteVFpWArith(
        IArchState state,
        VFpWideArithOp op,
        int vd,
        int vs2,
        bool vs2Wide,
        bool masked,
        Func<int, float> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("vfwArith: only SEW=32 supported");
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / 8);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            double a = vs2Wide
                ? BitConverter.Int64BitsToDouble((long)ReadVElement(vs2Data, i, 8))
                : BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            var b = (double)getB(i);
            double res = op switch {
                VFpWideArithOp.Add => a + b,
                VFpWideArithOp.Sub => a - b,
                VFpWideArithOp.Mul => a * b,
                _                  => double.NaN,
            };
            WriteVFpElemD(result, i, res);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVFpWMac(
        IArchState state,
        VFpWMacOp op,
        int vd,
        int vs2,
        bool masked,
        Func<int, float> getB
    ) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("vfwMac: only SEW=32 supported");
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / 8);
        VectorRegisterFile vregs = VState(state).VectorRegisters;
        byte[] vs2Data = vregs.Read(vs2);
        byte[] vdData = vregs.Read(vd);
        byte[] mask = vregs.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];
        Array.Copy(vdData, result, vdData.Length);

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            double acc = BitConverter.Int64BitsToDouble((long)ReadVElement(result, i, 8));
            var a = (double)BitConverter.Int32BitsToSingle((int)ReadVElement(vs2Data, i, 4));
            var b = (double)getB(i);
            double res = op switch {
                VFpWMacOp.Macc  => acc + a * b,
                VFpWMacOp.Nmacc => -acc - a * b,
                VFpWMacOp.Msac  => a * b - acc,
                VFpWMacOp.Nmsac => acc - a * b,
                _               => double.NaN,
            };
            WriteVFpElemD(result, i, res);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVFpWCvt(IArchState state, VFpWCvtOp op, int vd, int vs2, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("vfwcvt: only SEW=32 supported");
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / 8);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            var src = (uint)ReadVElement(vs2Data, i, 4);
            float srcF = BitConverter.Int32BitsToSingle((int)src);
            ulong val = op switch {
                VFpWCvtOp.XuFromF    => VFcvtXuFromF32(srcF),
                VFpWCvtOp.XFromF     => VFcvtXFromF32(srcF),
                VFpWCvtOp.FFromXu    => (ulong)BitConverter.DoubleToInt64Bits(src),
                VFpWCvtOp.FFromX     => (ulong)BitConverter.DoubleToInt64Bits((int)src),
                VFpWCvtOp.FFromF     => (ulong)BitConverter.DoubleToInt64Bits(srcF),
                VFpWCvtOp.RtzXuFromF => VFcvtXuFromF32(srcF),
                VFpWCvtOp.RtzXFromF  => VFcvtXFromF32(srcF),
                _                    => 0,
            };
            WriteVElement(result, i, 8, val);
        }

        return VectorWrite(vd, result);
    }

    private static ExecuteResult ExecuteVFpNCvt(IArchState state, VFpNCvtOp op, int vd, int vs2, bool masked) {
        (uint vl, int ewBytes) = VGetVlEw(state);
        if (ewBytes != 4) throw new NotImplementedException("vfncvt: only SEW=32 (output) supported");
        int effectiveVl = Math.Min((int)vl, VectorRegisterFile.VLenB / 8);
        byte[] vs2Data = VState(state).VectorRegisters.Read(vs2);
        byte[] mask = VState(state).VectorRegisters.Read(0);
        var result = new byte[VectorRegisterFile.VLenB];

        for (var i = 0; i < effectiveVl; i++) {
            if (masked && ((mask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            ulong raw64 = ReadVElement(vs2Data, i, 8);
            double srcD = BitConverter.Int64BitsToDouble((long)raw64);
            ulong val = op switch {
                VFpNCvtOp.XuFromF    => VFcvtXuFromF64(srcD),
                VFpNCvtOp.XFromF     => VFcvtXFromF64(srcD),
                VFpNCvtOp.FFromXu    => (uint)BitConverter.SingleToInt32Bits(raw64),
                VFpNCvtOp.FFromX     => (uint)BitConverter.SingleToInt32Bits((long)raw64),
                VFpNCvtOp.FFromF     => (uint)BitConverter.SingleToInt32Bits((float)srcD),
                VFpNCvtOp.RodFFromF  => (uint)BitConverter.SingleToInt32Bits(VFcvtRodF32FromF64(srcD)),
                VFpNCvtOp.RtzXuFromF => VFcvtXuFromF64(srcD),
                VFpNCvtOp.RtzXFromF  => VFcvtXFromF64(srcD),
                _                    => 0,
            };
            WriteVElement(result, i, 4, val);
        }

        return VectorWrite(vd, result);
    }
}