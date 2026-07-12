using Mechanism;
using RiscV32.State;
using RiscV64.State;

namespace RiscV64.Memory;

public sealed class Rv64FetchTranslator(IArchState state, IMemory memory) : IFetchTranslator {
    public (ulong PhysAddr, int FaultCause) Translate(ulong virtualPc) {
        // M-mode instruction fetch always uses physical addresses (RISC-V priv spec §3.1.6).
        if (state.PrivilegeLevel == RvPrivilege.Machine) return (virtualPc, 0);
        if (state is not Rv64ArchState rv64State) return (virtualPc, 0);
        return Sv39Walker.Translate(
            memory, rv64State.Rv64Csrs.Satp,
            virtualPc, false, true, state.PrivilegeLevel
        );
    }
}
