using System.Text;

namespace Mechanism;

/// <summary>
/// Architectural-state-only checkpoint: PC, privilege, integer registers, memory, and
/// an ISA-specific blob (CSRs, VRF, UVE — whatever <see cref="IArchState.WriteState"/> captures).
/// <para>
/// Save after a run completes (all instructions committed) for a precise restart point.
/// Use to implement fast-forward→detailed handoffs: fast-forward with a lightweight pipeline,
/// save, then reload and run with a detailed OoO model.
/// </para>
/// </summary>
public sealed class ArchitecturalCheckpoint {
    private const uint Magic = 0x4F524F48; // "HORO" little-endian
    private const int Version = 1;

    /// <summary>Elapsed ticks at the time of the checkpoint.</summary>
    public ulong Tick { get; }

    /// <summary>Program counter (commit-boundary value).</summary>
    public ulong Pc { get; }

    /// <summary>Privilege level value at the time of the checkpoint.</summary>
    public int PrivilegeLevelValue { get; }

    /// <summary>Base address of the memory snapshot.</summary>
    public ulong MemoryBaseAddress { get; }

    /// <summary>Size of the memory snapshot in bytes.</summary>
    public int MemorySizeBytes { get; }

    private readonly ulong[] _intRegs;
    private readonly byte[] _memoryData;
    private readonly byte[] _isaBlob;

    private ArchitecturalCheckpoint(
        ulong tick,
        ulong pc,
        int privilegeLevel,
        ulong[] intRegs,
        byte[] memoryData,
        byte[] isaBlob,
        ulong memBase,
        int memSize
    ) {
        Tick = tick;
        Pc = pc;
        PrivilegeLevelValue = privilegeLevel;
        _intRegs = intRegs;
        _memoryData = memoryData;
        _isaBlob = isaBlob;
        MemoryBaseAddress = memBase;
        MemorySizeBytes = memSize;
    }

    /// <summary>Saves the current architectural state to <paramref name="path"/>.</summary>
    public static void Save(string path, IArchState state, ISnapshotableMemory memory, ulong tick) {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Save(fs, state, memory, tick);
    }

    /// <summary>
    /// Captures the current architectural state synchronously (registers, memory, and the
    /// ISA blob are copied before this call returns, so it is safe even if the caller keeps
    /// mutating <paramref name="state"/> or <paramref name="memory"/> afterwards), then writes
    /// the serialized checkpoint to <paramref name="path"/> on a worker thread.
    /// </summary>
    /// <returns>A task that completes once the file write finishes.</returns>
    public static Task SaveAsync(string path, IArchState state, ISnapshotableMemory memory, ulong tick) {
        using var ms = new MemoryStream();
        Save(ms, state, memory, tick);
        byte[] data = ms.ToArray();
        return Task.Run(() => File.WriteAllBytes(path, data));
    }

    /// <summary>Saves the current architectural state to <paramref name="stream"/>.</summary>
    public static void Save(Stream stream, IArchState state, ISnapshotableMemory memory, ulong tick) {
        using var w = new BinaryWriter(stream, Encoding.UTF8, true);

        w.Write(ArchitecturalCheckpoint.Magic);
        w.Write(ArchitecturalCheckpoint.Version);
        w.Write(tick);
        w.Write(state.Pc);
        w.Write(state.PrivilegeLevel.Level);

        // Integer (+ FP for unified files) registers
        int regCount = state.IntegerRegisters.Count;
        w.Write(regCount);
        w.Write(state.IntegerRegisters.Width);
        for (var i = 0; i < regCount; i++) w.Write(state.IntegerRegisters.Read(i));

        // Memory backing store
        w.Write(memory.BaseAddress);
        w.Write(memory.SizeBytes);
        var memBuf = new byte[memory.SizeBytes];
        memory.CopyTo(memBuf);
        w.Write(memBuf);

        // ISA-specific blob (CSRs, VRF, UVE, etc.)
        using var msIsa = new MemoryStream();
        using (var bwIsa = new BinaryWriter(msIsa, Encoding.UTF8, true)) { state.WriteState(bwIsa); }

        byte[] isaBlob = msIsa.ToArray();
        w.Write(isaBlob.Length);
        if (isaBlob.Length > 0) w.Write(isaBlob);
    }

