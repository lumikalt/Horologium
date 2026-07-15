using Mechanism;

namespace Subleq;

public sealed class SubleqArchState : IArchState {
    public ulong Pc { get; set; }

    public PrivilegeLevel PrivilegeLevel { get; set; } = PrivilegeLevel.User;

    // Subleq only ever runs on SingleCycleTrain, which has no forwarding overlay to swap this
    // property for; the setter exists solely to satisfy IArchState.
    public IRegisterFile IntegerRegisters { get; set; } = new EmptyRegisterFile();
    public ISystemRegisters SystemRegisters { get; } = new NullSystemRegisters();

    public IArchState Snapshot() => new SubleqArchState { Pc = Pc, };
    public void Reset() => Pc = 0;
}

internal sealed class EmptyRegisterFile : IRegisterFile {
    public int Count => 0;
    public int Width => 32;
    public ulong Read(int index) => 0;
    public void Write(int index, ulong value) { }
}

internal sealed class NullSystemRegisters : ISystemRegisters {
    public ulong Read(uint address, PrivilegeLevel currentPrivilege) => 0;
    public void Write(uint address, ulong value, PrivilegeLevel currentPrivilege) { }
    public bool Exists(uint address) => false;
}