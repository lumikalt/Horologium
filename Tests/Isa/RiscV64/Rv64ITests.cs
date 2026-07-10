using Mechanism;
using RiscV32.Memory;
using RiscV64.Decode;
using RiscV64.Execute;
using RiscV64.State;

namespace Tests.Isa.RiscV64;

/// <summary>
/// Tests for RV64I — the W-suffix instructions, new loads/stores, and
/// corrected 64-bit semantics for shifts, comparisons, and LW.
/// </summary>
public class Rv64ITests {
    private readonly Rv64Decoder _dec = new();
    private readonly Rv64Executor _exe = new();
    private readonly FlatMemory _mem = new(65536);

    private Rv64ArchState MakeState(params (int reg, ulong val)[] regs) {
        var s = new Rv64ArchState();
        foreach ((int r, ulong v) in regs) s.IntegerRegisters.Write(r, v);
        return s;
    }

    private ExecuteResult Exec(uint raw, Rv64ArchState state, ulong pc = 0) {
        ITooth instr = _dec.Decode(pc, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // ── ADDW / SUBW ──────────────────────────────────────────────────────────────

    [Fact]
    public void Addw_SignExtends32To64() {
        // addw x1, x2, x3: 0x003100BB  (opcode=0x3B funct3=0 funct7=0)
        // rs2=3,rs1=2,rd=1 → raw = (0<<25)|(3<<20)|(2<<15)|(0<<12)|(1<<7)|0x3B
        uint raw = (0x00u << 25) | (3u << 20) | (2u << 15) | (0u << 12) | (1u << 7) | 0x3B;
        // x2 = 0x7FFFFFFF (max positive 32-bit), x3 = 1 → result = 0x80000000 → sign-extended = 0xFFFFFFFF_80000000
        Rv64ArchState s = MakeState((2, 0x7FFFFFFF), (3, 1));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0xFFFFFFFF80000000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Addw_PositiveResult_ZeroExtended() {
        uint raw = (0x00u << 25) | (3u << 20) | (2u << 15) | (0u << 12) | (1u << 7) | 0x3B;
        Rv64ArchState s = MakeState((2, 10), (3, 20));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(30UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Subw_SignExtends32To64() {
        // subw x1, x2, x3 (funct7=0x20)
        uint raw = (0x20u << 25) | (3u << 20) | (2u << 15) | (0u << 12) | (1u << 7) | 0x3B;
        Rv64ArchState s = MakeState((2, 0UL), (3, 1UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, r.RegisterResult.Value); // -1 sign-extended
    }

    // ── SLLW / SRLW / SRAW ───────────────────────────────────────────────────────

    [Fact]
    public void Sllw_Uses5BitShamtFromRs2() {
        // sllw x1, x2, x3 (funct3=1, funct7=0)
        uint raw = (0x00u << 25) | (3u << 20) | (2u << 15) | (1u << 12) | (1u << 7) | 0x3B;
        Rv64ArchState s = MakeState((2, 1UL), (3, 31UL)); // shift by 31
        ExecuteResult r = Exec(raw, s);
        // 1 << 31 = 0x80000000 → sign-extended to 0xFFFFFFFF80000000
        Assert.Equal(0xFFFFFFFF80000000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Srlw_ZeroExtendsBefore32BitShift() {
        // srlw x1, x2, x3 (funct3=5, funct7=0)
        uint raw = (0x00u << 25) | (3u << 20) | (2u << 15) | (5u << 12) | (1u << 7) | 0x3B;
        // x2 = 0x80000000 (negative as signed 32), shift right logical by 1 → 0x40000000 (positive)
        Rv64ArchState s = MakeState((2, 0x80000000UL), (3, 1UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0x40000000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Sraw_SignFillsFromBit31() {
        // sraw x1, x2, x3 (funct3=5, funct7=0x20)
        uint raw = (0x20u << 25) | (3u << 20) | (2u << 15) | (5u << 12) | (1u << 7) | 0x3B;
        // x2 = 0x80000000 (bit31=1), shift right arith by 1 → 0xC0000000 → sign-ext = 0xFFFFFFFFC0000000
        Rv64ArchState s = MakeState((2, 0x80000000UL), (3, 1UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0xFFFFFFFFC0000000UL, r.RegisterResult.Value);
    }

    // ── ADDIW ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Addiw_OverflowSign() {
        // addiw x1, x2, 1 (funct3=0, opcode=0x1B, imm=1)
        uint raw = (1u << 20) | (2u << 15) | (0u << 12) | (1u << 7) | 0x1B;
        // x2 = 0x7FFFFFFF → +1 → 0x80000000 → sign-ext = 0xFFFFFFFF80000000
        Rv64ArchState s = MakeState((2, 0x7FFFFFFF));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0xFFFFFFFF80000000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Addiw_NegativeImm() {
        // addiw x1, x2, -1 (imm = 0xFFF sign-extended = -1)
        uint raw = (0xFFFu << 20) | (2u << 15) | (0u << 12) | (1u << 7) | 0x1B;
        Rv64ArchState s = MakeState((2, 0UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, r.RegisterResult.Value); // -1 sign-extended
    }

    // ── SLLIW / SRLIW / SRAIW ────────────────────────────────────────────────────

    [Fact]
    public void Slliw_ShiftsLower32AndSignExtends() {
        // slliw x1, x2, 1 (funct3=1, funct7=0, shamt=1, opcode=0x1B)
        uint raw = (0x00u << 25) | (1u << 20) | (2u << 15) | (1u << 12) | (1u << 7) | 0x1B;
        // x2 = 0x40000000, <<1 = 0x80000000 → sign-ext = 0xFFFFFFFF80000000
        Rv64ArchState s = MakeState((2, 0x40000000UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0xFFFFFFFF80000000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Srliw_LogicalShift() {
        // srliw x1, x2, 1 (funct3=5, funct7=0, shamt=1, opcode=0x1B)
        uint raw = (0x00u << 25) | (1u << 20) | (2u << 15) | (5u << 12) | (1u << 7) | 0x1B;
        Rv64ArchState s = MakeState((2, 0x80000000UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0x40000000UL, r.RegisterResult.Value); // positive → no sign extension
    }

    [Fact]
    public void Sraiw_ArithmeticShift() {
        // sraiw x1, x2, 1 (funct3=5, funct7=0x20, shamt=1, opcode=0x1B)
        uint raw = (0x20u << 25) | (1u << 20) | (2u << 15) | (5u << 12) | (1u << 7) | 0x1B;
        Rv64ArchState s = MakeState((2, 0x80000000UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0xFFFFFFFFC0000000UL, r.RegisterResult.Value);
    }

    // ── LD / SD ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Ld_LoadsFullDoubleword() {
        // Write 0xDEADBEEFCAFEBABE at address 0x1000
        _mem.Write(0x1000, 0xDEADBEEFCAFEBABEUL, 8);

        // ld x1, 0(x2)  (funct3=3, opcode=0x03, imm=0)
        uint raw = (0u << 20) | (2u << 15) | (3u << 12) | (1u << 7) | 0x03;
        Rv64ArchState s = MakeState((2, 0x1000UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0xDEADBEEFCAFEBABEUL, r.RegisterResult.Value);
    }

    [Fact]
    public void Sd_StoresFullDoubleword() {
        // sd x3, 0(x2)  (funct3=3, opcode=0x23, imm=0)
        // S-type: imm[11:5]=bits[31:25], imm[4:0]=bits[11:7]; for imm=0 both are 0
        uint raw = (0u << 25) | (3u << 20) | (2u << 15) | (3u << 12) | (0u << 7) | 0x23;
        Rv64ArchState s = MakeState((2, 0x2000UL), (3, 0x1122334455667788UL));
        Exec(raw, s);
        ulong stored = _mem.Read(0x2000, 8);
        Assert.Equal(0x1122334455667788UL, stored);
    }

    // ── LWU ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Lwu_ZeroExtends() {
        _mem.Write(0x3000, 0x80000001UL, 4);

        // lwu x1, 0(x2)  (funct3=6, opcode=0x03, imm=0)
        uint raw = (0u << 20) | (2u << 15) | (6u << 12) | (1u << 7) | 0x03;
        Rv64ArchState s = MakeState((2, 0x3000UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0x80000001UL, r.RegisterResult.Value); // zero-extended, not sign-extended
    }

    // ── LW sign-extension in RV64 ─────────────────────────────────────────────────

    [Fact]
    public void Lw_SignExtends32To64() {
        _mem.Write(0x4000, 0x80000001UL, 4);

        // lw x1, 0(x2)  (funct3=2, opcode=0x03, imm=0)
        uint raw = (0u << 20) | (2u << 15) | (2u << 12) | (1u << 7) | 0x03;
        Rv64ArchState s = MakeState((2, 0x4000UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0xFFFFFFFF80000001UL, r.RegisterResult.Value);
    }

    // ── 64-bit ADD / ADDI ─────────────────────────────────────────────────────────

    [Fact]
    public void Add_64BitResult() {
        // add x1, x2, x3
        uint raw = 0x003100B3; // same encoding as RV32 add
        Rv64ArchState s = MakeState((2, 0x7FFFFFFF_FFFFFFFFUL), (3, 1UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0x80000000_00000000UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Addi_NegativeImmSign64() {
        // addi x1, x2, -1 (imm = 0xFFF)
        uint raw = (0xFFFu << 20) | (2u << 15) | (0u << 12) | (1u << 7) | 0x13;
        Rv64ArchState s = MakeState((2, 0UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0xFFFFFFFFFFFFFFFFUL, r.RegisterResult.Value);
    }

    // ── 64-bit shifts ─────────────────────────────────────────────────────────────

    [Fact]
    public void Slli_6BitShamt() {
        // slli x1, x2, 32  — in RV64, shamt=32 is encoded with bit[25]=1
        // opcode=0x13, funct3=1, rd=1, rs1=2, shamt=32 (0b100000) → bits[25:20]=100000, bits[31:26]=000000
        uint raw = (0u << 26) | (32u << 20) | (2u << 15) | (1u << 12) | (1u << 7) | 0x13;
        Rv64ArchState s = MakeState((2, 1UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(1UL << 32, r.RegisterResult.Value);
    }

    [Fact]
    public void Srli_6BitShamt() {
        // srli x1, x2, 32 — opcode=0x13, funct3=5, shamt=32
        uint raw = (0u << 26) | (32u << 20) | (2u << 15) | (5u << 12) | (1u << 7) | 0x13;
        Rv64ArchState s = MakeState((2, 0x1_0000_0000UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(1UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Srai_6BitShamt() {
        // srai x1, x2, 32 — opcode=0x13, funct3=5, bits[31:26]=010000 (top6=0x10), shamt[5:0]=32 (=0b100000)
        // full imm field [11:0] = 010000_100000 = 0x420
        uint raw = (0x10u << 26) | (32u << 20) | (2u << 15) | (5u << 12) | (1u << 7) | 0x13;
        Rv64ArchState s = MakeState((2, 0x8000_0000_0000_0000UL)); // negative 64-bit
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0xFFFFFFFF80000000UL, r.RegisterResult.Value);
    }

    // ── 64-bit SLT / BLT ─────────────────────────────────────────────────────────

    [Fact]
    public void Slt_64BitSigned() {
        // slt x1, x2, x3  — x2 = 0x8000_0000_0000_0000 (very negative), x3 = 1
        uint raw = 0x003120B3; // slt x1, x2, x3
        Rv64ArchState s = MakeState((2, 0x8000_0000_0000_0000UL), (3, 1UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(1UL, r.RegisterResult.Value); // negative < positive in 64-bit signed
    }

    [Fact]
    public void Slt_WouldBe0_If32BitOnly() {
        // This test verifies RV64 SLT uses 64-bit comparisons.
        // x2 = 0x0000_0001_0000_0000 (positive in 64-bit, but upper 32 bits would be 1)
        // x3 = 1
        // In 32-bit (wrong): (int)x2 = 0 < 1 → slt=1
        // In 64-bit (correct): x2=4294967296 > 1 → slt=0
        uint raw = 0x003120B3;
        Rv64ArchState s = MakeState((2, 0x0000_0001_0000_0000UL), (3, 1UL));
        ExecuteResult r = Exec(raw, s);
        Assert.Equal(0UL, r.RegisterResult.Value);
    }

    [Fact]
    public void Blt_64BitSigned_Taken() {
        // blt x2, x3, +4: taken if x2 < x3 (signed 64-bit)
        // B-type: funct3=4, rs1=2, rs2=3, imm=4 → offset=4
        // imm[12|10:5|4:1|11] for offset=4: imm[2:1]=10 → bits[11:8]=0010, rest 0
        uint raw = (0u << 31) | (0u << 25) | (3u << 20) | (2u << 15) | (4u << 12) | (2u << 8) | (0u << 7) | 0x63;
        Rv64ArchState s = MakeState((2, 0x8000_0000_0000_0000UL), (3, 1UL));
        ExecuteResult r = Exec(raw, s);
        Assert.True(r.BranchTaken); // -huge < 1 → taken
    }
}