    /// <summary>
    /// Loads a checkpoint from <paramref name="path"/>.
    /// Does not modify any live state — call <see cref="RestoreInto"/> to apply.
    /// </summary>
    /// <exception cref="CheckpointException">Thrown when the file is invalid or the version is unsupported.</exception>
    public static ArchitecturalCheckpoint Load(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return Load(fs, path);
    }

    /// <summary>
    /// Loads a checkpoint from <paramref name="stream"/>.
    /// Does not modify any live state — call <see cref="RestoreInto"/> to apply.
    /// </summary>
    /// <exception cref="CheckpointException">Thrown when the stream data is invalid.</exception>
    public static ArchitecturalCheckpoint Load(Stream stream) => Load(stream, "<stream>");

    private static ArchitecturalCheckpoint Load(Stream stream, string source) {
        using var r = new BinaryReader(stream, Encoding.UTF8, true);

        uint magic = r.ReadUInt32();
        if (magic != ArchitecturalCheckpoint.Magic)
            throw new CheckpointException($"Not a Horologium checkpoint: {source}");
        int ver = r.ReadInt32();
        if (ver != ArchitecturalCheckpoint.Version)
            throw new CheckpointException($"Unsupported checkpoint version {ver} in {source}.");

        ulong tick = r.ReadUInt64();
        ulong pc = r.ReadUInt64();
        int privilege = r.ReadInt32();

        int regCount = r.ReadInt32();
        r.ReadInt32(); // regWidth — not needed for restore; width is ISA-defined
        var intRegs = new ulong[regCount];
        for (var i = 0; i < regCount; i++) intRegs[i] = r.ReadUInt64();

        ulong memBase = r.ReadUInt64();
        int memSize = r.ReadInt32();
        byte[] memData = r.ReadBytes(memSize);

        int blobLen = r.ReadInt32();
        byte[] isaBlob = blobLen > 0 ? r.ReadBytes(blobLen) : [];

        return new ArchitecturalCheckpoint(tick, pc, privilege, intRegs, memData, isaBlob, memBase, memSize);
    }

    /// <summary>
    /// Restores this checkpoint into <paramref name="state"/> and <paramref name="memory"/>.
    /// The memory must have the same <see cref="ISnapshotableMemory.BaseAddress"/> and
    /// <see cref="ISnapshotableMemory.SizeBytes"/> as when the checkpoint was saved.
    /// </summary>
    /// <exception cref="CheckpointException">Thrown when the memory geometry does not match.</exception>
    public void RestoreInto(IArchState state, ISnapshotableMemory memory) {
        if (memory.BaseAddress != MemoryBaseAddress || memory.SizeBytes != MemorySizeBytes)
            throw new CheckpointException(
                $"Memory mismatch: checkpoint has base=0x{MemoryBaseAddress:X} size={MemorySizeBytes}, " +
                $"but target has base=0x{memory.BaseAddress:X} size={memory.SizeBytes}."
            );

        state.Pc = Pc;
        state.PrivilegeLevel = (PrivilegeLevel)PrivilegeLevelValue;

        int restoreCount = Math.Min(_intRegs.Length, state.IntegerRegisters.Count);
        for (var i = 0; i < restoreCount; i++) state.IntegerRegisters.Write(i, _intRegs[i]);

        memory.LoadFrom(_memoryData);

        if (_isaBlob.Length > 0) {
            using var ms = new MemoryStream(_isaBlob);
            using var br = new BinaryReader(ms);
            state.ReadState(br);
        }
    }
}

/// <summary>Thrown when a checkpoint file is invalid or cannot be restored.</summary>
public sealed class CheckpointException(string message) : Exception(message);