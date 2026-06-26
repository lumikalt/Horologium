using Mechanism;
using RiscV.Registers;
using RiscV.State;

namespace RiscV.Memory;

internal sealed class RvFetchTranslator(IArchState state, IMemory memory) : IFetchTranslator {
    public (ulong PhysAddr, int FaultCause) Translate(ulong virtualPc) {
        // M-mode instruction fetch always uses physical addresses (RISC-V priv spec §3.1.6).
        if (state.PrivilegeLevel == RvPrivilege.Machine) return (virtualPc, 0);
        if (state.SystemRegisters is not CsrFile csrs) return (virtualPc, 0);
        return Sv32Walker.Translate(memory, csrs.DirectRead(CsrFile.Satp),
            virtualPc, isWrite: false, isExec: true, state.PrivilegeLevel);
    }
}
