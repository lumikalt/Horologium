using System.Text;

namespace RiscV32.Trace;

/// <summary>
/// Translates a Horologium HELF elastic trace to the gem5 instruction-fetch
/// trace in the <c>Packet</c>/<c>PacketHeader</c> Protobuf format
/// (<c>packet.proto</c>). This is the <c>instTraceFile</c> that gem5 TraceCPU
/// requires alongside the elastic data trace (<c>dataTraceFile</c>).
/// <para>
/// File framing: identical to the elastic data trace — 4-byte LE magic
/// <c>0x356d6567</c> followed by varint32-length-prefixed proto messages.
/// </para>
/// <para>
/// <c>PacketHeader</c> field mapping:
/// <list type="table">
///   <item><term>obj_id    (1, string)</term><description>"gem5.fetch_trace"</description></item>
///   <item><term>tick_freq (3, uint64)</term><description>HELF TickFreq or 1 GHz fallback</description></item>
/// </list>
/// </para>
/// <para>
/// <c>Packet</c> field mapping (one per committed instruction):
/// <list type="table">
///   <item><term>tick  (1, uint64)</term><description>monotonically increasing; spaced by <c>ticksPerInstr</c></description></item>
///   <item><term>cmd   (2, uint32)</term><description>1 = MemCmd::ReadReq (instruction fetch)</description></item>
///   <item><term>addr  (3, uint64)</term><description>HELF pc (no V→P translation)</description></item>
///   <item><term>size  (4, uint32)</term><description>4 (RV32 fixed-width fetch)</description></item>
///   <item><term>flags (5, uint32)</term><description>0x100 = Request::INST_FETCH</description></item>
///   <item><term>pc    (7, uint64)</term><description>HELF pc (same as addr)</description></item>
/// </list>
/// Note: this is an approximation — one 4-byte ReadReq per committed instruction,
/// not the cache-line-granular wrong-path fetches gem5's O3 CPU would generate.
/// </para>
/// </summary>
public static class Gem5FetchTraceConverter {
    private const uint MagicNumber = 0x356d6567;
    private const uint MemCmdReadReq = 1;
    private const uint InstFetchFlag = 0x100; // Request::INST_FETCH
    private const uint FetchBytes = 4;        // RV32 fixed-width

    /// <summary>
    /// Converts a HELF stream to a gem5 packet-proto fetch-trace stream.
    /// Both streams are read/written sequentially; the caller owns both.
    /// </summary>
    /// <param name="input">HELF elastic trace stream to read from.</param>
    /// <param name="output">gem5 proto stream to write to.</param>
    /// <param name="tickFreq">Tick frequency for the proto header; 0 uses the HELF header value (falling back to 1 GHz).</param>
    /// <param name="ticksPerInstr">
    /// Tick delta between consecutive fetch packets. Defaults to 500 (≈ 500 ns
    /// at 1 GHz), which is conservative but keeps ticks strictly increasing.
    /// TraceCPU only needs a monotonic sequence; exact spacing does not affect
    /// execution timing (driven by the elastic data trace).
    /// </param>
    public static long Convert(
        Stream input,
        Stream output,
        ulong tickFreq = 0,
        ulong ticksPerInstr = 500
    ) {
        using var reader = new ElasticTraceReader(input);
        ulong freq = tickFreq > 0 ? tickFreq
            : reader.TickFreq > 0 ? reader.TickFreq : 1_000_000_000;

        WriteLittleEndian32(output, Gem5FetchTraceConverter.MagicNumber);

        // PacketHeader: obj_id (field 1), tick_freq (field 3)
        WriteMessage(
            output, w => {
                WriteTaggedString(w, 1, "gem5.fetch_trace");
                WriteTaggedVarint(w, 3, freq);
            }
        );

        long count = 0;
        ulong tick = ticksPerInstr;
        foreach (ElasticTraceRecord rec in reader.ReadAll()) {
            ulong t = tick;
            WriteMessage(output, w => WriteFetchPacket(w, rec, t));
            tick += ticksPerInstr;
            count++;
        }

        return count;
    }

    private static void WriteFetchPacket(BinaryWriter w, ElasticTraceRecord rec, ulong tick) {
        WriteTaggedVarint(w, 1, tick);                                  // tick
        WriteTaggedVarint(w, 2, Gem5FetchTraceConverter.MemCmdReadReq); // cmd = ReadReq
        WriteTaggedVarint(w, 3, rec.Pc);                                // addr = PC (no V→P)
        WriteTaggedVarint(w, 4, Gem5FetchTraceConverter.FetchBytes);    // size = 4
        WriteTaggedVarint(w, 5, Gem5FetchTraceConverter.InstFetchFlag); // flags = INST_FETCH
        // field 6 (pkt_id): omitted
        WriteTaggedVarint(w, 7, rec.Pc); // pc = same as addr
    }

    // ── gem5 framing ─────────────────────────────────────────────────────────

    private static void WriteLittleEndian32(Stream s, uint value) {
        Span<byte> buf = stackalloc byte[4];
        buf[0] = (byte)value;
        buf[1] = (byte)(value >> 8);
        buf[2] = (byte)(value >> 16);
        buf[3] = (byte)(value >> 24);
        s.Write(buf);
    }

    private static void WriteMessage(Stream output, Action<BinaryWriter> body) {
        using var msgBuf = new MemoryStream();
        using (var msgWriter = new BinaryWriter(msgBuf, Encoding.UTF8, true)) { body(msgWriter); }

        byte[] bytes = msgBuf.ToArray();
        WriteVarintToStream(output, (ulong)bytes.Length);
        output.Write(bytes);
    }

    private static void WriteVarintToStream(Stream s, ulong value) {
        Span<byte> buf = stackalloc byte[10];
        var len = 0;
        while (value > 0x7F) {
            buf[len++] = (byte)(value | 0x80);
            value >>= 7;
        }

        buf[len++] = (byte)value;
        s.Write(buf[..len]);
    }

    // ── Protobuf wire encoding ────────────────────────────────────────────────

    private static void WriteTaggedVarint(BinaryWriter w, int fieldNumber, ulong value) {
        WriteVarint(w, (ulong)((fieldNumber << 3) | 0)); // wire type 0
        WriteVarint(w, value);
    }

    private static void WriteTaggedString(BinaryWriter w, int fieldNumber, string value) {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(w, (ulong)((fieldNumber << 3) | 2)); // wire type 2
        WriteVarint(w, (ulong)bytes.Length);
        w.Write(bytes);
    }

    private static void WriteVarint(BinaryWriter w, ulong value) {
        while (value > 0x7F) {
            w.Write((byte)(value | 0x80));
            value >>= 7;
        }

        w.Write((byte)value);
    }
}