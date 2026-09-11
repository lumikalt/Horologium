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

/// <summary>
///     Direct executor-level tests for <see cref="RvFusedLoadAlu" /> (TODO.md's µops
///     micro-fusion item): a load immediately followed by an ALU op that reads and
///     overwrites the load's own destination register, folded by
///     <see cref="RvMacroFuser" /> into a single fused execution. These bypass the
///     pipeline entirely and call <see cref="Rv32Executor.Execute" /> directly, so they
///     can exercise the one genuinely load-bearing correctness property: if the load's
///     address translation faults, the ALU half must never run.
/// </summary>
public class FusedLoadAluExecutorTests {
    // ── Sv32 memory builder (adapted from FetchTranslationTests, data-fault variant) ──
    // Root PT @ 0x1000 entry 0 points at L1 PT @ 0x2000; L1 PT entry 0 is left invalid
    // (V=0), so any access to VA 0x0000_0xxx raises a page fault.
    private const uint Satp = 0x80000001u;
    private const ulong RootPtPa = 0x1000;
    private readonly Rv32Executor _exe = new();

    private static FlatMemory BuildFaultingMemory() {
        var mem = new FlatMemory(0x3000);
        mem.Write(FusedLoadAluExecutorTests.RootPtPa, (2u << 10) | 1u, 4);
        // L1 PT entry 0 left at its zero-initialized value (V=0) — deliberately invalid.
        return mem;
    }

    private static Rv32ArchState MakeUserState(int rs1, ulong rs1Val) {
        var s = new Rv32ArchState();
        s.SystemRegisters.Write(CsrFile.Satp, FusedLoadAluExecutorTests.Satp, RvPrivilege.Machine);
        s.PrivilegeLevel = RvPrivilege.User;
        s.IntegerRegisters.Write(rs1, rs1Val);
        return s;
    }

    [Fact]
    public void FusedLoadAlu_ComputesLoadThenAlu_RegisterRegister() {
        var mem = new FlatMemory(4096);
        mem.Write(64, 10, 4); // *[64] = 10
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(1, 64); // rs1 (address base)
        state.IntegerRegisters.Write(2, 7);  // rs2 (independent operand for the ALU half)

        // lw t0, 0(x1); add t0, t0, x2  =>  t0 = mem[64] + x2 = 10 + 7 = 17
        var payload = new RvFusedLoadAlu(new RvLw(5, 1, 0), new RvAdd(5, 5, 2));
        ITooth tooth = new RvInstruction(0, 0, 5, [1, 2,], ToothClass.Load, payload);

        ExecuteResult result = _exe.Execute(tooth, state, mem);

        Assert.False(result.HasTrap);
        Assert.True(result.RegisterResult.HasValue);
        Assert.Equal(17UL, result.RegisterResult.Value);
    }

    [Fact]
    public void FusedLoadAlu_ComputesLoadThenAlu_RegisterImmediate() {
        var mem = new FlatMemory(4096);
        mem.Write(64, 10, 4); // *[64] = 10
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(1, 64);

        // lw t0, 0(x1); addi t0, t0, 4  =>  t0 = mem[64] + 4 = 14
        var payload = new RvFusedLoadAlu(new RvLw(5, 1, 0), new RvAddi(5, 5, 4));
        ITooth tooth = new RvInstruction(0, 0, 5, [1,], ToothClass.Load, payload);

        ExecuteResult result = _exe.Execute(tooth, state, mem);

        Assert.False(result.HasTrap);
        Assert.Equal(14UL, result.RegisterResult.Value);
    }

    [Fact]
    public void FusedLoadAlu_SignExtendsNarrowLoadBeforeApplyingAlu() {
        var mem = new FlatMemory(4096);
        mem.Write(64, 0xFFu, 1); // *[64] = -1 as a signed byte

        // Plain (unfused) lb, for reference.
        var plainState = new Rv32ArchState();
        plainState.IntegerRegisters.Write(1, 64);
        ExecuteResult plain = _exe.Execute(
            new RvInstruction(0, 0, 5, [1,], ToothClass.Load, new RvLb(5, 1, 0)), plainState, mem
        );
        Assert.Equal(0xFFFFFFFFUL, plain.RegisterResult.Value); // sign-extended -1

        // lb t0, 0(x1); addi t0, t0, 1  =>  t0 = -1 + 1 = 0
        var state = new Rv32ArchState();
        state.IntegerRegisters.Write(1, 64);
        var payload = new RvFusedLoadAlu(new RvLb(5, 1, 0), new RvAddi(5, 5, 1));
        ITooth tooth = new RvInstruction(0, 0, 5, [1,], ToothClass.Load, payload);
        ExecuteResult result = _exe.Execute(tooth, state, mem);

        Assert.Equal(0UL, result.RegisterResult.Value);
    }

    [Fact]
    public void FusedLoadAlu_LoadPageFault_PropagatesTrapWithoutApplyingAlu() {
        FlatMemory mem = BuildFaultingMemory();
        Rv32ArchState state = MakeUserState(1, 0); // VA 0x0 — deliberately unmapped

        var payload = new RvFusedLoadAlu(new RvLw(5, 1, 0), new RvAddi(5, 5, 1));
        ITooth tooth = new RvInstruction(0, 0, 5, [1,], ToothClass.Load, payload);

        ExecuteResult result = _exe.Execute(tooth, state, mem);

        Assert.True(result.HasTrap);
        Assert.Equal(RvTrapCause.LoadPageFault, result.Trap!.Cause);
        // The ALU half never ran: no register value was ever produced for this trapped attempt.
        Assert.False(result.RegisterResult.HasValue);
    }

    [Fact]
    public void FusedLoadAlu_LoadPageFault_MatchesUnfusedLoadsFaultBehavior() {
        FlatMemory mem = BuildFaultingMemory();

        Rv32ArchState plainState = MakeUserState(1, 0);
        ExecuteResult plain = _exe.Execute(
            new RvInstruction(0, 0, 5, [1,], ToothClass.Load, new RvLw(5, 1, 0)), plainState, mem
        );

        Rv32ArchState fusedState = MakeUserState(1, 0);
        var payload = new RvFusedLoadAlu(new RvLw(5, 1, 0), new RvAddi(5, 5, 1));
        ExecuteResult fused = _exe.Execute(
            new RvInstruction(0, 0, 5, [1,], ToothClass.Load, payload), fusedState, mem
        );

        Assert.True(plain.HasTrap);
        Assert.True(fused.HasTrap);
        Assert.Equal(plain.Trap!.Cause, fused.Trap!.Cause);
        Assert.Equal(plain.Trap!.TrapValue, fused.Trap!.TrapValue);
    }
}