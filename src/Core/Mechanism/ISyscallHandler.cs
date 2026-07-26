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
    /// <param name="syscallNum">The syscall number, read from the calling convention's syscall register.</param>
    /// <param name="state">
    ///     The calling hart's full architectural state — not just its registers — so a
    ///     thread-creation syscall (<c>clone</c>) can <see cref="IArchState.Snapshot" /> it to
    ///     build a new hart's initial state (the new hart inherits everything from the caller
    ///     except the overrides <c>clone</c>'s own arguments specify, matching real clone()
    ///     semantics).
    /// </param>
    /// <param name="memory">The calling hart's memory, for syscalls that read/write guest buffers.</param>
    /// <param name="pc">The ECALL instruction's own address, for syscalls that compute a resume PC (e.g. <c>clone</c>).</param>
    /// <param name="hartId">
    ///     The calling hart's index (matches <c>MultiHartKernel</c>'s hart slots and the id
    ///     <c>clone()</c> returns from <see cref="IHartSpawner.SpawnHart" />), for syscalls whose
    ///     result varies per hart (e.g. <c>gettid</c>). Always 0 in single-hart setups.
    /// </param>
    ExecuteResult Handle(ulong syscallNum, IArchState state, IMemory memory, ulong pc, int hartId);
}