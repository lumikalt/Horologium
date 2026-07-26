namespace Mechanism;

/// <summary>
///     Linux syscall-emulation shim plugged into <see cref="IExecutor" /> implementations.
///     When wired up, ECALL intercepts route here instead of generating a trap.
/// </summary>
public interface ISyscallHandler {
    /// <summary>
    ///     Execute the syscall identified by <paramref name="syscallNum" /> and return
    ///     the appropriate <see cref="ExecuteResult" />.  Normal syscalls return a result
    ///     with a <see cref="ExecuteResult.SideEffect" /> that writes the return value to
    ///     <c>a0</c>; <c>SYS_exit</c> and <c>SYS_exit_group</c> set
    ///     <see cref="ExecuteResult.RequestHalt" /> instead.
    /// </summary>
    /// <param name="state">
    ///     The calling hart's full architectural state — not just its registers — so a
    ///     thread-creation syscall (<c>clone</c>) can <see cref="IArchState.Snapshot" /> it to
    ///     build a new hart's initial state (the new hart inherits everything from the caller
    ///     except the overrides <c>clone</c>'s own arguments specify, matching real clone()
    ///     semantics).
    /// </param>
    ExecuteResult Handle(ulong syscallNum, IArchState state, IMemory memory, ulong pc);
}