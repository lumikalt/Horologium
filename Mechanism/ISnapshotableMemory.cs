namespace Mechanism;

/// <summary>
/// An <see cref="IMemory"/> that can export and import its entire backing store as a flat byte span.
/// Implemented by <c>FlatMemory</c>; used by <see cref="ArchitecturalCheckpoint"/>.
/// </summary>
public interface ISnapshotableMemory : IMemory {
    /// <summary>The base address of the backing store.</summary>
    ulong BaseAddress { get; }

    /// <summary>The total size of the backing store in bytes.</summary>
    int SizeBytes { get; }

    /// <summary>Copies the entire backing store into <paramref name="dest"/>.</summary>
    void CopyTo(Span<byte> dest);

    /// <summary>Overwrites the entire backing store from <paramref name="data"/>.</summary>
    void LoadFrom(ReadOnlySpan<byte> data);
}
