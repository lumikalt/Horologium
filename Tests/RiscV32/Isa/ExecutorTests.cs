#region

using Mechanism;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

#endregion

namespace Tests.RiscV32.Isa;

public class ExecutorTests {
    private readonly Rv32Decoder _dec = new();
    private readonly Rv32Executor _exe = new();
    private readonly FlatMemory _mem = new(4096);

    private static Rv32ArchState MakeState(params (int reg, uint val)[] regs) {
        var s = new Rv32ArchState();
        foreach ((int r, uint v) in regs) {
            // FP registers (32-63) require NaN-boxing when D extension is present.
            ulong stored = r >= 32 ? 0xFFFFFFFF00000000UL | v : v;
            s.IntegerRegisters.Write(r, stored);
        }

        return s;
    }

    private ExecuteResult Exec(uint raw, Rv32ArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // ── R-type ────────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Add() {
        Rv32ArchState s = MakeState((2, 10), (3, 20));
        ExecuteResult r = Exec(0x003100B3, s); // add x1, x2, x3
        Assert.Equal(30UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Sub() {
        Rv32ArchState s = MakeState((2, 20), (3, 7));
        ExecuteResult r = Exec(0x403100B3, s); // sub x1, x2, x3
        Assert.Equal(13UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Sub_Wraps() {
        Rv32ArchState s = MakeState((2, 0), (3, 1));
        ExecuteResult r = Exec(0x403100B3, s); // sub x1, x2, x3  → 0 - 1 = 0xFFFFFFFF
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Slt_True() {
        Rv32ArchState s = MakeState((2, unchecked((uint)-5)), (3, 1));
        ExecuteResult r = Exec(0x003120B3, s); // slt x1, x2, x3  (-5 < 1 signed)
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Slt_False() {
        Rv32ArchState s = MakeState((2, 5), (3, 1));
        ExecuteResult r = Exec(0x003120B3, s); // slt x1, x2, x3
        Assert.Equal(0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Sltu_UnsignedComparison() {
        // x2 = 0xFFFFFFFF (large unsigned), x3 = 1
        Rv32ArchState s = MakeState((2, 0xFFFFFFFF), (3, 1));
        ExecuteResult r = Exec(0x003130B3, s); // sltu x1, x2, x3  → 0 (0xFFFFFFFF > 1 unsigned)
        Assert.Equal(0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Sra_SignExtends() {
        Rv32ArchState s = MakeState((2, 0x80000000), (3, 1));
        ExecuteResult r = Exec(0x403150B3, s); // sra x1, x2, x3
        Assert.Equal(0xC0000000UL, r.RegisterResult.Value);
    }

    // ── I-type ALU ────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Addi() {
        Rv32ArchState s = MakeState((2, 100));
        ExecuteResult r = Exec(0x02A10093, s); // addi x1, x2, 42
        Assert.Equal(142UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Addi_Negative() {
        Rv32ArchState s = MakeState((2, 10));
        ExecuteResult r = Exec(0xFFF10093, s); // addi x1, x2, -1
        Assert.Equal(9UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Andi() {
        Rv32ArchState s = MakeState((2, 0xFF));
        ExecuteResult r = Exec(0x00F17093, s); // andi x1, x2, 15
        Assert.Equal(0xFUL, r.RegisterResult.Value);
    }

    // ── Loads and Stores ──────────────────────────────────────────────────────

    [Fact]
    public void Execute_Sw_Then_Lw() {
        Rv32ArchState s = MakeState((1, 100), (2, 0xDEADBEEF));
        // sw x2, 0(x1)  →  store 0xDEADBEEF at address 100
        Exec(0x0020a023, s); // sw x2, 0(x1)
        // lw x3, 0(x1)
        ExecuteResult r = Exec(0x0000a183, s); // lw x3, 0(x1)
        Assert.Equal(0xDEADBEEFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Lb_SignExtends() {
        _mem.Write(200, 0xFF, 1); // write -1 as byte
        Rv32ArchState s = MakeState((1, 200));
        ExecuteResult r = Exec(0x00008083, s); // lb x1, 0(x1)
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Lbu_ZeroExtends() {
        _mem.Write(200, 0xFF, 1);
        Rv32ArchState s = MakeState((1, 200));
        ExecuteResult r = Exec(0x00008083 | (0x4u << 12), s); // lbu x1, 0(x1)
        Assert.Equal(0xFFUL, r.RegisterResult.Value);
    }

    // ── Branches ──────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Beq_Taken() {
        Rv32ArchState s = MakeState((1, 5), (2, 5));
        ExecuteResult r = Exec(0x00208463, s, 0x100); // beq x1, x2, +8
        Assert.True(r.BranchTaken);
        Assert.Equal(0x108UL, r.BranchTarget);
    }

    [Fact]
    public void Execute_Beq_NotTaken() {
        Rv32ArchState s = MakeState((1, 5), (2, 6));
        ExecuteResult r = Exec(0x00208463, s, 0x100);
        Assert.False(r.BranchTaken);
        Assert.Equal(0x104UL, r.BranchTarget);
    }

    [Fact]
    public void Execute_Blt_Taken_Signed() {
        Rv32ArchState s = MakeState((1, unchecked((uint)-1)), (2, 0));
        // blt x1, x2, +8  (-1 < 0 signed → taken)
        ExecuteResult r = Exec(0x0020c463, s, 0x100);
        Assert.True(r.BranchTaken);
    }

    // ── JAL / JALR ────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Jal() {
        Rv32ArchState s = MakeState();
        ExecuteResult r = Exec(0x008000EF, s, 0x100);  // jal x1, +8
        Assert.Equal(0x104UL, r.RegisterResult.Value); // return address = PC+4
        Assert.True(r.BranchTaken);
        Assert.Equal(0x108UL, r.BranchTarget); // target = PC+8
    }

    [Fact]
    public void Execute_Jalr_ClearsLSB() {
        Rv32ArchState s = MakeState((2, 0x101));      // rs1 has LSB set
        ExecuteResult r = Exec(0x00010067, s, 0x100); // jalr x0, x2, 0
        Assert.Equal(0x100UL, r.BranchTarget);        // LSB cleared
    }

    // ── Upper immediates ──────────────────────────────────────────────────────

    [Fact]
    public void Execute_Lui() {
        Rv32ArchState s = MakeState();
        ExecuteResult r = Exec(0x000010B7, s); // lui x1, 1  →  x1 = 0x1000
        Assert.Equal(0x1000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Auipc() {
        Rv32ArchState s = MakeState();
        ExecuteResult r = Exec(0x00001097, s, 0x1000);  // auipc x1, 1
        Assert.Equal(0x2000UL, r.RegisterResult.Value); // PC(0x1000) + (1 << 12)
    }

    // ── System ────────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Ecall_RaisesTrap() {
        Rv32ArchState s = MakeState();
        ExecuteResult r = Exec(0x00000073, s);
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.EnvironmentCallFromM, r.Trap!.Cause);
    }

    [Fact]
    public void Execute_Ebreak_Halts() {
        Rv32ArchState s = MakeState();
        ExecuteResult r = Exec(0x00100073, s);
        Assert.True(r.IsHalt);
        Assert.False(r.HasTrap);
    }

    // ── x0 is hardwired zero ──────────────────────────────────────────────────

    [Fact]
    public void Execute_WriteToX0_IsIgnored() {
        Rv32ArchState s = MakeState((1, 5), (2, 3));
        // add x0, x1, x2 — result goes to x0, should stay 0
        Exec(0x00208033, s);
        // The executor returns the result — the writeback stage ignores rd=0
        // But we can verify x0 reads as 0 regardless
        Assert.Equal(0UL, s.IntegerRegisters.Read(0));
    }

    // ── R-type (additional) ───────────────────────────────────────────────────

    [Fact]
    public void Execute_And() {
        Rv32ArchState s = MakeState((1, 0xAA), (2, 0xF0));
        ExecuteResult r = Exec(0x0020F1B3, s); // and x3, x1, x2
        Assert.Equal(0xA0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Or() {
        Rv32ArchState s = MakeState((1, 0x0F), (2, 0xF0));
        ExecuteResult r = Exec(0x0020E1B3, s); // or x3, x1, x2
        Assert.Equal(0xFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Xor() {
        Rv32ArchState s = MakeState((1, 0xFF), (2, 0xF0));
        ExecuteResult r = Exec(0x0020C1B3, s); // xor x3, x1, x2
        Assert.Equal(0x0FUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Sll() {
        Rv32ArchState s = MakeState((1, 1), (2, 4));
        ExecuteResult r = Exec(0x002091B3, s); // sll x3, x1, x2  →  1 << 4 = 16
        Assert.Equal(16UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Srl_LogicalShift() {
        Rv32ArchState s = MakeState((1, 0x80000000), (2, 1));
        ExecuteResult r = Exec(0x0020D1B3, s); // srl x3, x1, x2  →  0x40000000
        Assert.Equal(0x40000000UL, r.RegisterResult.Value);
    }

    // ── I-type ALU (additional) ───────────────────────────────────────────────

    [Fact]
    public void Execute_Ori() {
        Rv32ArchState s = MakeState((1, 0xF0));
        ExecuteResult r = Exec(0x00F0E113, s); // ori x2, x1, 0xF  →  0xFF
        Assert.Equal(0xFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Xori() {
        Rv32ArchState s = MakeState((1, 0xFF));
        ExecuteResult r = Exec(0x00F0C113, s); // xori x2, x1, 0xF  →  0xF0
        Assert.Equal(0xF0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Slti_True() {
        Rv32ArchState s = MakeState((1, unchecked((uint)-1)));
        ExecuteResult r = Exec(0x0000A113, s); // slti x2, x1, 0  →  -1 < 0 signed → 1
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Slti_False() {
        Rv32ArchState s = MakeState((1, 5));
        ExecuteResult r = Exec(0x0000A113, s); // slti x2, x1, 0  →  5 < 0 signed → 0
        Assert.Equal(0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Sltiu_UnsignedComparison() {
        Rv32ArchState s = MakeState((1, 0));
        ExecuteResult r = Exec(0x0010B113, s); // sltiu x2, x1, 1  →  0 < 1 unsigned → 1
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Slli() {
        Rv32ArchState s = MakeState((1, 1));
        ExecuteResult r = Exec(0x00409113, s); // slli x2, x1, 4  →  16
        Assert.Equal(16UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Srli_LogicalShift() {
        Rv32ArchState s = MakeState((1, 0x80000000));
        ExecuteResult r = Exec(0x0010D113, s); // srli x2, x1, 1  →  0x40000000
        Assert.Equal(0x40000000UL, r.RegisterResult.Value);
    }

    // ── Branches (additional) ─────────────────────────────────────────────────

    [Fact]
    public void Execute_Bne_Taken() {
        Rv32ArchState s = MakeState((1, 1), (2, 2));
        ExecuteResult r = Exec(0x00209463, s, 0x100); // bne x1, x2, +8  →  1 != 2 → taken
        Assert.True(r.BranchTaken);
        Assert.Equal(0x108UL, r.BranchTarget);
    }

    [Fact]
    public void Execute_Bne_NotTaken() {
        Rv32ArchState s = MakeState((1, 5), (2, 5));
        ExecuteResult r = Exec(0x00209463, s, 0x100); // bne x1, x2, +8  →  5 == 5 → not taken
        Assert.False(r.BranchTaken);
    }

    [Fact]
    public void Execute_Bge_Taken_Equal() {
        Rv32ArchState s = MakeState((1, 5), (2, 5));
        ExecuteResult r = Exec(0x0020D463, s, 0x100); // bge x1, x2, +8  →  5 >= 5 → taken
        Assert.True(r.BranchTaken);
        Assert.Equal(0x108UL, r.BranchTarget);
    }

    [Fact]
    public void Execute_Bge_NotTaken_Negative() {
        Rv32ArchState s = MakeState((1, unchecked((uint)-1)), (2, 0));
        ExecuteResult r = Exec(0x0020D463, s, 0x100); // bge x1, x2, +8  →  -1 >= 0 signed → false
        Assert.False(r.BranchTaken);
    }

    [Fact]
    public void Execute_Bgeu_Taken() {
        Rv32ArchState s = MakeState((1, 0xFFFFFFFF), (2, 1));
        ExecuteResult r = Exec(0x0020F463, s, 0x100); // bgeu x1, x2, +8  →  large unsigned >= 1 → taken
        Assert.True(r.BranchTaken);
    }

    [Fact]
    public void Execute_Bltu_Taken() {
        Rv32ArchState s = MakeState((1, 0), (2, 1));
        ExecuteResult r = Exec(0x0020E463, s, 0x100); // bltu x1, x2, +8  →  0 < 1 unsigned → taken
        Assert.True(r.BranchTaken);
    }

    // ── Half-word memory ──────────────────────────────────────────────────────

    [Fact]
    public void Execute_Sh_Then_Lh_SignExtends() {
        Rv32ArchState s = MakeState((1, 100), (2, 0x8000)); // 0x8000 = -32768 as int16
        Exec(0x00209023, s);                                // sh x2, 0(x1)
        ExecuteResult r = Exec(0x00009183, s);              // lh x3, 0(x1)
        Assert.Equal(0xFFFF8000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Sh_Then_Lhu_ZeroExtends() {
        Rv32ArchState s = MakeState((1, 100), (2, 0x8000));
        Exec(0x00209023, s);                   // sh x2, 0(x1)
        ExecuteResult r = Exec(0x0000D183, s); // lhu x3, 0(x1)
        Assert.Equal(0x8000UL, r.RegisterResult.Value);
    }

    // ── JALR with link register ───────────────────────────────────────────────

    [Fact]
    public void Execute_Jalr_WithLinkRegister() {
        Rv32ArchState s = MakeState((2, 0x200));
        ExecuteResult r = Exec(0x004100E7, s, 0x100);  // jalr x1, 4(x2)
        Assert.Equal(0x104UL, r.RegisterResult.Value); // link = PC+4
        Assert.True(r.BranchTaken);
        Assert.Equal(0x204UL, r.BranchTarget); // target = x2+4 = 0x204
    }

    // ── M extension ───────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Mul_LowerHalf() {
        // mul x1, x2, x3  (x2=7, x3=6 → x1=42)
        Rv32ArchState s = MakeState((2, 7), (3, 6));
        ExecuteResult r = Exec(0x023100B3, s);
        Assert.Equal(42UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Mul_Overflow_TruncatesToLower32() {
        // 0x80000001 × 2 = 0x100000002 → lower 32 = 0x00000002
        Rv32ArchState s = MakeState((2, 0x80000001), (3, 2));
        ExecuteResult r = Exec(0x023100B3, s);
        Assert.Equal(2UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Mulh_SignedUpperHalf() {
        // (-1) × (-1) = 1, upper 32 of 0x0000_0000_0000_0001 = 0
        Rv32ArchState s = MakeState((2, 0xFFFFFFFF), (3, 0xFFFFFFFF)); // -1 × -1
        ExecuteResult r = Exec(0x023110B3, s);                         // mulh x1, x2, x3
        Assert.Equal(0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Mulh_NegativeTimesPositive() {
        // (-1) × 1 = -1, upper 32 of -1 as 64-bit = 0xFFFFFFFF
        Rv32ArchState s = MakeState((2, 0xFFFFFFFF), (3, 1)); // -1 × 1
        ExecuteResult r = Exec(0x023110B3, s);
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Mulhu_UnsignedUpperHalf() {
        // 0xFFFFFFFF × 0xFFFFFFFF = 0xFFFFFFFE_00000001, upper = 0xFFFFFFFE
        Rv32ArchState s = MakeState((2, 0xFFFFFFFF), (3, 0xFFFFFFFF));
        ExecuteResult r = Exec(0x023130B3, s); // mulhu x1, x2, x3
        Assert.Equal(0xFFFFFFFEUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Mulhsu_SignedUnsignedUpperHalf() {
        // -1 (signed) × 0xFFFFFFFF (unsigned) = -0xFFFFFFFF = -4294967295
        // As 64-bit: 0xFFFF_FFFF_0000_0001, upper 32 = 0xFFFFFFFF
        Rv32ArchState s = MakeState((2, 0xFFFFFFFF), (3, 0xFFFFFFFF)); // -1 × 4294967295
        ExecuteResult r = Exec(0x023120B3, s);                         // mulhsu x1, x2, x3
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Div_Basic() {
        // 10 / 3 = 3 (truncated toward zero)
        Rv32ArchState s = MakeState((2, 10), (3, 3));
        ExecuteResult r = Exec(0x023140B3, s); // div x1, x2, x3
        Assert.Equal(3UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Div_NegativeResult_TruncatesTowardZero() {
        // -7 / 2 = -3 (truncate toward zero, not -4)
        Rv32ArchState s = MakeState((2, unchecked((uint)-7)), (3, 2));
        ExecuteResult r = Exec(0x023140B3, s);
        Assert.Equal(unchecked((uint)-3), (uint)r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Div_ByZero_ReturnsMinusOne() {
        Rv32ArchState s = MakeState((2, 5), (3, 0));
        ExecuteResult r = Exec(0x023140B3, s);
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Div_Overflow_IntMinDivMinusOne() {
        // INT_MIN / -1 overflows — result is INT_MIN per spec
        Rv32ArchState s = MakeState((2, unchecked((uint)int.MinValue)), (3, unchecked((uint)-1)));
        ExecuteResult r = Exec(0x023140B3, s);
        Assert.Equal(unchecked((uint)int.MinValue), (uint)r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Divu_Basic() {
        // 10u / 3u = 3u
        Rv32ArchState s = MakeState((2, 10), (3, 3));
        ExecuteResult r = Exec(0x023150B3, s); // divu x1, x2, x3
        Assert.Equal(3UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Divu_ByZero_ReturnsMaxUint() {
        Rv32ArchState s = MakeState((2, 5), (3, 0));
        ExecuteResult r = Exec(0x023150B3, s);
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Rem_Basic() {
        // 10 % 3 = 1
        Rv32ArchState s = MakeState((2, 10), (3, 3));
        ExecuteResult r = Exec(0x023160B3, s); // rem x1, x2, x3
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Rem_ByZero_ReturnsRs1() {
        Rv32ArchState s = MakeState((2, 42), (3, 0));
        ExecuteResult r = Exec(0x023160B3, s);
        Assert.Equal(42UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Rem_Overflow_ReturnsZero() {
        // INT_MIN % -1 → 0
        Rv32ArchState s = MakeState((2, unchecked((uint)int.MinValue)), (3, unchecked((uint)-1)));
        ExecuteResult r = Exec(0x023160B3, s);
        Assert.Equal(0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Remu_Basic() {
        // 10u % 3u = 1u
        Rv32ArchState s = MakeState((2, 10), (3, 3));
        ExecuteResult r = Exec(0x023170B3, s); // remu x1, x2, x3
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    // ── A extension ───────────────────────────────────────────────────────────

    [Fact]
    public void Execute_AmoaddW_ReturnsPreviousAndUpdatesMemory() {
        // amoadd.w x1, x3, (x2)  — x2=address=100, x3=operand=5, mem[100]=10
        Rv32ArchState s = MakeState((2, 100), (3, 5));
        _mem.Write(100, 10, 4);                     // pre-load memory
        ExecuteResult r = Exec(0x003120AF, s);      // amoadd.w x1, x3, (x2)
        Assert.Equal(10UL, r.RegisterResult.Value); // original value
        Assert.Equal(15UL, _mem.Read(100, 4));      // updated in memory
    }

    [Fact]
    public void Execute_AmoswapW_SwapsValue() {
        Rv32ArchState s = MakeState((2, 100), (3, 99));
        _mem.Write(100, 42, 4);
        ExecuteResult r = Exec(0x083120AF, s);      // amoswap.w x1, x3, (x2)
        Assert.Equal(42UL, r.RegisterResult.Value); // original
        Assert.Equal(99UL, _mem.Read(100, 4));      // swapped
    }

    [Fact]
    public void Execute_AmoandW_MasksMemory() {
        Rv32ArchState s = MakeState((2, 100), (3, 0x0F));
        _mem.Write(100, 0xFF, 4);
        Exec(0x603120AF, s); // amoand.w x1, x3, (x2)
        Assert.Equal(0x0FUL, _mem.Read(100, 4));
    }

    [Fact]
    public void Execute_AmoorW_SetsMemoryBits() {
        Rv32ArchState s = MakeState((2, 100), (3, 0xF0));
        _mem.Write(100, 0x0F, 4);
        Exec(0x403120AF, s); // amoor.w x1, x3, (x2)
        Assert.Equal(0xFFUL, _mem.Read(100, 4));
    }

    [Fact]
    public void Execute_AmominW_KeepsMinimum() {
        // Signed min: mem[100] = -1, rs2 = 1 → min(-1, 1) = -1
        Rv32ArchState s = MakeState((2, 100), (3, 1));
        _mem.Write(100, 0xFFFFFFFF, 4);                // -1 signed
        Exec(0x803120AF, s);                           // amomin.w x1, x3, (x2)  funct5=0x10
        Assert.Equal(0xFFFFFFFFUL, _mem.Read(100, 4)); // -1 is smaller
    }

    [Fact]
    public void Execute_LrW_LoadsValue() {
        Rv32ArchState s = MakeState((2, 100));
        _mem.Write(100, 0xDEADBEEF, 4);
        ExecuteResult r = Exec(0x100120AF, s); // lr.w x1, (x2)
        Assert.Equal(0xDEADBEEFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_ScW_StoresAndReturnsZero() {
        Rv32ArchState s = MakeState((2, 100), (3, 0xABCD));
        Exec(0x100120AF, s);                       // lr.w x1, (x2) — establishes reservation at 100
        ExecuteResult r = Exec(0x183120AF, s);     // sc.w x1, x3, (x2)
        Assert.Equal(0UL, r.RegisterResult.Value); // 0 = success
        Assert.Equal(0xABCDUL, _mem.Read(100, 4)); // value stored
    }

    // ── Zabha extension (byte/halfword AMOs) ─────────────────────────────────

    [Fact]
    public void Execute_AmoswapB_ReturnsSignExtendedOldByteAndWritesNew() {
        // amoswap.b x1, x3, (x2) — x2=100, x3=42, mem[100]=0xFF (byte -1 signed)
        // rd = sign_ext(0xFF from 8 bits) = 0xFFFFFFFF; mem[100] = 42
        Rv32ArchState s = MakeState((2, 100), (3, 42));
        _mem.Write(100, 0xFF, 1);
        ExecuteResult r = Exec(0x083100AF, s); // amoswap.b x1, x3, (x2)
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult.Value);
        Assert.Equal(42UL, _mem.Read(100, 1));
    }

    [Fact]
    public void Execute_AmominB_SignedByteComparison() {
        // amomin.b x1, x3, (x2) — mem[100]=0xFF (-1), rs2=1 → min(-1,1)=-1 → memory unchanged
        Rv32ArchState s = MakeState((2, 100), (3, 1));
        _mem.Write(100, 0xFF, 1);
        ExecuteResult r = Exec(0x803100AF, s);              // amomin.b x1, x3, (x2)
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult.Value); // sign-extended -1
        Assert.Equal(0xFFUL, _mem.Read(100, 1));            // unchanged (−1 is the minimum)
    }

    [Fact]
    public void Execute_AmoaddH_UpdatesHalfwordAndSignExtends() {
        // amoadd.h x1, x3, (x2) — mem[100]=255 (0x00FF halfword), rs2=1 → 256; rd=255
        Rv32ArchState s = MakeState((2, 100), (3, 1));
        _mem.Write(100, 0x00FF, 2);
        ExecuteResult r = Exec(0x003110AF, s);       // amoadd.h x1, x3, (x2)
        Assert.Equal(255UL, r.RegisterResult.Value); // sign_ext(0x00FF from 16 bits) = 255
        Assert.Equal(256UL, _mem.Read(100, 2));
    }

    [Fact]
    public void Execute_AmoaddH_NegativeHalfwordSignExtends() {
        // Halfword 0x8000 = -32768; sign-extended to 32 bits = 0xFFFF8000
        Rv32ArchState s = MakeState((2, 100), (3, 0));
        _mem.Write(100, 0x8000, 2);
        ExecuteResult r = Exec(0x003110AF, s); // amoadd.h x1, x3, (x2)  (add 0 → no change)
        Assert.Equal(0xFFFF8000UL, r.RegisterResult.Value);
    }

    // ── Zacas extension (compare-and-swap) ───────────────────────────────────

    [Fact]
    public void Execute_AmocasW_Success_WritesNewValueAndReturnsOld() {
        // amocas.w x1, x3, (x2) — x1=42 (comparand), x2=100 (address), x3=99 (new)
        // mem[100]=42 matches rd → write 99; return 42
        Rv32ArchState s = MakeState((1, 42), (2, 100), (3, 99));
        _mem.Write(100, 42, 4);
        ExecuteResult r = Exec(0x283120AF, s); // amocas.w x1, x3, (x2)
        Assert.Equal(42UL, r.RegisterResult.Value);
        Assert.Equal(99UL, _mem.Read(100, 4));
    }

    [Fact]
    public void Execute_AmocasW_Failure_LeavesMemoryUnchangedAndReturnsOld() {
        // amocas.w x1, x3, (x2) — x1=7 (comparand), x2=100, x3=99 (new)
        // mem[100]=42 ≠ rd → no write; return 42
        Rv32ArchState s = MakeState((1, 7), (2, 100), (3, 99));
        _mem.Write(100, 42, 4);
        ExecuteResult r = Exec(0x283120AF, s); // amocas.w x1, x3, (x2)
        Assert.Equal(42UL, r.RegisterResult.Value);
        Assert.Equal(42UL, _mem.Read(100, 4)); // unchanged
    }

    // ── Zabha+Zacas extension (narrow compare-and-swap) ───────────────────────

    // R-type AMO encoding: opcode=0x2F, funct5=0x05 (CAS), funct3 selects width
    // (0=byte, 1=halfword, 2=word), aq/rl bits left at 0.
    private static uint AmoCas(uint funct3, int rd, int rs1, int rs2) =>
        (0x05u << 27) | ((uint)rs2 << 20) | ((uint)rs1 << 15) | (funct3 << 12) | ((uint)rd << 7) | 0x2F;

    [Fact]
    public void Execute_AmocasB_Success_WritesNewValueAndReturnsOld() {
        // amocas.b x1, x3, (x2) — x1=0xFF (comparand, matches mem byte), x3=42 (new)
        Rv32ArchState s = MakeState((1, 0xFF), (2, 100), (3, 42));
        _mem.Write(100, 0xFF, 1);
        ExecuteResult r = Exec(AmoCas(0, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult.Value); // sign-extended old byte (-1)
        Assert.Equal(42UL, _mem.Read(100, 1));
    }

    [Fact]
    public void Execute_AmocasB_Failure_LeavesMemoryUnchangedAndReturnsOld() {
        Rv32ArchState s = MakeState((1, 7), (2, 100), (3, 42));
        _mem.Write(100, 0xFF, 1);
        ExecuteResult r = Exec(AmoCas(0, 1, 2, 3), s);
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult.Value);
        Assert.Equal(0xFFUL, _mem.Read(100, 1)); // unchanged
    }

    [Fact]
    public void Execute_AmocasB_OnlyComparesLowByteOfRd() {
        // rd holds 0xDEADBEFF — only the low byte (0xFF) participates in the comparison.
        Rv32ArchState s = MakeState((1, 0xDEADBEFF), (2, 100), (3, 42));
        _mem.Write(100, 0xFF, 1);
        Exec(AmoCas(0, 1, 2, 3), s);
        Assert.Equal(42UL, _mem.Read(100, 1));
    }

    [Fact]
    public void Execute_AmocasH_Success_WritesNewValueAndReturnsOld() {
        // amocas.h x1, x3, (x2) — x1=0x8000 (comparand, matches mem halfword), x3=256 (new)
        Rv32ArchState s = MakeState((1, 0x8000), (2, 100), (3, 256));
        _mem.Write(100, 0x8000, 2);
        ExecuteResult r = Exec(AmoCas(1, 1, 2, 3), s);
        Assert.Equal(0xFFFF8000UL, r.RegisterResult.Value); // sign-extended old halfword (-32768)
        Assert.Equal(256UL, _mem.Read(100, 2));
    }

    [Fact]
    public void Execute_AmocasH_Failure_LeavesMemoryUnchangedAndReturnsOld() {
        Rv32ArchState s = MakeState((1, 7), (2, 100), (3, 256));
        _mem.Write(100, 0x00FF, 2);
        ExecuteResult r = Exec(AmoCas(1, 1, 2, 3), s);
        Assert.Equal(255UL, r.RegisterResult.Value);
        Assert.Equal(0x00FFUL, _mem.Read(100, 2)); // unchanged
    }

    // ── Zacas extension, RV32 register-pair amocas.d ──────────────────────────
    // funct3=3 (doubleword); rd/rs2 must be even (rd, rd+1) and (rs2, rs2+1) hold
    // the low/high halves. rs1 holds the address.

    [Fact]
    public void Execute_AmocasDPair_Success_WritesNewValueAndReturnsOldAcrossBothHalves() {
        // x2/x3 = comparand (low/high), x4 = address, x6/x7 = new value (low/high)
        Rv32ArchState s = MakeState((2, 0x1111_2222), (3, 0x3333_4444), (4, 100), (6, 0x5555_6666), (7, 0x7777_8888));
        _mem.Write(100, 0x1111_2222UL | (0x3333_4444UL << 32), 8);
        ExecuteResult r = Exec(AmoCas(3, 2, 4, 6), s);       // amocas.d x2, x6, (x4)
        Assert.Equal(0x1111_2222UL, r.RegisterResult.Value); // low half via normal dest path
        r.SideEffect?.Invoke(s);
        Assert.Equal(0x3333_4444UL, s.IntegerRegisters.Read(3)); // high half via SideEffect
        Assert.Equal(0x5555_6666UL | (0x7777_8888UL << 32), _mem.Read(100, 8));
    }

    [Fact]
    public void Execute_AmocasDPair_Failure_LeavesMemoryUnchangedAndReturnsOld() {
        Rv32ArchState s = MakeState((2, 0), (3, 0), (4, 100), (6, 0x5555_6666), (7, 0x7777_8888));
        _mem.Write(100, 0x1111_2222UL | (0x3333_4444UL << 32), 8);
        ExecuteResult r = Exec(AmoCas(3, 2, 4, 6), s); // comparand (x2/x3=0) doesn't match mem
        Assert.Equal(0x1111_2222UL, r.RegisterResult.Value);
        r.SideEffect?.Invoke(s);
        Assert.Equal(0x3333_4444UL, s.IntegerRegisters.Read(3));
        Assert.Equal(0x1111_2222UL | (0x3333_4444UL << 32), _mem.Read(100, 8)); // unchanged
    }

    [Fact]
    public void Decode_AmocasDPair_OddRd_ThrowsIllegalInstruction() {
        Assert.Throws<IllegalInstructionException>(() => _dec.Decode(0, AmoCas(3, 1, 4, 6)));
    }

    [Fact]
    public void Decode_AmocasDPair_OddRs2_ThrowsIllegalInstruction() {
        Assert.Throws<IllegalInstructionException>(() => _dec.Decode(0, AmoCas(3, 2, 4, 5)));
    }

    [Fact]
    public void Decode_AmocasDPair_RdX0_IsLegal() {
        // rd=x0 is even, so the encoding itself is legal even though the low-half
        // write is discarded; the high half (x1) still gets the SideEffect write.
        ITooth instr = _dec.Decode(0, AmoCas(3, 0, 4, 6));
        Assert.Equal(1, instr.SecondaryDestinationRegister);
    }

    // ── F extension ───────────────────────────────────────────────────────────
    // FP registers are at unified indices 32-63 (f0=32 … f31=63).
    // MakeState accepts any index in 0-63; indices 32+ write float registers.

    private static uint Fb(float f) => BitConverter.SingleToUInt32Bits(f);
    private static float Af(ulong bits) => BitConverter.Int32BitsToSingle((int)bits);

    [Fact]
    public void Execute_Flw_LoadsFloatBitsFromMemory() {
        // flw f1, 4(x2)  0x00412087 — x2=100, mem[104]=bits of 3.14f; result is NaN-boxed
        uint bits = Fb(3.14f);
        Rv32ArchState s = MakeState((2, 100));
        _mem.Write(104, bits, 4);
        ExecuteResult r = Exec(0x00412087, s);
        Assert.Equal(0xFFFFFFFF00000000UL | bits, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Fsw_StoresFloatBitsToMemory() {
        // fsw f2, 4(x1)  0x0020A227 — x1=100, f2=2.5f
        uint bits = Fb(2.5f);
        Rv32ArchState s = MakeState((1, 100), (34, bits)); // f2 = index 34
        Exec(0x0020A227, s);
        Assert.Equal(bits, _mem.Read(104, 4));
    }

    [Fact]
    public void Execute_FaddS_AddsFloats() {
        // fadd.s f1, f2, f3  0x003100D3 — f2=2.0, f3=3.0 → f1=5.0
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(3.0f)));
        ExecuteResult r = Exec(0x003100D3, s);
        Assert.Equal(5.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsubS_SubtractsFloats() {
        // fsub.s f1, f2, f3  0x083100D3 — f2=5.0, f3=3.0 → 2.0
        Rv32ArchState s = MakeState((34, Fb(5.0f)), (35, Fb(3.0f)));
        ExecuteResult r = Exec(0x083100D3, s);
        Assert.Equal(2.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FmulS_MultipliesFloats() {
        // fmul.s f1, f2, f3  0x103100D3 — f2=2.0, f3=3.0 → 6.0
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(3.0f)));
        ExecuteResult r = Exec(0x103100D3, s);
        Assert.Equal(6.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FdivS_DividesFloats() {
        // fdiv.s f1, f2, f3  0x183100D3 — f2=6.0, f3=2.0 → 3.0
        Rv32ArchState s = MakeState((34, Fb(6.0f)), (35, Fb(2.0f)));
        ExecuteResult r = Exec(0x183100D3, s);
        Assert.Equal(3.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsqrtS_ComputesSquareRoot() {
        // fsqrt.s f1, f2  0x580100D3 — f2=4.0 → 2.0
        Rv32ArchState s = MakeState((34, Fb(4.0f)));
        ExecuteResult r = Exec(0x580100D3, s);
        Assert.Equal(2.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsgnjS_InjectsPositiveSign() {
        // fsgnj.s f1, f2, f3  0x203100D3 — f2=-2.0 (neg), f3=3.0 (pos) → +2.0
        Rv32ArchState s = MakeState((34, Fb(-2.0f)), (35, Fb(3.0f)));
        ExecuteResult r = Exec(0x203100D3, s);
        Assert.Equal(2.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsgnjnS_InjectsNegatedSign() {
        // fsgnjn.s f1, f2, f3  0x203110D3 — f2=2.0 (pos), f3=3.0 (pos) → -2.0
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(3.0f)));
        ExecuteResult r = Exec(0x203110D3, s);
        Assert.Equal(-2.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsgnjxS_XorSign() {
        // fsgnjx.s f1, f2, f3  0x203120D3 — f2=2.0 (pos), f3=-3.0 (neg) → -2.0
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(-3.0f)));
        ExecuteResult r = Exec(0x203120D3, s);
        Assert.Equal(-2.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FminS_ReturnsSmaller() {
        // fmin.s f1, f2, f3  0x283100D3 — f2=2.0, f3=3.0 → 2.0
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(3.0f)));
        ExecuteResult r = Exec(0x283100D3, s);
        Assert.Equal(2.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FminS_ReturnsNonNanWhenOneIsNaN() {
        // fmin(NaN, 2.0) = 2.0 per RISC-V spec
        Rv32ArchState s = MakeState((34, Fb(float.NaN)), (35, Fb(2.0f)));
        ExecuteResult r = Exec(0x283100D3, s);
        Assert.Equal(2.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FminS_ReturnsNegativeZeroWhenBothAreZero() {
        // fmin(-0.0, +0.0) = -0.0; result is NaN-boxed
        Rv32ArchState s = MakeState((34, Fb(-0.0f)), (35, Fb(0.0f)));
        ExecuteResult r = Exec(0x283100D3, s);
        Assert.Equal(0xFFFFFFFF80000000UL, r.RegisterResult.Value); // NaN-boxed -0.0
    }

    [Fact]
    public void Execute_FmaxS_ReturnsLarger() {
        // fmax.s f1, f2, f3  0x283110D3 — f2=2.0, f3=3.0 → 3.0
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(3.0f)));
        ExecuteResult r = Exec(0x283110D3, s);
        Assert.Equal(3.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FmaxS_ReturnsNonNanWhenOneIsNaN() {
        // fmax(NaN, 3.0) = 3.0
        Rv32ArchState s = MakeState((34, Fb(float.NaN)), (35, Fb(3.0f)));
        ExecuteResult r = Exec(0x283110D3, s);
        Assert.Equal(3.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FmaxS_ReturnsPositiveZeroWhenBothAreZero() {
        // fmax(-0.0, +0.0) = +0.0; result is NaN-boxed
        Rv32ArchState s = MakeState((34, Fb(-0.0f)), (35, Fb(0.0f)));
        ExecuteResult r = Exec(0x283110D3, s);
        Assert.Equal(0xFFFFFFFF00000000UL, r.RegisterResult.Value); // NaN-boxed +0.0
    }

    [Fact]
    public void Execute_FeqS_ReturnsOneWhenEqual() {
        // feq.s x1, f2, f3  0xA03120D3
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(2.0f)));
        ExecuteResult r = Exec(0xA03120D3, s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FeqS_ReturnsZeroWhenNotEqual() {
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(3.0f)));
        ExecuteResult r = Exec(0xA03120D3, s);
        Assert.Equal(0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FltS_ReturnsOneWhenLess() {
        // flt.s x1, f2, f3  0xA03110D3
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(3.0f)));
        ExecuteResult r = Exec(0xA03110D3, s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FleS_ReturnsOneWhenEqual() {
        // fle.s x1, f2, f3  0xA03100D3 — 2.0 ≤ 2.0
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(2.0f)));
        ExecuteResult r = Exec(0xA03100D3, s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FclassS_PositiveNormal() {
        // fclass.s x1, f2  0xE00110D3 — 2.0f is +normal → bit 6
        Rv32ArchState s = MakeState((34, Fb(2.0f)));
        ExecuteResult r = Exec(0xE00110D3, s);
        Assert.Equal(1UL << 6, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FclassS_PositiveInfinity() {
        Rv32ArchState s = MakeState((34, Fb(float.PositiveInfinity)));
        ExecuteResult r = Exec(0xE00110D3, s);
        Assert.Equal(1UL << 7, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FclassS_NegativeZero() {
        Rv32ArchState s = MakeState((34, Fb(-0.0f)));
        ExecuteResult r = Exec(0xE00110D3, s);
        Assert.Equal(1UL << 3, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FclassS_PositiveZero() {
        Rv32ArchState s = MakeState((34, 0u));
        ExecuteResult r = Exec(0xE00110D3, s);
        Assert.Equal(1UL << 4, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FclassS_QuietNaN() {
        // float.NaN on .NET is a quiet NaN (signaling bit set in fraction)
        Rv32ArchState s = MakeState((34, Fb(float.NaN)));
        ExecuteResult r = Exec(0xE00110D3, s);
        Assert.Equal(1UL << 9, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtWS_TruncatesPositiveFloat() {
        // fcvt.w.s x1, f2, rtz  0xC00110D3 — 3.7f → 3 (RTZ truncates toward zero)
        Rv32ArchState s = MakeState((34, Fb(3.7f)));
        ExecuteResult r = Exec(0xC00110D3, s);
        Assert.Equal(3UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtWS_TruncatesNegativeFloat() {
        // -3.7f → -3 (truncation toward zero) → 0xFFFFFFFD as uint32
        Rv32ArchState s = MakeState((34, Fb(-3.7f)));
        ExecuteResult r = Exec(0xC00110D3, s);
        Assert.Equal(unchecked((uint)-3), r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtWuS_ConvertsPositiveFloat() {
        // fcvt.wu.s x1, f2, rtz  0xC01110D3 — 5.9f → 5u (RTZ truncates toward zero)
        Rv32ArchState s = MakeState((34, Fb(5.9f)));
        ExecuteResult r = Exec(0xC01110D3, s);
        Assert.Equal(5UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtSW_ConvertsNegativeInt() {
        // fcvt.s.w f1, x2  0xD00100D3 — x2=-5 → -5.0f
        Rv32ArchState s = MakeState((2, unchecked((uint)-5)));
        ExecuteResult r = Exec(0xD00100D3, s);
        Assert.Equal(-5.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FcvtSWu_ConvertsLargeUnsigned() {
        // fcvt.s.wu f1, x2  0xD01100D3 — x2=0xFFFFFFFF → 4294967295.0f
        Rv32ArchState s = MakeState((2, 0xFFFFFFFF));
        ExecuteResult r = Exec(0xD01100D3, s);
        Assert.Equal(0xFFFFFFFFu, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FmvXW_CopiesBitsToIntReg() {
        // fmv.x.w x1, f2  0xE00100D3 — f2 holds bits of -1.0f
        uint bits = Fb(-1.0f); // 0xBF800000
        Rv32ArchState s = MakeState((34, bits));
        ExecuteResult r = Exec(0xE00100D3, s);
        Assert.Equal(bits, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FmvWX_CopiesBitsToFpReg() {
        // fmv.w.x f1, x2  0xF00100D3 — x2=0x40000000 (bits of 2.0f); writes NaN-boxed
        uint bits = Fb(2.0f); // 0x40000000
        Rv32ArchState s = MakeState((2, bits));
        ExecuteResult r = Exec(0xF00100D3, s);
        Assert.Equal(0xFFFFFFFF00000000UL | bits, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FmaddS_FusedMultiplyAdd() {
        // fmadd.s f1, f2, f3, f4  0x203100C3 — f2=2.0, f3=3.0, f4=1.0 → 7.0
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(3.0f)), (36, Fb(1.0f)));
        ExecuteResult r = Exec(0x203100C3, s);
        Assert.Equal(7.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FmsubS_FusedMultiplySubtract() {
        // fmsub.s f1, f2, f3, f4  0x203100C7 — f2*f3 - f4 = 6-1 = 5.0
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(3.0f)), (36, Fb(1.0f)));
        ExecuteResult r = Exec(0x203100C7, s);
        Assert.Equal(5.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FnmsubS_NegatedFusedMultiplySubtract() {
        // fnmsub.s f1, f2, f3, f4  0x203100CB — -(f2*f3) + f4 = -6+1 = -5.0
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(3.0f)), (36, Fb(1.0f)));
        ExecuteResult r = Exec(0x203100CB, s);
        Assert.Equal(-5.0f, Af(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FnmaddS_NegatedFusedMultiplyAdd() {
        // fnmadd.s f1, f2, f3, f4  0x203100CF — -(f2*f3) - f4 = -6-1 = -7.0
        Rv32ArchState s = MakeState((34, Fb(2.0f)), (35, Fb(3.0f)), (36, Fb(1.0f)));
        ExecuteResult r = Exec(0x203100CF, s);
        Assert.Equal(-7.0f, Af(r.RegisterResult.Value));
    }

    // ── Privilege guards ───────────────────────────────────────────────────────

    [Fact]
    public void Execute_Mret_FromSupervisor_RaisesIllegalInstruction() {
        Rv32ArchState s = MakeState();
        s.PrivilegeLevel = RvPrivilege.Supervisor;
        ExecuteResult r = Exec(0x30200073, s); // mret
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap.Cause);
    }

    [Fact]
    public void Execute_Sret_FromUser_RaisesIllegalInstruction() {
        Rv32ArchState s = MakeState();
        s.PrivilegeLevel = RvPrivilege.User;
        ExecuteResult r = Exec(0x10200073, s); // sret
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap.Cause);
    }

    // ── Sv32 address translation ───────────────────────────────────────────────

    // Builds a 64KB flat memory with a two-level Sv32 page table:
    //   Root PT (PPN=1) at PA 0x1000 — entry 0 points to level-1 PT (PPN=2) at PA 0x2000
    //   Level-1 PT (PPN=2) at PA 0x2000 — entries 0-7 each map to PA (PPN+3)*0x1000
    //   satp = 0x80000001 (MODE=1, PPN=1)
    // Returns (mem, satp). The caller writes PTEs for individual VPN[0] entries.
    private static (FlatMemory mem, uint satp) BuildSv32Memory() {
        var mem = new FlatMemory(0x10000);
        // Root PT entry 0: pointer to level-1 PT at PA 0x2000 (PPN=2), V=1
        mem.Write(0x1000UL, 0x801u, 4); // (2<<10)|1
        return (mem, 0x80000001u);
    }

    // PTE for a read-write user page at the given PPN, with A and D bits set.
    private static uint UserRwPte(uint ppn) => (ppn << 10) | 0b1101_0111u; // D|A|U|W|R|V = 0xD7

    [Fact]
    public void Execute_Load_BareMode_NoTranslation() {
        // satp = 0 (MODE=0) — VA is used directly as PA; existing memory semantics unchanged
        var mem = new FlatMemory(4096);
        mem.Write(100UL, 0xDEADBEEFu, 4);
        Rv32ArchState s = MakeState((1, 100u));
        s.PrivilegeLevel = RvPrivilege.User;
        // satp defaults to 0 in a freshly-constructed Rv32ArchState
        ITooth instr = _dec.Decode(0, 0x0000A183); // lw x3, 0(x1)
        ExecuteResult r = _exe.Execute(instr, s, mem);
        Assert.Null(r.Trap);
        Assert.Equal(0xDEADBEEFUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Load_Sv32_ValidUserMapping_TranslatesAddress() {
        // VA 0x00003000 → VPN[1]=0, VPN[0]=3, offset=0 → PA 0x3000
        (FlatMemory mem, uint satp) = BuildSv32Memory();
        mem.Write(0x200CUL, UserRwPte(3), 4); // level-1 PT entry 3 → PA 0x3000
        mem.Write(0x3000UL, 0xBEEFCAFEu, 4);
        Rv32ArchState s = MakeState((1, 0x00003000u));
        s.SystemRegisters.Write(CsrFile.Satp, satp, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.User;
        ITooth instr = _dec.Decode(0, 0x0000A183); // lw x3, 0(x1)
        ExecuteResult r = _exe.Execute(instr, s, mem);
        Assert.Null(r.Trap);
        Assert.Equal(0xBEEFCAFEUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Load_Sv32_InvalidPte_RaisesLoadPageFault() {
        // Level-1 PTE for VPN[0]=4 is zero (V=0) — page not present
        (FlatMemory mem, uint satp) = BuildSv32Memory();
        // PTE at 0x2010 left as 0 (default FlatMemory)
        Rv32ArchState s = MakeState((1, 0x00004000u)); // VA → VPN[0]=4
        s.SystemRegisters.Write(CsrFile.Satp, satp, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.User;
        ITooth instr = _dec.Decode(0, 0x0000A183); // lw x3, 0(x1)
        ExecuteResult r = _exe.Execute(instr, s, mem);
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.LoadPageFault, r.Trap.Cause);
        Assert.Equal(0x00004000UL, r.Trap.TrapValue); // tval = faulting VA
    }

    [Fact]
    public void Execute_Store_Sv32_WriteProtected_RaisesStorePageFault() {
        // PTE has R=1, V=1, U=1, A=1 but W=0 — read-only page
        (FlatMemory mem, uint satp) = BuildSv32Memory();
        const uint roPage = (5u << 10) | 0b0101_0011u; // A|U|R|V, no W, no D
        mem.Write(0x2014UL, roPage, 4);                // level-1 PT entry 5 → PA 0x5000
        Rv32ArchState s = MakeState((1, 0x00005000u), (2, 0xABCDu));
        s.SystemRegisters.Write(CsrFile.Satp, satp, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.User;
        ITooth instr = _dec.Decode(0, 0x0020A023); // sw x2, 0(x1)
        ExecuteResult r = _exe.Execute(instr, s, mem);
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.StorePageFault, r.Trap.Cause);
        Assert.Equal(0x00005000UL, r.Trap.TrapValue);
    }

    [Fact]
    public void Execute_Load_Sv32_AccessBitClear_RaisesLoadPageFault() {
        // PTE is otherwise valid but A=0 (fault-on-access model)
        (FlatMemory mem, uint satp) = BuildSv32Memory();
        const uint noABit = (6u << 10) | 0b0001_0111u; // U|W|R|V, no A, no D
        mem.Write(0x2018UL, noABit, 4);                // level-1 PT entry 6
        Rv32ArchState s = MakeState((1, 0x00006000u));
        s.SystemRegisters.Write(CsrFile.Satp, satp, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.User;
        ITooth instr = _dec.Decode(0, 0x0000A183); // lw x3, 0(x1)
        ExecuteResult r = _exe.Execute(instr, s, mem);
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.LoadPageFault, r.Trap.Cause);
    }

    [Fact]
    public void Execute_Load_Sv32_KernelPage_UserAccess_RaisesLoadPageFault() {
        // PTE has U=0 (kernel page); U-mode access must fault
        (FlatMemory mem, uint satp) = BuildSv32Memory();
        const uint kernelPage = (7u << 10) | 0b1100_0011u; // D|A|R|V, no U, no W
        mem.Write(0x201CUL, kernelPage, 4);                // level-1 PT entry 7
        Rv32ArchState s = MakeState((1, 0x00007000u));
        s.SystemRegisters.Write(CsrFile.Satp, satp, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.User;
        ITooth instr = _dec.Decode(0, 0x0000A183); // lw x3, 0(x1)
        ExecuteResult r = _exe.Execute(instr, s, mem);
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.LoadPageFault, r.Trap.Cause);
    }

    [Fact]
    public void Execute_Load_Sv32_UserPage_SMode_SumDisabled_RaisesLoadPageFault() {
        // S-mode + U-page + SUM=0 (default): data load must fault (priv spec §4.3.1)
        (FlatMemory mem, uint satp) = BuildSv32Memory();
        mem.Write(0x2000UL, UserRwPte(3), 4); // L1 entry 0 → PA 0x3000
        mem.Write(0x3000UL, 0xCAFEBABEu, 4);
        Rv32ArchState s = MakeState((1, 0x00000000u));
        s.SystemRegisters.Write(CsrFile.Satp, satp, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.Supervisor; // SUM bit left clear
        ITooth instr = _dec.Decode(0, 0x0000A183); // lw x3, 0(x1)
        ExecuteResult r = _exe.Execute(instr, s, mem);
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.LoadPageFault, r.Trap.Cause);
    }

    [Fact]
    public void Execute_Load_Sv32_UserPage_SMode_SumEnabled_Succeeds() {
        // S-mode + U-page + SUM=1: data load must succeed (priv spec §4.3.1)
        (FlatMemory mem, uint satp) = BuildSv32Memory();
        mem.Write(0x2004UL, UserRwPte(4), 4); // L1 entry 1 → PA 0x4000
        mem.Write(0x4000UL, 0xDEADC0DEu, 4);
        Rv32ArchState s = MakeState((1, 0x00001000u));
        s.SystemRegisters.Write(CsrFile.Satp, satp, RvPrivilege.Machine);
        s.SystemRegisters.Write(CsrFile.Sstatus, CsrFile.SstatusSum, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.Supervisor;
        ITooth instr = _dec.Decode(0, 0x0000A183); // lw x3, 0(x1)
        ExecuteResult r = _exe.Execute(instr, s, mem);
        Assert.Null(r.Trap);
        Assert.Equal(0xDEADC0DEUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Fetch_Sv32_UserPage_SMode_SumEnabled_StillRaisesInstructionPageFault() {
        // SUM never grants S-mode instruction-fetch access to U-pages (priv spec §4.3.1).
        // RvFetchTranslator always passes sum=false regardless of sstatus.SUM.
        (FlatMemory mem, uint satp) = BuildSv32Memory();
        const uint execUserPte = (5u << 10) | 0b0101_1111u; // A|U|X|R|V
        mem.Write(0x2008UL, execUserPte, 4);                // L1 entry 2 → VA 0x2000 → PA 0x5000
        Rv32ArchState s = MakeState();
        s.SystemRegisters.Write(CsrFile.Satp, satp, RvPrivilege.Machine);
        s.SystemRegisters.Write(CsrFile.Sstatus, CsrFile.SstatusSum, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.Supervisor;
        var translator = new RvFetchTranslator(s, mem);
        (_, int fault) = translator.Translate(0x00002000UL);
        Assert.Equal(RvTrapCause.InstructionPageFault, fault);
    }

    [Fact]
    public void Execute_CsrRead_InsufficientPrivilege_RaisesIllegalInstruction() {
        // csrrs x10, mstatus, x0 (0x30002573) — U-mode cannot read M-mode CSR 0x300
        Rv32ArchState s = MakeState();
        s.PrivilegeLevel = RvPrivilege.User;
        ExecuteResult r = Exec(0x30002573, s);
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap.Cause);
    }

    [Fact]
    public void Execute_CsrWrite_ReadOnlyCsr_RaisesIllegalInstruction() {
        // csrrw x0, cycle, x1 (0xC0009073) — cycle (0xC00) is a read-only CSR
        Rv32ArchState s = MakeState((1, 42));
        s.PrivilegeLevel = RvPrivilege.Machine;
        ExecuteResult r = Exec(0xC0009073, s);
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap.Cause);
    }

    // ── D extension ───────────────────────────────────────────────────────────
    // FP registers hold 64-bit values for D; helpers write/read raw bits.

    private static ulong Dbl(double d) => (ulong)BitConverter.DoubleToInt64Bits(d);
    private static double Adbl(ulong bits) => BitConverter.Int64BitsToDouble((long)bits);

    private static Rv32ArchState MakeDState(params (int reg, ulong val)[] regs) {
        var s = new Rv32ArchState();
        foreach ((int r, ulong v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    [Fact]
    public void Execute_Fld_LoadsDoubleBitsFromMemory() {
        // fld f1, 4(x2)  — x2=100, mem[104]=bits of 3.14, result is raw double bits
        ulong bits = Dbl(3.14);
        Rv32ArchState s = MakeDState((2, 100));
        _mem.Write(104, (uint)bits, 4);
        _mem.Write(108, (uint)(bits >> 32), 4);
        ExecuteResult r = Exec(0x00413087, s); // fld f1, 4(x2)
        Assert.Equal(bits, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Fsd_StoresDoubleBitsToMemory() {
        // fsd f2, 4(x1)  — x1=100, f2=2.5
        ulong bits = Dbl(2.5);
        Rv32ArchState s = MakeDState((1, 100), (34, bits)); // f2 = index 34
        Exec(0x0020B227, s);                                // fsd f2, 4(x1)
        ulong lo = _mem.Read(104, 4);
        ulong hi = _mem.Read(108, 4);
        Assert.Equal(bits, lo | (hi << 32));
    }

    [Fact]
    public void Execute_FaddD_AddsDoubles() {
        // fadd.d f1, f2, f3  0x023100D3 — f2=2.0, f3=3.0 → 5.0
        Rv32ArchState s = MakeDState((34, Dbl(2.0)), (35, Dbl(3.0)));
        ExecuteResult r = Exec(0x023100D3, s);
        Assert.Equal(5.0, Adbl(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsubD_SubtractsDoubles() {
        // fsub.d f1, f2, f3  0x0A3100D3 — f2=5.0, f3=3.0 → 2.0
        Rv32ArchState s = MakeDState((34, Dbl(5.0)), (35, Dbl(3.0)));
        ExecuteResult r = Exec(0x0A3100D3, s);
        Assert.Equal(2.0, Adbl(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FmulD_MultipliesDoubles() {
        // fmul.d f1, f2, f3  0x123100D3 — f2=2.0, f3=3.0 → 6.0
        Rv32ArchState s = MakeDState((34, Dbl(2.0)), (35, Dbl(3.0)));
        ExecuteResult r = Exec(0x123100D3, s);
        Assert.Equal(6.0, Adbl(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FdivD_DividesDoubles() {
        // fdiv.d f1, f2, f3  0x1A3100D3 — f2=6.0, f3=2.0 → 3.0
        Rv32ArchState s = MakeDState((34, Dbl(6.0)), (35, Dbl(2.0)));
        ExecuteResult r = Exec(0x1A3100D3, s);
        Assert.Equal(3.0, Adbl(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsqrtD_ComputesSquareRoot() {
        // fsqrt.d f1, f2  0x5A0100D3 — f2=4.0 → 2.0
        Rv32ArchState s = MakeDState((34, Dbl(4.0)));
        ExecuteResult r = Exec(0x5A0100D3, s);
        Assert.Equal(2.0, Adbl(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FcvtWD_ConvertDoubleToInt() {
        // fcvt.w.d x1, f2  0xC20100D3 — f2=3.7 → x1=3 (truncate toward zero)
        Rv32ArchState s = MakeDState((34, Dbl(3.7)));
        ExecuteResult r = Exec(0xC20100D3, s); // rm=0 (RNE), but for 3→int RISC-V uses dynamic rm
        // rm=1 is RTZ (round toward zero); encoding above uses rm=0 (RNE); 3.7 rounds to 4 with RNE
        Assert.Equal(4UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtWuD_ConvertDoubleToUnsignedInt() {
        // fcvt.wu.d x1, f2  0xC21100D3 — f2=5.0 → x1=5
        Rv32ArchState s = MakeDState((34, Dbl(5.0)));
        ExecuteResult r = Exec(0xC21100D3, s);
        Assert.Equal(5UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtDW_ConvertIntToDouble() {
        // fcvt.d.w f1, x2  0xD20100D3 — x2=42 → f1=42.0
        Rv32ArchState s = MakeDState((2, 42));
        ExecuteResult r = Exec(0xD20100D3, s);
        Assert.Equal(42.0, Adbl(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FcvtSD_ConvertDoubleToSingleWithNaNBox() {
        // fcvt.s.d f1, f2  0x401100D3 — f2=2.5 → f1=2.5f NaN-boxed
        Rv32ArchState s = MakeDState((34, Dbl(2.5)));
        ExecuteResult r = Exec(0x401100D3, s);
        ulong result = r.RegisterResult.Value;
        Assert.Equal(0xFFFFFFFF00000000UL, result & 0xFFFFFFFF00000000UL); // NaN-boxed
        Assert.Equal(2.5f, BitConverter.Int32BitsToSingle((int)(uint)result));
    }

    [Fact]
    public void Execute_FcvtDS_ConvertSingleToDouble() {
        // fcvt.d.s f1, f2  0x420100D3 — f2=2.5f (NaN-boxed) → f1=2.5
        uint fbits = BitConverter.SingleToUInt32Bits(2.5f);
        Rv32ArchState s = MakeDState((34, 0xFFFFFFFF00000000UL | fbits));
        ExecuteResult r = Exec(0x420100D3, s);
        Assert.Equal(2.5, Adbl(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FeqD_ReturnsTrueWhenEqual() {
        // feq.d x1, f2, f3  0xA23120D3 — f2=f3=1.0 → x1=1
        Rv32ArchState s = MakeDState((34, Dbl(1.0)), (35, Dbl(1.0)));
        ExecuteResult r = Exec(0xA23120D3, s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FltD_ReturnsTrueWhenLess() {
        // flt.d x1, f2, f3  0xA23110D3 — f2=1.0, f3=2.0 → x1=1
        Rv32ArchState s = MakeDState((34, Dbl(1.0)), (35, Dbl(2.0)));
        ExecuteResult r = Exec(0xA23110D3, s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FleD_ReturnsTrueWhenEqual() {
        // fle.d x1, f2, f3  0xA23100D3 — f2=f3=1.0 → x1=1
        Rv32ArchState s = MakeDState((34, Dbl(1.0)), (35, Dbl(1.0)));
        ExecuteResult r = Exec(0xA23100D3, s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FmaddD_FusedMultiplyAdd() {
        // fmadd.d f1, f2, f3, f4  — f2=2.0, f3=3.0, f4=1.0 → f1=7.0
        Rv32ArchState s = MakeDState((34, Dbl(2.0)), (35, Dbl(3.0)), (36, Dbl(1.0)));
        ExecuteResult r = Exec(0x223100C3, s);
        Assert.Equal(7.0, Adbl(r.RegisterResult.Value));
    }

    // ── Zfh extension (half precision) ────────────────────────────────────────
    // FP registers are at unified indices 32-63 (f0=32 … f31=63); half values NaN-box
    // with the upper 48 bits set to 1 (vs. 32 for float, 0/native for double).

    private static ushort Hb(Half h) => BitConverter.HalfToUInt16Bits(h);
    private static Half Ah(ulong bits) => BitConverter.UInt16BitsToHalf((ushort)bits);

    private static Rv32ArchState MakeHState(params (int reg, ulong val)[] regs) {
        var s = new Rv32ArchState();
        foreach ((int r, ulong v) in regs) {
            ulong stored = r >= 32 ? 0xFFFFFFFFFFFF0000UL | (v & 0xFFFF) : v;
            s.IntegerRegisters.Write(r, stored);
        }

        return s;
    }

    [Fact]
    public void Execute_Flh_LoadsHalfBitsFromMemory() {
        // flh f1, 4(x2)  0x00411087 — x2=100, mem[104]=bits of 3.5h; result is NaN-boxed
        ushort bits = Hb((Half)3.5f);
        Rv32ArchState s = MakeHState((2, 100));
        _mem.Write(104, bits, 2);
        ExecuteResult r = Exec(0x00411087, s);
        Assert.Equal(0xFFFFFFFFFFFF0000UL | bits, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_Fsh_StoresHalfBitsToMemory() {
        // fsh f2, 4(x1)  0x00209227 — x1=100, f2=2.5h
        ushort bits = Hb((Half)2.5f);
        Rv32ArchState s = MakeHState((1, 100), (34, bits)); // f2 = index 34
        Exec(0x00209227, s);
        Assert.Equal(bits, (ushort)_mem.Read(104, 2));
    }

    [Fact]
    public void Execute_FaddH_AddsHalves() {
        // fadd.h f1, f2, f3  0x043100D3 — f2=2.0, f3=3.0 → 5.0
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)3.0f)));
        ExecuteResult r = Exec(0x043100D3, s);
        Assert.Equal((Half)5.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsubH_SubtractsHalves() {
        // fsub.h f1, f2, f3  0x0C3100D3 — f2=5.0, f3=3.0 → 2.0
        Rv32ArchState s = MakeHState((34, Hb((Half)5.0f)), (35, Hb((Half)3.0f)));
        ExecuteResult r = Exec(0x0C3100D3, s);
        Assert.Equal((Half)2.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FmulH_MultipliesHalves() {
        // fmul.h f1, f2, f3  0x143100D3 — f2=2.0, f3=3.0 → 6.0
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)3.0f)));
        ExecuteResult r = Exec(0x143100D3, s);
        Assert.Equal((Half)6.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FdivH_DividesHalves() {
        // fdiv.h f1, f2, f3  0x1C3100D3 — f2=6.0, f3=2.0 → 3.0
        Rv32ArchState s = MakeHState((34, Hb((Half)6.0f)), (35, Hb((Half)2.0f)));
        ExecuteResult r = Exec(0x1C3100D3, s);
        Assert.Equal((Half)3.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsqrtH_ComputesSquareRoot() {
        // fsqrt.h f1, f2  0x5C0100D3 — f2=4.0 → 2.0
        Rv32ArchState s = MakeHState((34, Hb((Half)4.0f)));
        ExecuteResult r = Exec(0x5C0100D3, s);
        Assert.Equal((Half)2.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsgnjH_InjectsPositiveSign() {
        // fsgnj.h f1, f2, f3  0x243100D3 — f2=-2.0 (neg), f3=3.0 (pos) → +2.0
        Rv32ArchState s = MakeHState((34, Hb((Half)(-2.0f))), (35, Hb((Half)3.0f)));
        ExecuteResult r = Exec(0x243100D3, s);
        Assert.Equal((Half)2.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsgnjnH_InjectsNegatedSign() {
        // fsgnjn.h f1, f2, f3  0x243110D3 — f2=2.0 (pos), f3=3.0 (pos) → -2.0
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)3.0f)));
        ExecuteResult r = Exec(0x243110D3, s);
        Assert.Equal((Half)(-2.0f), Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FsgnjxH_XorSign() {
        // fsgnjx.h f1, f2, f3  0x243120D3 — f2=2.0 (pos), f3=-3.0 (neg) → -2.0
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)(-3.0f))));
        ExecuteResult r = Exec(0x243120D3, s);
        Assert.Equal((Half)(-2.0f), Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FminH_ReturnsSmaller() {
        // fmin.h f1, f2, f3  0x2C3100D3 — f2=2.0, f3=3.0 → 2.0
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)3.0f)));
        ExecuteResult r = Exec(0x2C3100D3, s);
        Assert.Equal((Half)2.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FminH_ReturnsNonNanWhenOneIsNaN() {
        // fmin(NaN, 2.0) = 2.0 per RISC-V spec
        Rv32ArchState s = MakeHState((34, Hb(Half.NaN)), (35, Hb((Half)2.0f)));
        ExecuteResult r = Exec(0x2C3100D3, s);
        Assert.Equal((Half)2.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FminH_ReturnsNegativeZeroWhenBothAreZero() {
        // fmin(-0.0, +0.0) = -0.0; result is NaN-boxed
        Rv32ArchState s = MakeHState((34, Hb((Half)(-0.0f))), (35, Hb((Half)0.0f)));
        ExecuteResult r = Exec(0x2C3100D3, s);
        Assert.Equal(0xFFFFFFFFFFFF8000UL, r.RegisterResult.Value); // NaN-boxed -0.0
    }

    [Fact]
    public void Execute_FmaxH_ReturnsLarger() {
        // fmax.h f1, f2, f3  0x2C3110D3 — f2=2.0, f3=3.0 → 3.0
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)3.0f)));
        ExecuteResult r = Exec(0x2C3110D3, s);
        Assert.Equal((Half)3.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FmaxH_ReturnsNonNanWhenOneIsNaN() {
        // fmax(NaN, 3.0) = 3.0
        Rv32ArchState s = MakeHState((34, Hb(Half.NaN)), (35, Hb((Half)3.0f)));
        ExecuteResult r = Exec(0x2C3110D3, s);
        Assert.Equal((Half)3.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FmaxH_ReturnsPositiveZeroWhenBothAreZero() {
        // fmax(-0.0, +0.0) = +0.0; result is NaN-boxed
        Rv32ArchState s = MakeHState((34, Hb((Half)(-0.0f))), (35, Hb((Half)0.0f)));
        ExecuteResult r = Exec(0x2C3110D3, s);
        Assert.Equal(0xFFFFFFFFFFFF0000UL, r.RegisterResult.Value); // NaN-boxed +0.0
    }

    [Fact]
    public void Execute_FeqH_ReturnsOneWhenEqual() {
        // feq.h x1, f2, f3  0xA43120D3
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)2.0f)));
        ExecuteResult r = Exec(0xA43120D3, s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FeqH_ReturnsZeroWhenNotEqual() {
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)3.0f)));
        ExecuteResult r = Exec(0xA43120D3, s);
        Assert.Equal(0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FltH_ReturnsOneWhenLess() {
        // flt.h x1, f2, f3  0xA43110D3
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)3.0f)));
        ExecuteResult r = Exec(0xA43110D3, s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FleH_ReturnsOneWhenEqual() {
        // fle.h x1, f2, f3  0xA43100D3 — 2.0 ≤ 2.0
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)2.0f)));
        ExecuteResult r = Exec(0xA43100D3, s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FclassH_PositiveNormal() {
        // fclass.h x1, f2  0xE40110D3 — 2.0h is +normal → bit 6
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)));
        ExecuteResult r = Exec(0xE40110D3, s);
        Assert.Equal(1UL << 6, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FclassH_PositiveInfinity() {
        Rv32ArchState s = MakeHState((34, Hb(Half.PositiveInfinity)));
        ExecuteResult r = Exec(0xE40110D3, s);
        Assert.Equal(1UL << 7, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FclassH_NegativeZero() {
        Rv32ArchState s = MakeHState((34, Hb((Half)(-0.0f))));
        ExecuteResult r = Exec(0xE40110D3, s);
        Assert.Equal(1UL << 3, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FclassH_PositiveZero() {
        Rv32ArchState s = MakeHState((34, 0UL));
        ExecuteResult r = Exec(0xE40110D3, s);
        Assert.Equal(1UL << 4, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FclassH_QuietNaN() {
        Rv32ArchState s = MakeHState((34, Hb(Half.NaN)));
        ExecuteResult r = Exec(0xE40110D3, s);
        Assert.Equal(1UL << 9, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtWH_TruncatesPositiveHalf() {
        // fcvt.w.h x1, f2, rtz  0xC40110D3 — 3.5h → 3 (RTZ truncates toward zero)
        Rv32ArchState s = MakeHState((34, Hb((Half)3.5f)));
        ExecuteResult r = Exec(0xC40110D3, s);
        Assert.Equal(3UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtWH_TruncatesNegativeHalf() {
        // -3.5h → -3 (truncation toward zero) → 0xFFFFFFFD as uint32
        Rv32ArchState s = MakeHState((34, Hb((Half)(-3.5f))));
        ExecuteResult r = Exec(0xC40110D3, s);
        Assert.Equal(unchecked((uint)-3), r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtWuH_ConvertsPositiveHalf() {
        // fcvt.wu.h x1, f2, rtz  0xC41110D3 — 5.5h → 5u (RTZ truncates toward zero)
        Rv32ArchState s = MakeHState((34, Hb((Half)5.5f)));
        ExecuteResult r = Exec(0xC41110D3, s);
        Assert.Equal(5UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtHW_ConvertsNegativeInt() {
        // fcvt.h.w f1, x2  0xD40100D3 — x2=-5 → -5.0h
        Rv32ArchState s = MakeHState((2, unchecked((uint)-5)));
        ExecuteResult r = Exec(0xD40100D3, s);
        Assert.Equal((Half)(-5.0f), Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FcvtHWu_ConvertsLargeUnsignedToInfinity() {
        // fcvt.h.wu f1, x2  0xD41100D3 — x2=0xFFFFFFFF (unsigned) overflows half range → +Infinity.
        // Distinguishes from FCVT.H.W, which would treat the same bits as -1 → -1.0h.
        Rv32ArchState s = MakeHState((2, 0xFFFFFFFFUL));
        ExecuteResult r = Exec(0xD41100D3, s);
        Assert.True(Half.IsPositiveInfinity(Ah(r.RegisterResult.Value)));
    }

    [Fact]
    public void Execute_FmvXH_SignExtendsBitsToIntReg() {
        // fmv.x.h x1, f2  0xE40100D3 — f2 holds bits of -2.0h; result sign-extends the 16-bit
        // pattern to XLEN=32 under RV32 (then zero-extended into the 64-bit unified register file).
        ushort bits = Hb((Half)(-2.0f));
        Rv32ArchState s = MakeHState((34, bits));
        ExecuteResult r = Exec(0xE40100D3, s);
        Assert.Equal((uint)(short)bits, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FmvHX_CopiesLower16BitsToFpReg() {
        // fmv.h.x f1, x2  0xF40100D3 — only the low 16 bits of x2 matter; writes NaN-boxed
        Rv32ArchState s = MakeHState((2, 0xFFFF1234UL));
        ExecuteResult r = Exec(0xF40100D3, s);
        Assert.Equal(0xFFFFFFFFFFFF1234UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FmaddH_FusedMultiplyAdd() {
        // fmadd.h f1, f2, f3, f4  0x243100C3 — f2=2.0, f3=3.0, f4=1.0 → 7.0
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)3.0f)), (36, Hb((Half)1.0f)));
        ExecuteResult r = Exec(0x243100C3, s);
        Assert.Equal((Half)7.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FmsubH_FusedMultiplySubtract() {
        // fmsub.h f1, f2, f3, f4  0x243100C7 — f2*f3 - f4 = 6-1 = 5.0
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)3.0f)), (36, Hb((Half)1.0f)));
        ExecuteResult r = Exec(0x243100C7, s);
        Assert.Equal((Half)5.0f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FnmsubH_NegatedFusedMultiplySubtract() {
        // fnmsub.h f1, f2, f3, f4  0x243100CB — -(f2*f3) + f4 = -6+1 = -5.0
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)3.0f)), (36, Hb((Half)1.0f)));
        ExecuteResult r = Exec(0x243100CB, s);
        Assert.Equal((Half)(-5.0f), Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FnmaddH_NegatedFusedMultiplyAdd() {
        // fnmadd.h f1, f2, f3, f4  0x243100CF — -(f2*f3) - f4 = -6-1 = -7.0
        Rv32ArchState s = MakeHState((34, Hb((Half)2.0f)), (35, Hb((Half)3.0f)), (36, Hb((Half)1.0f)));
        ExecuteResult r = Exec(0x243100CF, s);
        Assert.Equal((Half)(-7.0f), Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FcvtHS_ConvertSingleToHalf() {
        // fcvt.h.s f1, f2  0x440100D3 — f2=2.5f → f1=2.5h
        Rv32ArchState s = MakeState((34, Fb(2.5f)));
        ExecuteResult r = Exec(0x440100D3, s);
        Assert.Equal((Half)2.5f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FcvtSH_ConvertHalfToSingleWithNaNBox() {
        // fcvt.s.h f1, f2  0x402100D3 — f2=2.5h → f1=2.5f NaN-boxed
        Rv32ArchState s = MakeHState((34, Hb((Half)2.5f)));
        ExecuteResult r = Exec(0x402100D3, s);
        ulong result = r.RegisterResult.Value;
        Assert.Equal(0xFFFFFFFF00000000UL, result & 0xFFFFFFFF00000000UL); // NaN-boxed
        Assert.Equal(2.5f, Af(result));
    }

    [Fact]
    public void Execute_FcvtHD_ConvertDoubleToHalf() {
        // fcvt.h.d f1, f2  0x441100D3 — f2=2.5 → f1=2.5h
        Rv32ArchState s = MakeDState((34, Dbl(2.5)));
        ExecuteResult r = Exec(0x441100D3, s);
        Assert.Equal((Half)2.5f, Ah(r.RegisterResult.Value));
    }

    [Fact]
    public void Execute_FcvtDH_ConvertHalfToDouble() {
        // fcvt.d.h f1, f2  0x422100D3 — f2=2.5h → f1=2.5
        Rv32ArchState s = MakeHState((34, Hb((Half)2.5f)));
        ExecuteResult r = Exec(0x422100D3, s);
        Assert.Equal(2.5, Adbl(r.RegisterResult.Value));
    }
}