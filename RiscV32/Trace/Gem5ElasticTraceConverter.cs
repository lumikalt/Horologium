namespace RiscV32.Trace;

/// <summary>
/// Translates a Horologium HELF elastic trace to the gem5
/// <c>inst_dep_record.proto</c> length-delimited binary stream.
/// <para>
/// File framing (from <c>gem5/src/proto/protoio.cc</c>):
/// <list type="number">
///   <item>4-byte little-endian magic <c>0x356d6567</c> ("gem5") written once at start.</item>
///   <item>Each message: protobuf varint32 byte-count followed by the serialised proto bytes.</item>
/// </list>
/// The first message is an <c>InstDepRecordHeader</c>; the remainder are
/// <c>InstDepRecord</c> messages (both defined in <c>inst_dep_record.proto</c>).
/// </para>
/// <para>
/// <c>InstDepRecordHeader</c> field mapping:
/// <list type="table">
///   <item><term>obj_id    (1, string)</term><description>"gem5.elastic_data_trace"</description></item>
///   <item><term>tick_freq (3, uint64)</term><description>HELF TickFreq or 1 GHz fallback</description></item>
/// </list>
/// </para>
/// <para>
/// <c>InstDepRecord</c> field mapping (verified against <c>gem5/src/proto/inst_dep_record.proto</c>
/// and <c>gem5/src/cpu/o3/probe/elastic_trace.cc</c>):
/// <list type="table">
///   <item><term>seq_num   (1,  uint64)         </term><description>HELF seqno</description></item>
///   <item><term>type      (2,  enum)            </term><description>LOAD=1, STORE=2, COMP=3</description></item>
///   <item><term>p_addr    (3,  uint64)          </term><description>HELF vAddr (no physical translation)</description></item>
///   <item><term>size      (4,  uint32)          </term><description>HELF accessSize</description></item>
///   <item><term>flags     (5,  uint32)          </term><description>omitted (optional)</description></item>
///   <item><term>rob_dep   (6,  repeated uint64) </term><description>HELF addrDeps (memory/commit-order deps)</description></item>
///   <item><term>comp_delay(7,  uint64)          </term><description>HELF compDelay</description></item>
///   <item><term>reg_dep   (8,  repeated uint64) </term><description>HELF robDeps (register data deps)</description></item>
///   <item><term>weight    (9,  uint32)          </term><description>omitted (optional)</description></item>
///   <item><term>pc        (10, uint64)          </term><description>HELF pc</description></item>
///   <item><term>v_addr    (11, uint64)          </term><description>omitted (optional)</description></item>
/// </list>
/// </para>
/// </summary>
public static class Gem5ElasticTraceConverter {
    // gem5 ProtoStream magic number (ASCII "gem5", little-endian uint32)
    private const uint MagicNumber = 0x356d6567;

    // gem5 RecordType enum values (from inst_dep_record.proto)
    private const uint Gem5Load = 1;
    private const uint Gem5Store = 2;
    private const uint Gem5Comp = 3;

    /// <summary>
    /// Converts a HELF stream to a gem5 inst_dep_record proto stream.
    /// Both streams are read/written sequentially; the caller owns both streams.
    /// </summary>
    /// <param name="tickFreq">
    /// Tick frequency written into the <c>InstDepRecordHeader</c>. Use the
    /// recorder's frequency if known, or 0 to use the HELF header value
    /// (falling back to 1 GHz if unset).
    /// </param>
    public static long Convert(Stream input, Stream output, ulong tickFreq = 0) {
        using var reader = new ElasticTraceReader(input);
        ulong freq = tickFreq > 0 ? tickFreq : reader.TickFreq > 0 ? reader.TickFreq : 1_000_000_000;

        // gem5 ProtoOutputStream writes a 4-byte LE magic number before any messages.
        WriteLittleEndian32(output, Gem5ElasticTraceConverter.MagicNumber);

        // InstDepRecordHeader: obj_id (field 1), tick_freq (field 3)
        WriteMessage(
            output, w => {
                WriteTaggedString(w, 1, "gem5.elastic_data_trace");
                WriteTaggedVarint(w, 3, freq);
            }
        );

        long count = 0;
        foreach (ElasticTraceRecord rec in reader.ReadAll()) {
            WriteMessage(output, w => WriteInstDepRecord(w, rec));
            count++;
        }

        return count;
    }

    // ── InstDepRecord serialisation ────────────────────────────────────────────

    private static void WriteInstDepRecord(BinaryWriter w, ElasticTraceRecord rec) {
        // field 1: seq_num (uint64)
        WriteTaggedVarint(w, 1, rec.SeqNo);

        // field 2: type (enum)
        uint gem5Type = rec.Type switch {
            ElasticTraceType.Load  => Gem5ElasticTraceConverter.Gem5Load,
            ElasticTraceType.Store => Gem5ElasticTraceConverter.Gem5Store,
            _                      => Gem5ElasticTraceConverter.Gem5Comp,
        };
        WriteTaggedVarint(w, 2, gem5Type);

        // field 3: p_addr = vAddr (no V→P translation; optional, skipped if zero)
        if (rec.VAddr != 0) WriteTaggedVarint(w, 3, rec.VAddr);

        // field 4: size (uint32; optional, skipped if zero)
        if (rec.AccessSize != 0) WriteTaggedVarint(w, 4, (ulong)rec.AccessSize);

        // field 5: flags — omitted

        // field 6: rob_dep (repeated) — memory/commit-order dependencies (HELF addrDeps)
        foreach (ulong dep in rec.AddrDeps) WriteTaggedVarint(w, 6, dep);

        // field 7: comp_delay (uint64)
        WriteTaggedVarint(w, 7, rec.CompDelay);

        // field 8: reg_dep (repeated) — register data dependencies (HELF robDeps)
        foreach (ulong dep in rec.RobDeps) WriteTaggedVarint(w, 8, dep);

        // field 9: weight — omitted

        // field 10: pc (uint64)
        WriteTaggedVarint(w, 10, rec.Pc);
    }

    // ── gem5 framing: LE magic + varint32 length per message ──────────────────

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
        using (var msgWriter = new BinaryWriter(msgBuf, System.Text.Encoding.UTF8, true)) { body(msgWriter); }

        byte[] bytes = msgBuf.ToArray();
        // Varint32 length prefix (as in gem5 CodedOutputStream::WriteVarint32)
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

    // ── Protobuf wire encoding ─────────────────────────────────────────────────

    // Wire type 0 = varint, wire type 2 = length-delimited
    private static void WriteTaggedVarint(BinaryWriter w, int fieldNumber, ulong value) {
        WriteVarint(w, (ulong)((fieldNumber << 3) | 0)); // wire type 0
        WriteVarint(w, value);
    }

    private static void WriteTaggedString(BinaryWriter w, int fieldNumber, string value) {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteVarint(w, (ulong)((fieldNumber << 3) | 2)); // wire type 2 (length-delimited)
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