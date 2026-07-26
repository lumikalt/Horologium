namespace Mechanism;

/// <summary>
///     Lets a <see cref="ISyscallHandler" /> (specifically, a <c>clone()</c> implementation) bring
///     a new hart to life on whatever is driving the simulation (e.g. <c>MultiHartKernel</c>).
///     Wired up post-construction (a settable property on the syscall handler) to break the
///     construction-order cycle: the driver needs its mechanisms/syscall handler built first, but
///     the syscall handler needs a reference to the driver to spawn into it.
/// </summary>
public interface IHartSpawner {
    /// <summary>
    ///     Activates the next available dormant hart slot with <paramref name="initialState" /> as
    ///     its starting architectural state (already <see cref="IArchState.Snapshot" />-derived from
    ///     the parent by the caller, with clone()'s own overrides — new stack pointer, thread
    ///     pointer, zeroed return value — already applied). Returns the new hart's id.
    /// </summary>
    /// <exception cref="InvalidOperationException">No dormant hart slot remains to spawn into.</exception>
    int SpawnHart(IArchState initialState);
}
