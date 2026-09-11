#region

using System.Numerics;
using Mechanism;
using Orrery.Cache;
using Orrery.Devices;
using RiscV32.Decode;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

#endregion

// ReSharper disable CompareOfFloatsByEqualityOperator

namespace RiscV32.Execute;

/// <summary>
///     Executes a single decoded RV32I instruction.
///     Reads from IArchState, returns an ExecuteResult — never writes back directly.
/// </summary>
public partial class Rv32Executor : IExecutor {
    // Single-hart fallback: used when ReservationTable is null.
    protected ulong? Reservation;

    /// <summary>
    ///     Address of the HTIF <c>tohost</c> register, if this workload uses HTIF.
    ///     A 4-byte store of an odd value here is a tohost exit code: the executor
    ///     flags it with <see cref="ExecuteResult.RequestHalt" /> so the engine
    ///     terminates at the exit write. Null disables the check.
    /// </summary>
    public ulong? HtifTohostAddress { get; init; }

    /// <summary>CLINT for WFI fast-forward: skips mtime to mtimecmp so the timer fires in one tick.</summary>
    public ClintDevice? Clint { get; init; }

    /// <summary>
    ///     Shared reservation table for multi-hart LR/SC.  When set, LR.W registers
    ///     this hart's reservation in the table, and SC.W consults it; a write from
    ///     any other hart to the same granule will cancel the reservation before SC
    ///     even executes.  Null = single-hart mode (private <see cref="Reservation" />
    ///     field is used instead, preserving backward compatibility).
    /// </summary>
    public ReservationTable? ReservationTable { get; init; }

    /// <summary>
    ///     Hart identifier used as the key in <see cref="ReservationTable" />.
    ///     Ignored when <see cref="ReservationTable" /> is null.
    /// </summary>
    public int HartId { get; init; }

    /// <summary>
    ///     When true, EBREAK always halts the simulation, even if mtvec is non-zero.
    ///     The default (false) routes EBREAK through mtvec as a real Breakpoint trap
    ///     whenever a handler is installed, matching OpenSBI's semihosting probe.
    ///     Horologium's own ISA-conformance test environment (env/riscv_test*.h) installs
    ///     its own mtvec handler for unrelated traps but still expects its own terminating
    ///     EBREAK (RVTEST_PASS/RVTEST_FAIL) to halt unconditionally — set this for that case.
    /// </summary>
    public bool EbreakAlwaysHalts { get; init; }

    /// <summary>
    ///     When true, WFI never halts the simulation — it degrades to a NOP whenever there
    ///     is no pending-and-enabled interrupt to wake it (mirrors Spike's functional-only
    ///     WFI semantics). The default (false) halts the simulation on an unwakeable WFI,
    ///     which system-boot tests use as a deliberate "idle loop reached" stop signal.
    /// </summary>
    public bool WfiNeverHalts { get; init; }

    /// <summary>
    ///     When non-null, ECALL instructions are routed to this handler instead of
    ///     generating a trap, enabling Linux syscall-emulation (gem5 SE) mode.
    /// </summary>
    public ISyscallHandler? SyscallHandler { get; init; }

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
                Reg(regs.Read(rs1) | unchecked((uint)imm)),
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

            // ── Macro-fused compare+branch ───────────────────────────────────────
            RvFusedCompareBranch(var cmpOp, var takenWhenNonZero, var branchPc, var branchImm) =>
                FusedCompareBranch(cmpOp, takenWhenNonZero, branchPc, branchImm, regs, pc, instruction.SizeBytes),

            // ── Micro-fused load+ALU ─────────────────────────────────────────────
            RvFusedLoadAlu(var loadOp, var aluOp) =>
                FusedLoadAlu(loadOp, aluOp, memory, state, pc, regs),

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
            RvEcall when SyscallHandler != null =>
                SyscallHandler.Handle(state.IntegerRegisters.Read(17), state, memory, pc, HartId),
            RvEcall => ExecuteResult.WithTrap(
                new TrapInfo(EcallCause(state.PrivilegeLevel), 0, pc)
            ),

            // If a trap handler is installed (mtvec != 0), generate a real breakpoint exception
            // so OpenSBI's semihosting probe (and similar) can recover via their mtvec handler.
            // If mtvec == 0, halt — matches Spike's non-interactive behavior for bare-metal tests.
            RvEbreak => !EbreakAlwaysHalts &&
                        state.SystemRegisters is CsrFile ebreakCsrs && ebreakCsrs.DirectRead(CsrFile.Mtvec) != 0
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
            RvFenceI    => ExecuteResult.Clean, // I-cache invalidation isn't modeled
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
            RvMopR  => Reg(0),
            RvMopRr => Reg(0),

