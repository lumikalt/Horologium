#region

using Mechanism;

#endregion

namespace Pdp8;

public sealed class Pdp8TrapController : ITrapController {
    public ulong RaiseTrap(TrapInfo trap, IArchState state) => trap.Pc;
    public ulong ReturnFromTrap(PrivilegeLevel returningFrom, IArchState state) => state.Pc;
}