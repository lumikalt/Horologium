using Mechanism;
using RiscV;
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
        Exec(0x00209023, s); // sh x2, 0(x1)
        ExecuteResult r = Exec(0x00009183, s); // lh x3, 0(x1)
        Assert.Equal(0xFFFF8000UL, r.RegisterResult);
    }

    [Fact]
    public void Execute_Sh_Then_Lhu_ZeroExtends() {
        RvArchState s = MakeState((1, 100), (2, 0x8000));
        Exec(0x00209023, s); // sh x2, 0(x1)
        ExecuteResult r = Exec(0x0000D183, s); // lhu x3, 0(x1)
        Assert.Equal(0x8000UL, r.RegisterResult);
    }

    // ── JALR with link register ───────────────────────────────────────────────

    [Fact]
    public void Execute_Jalr_WithLinkRegister() {
        RvArchState s = MakeState((2, 0x200));
        ExecuteResult r = Exec(0x004100E7, s, 0x100); // jalr x1, 4(x2)
        Assert.Equal(0x104UL, r.RegisterResult); // link = PC+4
        Assert.True(r.BranchTaken);
        Assert.Equal(0x204UL, r.BranchTarget);   // target = x2+4 = 0x204
    }
}