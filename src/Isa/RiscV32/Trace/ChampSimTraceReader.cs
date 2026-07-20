#region

using System.IO.Compression;

#endregion

namespace RiscV32.Trace;

/// <summary>
///     Reads a ChampSim binary trace: a dense stream of 64-byte <c>input_instr</c> records
///     (see ChampSim's <c>inc/trace_instruction.h</c>) — <c>ip</c> (u64), <c>is_branch</c>/
///     <c>branch_taken</c> (u8 each), 2 destination + 4 source register indices (u8 each), then
///     2 destination + 4 source memory addresses (u64 each, 0 = unused slot). Little-endian, no
///     header. Transparently gunzips a stream that starts with the gzip magic (the format ChampSim
///     traces are normally distributed in); a plain <c>.trace</c> stream is read as-is. <c>.xz</c>-
///     compressed traces must be decompressed externally first — .NET has no built-in xz decoder.
/// </summary>
public sealed class ChampSimTraceReader : IDisposable {
    private const int NumDestinations = 2;
    private const int NumSources = 4;

    private const int RecordSize = 8 + 1 + 1 + ChampSimTraceReader.NumDestinations + ChampSimTraceReader.NumSources
                                 + ChampSimTraceReader.NumDestinations * 8 + ChampSimTraceReader.NumSources * 8;

    private readonly Stream _stream;

    public ChampSimTraceReader(Stream input) {
        if (!input.CanRead) throw new ArgumentException("Stream must be readable.", nameof(input));
        _stream = LooksGzipped(input) ? new GZipStream(input, CompressionMode.Decompress) : input;
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Peeks the first two bytes for the gzip magic (0x1F 0x8B), rewinding if seekable.</summary>
    private static bool LooksGzipped(Stream s) {
        if (!s.CanSeek) return false;
        long pos = s.Position;
        int b0 = s.ReadByte();
        int b1 = s.ReadByte();
        s.Position = pos;
        return b0 == 0x1F && b1 == 0x8B;
    }

    /// <summary>Lazily enumerates all records in the trace, in file order.</summary>
    public IEnumerable<ChampSimTraceRecord> ReadAll() {
        var buf = new byte[ChampSimTraceReader.RecordSize];
        while (true) {
            int read = ReadFully(buf);
            if (read == 0) yield break;
            if (read != ChampSimTraceReader.RecordSize)
                throw new InvalidDataException(
                    $"Truncated ChampSim trace record: got {read} of {ChampSimTraceReader.RecordSize} bytes."
                );

            var ip = BitConverter.ToUInt64(buf, 0);
            bool isBranch = buf[8] != 0;
            bool branchTaken = buf[9] != 0;

            var destRegs = new byte[ChampSimTraceReader.NumDestinations];
            Array.Copy(buf, 10, destRegs, 0, ChampSimTraceReader.NumDestinations);
            var srcRegs = new byte[ChampSimTraceReader.NumSources];
            Array.Copy(buf, 10 + ChampSimTraceReader.NumDestinations, srcRegs, 0, ChampSimTraceReader.NumSources);

            int memOffset = 10 + ChampSimTraceReader.NumDestinations + ChampSimTraceReader.NumSources;
            var destMem = new ulong[ChampSimTraceReader.NumDestinations];
            for (var i = 0; i < ChampSimTraceReader.NumDestinations; i++)
                destMem[i] = BitConverter.ToUInt64(buf, memOffset + i * 8);
            var srcMem = new ulong[ChampSimTraceReader.NumSources];
            for (var i = 0; i < ChampSimTraceReader.NumSources; i++)
                srcMem[i] = BitConverter.ToUInt64(buf, memOffset + ChampSimTraceReader.NumDestinations * 8 + i * 8);

            yield return new ChampSimTraceRecord(ip, isBranch, branchTaken, destRegs, srcRegs, destMem, srcMem);
        }
    }

    /// <summary>Reads until <paramref name="buffer" /> is full or the stream is exhausted.</summary>
    private int ReadFully(byte[] buffer) {
        var total = 0;
        while (total < buffer.Length) {
            int n = _stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }

        return total;
    }
}