            // ── Zcmop extension (compressed NOPs, no effect) ──────────────────
            RvCMopN => ExecuteResult.Clean,

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
            RvAndn(_, var rs1, var rs2) => Reg(regs.Read(rs1) & ~regs.Read(rs2)),
            RvOrn (_, var rs1, var rs2) => Reg(regs.Read(rs1) | ~regs.Read(rs2)),
            RvXnor(_, var rs1, var rs2) => Reg(~(regs.Read(rs1) ^ regs.Read(rs2))),
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

            // ── Zbkb/Zbkx extension (crypto-adjacent bit manipulation) ─────────────────
            RvPack (_, var rs1, var rs2)  => Pack(regs, rs1, rs2, 16),
            RvPackh (_, var rs1, var rs2) => Reg(((regs.Read(rs2) & 0xFF) << 8) | (regs.Read(rs1) & 0xFF)),
            RvBrev8 (_, var rs1)          => Brev8(regs, rs1, 4),
            RvZip (_, var rs1)            => Zip(regs, rs1),
            RvUnzip (_, var rs1)          => Unzip(regs, rs1),
            RvXperm4(_, var rs1, var rs2) => Xperm(regs, rs1, rs2, 4, 8),
            RvXperm8(_, var rs1, var rs2) => Xperm(regs, rs1, rs2, 8, 4),

            // ── Zknd/Zkne extension (NIST AES, RV32) ──────────────────────────────────
            RvAes32Dsi (_, var rs1, var rs2, var bs) => Aes32(regs, rs1, rs2, bs, true, false),
            RvAes32Dsmi(_, var rs1, var rs2, var bs) => Aes32(regs, rs1, rs2, bs, true, true),
            RvAes32Esi (_, var rs1, var rs2, var bs) => Aes32(regs, rs1, rs2, bs, false, false),
            RvAes32Esmi(_, var rs1, var rs2, var bs) => Aes32(regs, rs1, rs2, bs, false, true),

            // ── Zknh extension (NIST SHA2 hash function instructions) ─────────────────
            // Cast the 32-bit result through (int) before Reg() so RV64 (which inherits these
            // cases unmodified and doesn't truncate in Reg()) gets the spec-mandated sign
            // extension to XLEN for free, matching the RvSextB/RvSextH pattern above — a no-op
            // for RV32, where Reg() masks back down to the low 32 bits regardless.
            RvSha256Sig0(_, var rs1) => Reg(
                (ulong)(int)(
                    BitOperations.RotateRight((uint)regs.Read(rs1), 7) ^
                    BitOperations.RotateRight((uint)regs.Read(rs1), 18) ^
                    ((uint)regs.Read(rs1) >> 3)
                )
            ),
            RvSha256Sig1(_, var rs1) => Reg(
                (ulong)(int)(
                    BitOperations.RotateRight((uint)regs.Read(rs1), 17) ^
                    BitOperations.RotateRight((uint)regs.Read(rs1), 19) ^
                    ((uint)regs.Read(rs1) >> 10)
                )
            ),
            RvSha256Sum0(_, var rs1) => Reg(
                (ulong)(int)(
                    BitOperations.RotateRight((uint)regs.Read(rs1), 2) ^
                    BitOperations.RotateRight((uint)regs.Read(rs1), 13) ^
                    BitOperations.RotateRight((uint)regs.Read(rs1), 22)
                )
            ),
            RvSha256Sum1(_, var rs1) => Reg(
                (ulong)(int)(
                    BitOperations.RotateRight((uint)regs.Read(rs1), 6) ^
                    BitOperations.RotateRight((uint)regs.Read(rs1), 11) ^
                    BitOperations.RotateRight((uint)regs.Read(rs1), 25)
                )
            ),
            RvSha512Sig0H(_, var rs1, var rs2) => Reg(Sha512Sig0H((uint)regs.Read(rs1), (uint)regs.Read(rs2))),
            RvSha512Sig0L(_, var rs1, var rs2) => Reg(Sha512Sig0L((uint)regs.Read(rs1), (uint)regs.Read(rs2))),
            RvSha512Sig1H(_, var rs1, var rs2) => Reg(Sha512Sig1H((uint)regs.Read(rs1), (uint)regs.Read(rs2))),
            RvSha512Sig1L(_, var rs1, var rs2) => Reg(Sha512Sig1L((uint)regs.Read(rs1), (uint)regs.Read(rs2))),
            RvSha512Sum0R(_, var rs1, var rs2) => Reg(Sha512Sum0R((uint)regs.Read(rs1), (uint)regs.Read(rs2))),
            RvSha512Sum1R(_, var rs1, var rs2) => Reg(Sha512Sum1R((uint)regs.Read(rs1), (uint)regs.Read(rs2))),

