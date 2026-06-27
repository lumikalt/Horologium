using Mechanism;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

namespace Tests.RiscV32;

public class ExecutorTests {
    private readonly Rv32Decoder _dec = new();
    private readonly Rv32Executor _exe = new();
    private readonly FlatMemory _mem = new(4096);

    private Rv32ArchState MakeState(params (int reg, uint val)[] regs) {
        var s = new Rv32ArchState();
        foreach ((int r, uint v) in regs) s.IntegerRegisters.Write(r, v);
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
        ExecuteResult r = Exec(0x183120AF, s);     // sc.w x1, x3, (x2)
        Assert.Equal(0UL, r.RegisterResult.Value); // 0 = success
        Assert.Equal(0xABCDUL, _mem.Read(100, 4)); // value stored
    }

    // ── F extension ───────────────────────────────────────────────────────────
    // FP registers are at unified indices 32-63 (f0=32 … f31=63).
    // MakeState accepts any index in 0-63; indices 32+ write float registers.

    private static uint Fb(float f) => BitConverter.SingleToUInt32Bits(f);
    private static float Af(ulong bits) => BitConverter.Int32BitsToSingle((int)bits);

    [Fact]
    public void Execute_Flw_LoadsFloatBitsFromMemory() {
        // flw f1, 4(x2)  0x00412087 — x2=100, mem[104]=bits of 3.14f
        uint bits = Fb(3.14f);
        Rv32ArchState s = MakeState((2, 100));
        _mem.Write(104, bits, 4);
        ExecuteResult r = Exec(0x00412087, s);
        Assert.Equal(bits, r.RegisterResult.Value);
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
        // fmin(-0.0, +0.0) = -0.0
        Rv32ArchState s = MakeState((34, Fb(-0.0f)), (35, Fb(0.0f)));
        ExecuteResult r = Exec(0x283100D3, s);
        Assert.Equal(0x80000000UL, r.RegisterResult.Value); // -0.0 raw bits
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
        // fmax(-0.0, +0.0) = +0.0
        Rv32ArchState s = MakeState((34, Fb(-0.0f)), (35, Fb(0.0f)));
        ExecuteResult r = Exec(0x283110D3, s);
        Assert.Equal(0UL, r.RegisterResult.Value); // +0.0 raw bits = 0
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
        // fcvt.w.s x1, f2  0xC00100D3 — 3.7f → 3
        Rv32ArchState s = MakeState((34, Fb(3.7f)));
        ExecuteResult r = Exec(0xC00100D3, s);
        Assert.Equal(3UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtWS_TruncatesNegativeFloat() {
        // -3.7f → -3 (truncation toward zero) → 0xFFFFFFFD as uint32
        Rv32ArchState s = MakeState((34, Fb(-3.7f)));
        ExecuteResult r = Exec(0xC00100D3, s);
        Assert.Equal(unchecked((uint)-3), r.RegisterResult.Value);
    }

    [Fact]
    public void Execute_FcvtWuS_ConvertsPositiveFloat() {
        // fcvt.wu.s x1, f2  0xC01100D3 — 5.9f → 5u
        Rv32ArchState s = MakeState((34, Fb(5.9f)));
        ExecuteResult r = Exec(0xC01100D3, s);
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
        // fmv.w.x f1, x2  0xF00100D3 — x2=0x40000000 (bits of 2.0f)
        uint bits = Fb(2.0f); // 0x40000000
        Rv32ArchState s = MakeState((2, bits));
        ExecuteResult r = Exec(0xF00100D3, s);
        Assert.Equal(bits, r.RegisterResult.Value);
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
        uint roPage = (5u << 10) | 0b0101_0011u; // A|U|R|V, no W, no D
        mem.Write(0x2014UL, roPage, 4);          // level-1 PT entry 5 → PA 0x5000
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
        uint noABit = (6u << 10) | 0b0001_0111u; // U|W|R|V, no A, no D
        mem.Write(0x2018UL, noABit, 4);          // level-1 PT entry 6
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
        uint kernelPage = (7u << 10) | 0b1100_0011u; // D|A|R|V, no U, no W
        mem.Write(0x201CUL, kernelPage, 4);          // level-1 PT entry 7
        Rv32ArchState s = MakeState((1, 0x00007000u));
        s.SystemRegisters.Write(CsrFile.Satp, satp, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.User;
        ITooth instr = _dec.Decode(0, 0x0000A183); // lw x3, 0(x1)
        ExecuteResult r = _exe.Execute(instr, s, mem);
        Assert.NotNull(r.Trap);
        Assert.Equal(RvTrapCause.LoadPageFault, r.Trap.Cause);
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
}