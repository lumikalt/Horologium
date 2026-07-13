using System.Text;

namespace RiscV32.Trace;

/// <summary>
///     Reads a Horologium elastic DDG trace written by <see cref="ElasticTraceWriter" />.
///     Dispose when done; the stream is not closed (it is left open if
///     <see cref="ElasticTraceReader(Stream)" /> was used).
/// </summary>
public sealed class ElasticTraceReader : IDisposable {
    private readonly BinaryReader _in;

    public ElasticTraceReader(Stream input) {
        _in = new BinaryReader(input, Encoding.UTF8, true);

        uint magic = _in.ReadUInt32();
        if (magic != ElasticTraceWriter.Magic)
            throw new InvalidDataException($"Expected HELF magic 0x{ElasticTraceWriter.Magic:X8}, got 0x{magic:X8}");

        uint version = _in.ReadUInt32();
        if (version != ElasticTraceWriter.Version)
            throw new InvalidDataException($"Unsupported HELF version {version}");

        TickFreq = _in.ReadUInt64();
        _in.ReadUInt64(); // reserved
    }

    /// <summary>Tick frequency stored in the trace header (0 if not set by the recorder).</summary>
    public ulong TickFreq { get; }

    public void Dispose() => _in.Dispose();

    /// <summary>
    ///     Lazily enumerates all records in the trace. Records are returned in seqno order.
    ///     Do not call <see cref="Dispose" /> until enumeration is complete.
    /// </summary>
    public IEnumerable<ElasticTraceRecord> ReadAll() {
        while (_in.BaseStream.Position < _in.BaseStream.Length) {
            ulong seqno = _in.ReadUInt64();
            ulong pc = _in.ReadUInt64();
            uint rawEncoding = _in.ReadUInt32();
            var type = (ElasticTraceType)_in.ReadByte();
            uint compDelay = _in.ReadUInt32();
            ulong vAddr = _in.ReadUInt64();
            int accessSize = _in.ReadByte();
            int robCount = _in.ReadByte();
            int addrCount = _in.ReadByte();

            var robDeps = new ulong[robCount];
            for (var i = 0; i < robCount; i++) robDeps[i] = _in.ReadUInt64();
            var addrDeps = new ulong[addrCount];
            for (var i = 0; i < addrCount; i++) addrDeps[i] = _in.ReadUInt64();

            yield return new ElasticTraceRecord(
                seqno, pc, rawEncoding, type, compDelay, vAddr, accessSize, robDeps, addrDeps
            );
        }
    }
}