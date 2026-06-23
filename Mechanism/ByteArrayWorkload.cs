namespace Mechanism;

/// <summary>
/// An <see cref="IWorkload"/> backed by a raw byte array.
/// The bytes are loaded at <paramref name="loadAddress"/>; execution starts at <paramref name="entryPoint"/>.
/// </summary>
public sealed class ByteArrayWorkload(
    byte[] program,
    ulong loadAddress = 0,
    ulong? entryPoint = null,
    int? memorySizeBytes = null
) : IWorkload {
    public ulong EntryPoint { get; } = entryPoint ?? loadAddress;
    public int MemorySize { get; } = memorySizeBytes ?? Math.Max(65536, program.Length + (int)loadAddress);
    public int CodeSize => program.Length;

    public void Load(IMemory memory) => memory.Load(loadAddress, program);
}