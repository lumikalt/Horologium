namespace Mechanism;

/// <summary>
/// Linux syscall-emulation shim plugged into <see cref="IExecutor"/> implementations.
/// When wired up, ECALL intercepts route here instead of generating a trap.
/// </summary>
public interface ISyscallHandler {
    /// <summary>
    /// Execute the syscall identified by <paramref name="syscallNum"/> and return
    /// the appropriate <see cref="ExecuteResult"/>.  Normal syscalls return a result
    /// with a <see cref="ExecuteResult.SideEffect"/> that writes the return value to
    /// <c>a0</c>; <c>SYS_exit</c> and <c>SYS_exit_group</c> set
    /// <see cref="ExecuteResult.RequestHalt"/> instead.
    /// </summary>
    ExecuteResult Handle(ulong syscallNum, IRegisterFile regs, IMemory memory, ulong pc);
}