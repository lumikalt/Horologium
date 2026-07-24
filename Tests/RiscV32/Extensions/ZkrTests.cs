#region

using Mechanism;
using RiscV32;
using RiscV32.Decode;
using RiscV32.Execute;
using RiscV32.Memory;
using RiscV32.Registers;
using RiscV32.State;

#endregion

namespace Tests.RiscV32.Extensions;

/// <summary>
///     Tests for the Zkr entropy source extension: the <c>seed</c> CSR (address 0x015).
///     <para>
///         This simulator models <c>seed</c> as a virtual entropy source (spec §4.2.3): every
///         poll succeeds with fresh pseudorandomness (OPST=ES16), so BIST/WAIT/DEAD never appear.
///         What's tested here is the architecturally-visible contract: the ES16 status encoding,
///         wipe-on-read (successive polls differ), the read-only-access illegal-instruction rule
///         (spec §4.1), and the default M-mode-only access control (spec §4.3).
///     </para>
/// </summary>
public class ZkrTests {
    private const int Seed = 0x015;
    private const uint OpstMask = 0xC0000000; // bits[31:30]
    private const uint OpstEs16 = 0x80000000; // 0b10 << 30

    private readonly Rv32Decoder _dec = new();
    private readonly Rv32Executor _exe = new();
    private readonly FlatMemory _mem = new(4096);

    private static Rv32ArchState MakeState(PrivilegeLevel priv) =>
        new() { PrivilegeLevel = priv, };

    private ExecuteResult Exec(uint raw, Rv32ArchState state) {
        ITooth instr = _dec.Decode(0, raw);
        return _exe.Execute(instr, state, _mem);
    }

    // ── Encoders ───────────────────────────────────────────────────────────────

    private static uint CsrInstr(int csr, int rs1, int funct3, int rd) =>
        (uint)(((csr & 0xFFF) << 20) | ((rs1 & 0x1F) << 15) | ((funct3 & 0x7) << 12) | ((rd & 0x1F) << 7) | 0x73);

    private static uint CsrrwSeed(int rd, int rs1) => ZkrTests.CsrInstr(ZkrTests.Seed, rs1, 1, rd);
    private static uint CsrrsSeed(int rd, int rs1) => ZkrTests.CsrInstr(ZkrTests.Seed, rs1, 2, rd);
    private static uint CsrrcSeed(int rd, int rs1) => ZkrTests.CsrInstr(ZkrTests.Seed, rs1, 3, rd);

    private static uint CsrrsiSeed(int rd, int zimm) => ZkrTests.CsrInstr(ZkrTests.Seed, zimm, 6, rd);

    // ── ES16 status / entropy shape (spec §4.1) ───────────────────────────────

    [Fact]
    public void CsrrwSeed_ReturnsEs16Status() {
        // csrrw rd, seed, x0 — the spec's documented polling idiom.
        ExecuteResult r = Exec(ZkrTests.CsrrwSeed(1, 0), ZkrTests.MakeState(RvPrivilege.Machine));
        Assert.False(r.HasTrap);
        var word = (uint)r.RegisterResult.Value;
        Assert.Equal(ZkrTests.OpstEs16, word & ZkrTests.OpstMask);
    }

    [Fact]
    public void CsrrwSeed_ReservedAndCustomBitsAreZero() {
        // §4.1: "An implementation may safely set reserved and custom bits to zeros."
        ExecuteResult r = Exec(ZkrTests.CsrrwSeed(1, 0), ZkrTests.MakeState(RvPrivilege.Machine));
        var word = (uint)r.RegisterResult.Value;
        Assert.Equal(0u, word & 0x3FFF0000); // bits[29:16] = reserved(29:24) + custom(23:16)
    }

    [Fact]
    public void CsrrwSeed_SuccessivePolls_WipeOnRead() {
        // Wipe-on-read: successive polls must not keep returning the same entropy value.
        // With a 16-bit entropy space, requiring *some* difference across 20 polls makes an
        // honest implementation's false-positive rate negligible (not a strict every-poll check,
        // to avoid a flaky test on a genuine same-value coincidence).
        Rv32ArchState s = ZkrTests.MakeState(RvPrivilege.Machine);
        var values = new HashSet<uint>();
        for (var i = 0; i < 20; i++) {
            ExecuteResult r = Exec(ZkrTests.CsrrwSeed(1, 0), s);
            values.Add((uint)r.RegisterResult.Value & 0xFFFF);
        }

        Assert.True(values.Count > 1, "seed CSR returned the same entropy value on every poll");
    }

    // ── Read-only access is illegal (spec §4.1) ───────────────────────────────

    [Fact]
    public void CsrrsSeed_WithRs1Zero_IsIllegalInstruction() {
        // csrrs rd, seed, x0 performs no write — a read-only access, which must trap.
        ExecuteResult r = Exec(ZkrTests.CsrrsSeed(1, 0), ZkrTests.MakeState(RvPrivilege.Machine));
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void CsrrcSeed_WithRs1Zero_IsIllegalInstruction() {
        ExecuteResult r = Exec(ZkrTests.CsrrcSeed(1, 0), ZkrTests.MakeState(RvPrivilege.Machine));
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void CsrrsiSeed_WithZimmZero_IsIllegalInstruction() {
        ExecuteResult r = Exec(ZkrTests.CsrrsiSeed(1, 0), ZkrTests.MakeState(RvPrivilege.Machine));
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void CsrrsSeed_WithNonzeroRs1_Succeeds() {
        // csrrs rd, seed, rs1 with rs1 != x0 does write (the write value is ignored, but the
        // access itself is a legal read-write) — must not trap.
        var s = new Rv32ArchState();
        s.PrivilegeLevel = RvPrivilege.Machine;
        s.IntegerRegisters.Write(2, 0xFFFFFFFF);
        ExecuteResult r = Exec(ZkrTests.CsrrsSeed(1, 2), s);
        Assert.False(r.HasTrap);
        Assert.Equal(ZkrTests.OpstEs16, (uint)r.RegisterResult.Value & ZkrTests.OpstMask);
    }

    // ── Access control: default M-mode-only (spec §4.3) ───────────────────────

    [Fact]
    public void CsrrwSeed_FromMachineMode_Succeeds() {
        ExecuteResult r = Exec(ZkrTests.CsrrwSeed(1, 0), ZkrTests.MakeState(RvPrivilege.Machine));
        Assert.False(r.HasTrap);
    }

    [Fact]
    public void CsrrwSeed_FromSupervisorMode_IsIllegalInstruction() {
        // mseccfg.sseed is not modeled — this simulator always enforces the un-overridden
        // default (M-mode-only), per CLAUDE.md-documented simplification in CsrFile.
        ExecuteResult r = Exec(ZkrTests.CsrrwSeed(1, 0), ZkrTests.MakeState(RvPrivilege.Supervisor));
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }

    [Fact]
    public void CsrrwSeed_FromUserMode_IsIllegalInstruction() {
        ExecuteResult r = Exec(ZkrTests.CsrrwSeed(1, 0), ZkrTests.MakeState(RvPrivilege.User));
        Assert.True(r.HasTrap);
        Assert.Equal(RvTrapCause.IllegalInstruction, r.Trap!.Cause);
    }
}
