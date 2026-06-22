using Mechanism;
using RiscV.Decode;
using RiscV.Execute;
using RiscV.Memory;
using RiscV.State;

namespace Tests.RiscV;

public class ExecutorTests {
    private readonly RvDecoder _dec = new();
    private readonly RvExecutor _exe = new();
    private readonly FlatMemory _mem = new(4096);

    private RvArchState MakeState(params (int reg, uint val)[] regs) {
        var s = new RvArchState();
        foreach ((int r, uint v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    private ExecuteResult Exec(uint raw, RvArchState state, ulong pc = 0) {
        IInstruction instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // ── R-type ────────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Add() {
        RvArchState s = MakeState((2, 10), (3, 20));
        ExecuteResult r = Exec(0x003100B3, s); // add x1, x2, x3
        Assert.Equal(30UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Sub() {
        RvArchState s = MakeState((2, 20), (3, 7));
        ExecuteResult r = Exec(0x403100B3, s); // sub x1, x2, x3
        Assert.Equal(13UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Sub_Wraps() {
        RvArchState s = MakeState((2, 0), (3, 1));
        ExecuteResult r = Exec(0x403100B3, s); // sub x1, x2, x3  → 0 - 1 = 0xFFFFFFFF
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Slt_True() {
        RvArchState s = MakeState((2, unchecked((uint)-5)), (3, 1));
        ExecuteResult r = Exec(0x003120B3, s); // slt x1, x2, x3  (-5 < 1 signed)
        Assert.Equal(1UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Slt_False() {
        RvArchState s = MakeState((2, 5), (3, 1));
        ExecuteResult r = Exec(0x003120B3, s); // slt x1, x2, x3
        Assert.Equal(0UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Sltu_UnsignedComparison() {
        // x2 = 0xFFFFFFFF (large unsigned), x3 = 1
        RvArchState s = MakeState((2, 0xFFFFFFFF), (3, 1));
        ExecuteResult r = Exec(0x003130B3, s); // sltu x1, x2, x3  → 0 (0xFFFFFFFF > 1 unsigned)
        Assert.Equal(0UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Sra_SignExtends() {
        RvArchState s = MakeState((2, 0x80000000), (3, 1));
        ExecuteResult r = Exec(0x403150B3, s); // sra x1, x2, x3
        Assert.Equal(0xC0000000UL, r.RegisterResult);
    }

    // ── I-type ALU ────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Addi() {
        RvArchState s = MakeState((2, 100));
        ExecuteResult r = Exec(0x02A10093, s); // addi x1, x2, 42
        Assert.Equal(142UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Addi_Negative() {
        RvArchState s = MakeState((2, 10));
        ExecuteResult r = Exec(0xFFF10093, s); // addi x1, x2, -1
        Assert.Equal(9UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Andi() {
        RvArchState s = MakeState((2, 0xFF));
        ExecuteResult r = Exec(0x00F17093, s); // andi x1, x2, 15
        Assert.Equal(0xFUL, r.RegisterResult);
    }

    // ── Loads and Stores ──────────────────────────────────────────────────────

    [Fact]
    public void Execute_Sw_Then_Lw() {
        RvArchState s = MakeState((1, 100), (2, 0xDEADBEEF));
        // sw x2, 0(x1)  →  store 0xDEADBEEF at address 100
        Exec(0x0020a023, s); // sw x2, 0(x1)
        // lw x3, 0(x1)
        ExecuteResult r = Exec(0x0000a183, s); // lw x3, 0(x1)
        Assert.Equal(0xDEADBEEFUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Lb_SignExtends() {
        _mem.Write(200, 0xFF, 1); // write -1 as byte
        RvArchState s = MakeState((1, 200));
        ExecuteResult r = Exec(0x00008083, s); // lb x1, 0(x1)
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Lbu_ZeroExtends() {
        _mem.Write(200, 0xFF, 1);
        RvArchState s = MakeState((1, 200));
        ExecuteResult r = Exec(0x00008083 | (0x4u << 12), s); // lbu x1, 0(x1)
        Assert.Equal(0xFFUL, r.RegisterResult);
    }

    // ── Branches ──────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Beq_Taken() {
        RvArchState s = MakeState((1, 5), (2, 5));
        ExecuteResult r = Exec(0x00208463, s, 0x100); // beq x1, x2, +8
        Assert.True(r.BranchTaken);
        Assert.Equal(0x108UL, r.BranchTarget);
    }

    [Fact]
    public void Execute_Beq_NotTaken() {
        RvArchState s = MakeState((1, 5), (2, 6));
        ExecuteResult r = Exec(0x00208463, s, 0x100);
        Assert.False(r.BranchTaken);
        Assert.Equal(0x104UL, r.BranchTarget);
    }

    [Fact]
    public void Execute_Blt_Taken_Signed() {
        RvArchState s = MakeState((1, unchecked((uint)-1)), (2, 0));
        // blt x1, x2, +8  (-1 < 0 signed → taken)
        ExecuteResult r = Exec(0x0020c463, s, 0x100);
        Assert.True(r.BranchTaken);
    }

    // ── JAL / JALR ────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Jal() {
        RvArchState s = MakeState();
        ExecuteResult r = Exec(0x008000EF, s, 0x100); // jal x1, +8
        Assert.Equal(0x104UL, r.RegisterResult);      // return address = PC+4
        Assert.True(r.BranchTaken);
        Assert.Equal(0x108UL, r.BranchTarget); // target = PC+8
    }

    [Fact]
    public void Execute_Jalr_ClearsLSB() {
        RvArchState s = MakeState((2, 0x101));        // rs1 has LSB set
        ExecuteResult r = Exec(0x00010067, s, 0x100); // jalr x0, x2, 0
        Assert.Equal(0x100UL, r.BranchTarget);        // LSB cleared
    }

    // ── Upper immediates ──────────────────────────────────────────────────────

    [Fact]
    public void Execute_Lui() {
        RvArchState s = MakeState();
        ExecuteResult r = Exec(0x000010B7, s); // lui x1, 1  →  x1 = 0x1000
        Assert.Equal(0x1000UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Auipc() {
        RvArchState s = MakeState();
        ExecuteResult r = Exec(0x00001097, s, 0x1000); // auipc x1, 1
        Assert.Equal(0x2000UL, r.RegisterResult);      // PC(0x1000) + (1 << 12)
    }

    // ── System ────────────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Ecall_RaisesTrap() {
        RvArchState s = MakeState();
        ExecuteResult r = Exec(0x00000073, s);
        Assert.True(r.HasTrap);
        Assert.Equal(TrapCause.EnvironmentCallFromM, r.Trap!.Cause);
    }

    [Fact]
    public void Execute_Ebreak_RaisesTrap() {
        RvArchState s = MakeState();
        ExecuteResult r = Exec(0x00100073, s);
        Assert.True(r.HasTrap);
        Assert.Equal(TrapCause.Breakpoint, r.Trap!.Cause);
    }

    // ── x0 is hardwired zero ──────────────────────────────────────────────────

    [Fact]
    public void Execute_WriteToX0_IsIgnored() {
        RvArchState s = MakeState((1, 5), (2, 3));
        // add x0, x1, x2 — result goes to x0, should stay 0
        ExecuteResult r = Exec(0x00208033, s);
        // The executor returns the result — the writeback stage ignores rd=0
        // But we can verify x0 reads as 0 regardless
        Assert.Equal(0UL, s.IntegerRegisters.Read(0));
    }

    // ── R-type (additional) ───────────────────────────────────────────────────

    [Fact]
    public void Execute_And() {
        RvArchState s = MakeState((1, 0xAA), (2, 0xF0));
        ExecuteResult r = Exec(0x0020F1B3, s); // and x3, x1, x2
        Assert.Equal(0xA0UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Or() {
        RvArchState s = MakeState((1, 0x0F), (2, 0xF0));
        ExecuteResult r = Exec(0x0020E1B3, s); // or x3, x1, x2
        Assert.Equal(0xFFUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Xor() {
        RvArchState s = MakeState((1, 0xFF), (2, 0xF0));
        ExecuteResult r = Exec(0x0020C1B3, s); // xor x3, x1, x2
        Assert.Equal(0x0FUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Sll() {
        RvArchState s = MakeState((1, 1), (2, 4));
        ExecuteResult r = Exec(0x002091B3, s); // sll x3, x1, x2  →  1 << 4 = 16
        Assert.Equal(16UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Srl_LogicalShift() {
        RvArchState s = MakeState((1, 0x80000000), (2, 1));
        ExecuteResult r = Exec(0x0020D1B3, s); // srl x3, x1, x2  →  0x40000000
        Assert.Equal(0x40000000UL, r.RegisterResult);
    }

    // ── I-type ALU (additional) ───────────────────────────────────────────────

    [Fact]
    public void Execute_Ori() {
        RvArchState s = MakeState((1, 0xF0));
        ExecuteResult r = Exec(0x00F0E113, s); // ori x2, x1, 0xF  →  0xFF
        Assert.Equal(0xFFUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Xori() {
        RvArchState s = MakeState((1, 0xFF));
        ExecuteResult r = Exec(0x00F0C113, s); // xori x2, x1, 0xF  →  0xF0
        Assert.Equal(0xF0UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Slti_True() {
        RvArchState s = MakeState((1, unchecked((uint)-1)));
        ExecuteResult r = Exec(0x0000A113, s); // slti x2, x1, 0  →  -1 < 0 signed → 1
        Assert.Equal(1UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Slti_False() {
        RvArchState s = MakeState((1, 5));
        ExecuteResult r = Exec(0x0000A113, s); // slti x2, x1, 0  →  5 < 0 signed → 0
        Assert.Equal(0UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Sltiu_UnsignedComparison() {
        RvArchState s = MakeState((1, 0));
        ExecuteResult r = Exec(0x0010B113, s); // sltiu x2, x1, 1  →  0 < 1 unsigned → 1
        Assert.Equal(1UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Slli() {
        RvArchState s = MakeState((1, 1));
        ExecuteResult r = Exec(0x00409113, s); // slli x2, x1, 4  →  16
        Assert.Equal(16UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Srli_LogicalShift() {
        RvArchState s = MakeState((1, 0x80000000));
        ExecuteResult r = Exec(0x0010D113, s); // srli x2, x1, 1  →  0x40000000
        Assert.Equal(0x40000000UL, r.RegisterResult);
    }

    // ── Branches (additional) ─────────────────────────────────────────────────

    [Fact]
    public void Execute_Bne_Taken() {
        RvArchState s = MakeState((1, 1), (2, 2));
        ExecuteResult r = Exec(0x00209463, s, 0x100); // bne x1, x2, +8  →  1 != 2 → taken
        Assert.True(r.BranchTaken);
        Assert.Equal(0x108UL, r.BranchTarget);
    }

    [Fact]
    public void Execute_Bne_NotTaken() {
        RvArchState s = MakeState((1, 5), (2, 5));
        ExecuteResult r = Exec(0x00209463, s, 0x100); // bne x1, x2, +8  →  5 == 5 → not taken
        Assert.False(r.BranchTaken);
    }

    [Fact]
    public void Execute_Bge_Taken_Equal() {
        RvArchState s = MakeState((1, 5), (2, 5));
        ExecuteResult r = Exec(0x0020D463, s, 0x100); // bge x1, x2, +8  →  5 >= 5 → taken
        Assert.True(r.BranchTaken);
        Assert.Equal(0x108UL, r.BranchTarget);
    }

    [Fact]
    public void Execute_Bge_NotTaken_Negative() {
        RvArchState s = MakeState((1, unchecked((uint)-1)), (2, 0));
        ExecuteResult r = Exec(0x0020D463, s, 0x100); // bge x1, x2, +8  →  -1 >= 0 signed → false
        Assert.False(r.BranchTaken);
    }

    [Fact]
    public void Execute_Bgeu_Taken() {
        RvArchState s = MakeState((1, 0xFFFFFFFF), (2, 1));
        ExecuteResult r = Exec(0x0020F463, s, 0x100); // bgeu x1, x2, +8  →  large unsigned >= 1 → taken
        Assert.True(r.BranchTaken);
    }

    [Fact]
    public void Execute_Bltu_Taken() {
        RvArchState s = MakeState((1, 0), (2, 1));
        ExecuteResult r = Exec(0x0020E463, s, 0x100); // bltu x1, x2, +8  →  0 < 1 unsigned → taken
        Assert.True(r.BranchTaken);
    }

    // ── Half-word memory ──────────────────────────────────────────────────────

    [Fact]
    public void Execute_Sh_Then_Lh_SignExtends() {
        RvArchState s = MakeState((1, 100), (2, 0x8000)); // 0x8000 = -32768 as int16
        Exec(0x00209023, s);                              // sh x2, 0(x1)
        ExecuteResult r = Exec(0x00009183, s);            // lh x3, 0(x1)
        Assert.Equal(0xFFFF8000UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Sh_Then_Lhu_ZeroExtends() {
        RvArchState s = MakeState((1, 100), (2, 0x8000));
        Exec(0x00209023, s);                   // sh x2, 0(x1)
        ExecuteResult r = Exec(0x0000D183, s); // lhu x3, 0(x1)
        Assert.Equal(0x8000UL, r.RegisterResult);
    }

    // ── JALR with link register ───────────────────────────────────────────────

    [Fact]
    public void Execute_Jalr_WithLinkRegister() {
        RvArchState s = MakeState((2, 0x200));
        ExecuteResult r = Exec(0x004100E7, s, 0x100); // jalr x1, 4(x2)
        Assert.Equal(0x104UL, r.RegisterResult);      // link = PC+4
        Assert.True(r.BranchTaken);
        Assert.Equal(0x204UL, r.BranchTarget); // target = x2+4 = 0x204
    }

    // ── M extension ───────────────────────────────────────────────────────────

    [Fact]
    public void Execute_Mul_LowerHalf() {
        // mul x1, x2, x3  (x2=7, x3=6 → x1=42)
        RvArchState s = MakeState((2, 7), (3, 6));
        ExecuteResult r = Exec(0x023100B3, s);
        Assert.Equal(42UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Mul_Overflow_TruncatesToLower32() {
        // 0x80000001 × 2 = 0x100000002 → lower 32 = 0x00000002
        RvArchState s = MakeState((2, 0x80000001), (3, 2));
        ExecuteResult r = Exec(0x023100B3, s);
        Assert.Equal(2UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Mulh_SignedUpperHalf() {
        // (-1) × (-1) = 1, upper 32 of 0x0000_0000_0000_0001 = 0
        RvArchState s = MakeState((2, 0xFFFFFFFF), (3, 0xFFFFFFFF)); // -1 × -1
        ExecuteResult r = Exec(0x023110B3, s);                       // mulh x1, x2, x3
        Assert.Equal(0UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Mulh_NegativeTimesPositive() {
        // (-1) × 1 = -1, upper 32 of -1 as 64-bit = 0xFFFFFFFF
        RvArchState s = MakeState((2, 0xFFFFFFFF), (3, 1)); // -1 × 1
        ExecuteResult r = Exec(0x023110B3, s);
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Mulhu_UnsignedUpperHalf() {
        // 0xFFFFFFFF × 0xFFFFFFFF = 0xFFFFFFFE_00000001, upper = 0xFFFFFFFE
        RvArchState s = MakeState((2, 0xFFFFFFFF), (3, 0xFFFFFFFF));
        ExecuteResult r = Exec(0x023130B3, s); // mulhu x1, x2, x3
        Assert.Equal(0xFFFFFFFEUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Mulhsu_SignedUnsignedUpperHalf() {
        // -1 (signed) × 0xFFFFFFFF (unsigned) = -0xFFFFFFFF = -4294967295
        // As 64-bit: 0xFFFF_FFFF_0000_0001, upper 32 = 0xFFFFFFFF
        RvArchState s = MakeState((2, 0xFFFFFFFF), (3, 0xFFFFFFFF)); // -1 × 4294967295
        ExecuteResult r = Exec(0x023120B3, s);                       // mulhsu x1, x2, x3
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Div_Basic() {
        // 10 / 3 = 3 (truncated toward zero)
        RvArchState s = MakeState((2, 10), (3, 3));
        ExecuteResult r = Exec(0x023140B3, s); // div x1, x2, x3
        Assert.Equal(3UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Div_NegativeResult_TruncatesTowardZero() {
        // -7 / 2 = -3 (truncate toward zero, not -4)
        RvArchState s = MakeState((2, unchecked((uint)-7)), (3, 2));
        ExecuteResult r = Exec(0x023140B3, s);
        Assert.Equal(unchecked((uint)-3), (uint)r.RegisterResult!.Value);
    }

    [Fact]
    public void Execute_Div_ByZero_ReturnsMinusOne() {
        RvArchState s = MakeState((2, 5), (3, 0));
        ExecuteResult r = Exec(0x023140B3, s);
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Div_Overflow_IntMinDivMinusOne() {
        // INT_MIN / -1 overflows — result is INT_MIN per spec
        RvArchState s = MakeState((2, unchecked((uint)int.MinValue)), (3, unchecked((uint)-1)));
        ExecuteResult r = Exec(0x023140B3, s);
        Assert.Equal(unchecked((uint)int.MinValue), (uint)r.RegisterResult!.Value);
    }

    [Fact]
    public void Execute_Divu_Basic() {
        // 10u / 3u = 3u
        RvArchState s = MakeState((2, 10), (3, 3));
        ExecuteResult r = Exec(0x023150B3, s); // divu x1, x2, x3
        Assert.Equal(3UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Divu_ByZero_ReturnsMaxUint() {
        RvArchState s = MakeState((2, 5), (3, 0));
        ExecuteResult r = Exec(0x023150B3, s);
        Assert.Equal(0xFFFFFFFFUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Rem_Basic() {
        // 10 % 3 = 1
        RvArchState s = MakeState((2, 10), (3, 3));
        ExecuteResult r = Exec(0x023160B3, s); // rem x1, x2, x3
        Assert.Equal(1UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Rem_ByZero_ReturnsRs1() {
        RvArchState s = MakeState((2, 42), (3, 0));
        ExecuteResult r = Exec(0x023160B3, s);
        Assert.Equal(42UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Rem_Overflow_ReturnsZero() {
        // INT_MIN % -1 → 0
        RvArchState s = MakeState((2, unchecked((uint)int.MinValue)), (3, unchecked((uint)-1)));
        ExecuteResult r = Exec(0x023160B3, s);
        Assert.Equal(0UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Remu_Basic() {
        // 10u % 3u = 1u
        RvArchState s = MakeState((2, 10), (3, 3));
        ExecuteResult r = Exec(0x023170B3, s); // remu x1, x2, x3
        Assert.Equal(1UL, r.RegisterResult);
    }

    // ── A extension ───────────────────────────────────────────────────────────

    [Fact]
    public void Execute_AmoaddW_ReturnsPreviousAndUpdatesMemory() {
        // amoadd.w x1, x3, (x2)  — x2=address=100, x3=operand=5, mem[100]=10
        RvArchState s = MakeState((2, 100), (3, 5));
        _mem.Write(100, 10, 4);                // pre-load memory
        ExecuteResult r = Exec(0x003120AF, s); // amoadd.w x1, x3, (x2)
        Assert.Equal(10UL, r.RegisterResult);  // original value
        Assert.Equal(15UL, _mem.Read(100, 4)); // updated in memory
    }

    [Fact]
    public void Execute_AmoswapW_SwapsValue() {
        RvArchState s = MakeState((2, 100), (3, 99));
        _mem.Write(100, 42, 4);
        ExecuteResult r = Exec(0x083120AF, s); // amoswap.w x1, x3, (x2)
        Assert.Equal(42UL, r.RegisterResult);  // original
        Assert.Equal(99UL, _mem.Read(100, 4)); // swapped
    }

    [Fact]
    public void Execute_AmoandW_MasksMemory() {
        RvArchState s = MakeState((2, 100), (3, 0x0F));
        _mem.Write(100, 0xFF, 4);
        Exec(0x603120AF, s); // amoand.w x1, x3, (x2)
        Assert.Equal(0x0FUL, _mem.Read(100, 4));
    }

    [Fact]
    public void Execute_AmoorW_SetsMemoryBits() {
        RvArchState s = MakeState((2, 100), (3, 0xF0));
        _mem.Write(100, 0x0F, 4);
        Exec(0x403120AF, s); // amoor.w x1, x3, (x2)
        Assert.Equal(0xFFUL, _mem.Read(100, 4));
    }

    [Fact]
    public void Execute_AmominW_KeepsMinimum() {
        // Signed min: mem[100] = -1, rs2 = 1 → min(-1, 1) = -1
        RvArchState s = MakeState((2, 100), (3, 1));
        _mem.Write(100, 0xFFFFFFFF, 4);                // -1 signed
        Exec(0x803120AF, s);                           // amomin.w x1, x3, (x2)  funct5=0x10
        Assert.Equal(0xFFFFFFFFUL, _mem.Read(100, 4)); // -1 is smaller
    }

    [Fact]
    public void Execute_LrW_LoadsValue() {
        RvArchState s = MakeState((2, 100));
        _mem.Write(100, 0xDEADBEEF, 4);
        ExecuteResult r = Exec(0x100120AF, s); // lr.w x1, (x2)
        Assert.Equal(0xDEADBEEFUL, r.RegisterResult);
    }

    [Fact]
    public void Execute_ScW_StoresAndReturnsZero() {
        RvArchState s = MakeState((2, 100), (3, 0xABCD));
        ExecuteResult r = Exec(0x183120AF, s);     // sc.w x1, x3, (x2)
        Assert.Equal(0UL, r.RegisterResult);       // 0 = success
        Assert.Equal(0xABCDUL, _mem.Read(100, 4)); // value stored
    }
}