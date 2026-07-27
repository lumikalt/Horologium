#region

using System.Text;

#endregion

namespace Mechanism;

/// <summary>
///     <see cref="ArchitecturalCheckpoint" />'s multi-hart counterpart: N harts' architectural state
///     (PC, privilege, integer registers, ISA-specific blob) plus <em>one</em> shared-memory blob and
///     <em>one</em> optional shared syscall-handler blob — not N of either. Threads share memory and
///     an fd table/brk/mmap cursor by definition (real <c>clone()</c>'s <c>CLONE_VM|CLONE_FILES</c>),
///     and every established multi-hart config in this codebase (<c>MultiHartKernel</c>'s own
///     constructors, <c>PthreadProbeTests</c>, the LoopPoint profiling tests) already wires one
///     <see cref="IMemory" /> and one <c>LinuxSyscallEmulator</c>-shaped handler across every
///     hart's mechanism — duplicating either N-fold per hart would not just waste space, it would be
///     the wrong model (each hart's copy could drift independently on restore, which real shared
///     memory/fd state never does).
///     <para>
///         Captured at a LoopPoint region boundary, all harts already spawned — restoring a
///         checkpoint does not itself need to replay any <c>clone()</c> calls that happened before
///         the capture point, only the state <em>after</em> them. A region that itself calls
///         <c>clone()</c> mid-measurement is not covered by this alone; that needs
///         <c>MultiHartPipeline</c> dynamic hart activation, tracked separately in <c>TODO.md</c>.
///     </para>
/// </summary>
public sealed class MultiHartCheckpoint {
    private const uint Magic = 0x5254524F; // "ORTR" little-endian — distinct from ArchitecturalCheckpoint's
    private const int Version = 1;
    private readonly ulong[][] _hartIntRegs;

    private readonly byte[][] _hartIsaBlobs;
    private readonly ulong[] _hartPcs;
    private readonly int[] _hartPrivileges;
    private readonly byte[] _memoryData;
    private readonly byte[]? _syscallHandlerBlob;

    private MultiHartCheckpoint(
        ulong tick,
        ulong[] hartPcs,
        int[] hartPrivileges,
        ulong[][] hartIntRegs,
        byte[][] hartIsaBlobs,
        byte[] memoryData,
        ulong memBase,
        int memSize,
        byte[]? syscallHandlerBlob
    ) {
        Tick = tick;
        _hartPcs = hartPcs;
        _hartPrivileges = hartPrivileges;
        _hartIntRegs = hartIntRegs;
        _hartIsaBlobs = hartIsaBlobs;
        _memoryData = memoryData;
        MemoryBaseAddress = memBase;
        MemorySizeBytes = memSize;
        _syscallHandlerBlob = syscallHandlerBlob;
    }

    /// <summary>Elapsed ticks at the time of the checkpoint.</summary>
    public ulong Tick { get; }

    /// <summary>Number of harts this checkpoint covers.</summary>
    public int HartCount => _hartPcs.Length;

    /// <summary>Base address of the shared-memory snapshot.</summary>
    public ulong MemoryBaseAddress { get; }

    /// <summary>Size of the shared-memory snapshot in bytes.</summary>
    public int MemorySizeBytes { get; }

    /// <summary>
    ///     Hart <paramref name="hartId" />'s PC at capture time. Detailed pipeline trains
    ///     (e.g. <c>FiveStageTrain</c>) track their own fetch-address state separately from
    ///     <see cref="IArchState.Pc" /> — seeded once from the train's constructor <c>entryPoint</c>
    ///     parameter and never re-read afterward — so <see cref="RestoreInto" />, which only mutates
    ///     <see cref="IArchState" />, cannot by itself redirect fetch to the restored PC. Callers must
    ///     pass this value as that hart's train's own <c>entryPoint</c> constructor argument
    ///     <em>before</em> calling <see cref="RestoreInto" />, exactly as <c>Experiment.MeasureSimPointCheckpoints</c>
    ///     already does with <c>ArchitecturalCheckpoint.Pc</c>.
    /// </summary>
    public ulong PcOf(int hartId) => _hartPcs[hartId];

    /// <summary>
    ///     Captures every hart in <paramref name="hartStates" /> plus one shared-memory snapshot and
    ///     (if <paramref name="syscallHandler" /> is non-null) one shared syscall-handler-state blob.
    /// </summary>
    public static void Save(
        Stream stream,
        IReadOnlyList<IArchState> hartStates,
        ISnapshotableMemory sharedMemory,
        ICheckpointableSyscallHandler? syscallHandler,
        ulong tick
    ) {
        using var w = new BinaryWriter(stream, Encoding.UTF8, true);

        w.Write(MultiHartCheckpoint.Magic);
        w.Write(MultiHartCheckpoint.Version);
        w.Write(tick);

        w.Write(hartStates.Count);
        foreach (IArchState state in hartStates) {
            w.Write(state.Pc);
            w.Write(state.PrivilegeLevel.Level);

            int regCount = state.IntegerRegisters.Count;
            w.Write(regCount);
            for (var i = 0; i < regCount; i++) w.Write(state.IntegerRegisters.Read(i));

            using var msIsa = new MemoryStream();
            using (var bwIsa = new BinaryWriter(msIsa, Encoding.UTF8, true)) { state.WriteState(bwIsa); }

            byte[] isaBlob = msIsa.ToArray();
            w.Write(isaBlob.Length);
            if (isaBlob.Length > 0) w.Write(isaBlob);
        }

        w.Write(sharedMemory.BaseAddress);
        w.Write(sharedMemory.SizeBytes);
        var memBuf = new byte[sharedMemory.SizeBytes];
        sharedMemory.CopyTo(memBuf);
        w.Write(memBuf);

        if (syscallHandler is null) { w.Write(false); }
        else {
            w.Write(true);
            using var msHandler = new MemoryStream();
            using (var bwHandler = new BinaryWriter(msHandler, Encoding.UTF8, true)) {
                syscallHandler.WriteState(bwHandler);
            }

            byte[] handlerBlob = msHandler.ToArray();
            w.Write(handlerBlob.Length);
            if (handlerBlob.Length > 0) w.Write(handlerBlob);
        }
    }