            // ── Zksh extension (ShangMi SM3 hash function instructions) ───────────────
            // Sign-extend through (int) — see the Zknh comment above for why.
            RvSm3P0(_, var rs1) => Reg(
                (ulong)(int)(
                    (uint)regs.Read(rs1) ^
                    BitOperations.RotateLeft((uint)regs.Read(rs1), 9) ^
                    BitOperations.RotateLeft((uint)regs.Read(rs1), 17)
                )
            ),
            RvSm3P1(_, var rs1) => Reg(
                (ulong)(int)(
                    (uint)regs.Read(rs1) ^
                    BitOperations.RotateLeft((uint)regs.Read(rs1), 15) ^
                    BitOperations.RotateLeft((uint)regs.Read(rs1), 23)
                )
            ),

            // ── Zksed extension (ShangMi SM4 block cipher instructions) ───────────────
            RvSm4Ed(_, var rs1, var rs2, var bs) => Sm4(regs, rs1, rs2, bs, false),
            RvSm4Ks(_, var rs1, var rs2, var bs) => Sm4(regs, rs1, rs2, bs, true),

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

            // ── Zabha+Zacas: narrow compare-and-swap ───────────────────────────
            RvAmocasB(var rd, var rs1, var rs2) => AmoCasNarrow(memory, state, pc, regs, rd, rs1, rs2, 1),
            RvAmocasH(var rd, var rs1, var rs2) => AmoCasNarrow(memory, state, pc, regs, rd, rs1, rs2, 2),

            // ── Zacas: RV32 register-pair amocas.d ──────────────────────────────
            RvAmocasDPair(var rd, var rs1, var rs2) => AmoCasDPair(memory, state, pc, regs, rd, rs1, rs2),

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

            // fsgnj*.s operands go through the NaN-boxing check (§11.3): an improperly
            // boxed source register (upper 32 bits not all 1s) reads as the canonical
            // NaN, not its raw truncated bits — hence FBits() rather than a raw cast.
            RvFsgnjS (_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    0xFFFFFFFF00000000UL |
                    ((BitConverter.SingleToUInt32Bits(FBits(regs, rs1)) & 0x7FFFFFFFu) |
                     (BitConverter.SingleToUInt32Bits(FBits(regs, rs2)) & 0x80000000u))
                ),
            RvFsgnjnS(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    0xFFFFFFFF00000000UL |
                    ((BitConverter.SingleToUInt32Bits(FBits(regs, rs1)) & 0x7FFFFFFFu) |
                     (~BitConverter.SingleToUInt32Bits(FBits(regs, rs2)) & 0x80000000u))
                ),
            RvFsgnjxS(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    0xFFFFFFFF00000000UL |
                    ((BitConverter.SingleToUInt32Bits(FBits(regs, rs1)) & 0x7FFFFFFFu) |
                     ((BitConverter.SingleToUInt32Bits(FBits(regs, rs1)) ^
                       BitConverter.SingleToUInt32Bits(FBits(regs, rs2))) & 0x80000000u))
                ),

            RvFminS(_, var rs1, var rs2) => FpMinMax(regs, rs1, rs2, true),
            RvFmaxS(_, var rs1, var rs2) => FpMinMax(regs, rs1, rs2, false),

            // IEEE 754: NaN comparisons return false; sNaN inputs set NV flag
            RvFeqS(_, var rs1, var rs2) => FpCmp(regs, rs1, rs2, 0),
            RvFltS(_, var rs1, var rs2) => FpCmp(regs, rs1, rs2, 1),
            RvFleS(_, var rs1, var rs2) => FpCmp(regs, rs1, rs2, 2),

            RvFclassS(_, var rs1) => Reg(FClass(BitConverter.SingleToUInt32Bits(FBits(regs, rs1)))),

            RvFcvtWs (_, var rs1, var rm) => FcvtWsResult(FBits(regs, rs1), rm, state),
            RvFcvtWuS(_, var rs1, var rm) => FcvtWuSResult(FBits(regs, rs1), rm, state),
            RvFcvtSw (_, var rs1, _)      => FpIntToFloat((int)(uint)regs.Read(rs1)),
            RvFcvtSWu(_, var rs1, _)      => FpIntToFloat((uint)regs.Read(rs1)),

            RvFmvXw(_, var rs1) => Reg((ulong)(int)(uint)regs.Read(rs1)), // lower 32 fp bits → int reg, sign-extended
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

