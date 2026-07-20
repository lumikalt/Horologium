namespace Mechanism;

/// <summary>
///     Optional <see cref="ISyscallHandler" /> capability: capture/restore whatever mutable
///     host-side state the handler carries (fd table, brk/mmap cursors, stdin position, ...)
///     across a checkpoint boundary. <see cref="ArchitecturalCheckpoint" /> only covers guest
///     architectural state (registers, memory, ISA blob); a handler's own state lives outside
///     that and needs this separate capability so SimPoint checkpoint-and-measure sampling can
///     restore a syscall-emulated interval faithfully instead of resetting to a fresh handler.
/// </summary>
public interface ICheckpointableSyscallHandler : ISyscallHandler {
    /// <summary>Serializes this handler's mutable state to <paramref name="writer" />.</summary>
    void WriteState(BinaryWriter writer);

    /// <summary>Restores this handler's mutable state from <paramref name="reader" />.</summary>
    void ReadState(BinaryReader reader);
}