    /// <summary>Loads a checkpoint from <paramref name="stream" />. Does not modify any live state.</summary>
    /// <exception cref="CheckpointException">Thrown when the stream data is invalid.</exception>
    public static MultiHartCheckpoint Load(Stream stream) {
        using var r = new BinaryReader(stream, Encoding.UTF8, true);

        uint magic = r.ReadUInt32();
        if (magic != MultiHartCheckpoint.Magic)
            throw new CheckpointException("Not a Horologium multi-hart checkpoint.");
        int ver = r.ReadInt32();
        if (ver != MultiHartCheckpoint.Version)
            throw new CheckpointException($"Unsupported multi-hart checkpoint version {ver}.");

        ulong tick = r.ReadUInt64();

        int hartCount = r.ReadInt32();
        var pcs = new ulong[hartCount];
        var privileges = new int[hartCount];
        var intRegs = new ulong[hartCount][];
        var isaBlobs = new byte[hartCount][];
        for (var h = 0; h < hartCount; h++) {
            pcs[h] = r.ReadUInt64();
            privileges[h] = r.ReadInt32();

            int regCount = r.ReadInt32();
            var regs = new ulong[regCount];
            for (var i = 0; i < regCount; i++) regs[i] = r.ReadUInt64();
            intRegs[h] = regs;

            int blobLen = r.ReadInt32();
            isaBlobs[h] = blobLen > 0 ? r.ReadBytes(blobLen) : [];
        }

        ulong memBase = r.ReadUInt64();
        int memSize = r.ReadInt32();
        byte[] memData = r.ReadBytes(memSize);

        bool hasHandler = r.ReadBoolean();
        byte[]? handlerBlob = null;
        if (hasHandler) {
            int handlerLen = r.ReadInt32();
            handlerBlob = handlerLen > 0 ? r.ReadBytes(handlerLen) : [];
        }

        return new MultiHartCheckpoint(
            tick, pcs, privileges, intRegs, isaBlobs, memData, memBase, memSize, handlerBlob
        );
    }

    /// <summary>
    ///     Restores this checkpoint into <paramref name="hartStates" />, <paramref name="sharedMemory" />,
    ///     and (if this checkpoint captured one) <paramref name="syscallHandler" />.
    /// </summary>
    /// <exception cref="CheckpointException">
    ///     Thrown when <paramref name="hartStates" />'s count doesn't match, or the memory geometry
    ///     doesn't match.
    /// </exception>
    public void RestoreInto(
        IReadOnlyList<IArchState> hartStates,
        ISnapshotableMemory sharedMemory,
        ICheckpointableSyscallHandler? syscallHandler
    ) {
        if (hartStates.Count != HartCount)
            throw new CheckpointException($"Checkpoint has {HartCount} harts, but {hartStates.Count} were given.");
        if (sharedMemory.BaseAddress != MemoryBaseAddress || sharedMemory.SizeBytes != MemorySizeBytes)
            throw new CheckpointException(
                $"Memory mismatch: checkpoint has base=0x{MemoryBaseAddress:X} size={MemorySizeBytes}, " +
                $"but target has base=0x{sharedMemory.BaseAddress:X} size={sharedMemory.SizeBytes}."
            );

        for (var h = 0; h < HartCount; h++) {
            IArchState state = hartStates[h];
            state.Pc = _hartPcs[h];
            state.PrivilegeLevel = (PrivilegeLevel)_hartPrivileges[h];

            ulong[] regs = _hartIntRegs[h];
            int restoreCount = Math.Min(regs.Length, state.IntegerRegisters.Count);
            for (var i = 0; i < restoreCount; i++) state.IntegerRegisters.Write(i, regs[i]);

            byte[] isaBlob = _hartIsaBlobs[h];
            if (isaBlob.Length > 0) {
                using var ms = new MemoryStream(isaBlob);
                using var br = new BinaryReader(ms);
                state.ReadState(br);
            }
        }

        sharedMemory.LoadFrom(_memoryData);

        if (_syscallHandlerBlob is not null && syscallHandler is not null) {
            using var ms = new MemoryStream(_syscallHandlerBlob);
            using var br = new BinaryReader(ms);
            syscallHandler.ReadState(br);
        }
    }
}