#region

using System.Text;

#endregion

namespace Mechanism;

/// <summary>
///     A checkpoint of trained microarchitectural tables (caches, TLBs, branch predictor, RAS,
///     ...) layered on top of an <see cref="ArchitecturalCheckpoint" />.
///     <para>
///         Unlike <see cref="ArchitecturalCheckpoint" />, this is only valid at a <em>drained</em>
///         pipeline boundary — no in-flight instructions in the ROB/issue queues/load-store queues.
///         At that boundary the only useful state beyond the architectural registers/memory is the
///         steady-state tables trained by execution so far; capturing them lets a reload skip the
///         warmup a cold restart would otherwise need (the payoff for sampled/SimPoint-style
///         simulation). See <c>OooeTrain.Drain</c> for how a train reaches that boundary.
///     </para>
///     <para>
///         Sections are tagged and length-prefixed: a restore target that doesn't recognize a tag,
///         or has no matching live component (a different cache/predictor configuration), skips
///         that section rather than throwing — graceful degradation back to a cold table.
///     </para>
/// </summary>
public sealed class MicroarchitecturalCheckpoint {
    private const uint Magic = 0x4D524F48; // "HORM" little-endian
    private const int Version = 1;

    private readonly Dictionary<string, byte[]> _sections;

    private MicroarchitecturalCheckpoint(ArchitecturalCheckpoint architectural, Dictionary<string, byte[]> sections) {
        Architectural = architectural;
        _sections = sections;
    }

    /// <summary>The underlying architectural checkpoint (PC, registers, memory, ISA blob).</summary>
    public ArchitecturalCheckpoint Architectural { get; }

    /// <summary>
    ///     Saves a microarchitectural checkpoint: the architectural state plus a caller-supplied
    ///     set of tagged table writers. Each writer is invoked with its own private
    ///     <see cref="BinaryWriter" />; its output is captured as one length-prefixed section.
    /// </summary>
    public static void Save(
        string path,
        IArchState state,
        ISnapshotableMemory memory,
        ulong tick,
        IReadOnlyList<(string Tag, Action<BinaryWriter> Write)> sections
    ) {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Save(fs, state, memory, tick, sections);
    }

    /// <summary>Saves a microarchitectural checkpoint to <paramref name="stream" />.</summary>
    public static void Save(
        Stream stream,
        IArchState state,
        ISnapshotableMemory memory,
        ulong tick,
        IReadOnlyList<(string Tag, Action<BinaryWriter> Write)> sections
    ) {
        using var archMs = new MemoryStream();
        ArchitecturalCheckpoint.Save(archMs, state, memory, tick);
        byte[] archBlob = archMs.ToArray();

        using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        w.Write(MicroarchitecturalCheckpoint.Magic);
        w.Write(MicroarchitecturalCheckpoint.Version);
        w.Write(archBlob.Length);
        w.Write(archBlob);

        w.Write(sections.Count);
        foreach ((string tag, Action<BinaryWriter> write) in sections) {
            using var sectionMs = new MemoryStream();
            using (var sectionW = new BinaryWriter(sectionMs, Encoding.UTF8, true)) { write(sectionW); }

            byte[] blob = sectionMs.ToArray();

            w.Write(tag);
            w.Write(blob.Length);
            if (blob.Length > 0) w.Write(blob);
        }
    }

    /// <summary>
    ///     Loads a microarchitectural checkpoint from <paramref name="path" />.
    ///     Does not modify any live state — call <see cref="Architectural" />.RestoreInto and
    ///     <see cref="TryRestoreSection" /> to apply.
    /// </summary>
    /// <exception cref="CheckpointException">The file is invalid or the version is unsupported.</exception>
    public static MicroarchitecturalCheckpoint Load(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return Load(fs, path);
    }

    /// <summary>Loads a microarchitectural checkpoint from <paramref name="stream" />.</summary>
    /// <exception cref="CheckpointException">The stream data is invalid.</exception>
    public static MicroarchitecturalCheckpoint Load(Stream stream) => Load(stream, "<stream>");

    private static MicroarchitecturalCheckpoint Load(Stream stream, string source) {
        using var r = new BinaryReader(stream, Encoding.UTF8, true);

        uint magic = r.ReadUInt32();
        if (magic != MicroarchitecturalCheckpoint.Magic)
            throw new CheckpointException($"Not a Horologium microarchitectural checkpoint: {source}");
        int ver = r.ReadInt32();
        if (ver != MicroarchitecturalCheckpoint.Version)
            throw new CheckpointException($"Unsupported microarchitectural checkpoint version {ver} in {source}.");

        int archBlobLen = r.ReadInt32();
        byte[] archBlob = r.ReadBytes(archBlobLen);
        ArchitecturalCheckpoint architectural = ArchitecturalCheckpoint.Load(new MemoryStream(archBlob));

        int sectionCount = r.ReadInt32();
        var sections = new Dictionary<string, byte[]>(sectionCount);
        for (var i = 0; i < sectionCount; i++) {
            string tag = r.ReadString();
            int blobLen = r.ReadInt32();
            byte[] blob = blobLen > 0 ? r.ReadBytes(blobLen) : [];
            sections[tag] = blob;
        }

        return new MicroarchitecturalCheckpoint(architectural, sections);
    }

    /// <summary>
    ///     Feeds the section tagged <paramref name="tag" /> (if present) to <paramref name="read" />.
    ///     Returns false — a no-op, not an error — if the tag isn't in this checkpoint (e.g. it was
    ///     saved without that table, or by an older version). The caller decides whether the
    ///     corresponding live component should skip restoring, matching the format's graceful
    ///     degradation contract.
    /// </summary>
    public bool TryRestoreSection(string tag, Action<BinaryReader> read) {
        if (!_sections.TryGetValue(tag, out byte[]? blob)) return false;
        if (blob.Length == 0) return true;

        using var ms = new MemoryStream(blob);
        using var r = new BinaryReader(ms);
        read(r);
        return true;
    }
}