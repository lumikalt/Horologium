using Mechanism;

namespace RiscV.State;

/// <summary>RISC-V privilege levels (RISC-V Privileged Specification §3.1.6).</summary>
public static class RvPrivilege {
    public static readonly PrivilegeLevel User = new(0);
    public static readonly PrivilegeLevel Supervisor = new(1);
    public static readonly PrivilegeLevel Machine = new(3);
}