            // fsgnj*.d are pure bit-manipulation ops (§11.2): they must pass the
            // mantissa/exponent through verbatim, even for NaN-shaped payloads,
            // so bypass FloatRegD's NaN-canonicalization entirely.
            RvFsgnjD (_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    (regs.Read(rs1) & 0x7FFFFFFFFFFFFFFFUL) | (regs.Read(rs2) & 0x8000000000000000UL)
                ),
            RvFsgnjnD(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    (regs.Read(rs1) & 0x7FFFFFFFFFFFFFFFUL) | (~regs.Read(rs2) & 0x8000000000000000UL)
                ),
            RvFsgnjxD(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    (regs.Read(rs1) & 0x7FFFFFFFFFFFFFFFUL) |
                    ((regs.Read(rs1) ^ regs.Read(rs2)) & 0x8000000000000000UL)
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

            // ── Zfh extension ─────────────────────────────────────────────────
            RvFlh(_, var rs1, var imm) => NanBoxH(Load(memory, state, pc, regs.Read(rs1), imm, 2, false, 16)),

            RvFsh(var rs1, var rs2, var imm) =>
                Store(memory, state, pc, regs.Read(rs1), imm, regs.Read(rs2), 2),

            RvFaddH (_, var rs1, var rs2) => HpBin(HBits(regs, rs1), HBits(regs, rs2), 0),
            RvFsubH (_, var rs1, var rs2) => HpBin(HBits(regs, rs1), HBits(regs, rs2), 1),
            RvFmulH (_, var rs1, var rs2) => HpBin(HBits(regs, rs1), HBits(regs, rs2), 2),
            RvFdivH (_, var rs1, var rs2) => HpBin(HBits(regs, rs1), HBits(regs, rs2), 3),
            RvFsqrtH(_, var rs1)          => HpSqrt(HBits(regs, rs1)),

            // Same NaN-boxing caveat as fsgnj*.s above, at half-width (upper 48 bits).
            RvFsgnjH (_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    0xFFFFFFFFFFFF0000UL |
                    (BitConverter.HalfToUInt16Bits(HBits(regs, rs1)) & 0x7FFFUL) |
                    (BitConverter.HalfToUInt16Bits(HBits(regs, rs2)) & 0x8000UL)
                ),
            RvFsgnjnH(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    0xFFFFFFFFFFFF0000UL |
                    (BitConverter.HalfToUInt16Bits(HBits(regs, rs1)) & 0x7FFFUL) |
                    (~(ulong)BitConverter.HalfToUInt16Bits(HBits(regs, rs2)) & 0x8000UL)
                ),
            RvFsgnjxH(_, var rs1, var rs2) =>
                ExecuteResult.WithResult(
                    0xFFFFFFFFFFFF0000UL |
                    (BitConverter.HalfToUInt16Bits(HBits(regs, rs1)) & 0x7FFFUL) |
                    ((ulong)(BitConverter.HalfToUInt16Bits(HBits(regs, rs1)) ^
                             BitConverter.HalfToUInt16Bits(HBits(regs, rs2))) & 0x8000UL)
                ),

            RvFminH(_, var rs1, var rs2) => HpMinMax(regs, rs1, rs2, true),
            RvFmaxH(_, var rs1, var rs2) => HpMinMax(regs, rs1, rs2, false),

            RvFeqH(_, var rs1, var rs2) => HpCmp(regs, rs1, rs2, 0),
            RvFltH(_, var rs1, var rs2) => HpCmp(regs, rs1, rs2, 1),
            RvFleH(_, var rs1, var rs2) => HpCmp(regs, rs1, rs2, 2),

            RvFclassH(_, var rs1) => Reg(HClass(BitConverter.HalfToUInt16Bits(HBits(regs, rs1)))),

            // Half is exactly representable in float, so FCVT.W(U).H reuses the S-format
            // int-conversion helpers unchanged (widening loses no precision or rounding info).
            RvFcvtWh (_, var rs1, var rm) => FcvtWsResult((float)HBits(regs, rs1), rm, state),
            RvFcvtWuH(_, var rs1, var rm) => FcvtWuSResult((float)HBits(regs, rs1), rm, state),
            RvFcvtHw (_, var rs1, _)      => FpIntToHalf((int)(uint)regs.Read(rs1)),
            RvFcvtHWu(_, var rs1, _)      => FpIntToHalf((uint)regs.Read(rs1)),

            // FMV.X.H sign-extends the raw 16-bit pattern (bit 15 is the FP sign bit).
            RvFmvXh(_, var rs1) => Reg((ulong)(short)(ushort)regs.Read(rs1)),
            RvFmvHx(_, var rs1) => ExecuteResult.WithResult(0xFFFFFFFFFFFF0000UL | (regs.Read(rs1) & 0xFFFF)),

            RvFmaddH (_, var rs1, var rs2, var rs3) =>
                HpFma(HBits(regs, rs1), HBits(regs, rs2), HBits(regs, rs3)),
            RvFmsubH (_, var rs1, var rs2, var rs3) =>
                HpFma(HBits(regs, rs1), HBits(regs, rs2), -HBits(regs, rs3)),
            RvFnmsubH(_, var rs1, var rs2, var rs3) =>
                HpFma(-HBits(regs, rs1), HBits(regs, rs2), HBits(regs, rs3)),
            RvFnmaddH(_, var rs1, var rs2, var rs3) =>
                HpFma(-HBits(regs, rs1), HBits(regs, rs2), -HBits(regs, rs3)),

            RvFcvtHs(_, var rs1, var rm) => FpFcvtHs(FBits(regs, rs1), rm, state),
            RvFcvtSh(_, var rs1, _)      => FpFcvtSh(HBits(regs, rs1)),
            RvFcvtHd(_, var rs1, var rm) => FpFcvtHd(DBits(regs, rs1), rm, state),
            RvFcvtDh(_, var rs1, _)      => FpFcvtDh(HBits(regs, rs1)),

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
            RvVWideVi (var op2, var vd, var vs2, var imm, var masked) =>
                ExecuteVWide(state, op2, vd, vs2, masked, false, (_, _) => (ulong)imm),

            // ── Zvbb/Zvkb: unary bitmanip (vbrev8/vrev8/vbrev/vclz/vctz/vcpop) ───────────────
            RvVBitmanipUnaryVv (var op2, var vd, var vs2, var masked) =>
                ExecuteVBitmanipUnary(state, op2, vd, vs2, masked),

            // ── Zvbc: vector carryless multiply (SEW=64 only) ────────────────────────────────
            RvVClmulVv (var op2, var vd, var vs2, var vs1, var masked) =>
                ExecuteVClmul(state, pc, op2, vd, vs2, masked, (i, ew) => VReadElem(state, vs1, i, ew)),
            RvVClmulVx (var op2, var vd, var vs2, var rs1, var masked) =>
                ExecuteVClmul(state, pc, op2, vd, vs2, masked, (_, _) => regs.Read(rs1)),
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

            // ── Zvkned: AES round/round-zero/key-schedule instructions ─────────────────────
            RvVaesRoundVv (var kind, var vd, var vs2) =>
                ExecuteVAesRound(state, pc, vd, vs2, false, kind),
            RvVaesRoundVs (var kind, var vd, var vs2) =>
                ExecuteVAesRound(state, pc, vd, vs2, true, kind),
            RvVaesZVs (var vd, var vs2)              => ExecuteVAesZ(state, pc, vd, vs2),
            RvVaesKf1Vi (var vd, var vs2, var round) => ExecuteVAesKf1(state, pc, vd, vs2, round),
            RvVaesKf2Vi (var vd, var vs2, var round) => ExecuteVAesKf2(state, pc, vd, vs2, round),

            // ── Zvksed: SM4 round/key-schedule instructions ─────────────────────────────────
            RvSm4RVv (var vd, var vs2)            => ExecuteSm4R(state, pc, vd, vs2, false),
            RvSm4RVs (var vd, var vs2)            => ExecuteSm4R(state, pc, vd, vs2, true),
            RvSm4KVi (var vd, var vs2, var round) => ExecuteSm4K(state, pc, vd, vs2, round),

            // ── Zvknha/Zvknhb: SHA-2 compression/message-schedule instructions ──────────────
            RvSha2CVv (var kind, var vd, var vs1, var vs2) =>
                ExecuteSha2Compress(state, pc, kind, vd, vs1, vs2),
            RvSha2MsVv (var vd, var vs1, var vs2) => ExecuteSha2Ms(state, pc, vd, vs1, vs2),

            // ── Zvksh: SM3 compression/message-schedule instructions ────────────────────────
            RvSm3CVi (var vd, var vs2, var round) => ExecuteSm3C(state, pc, vd, vs2, round),
            RvSm3MeVv (var vd, var vs1, var vs2)  => ExecuteSm3Me(state, pc, vd, vs1, vs2),

            // ── Zvkg: GHASH add-multiply/multiply instructions ───────────────────────────────
            RvVGhshVv (var vd, var vs1, var vs2) => ExecuteVGhsh(state, pc, vd, vs1, vs2),
            RvVGmulVv (var vd, var vs2)          => ExecuteVGmul(state, pc, vd, vs2),

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
                => ExecuteUveSoVMv(state, memory, transpose, vd, vs1, predIdx),
            RvUveSoPCv (var pd, var ps1, var srcBytes, var destBytes, var zeroing)
                => ExecuteUveSoPCv(state, pd, ps1, srcBytes, destBytes, zeroing),
            RvUveSoVCv (var vd, var vs1, var destBytes, var isFp, var isSigned)
                => ExecuteUveSoVCv(state, vd, vs1, destBytes, isFp, isSigned),

            _ => throw new InvalidOperationException(
                $"Unhandled RvOp: {op.GetType().Name}"
            ),
        };
    }

    // RV64's RvSd isn't handled here — Rv64Executor doesn't override this, so a 64-bit store's
    // address is only ever resolved at its normal full Execute (a missed early-resolution
    // opportunity, not a correctness gap: AddressKnown just stays false until then).
    public virtual ulong? TryComputeStoreAddress(ITooth instruction, IArchState state, IMemory memory) {
        if (instruction.Payload is not RvOp op) return null;
        (int rs1, int imm) = op switch {
            RvSb(var r, _, var i) => (r, i),
            RvSh(var r, _, var i) => (r, i),
            RvSw(var r, _, var i) => (r, i),
            _                     => (-1, 0),
        };
        if (rs1 < 0) return null;

        ulong vaddr = state.IntegerRegisters.Read(rs1) + (ulong)imm;
        (ulong addr, int fault) = Translate(memory, state, vaddr, true, false);
        return fault != 0 ? null : addr;
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

    // pack: rs1's low halfBits bits in the low half of rd, rs2's low halfBits bits in the high
    // half. halfBits=16 on RV32 (32-bit result), halfBits=32 on RV64 (Rv64Executor override) —
    // either way the packed halves exactly fill XLEN, so no further sign/zero extension applies.
    protected ExecuteResult Pack(IRegisterFile regs, int rs1, int rs2, int halfBits) {
        ulong mask = (1UL << halfBits) - 1;
        ulong lo = regs.Read(rs1) & mask;
        ulong hi = regs.Read(rs2) & mask;
        return Reg((hi << halfBits) | lo);
    }

    // brev8: reverse the bits within each byte, byteCount bytes wide (4 on RV32, 8 on RV64).
    protected ExecuteResult Brev8(IRegisterFile regs, int rs1, int byteCount) {
        ulong v = regs.Read(rs1);
        ulong result = 0;
        for (var i = 0; i < byteCount; i++) result |= (ulong)ReverseBitsInByte((byte)(v >> (i * 8))) << (i * 8);
        return Reg(result);
    }

    protected static byte ReverseBitsInByte(byte b) {
        byte r = 0;
        for (var bit = 0; bit < 8; bit++)
            if ((b & (1 << bit)) != 0)
                r |= (byte)(1 << (7 - bit));
        return r;
    }

    // zip/unzip: bit-interleave a 32-bit word's low/high halves into even/odd bit positions
    // (and back). RV32-only — no RV64 counterpart exists in the spec.
    private ExecuteResult Zip(IRegisterFile regs, int rs1) {
        var v = (uint)regs.Read(rs1);
        uint result = 0;
        for (var i = 0; i < 16; i++) {
            result |= ((v >> i) & 1u) << (2 * i);
            result |= ((v >> (16 + i)) & 1u) << (2 * i + 1);
        }

        return Reg(result);
    }

    private ExecuteResult Unzip(IRegisterFile regs, int rs1) {
        var v = (uint)regs.Read(rs1);
        uint result = 0;
        for (var i = 0; i < 16; i++) {
            result |= ((v >> (2 * i)) & 1u) << i;
            result |= ((v >> (2 * i + 1)) & 1u) << (16 + i);
        }

        return Reg(result);
    }

    // xperm4/xperm8: crossbar lookup — each elementBits-wide element of rs2 indexes an element
    // of rs1; an out-of-range index (>= elementCount) yields zero. elementCount is XLEN/elementBits
    // (8/4 on RV32, 16/8 on RV64 via the Rv64Executor override).
    protected ExecuteResult Xperm(IRegisterFile regs, int rs1, int rs2, int elementBits, int elementCount) {
        ulong lut = regs.Read(rs1);
        ulong idxVec = regs.Read(rs2);
        ulong mask = (1UL << elementBits) - 1;
        ulong result = 0;
        for (var i = 0; i < elementCount; i++) {
            var idx = (int)((idxVec >> (i * elementBits)) & mask);
            ulong looked = idx < elementCount ? (lut >> (idx * elementBits)) & mask : 0;
            result |= looked << (i * elementBits);
        }

        return Reg(result);
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
                return Clint?.TimerPending() == true || WfiNeverHalts
                    ? ExecuteResult.Clean // timer now set; PeekInterrupt will catch it
                    : new ExecuteResult { IsHalt = true, };
            }

            // S/U-mode WFI: halt unless an S-mode interrupt is already pending.
            uint sip = csrs.DirectRead(CsrFile.Sip) & csrs.DirectRead(CsrFile.Sie);
            if (sip != 0) return ExecuteResult.Clean;
        }

        return WfiNeverHalts ? ExecuteResult.Clean : new ExecuteResult { IsHalt = true, };
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
        return Reg((ulong)(int)old); // AMO*.W results are sign-extended to XLEN (matters for RV64)
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
            Reservation = paddr;
        // LR.W is sign-extended to XLEN (matters for RV64); routed through the virtual
        // Reg() so RV32 truncates it back to 32 bits (a no-op there).
        return Reg((ulong)(int)(uint)memory.Read(paddr, 4));
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
    protected bool ConsumePrivateReservation(ulong paddr) {
        bool matched = Reservation == paddr;
        Reservation = null; // SC always releases the reservation
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
        int rd = bytes == 1
            ? (sbyte)(byte)old
            : (short)(ushort)old;
        return Reg((ulong)rd); // sign-extended to XLEN (matters for RV64)
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
        return Reg((ulong)(int)old); // sign-extended to XLEN (matters for RV64)
    }

    // Zabha+Zacas: narrow (byte/halfword) compare-and-swap. rdReg is both comparand and
    // destination for the old value; only the low `bytes` of rdReg participate in the compare.
    private ExecuteResult AmoCasNarrow(
        IMemory memory,
        IArchState state,
        ulong pc,
        IRegisterFile regs,
        int rdReg,
        int rs1,
        int rs2,
        int bytes
    ) {
        ulong vaddr = regs.Read(rs1);
        (ulong addr, int fault) = Translate(memory, state, vaddr, true, false);
        if (fault != 0) return ExecuteResult.WithTrap(new TrapInfo(fault, vaddr, pc));
        var old = (uint)memory.Read(addr, bytes);
        uint mask = bytes == 1 ? 0xFFu : 0xFFFFu;
        if (old == ((uint)regs.Read(rdReg) & mask)) memory.Write(addr, regs.Read(rs2), bytes);
        int rd = bytes == 1 ? (sbyte)(byte)old : (short)(ushort)old;
        return Reg((ulong)rd); // sign-extended to XLEN (matters for RV64)
    }

    // Zacas: RV32 amocas.d register-pair form. RV32 has no 64-bit register, so the
    // comparand/new-value/result are split across (rdReg, rdReg+1) and (rs2, rs2+1).
    // rdReg's low half commits through the normal DestinationRegister path; rdReg+1
    // commits via SideEffect (see ITooth.SecondaryDestinationRegister). Correct only
    // because the OoO train head-serializes this instruction's issue (see OooTrain),
    // guaranteeing regs here already reflects every older instruction's commit.
    private ExecuteResult AmoCasDPair(
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
        ulong compare = (regs.Read(rdReg) & 0xFFFFFFFFUL) | (regs.Read(rdReg + 1) << 32);
        ulong old = memory.Read(addr, 8);
        if (old == compare) {
            ulong newVal = (regs.Read(rs2) & 0xFFFFFFFFUL) | (regs.Read(rs2 + 1) << 32);
            memory.Write(addr, newVal, 8);
        }

        ulong oldHigh = old >> 32;
        return new ExecuteResult {
            RegisterResult = (old & 0xFFFFFFFFUL, true),
            SideEffect = s => s.IntegerRegisters.Write(rdReg + 1, oldHigh),
        };
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

    protected ExecuteResult Store(
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

    // Re-evaluates the SLT-family compare against live register state — deliberately not
    // cached from fuse time, since RvMacroFuser.TryFuse runs speculatively before the
    // compare's own operands are known to be ready.
    private static ulong FusedCompareValue(RvOp cmp, IRegisterFile regs) => cmp switch {
        RvSlt (_, var rs1, var rs2)  => (int)regs.Read(rs1) < (int)regs.Read(rs2) ? 1UL : 0UL,
        RvSltu(_, var rs1, var rs2)  => regs.Read(rs1) < regs.Read(rs2) ? 1UL : 0UL,
        RvSlti (_, var rs1, var imm) => (int)regs.Read(rs1) < imm ? 1UL : 0UL,
        RvSltiu(_, var rs1, var imm) => (uint)regs.Read(rs1) < (uint)imm ? 1UL : 0UL,
        _ => throw new InvalidOperationException(
            $"RvFusedCompareBranch.Compare held an unexpected payload type: {cmp.GetType().Name}"
        ),
    };

    // branchPc/branchImm are the branch's own PC-relative target math — independent of
    // macroOpPc/macroOpSizeBytes (the fused Tooth's Pc/SizeBytes, which span both original
    // instructions and drive the not-taken fall-through instead).
    private static ExecuteResult FusedCompareBranch(
        RvOp cmp,
        bool takenWhenNonZero,
        ulong branchPc,
        int branchImm,
        IRegisterFile regs,
        ulong macroOpPc,
        int macroOpSizeBytes
    ) {
        ulong cmpValue = FusedCompareValue(cmp, regs);
        bool taken = takenWhenNonZero ? cmpValue != 0 : cmpValue == 0;
        ulong target = taken ? (ulong)((long)branchPc + branchImm) : macroOpPc + (ulong)macroOpSizeBytes;
        return new ExecuteResult {
            RegisterResult = (cmpValue, true),
            BranchTaken = taken,
            BranchTarget = target,
        };
    }

    // Instance (not static) method: routes through the virtual Load()/Reg() so RV64Executor's
    // overrides (no 32-bit truncation, RV64 sign-extension) apply correctly when inherited.
    // If the load traps, the ALU half never runs — same as the unfused pair would behave,
    // since a trapped load's destination is never written.
    private ExecuteResult FusedLoadAlu(
        RvOp load,
        RvOp aluOp,
        IMemory memory,
        IArchState state,
        ulong pc,
        IRegisterFile regs
    ) {
        (int rdLoad, int rs1, int imm, int bytes, bool signExtend, int bits) = load switch {
            RvLw(var rd, var r, var i)  => (rd, r, i, 4, false, 32),
            RvLh(var rd, var r, var i)  => (rd, r, i, 2, true, 16),
            RvLhu(var rd, var r, var i) => (rd, r, i, 2, false, 16),
            RvLb(var rd, var r, var i)  => (rd, r, i, 1, true, 8),
            RvLbu(var rd, var r, var i) => (rd, r, i, 1, false, 8),
            _ => throw new InvalidOperationException(
                $"RvFusedLoadAlu.Load held an unexpected payload type: {load.GetType().Name}"
            ),
        };

        ExecuteResult loadResult = Load(memory, state, pc, regs.Read(rs1), imm, bytes, signExtend, bits);
        if (loadResult.HasTrap) return loadResult;
        ulong loadedValue = loadResult.RegisterResult.Value;

        ulong ReadSub(int reg) => reg == rdLoad ? loadedValue : regs.Read(reg);

        ulong result = aluOp switch {
            RvAdd(_, var a, var b)   => ReadSub(a) + ReadSub(b),
            RvSub(_, var a, var b)   => ReadSub(a) - ReadSub(b),
            RvXor(_, var a, var b)   => ReadSub(a) ^ ReadSub(b),
            RvOr(_, var a, var b)    => ReadSub(a) | ReadSub(b),
            RvAnd(_, var a, var b)   => ReadSub(a) & ReadSub(b),
            RvSll(_, var a, var b)   => ReadSub(a) << (int)(ReadSub(b) & 0x1F),
            RvSrl(_, var a, var b)   => (uint)ReadSub(a) >> (int)(ReadSub(b) & 0x1F),
            RvSra(_, var a, var b)   => (ulong)((int)ReadSub(a) >> (int)(ReadSub(b) & 0x1F)),
            RvSlt(_, var a, var b)   => (int)ReadSub(a) < (int)ReadSub(b) ? 1UL : 0UL,
            RvSltu(_, var a, var b)  => ReadSub(a) < ReadSub(b) ? 1UL : 0UL,
            RvAddi(_, var a, var i)  => ReadSub(a) + (ulong)i,
            RvXori(_, var a, var i)  => ReadSub(a) ^ (ulong)i,
            RvOri(_, var a, var i)   => ReadSub(a) | unchecked((uint)i),
            RvAndi(_, var a, var i)  => ReadSub(a) & (ulong)i,
            RvSlli(_, var a, var sh) => ReadSub(a) << sh,
            RvSrli(_, var a, var sh) => (uint)ReadSub(a) >> sh,
            RvSrai(_, var a, var sh) => (ulong)((int)ReadSub(a) >> sh),
            RvSlti(_, var a, var i)  => (int)ReadSub(a) < i ? 1UL : 0UL,
            RvSltiu(_, var a, var i) => (uint)ReadSub(a) < (uint)i ? 1UL : 0UL,
            _ => throw new InvalidOperationException(
                $"RvFusedLoadAlu.AluOp held an unexpected payload type: {aluOp.GetType().Name}"
            ),
        };

        return Reg(result);
    }

    private static ExecuteResult ExecuteCsr(
        IArchState state,
        int rs1,
        uint csr,
        ulong pc,
        Func<ulong, ulong, ulong> combine,
        bool writeIfSrcZero = true
    ) {
        // Zkr §4.1: a read-only access to seed (CSRRS/CSRRC with rs1==x0) is illegal — checked
        // before Read() since polling seed is stateful (wipe-on-read) and must not fire on a
        // trapped access.
        if (csr == CsrFile.Seed && !(writeIfSrcZero || rs1 != 0))
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));
        ISystemRegisters csrFile = state.SystemRegisters;
        try {
            ulong old = csrFile.Read(csr, state.PrivilegeLevel);
            ulong src = state.IntegerRegisters.Read(rs1);
            // Per spec §2.8: CSRRS/CSRRC with rs1==x0 must not write the CSR.
            if (writeIfSrcZero || rs1 != 0) csrFile.Write(csr, combine(old, src), state.PrivilegeLevel);
            return ExecuteResult.WithResult(old);
        }
        catch (SystemRegisterAccessException) {
            return ExecuteResult.WithTrap(new TrapInfo(RvTrapCause.IllegalInstruction, 0, pc));
        }
    }
}