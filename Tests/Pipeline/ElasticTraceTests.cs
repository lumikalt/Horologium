#region

using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.Trace;

#endregion

namespace Tests.Pipeline;

/// <summary>
///     Elastic DDG trace: recording, binary round-trip, dependence detection, and replay.
/// </summary>
public class ElasticTraceTests {
    // ── Instruction encodings ─────────────────────────────────────────────────
    //   addi x1, x0, 1     0x00100093
    //   addi x2, x0, 2     0x00200113
    //   add  x3, x1, x2    0x002080B3
    //   addi x2, x0, 256   0x10000113  (safe data address — well past instruction space)
    //   addi x1, x0, 42    0x02A00093  (store data)
    //   sw   x1, 0(x2)     0x00112023
    //   lw   x3, 0(x2)     0x00012183
    //   ebreak              0x00100073

    private static byte[] Encode(params uint[] words) {
        var b = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), words[i]);
        return b;
    }

    // Run a byte program through the recorder and return the records.
    private static ElasticTraceRecord[] Record(byte[] program, int memSize = 0x1000) {
        var mem = new FlatMemory(memSize);
        mem.Load(0, program);
        var tracing = new TracingMemory(mem);
        var mech = new Rv32Mechanism();
        using var ms = new MemoryStream();
        using (var writer = new ElasticTraceWriter(mech.Decoder, tracing, ms)) {
            new SingleCycleTrain(mech, tracing, commitObserver: writer).Run();
        }

        ms.Position = 0;
        using var reader = new ElasticTraceReader(ms);
        return [.. reader.ReadAll(),];
    }

    // ── Writer / Reader ───────────────────────────────────────────────────────

    [Fact]
    public void Header_Magic_And_Version_Survive_RoundTrip() {
        byte[] program = Encode(0x00100073u); // just ebreak — commits nothing
        var mem = new FlatMemory(0x100);
        mem.Load(0, program);
        var tracing = new TracingMemory(mem);
        var mech = new Rv32Mechanism();
        using var ms = new MemoryStream();
        using (var writer = new ElasticTraceWriter(mech.Decoder, tracing, ms)) {
            new SingleCycleTrain(mech, tracing, commitObserver: writer).Run(100);
        }

        ms.Position = 0;
        // Magic is the first 4 bytes
        uint magic = new BinaryReader(ms).ReadUInt32();
        Assert.Equal(ElasticTraceWriter.Magic, magic);
    }

    [Fact]
    public void ThreeInstructions_CorrectSeqnosAndPcs() {
        // addi x1; addi x2; add x3
        byte[] program = Encode(0x00100093u, 0x00200113u, 0x002080B3u, 0x00100073u);
        ElasticTraceRecord[] recs = Record(program);

        Assert.Equal(3, recs.Length);
        Assert.Equal(0UL, recs[0].SeqNo);
        Assert.Equal(0UL, recs[0].Pc);
        Assert.Equal(1UL, recs[1].SeqNo);
        Assert.Equal(4UL, recs[1].Pc);
        Assert.Equal(2UL, recs[2].SeqNo);
        Assert.Equal(8UL, recs[2].Pc);
    }

    [Fact]
    public void AllThreeAreComp() {
        byte[] program = Encode(0x00100093u, 0x00200113u, 0x002080B3u, 0x00100073u);
        ElasticTraceRecord[] recs = Record(program);

        Assert.All(recs, r => Assert.Equal(ElasticTraceType.Comp, r.Type));
    }

    [Fact]
    public void Add_HasRobDeps_On_BothAddi_Producers() {
        // add x3, x1, x2 — reads x1 (written by seqno 0) and x2 (written by seqno 1)
        byte[] program = Encode(0x00100093u, 0x00200113u, 0x002080B3u, 0x00100073u);
        ElasticTraceRecord[] recs = Record(program);

        IReadOnlyList<ulong> deps = recs[2].RobDeps; // add x3
        Assert.Contains(0UL, deps);                  // depends on addi x1 (seqno 0)
        Assert.Contains(1UL, deps);                  // depends on addi x2 (seqno 1)
    }

    [Fact]
    public void First_Addi_Has_No_RobDeps() {
        // addi x1, x0, 1 reads x0 only; x0 has no producer (hardwired zero)
        byte[] program = Encode(0x00100093u, 0x00100073u);
        ElasticTraceRecord[] recs = Record(program);

        Assert.Empty(recs[0].RobDeps);
    }

    [Fact]
    public void Store_ThenLoad_SameAddress_Produces_AddrDep() {
        // addi x2, x0, 256  → x2 = 0x100 (base address, well past instruction space)
        // addi x1, x0, 42   → x1 = 42 (store data)
        // sw x1, 0(x2)      → seqno 2; store to 0x100
        // lw x3, 0(x2)      → seqno 3; load from 0x100
        byte[] program = Encode(0x10000113u, 0x02A00093u, 0x00112023u, 0x00012183u, 0x00100073u);
        ElasticTraceRecord[] recs = Record(program);

        ElasticTraceRecord sw = recs[2];
        ElasticTraceRecord lw = recs[3];

        Assert.Equal(ElasticTraceType.Store, sw.Type);
        Assert.Equal(ElasticTraceType.Load, lw.Type);
        Assert.Equal(0x100UL, sw.VAddr);
        Assert.Equal(0x100UL, lw.VAddr);

        Assert.Contains(2UL, lw.AddrDeps); // load depends on the store (seqno 2)
    }

    [Fact]
    public void Store_HasNoAddrDeps_From_PreviousStore() {
        // Two stores to the same address: second store should NOT have an addrDep on the first
        // (store → store is not a true dependence; only store → load matters)
        byte[] program = Encode(0x10000113u, 0x02A00093u, 0x00112023u, 0x00112023u, 0x00100073u);
        ElasticTraceRecord[] recs = Record(program);

        ElasticTraceRecord sw2 = recs[3]; // second sw
        Assert.Equal(ElasticTraceType.Store, sw2.Type);
        Assert.Empty(sw2.AddrDeps);
    }

    [Fact]
    public void VAddr_And_AccessSize_Correct_For_Load() {
        byte[] program = Encode(0x10000113u, 0x02A00093u, 0x00112023u, 0x00012183u, 0x00100073u);
        ElasticTraceRecord[] recs = Record(program);

        ElasticTraceRecord lw = recs[3];
        Assert.Equal(0x100UL, lw.VAddr);
        Assert.Equal(4, lw.AccessSize); // LW = 4 bytes
    }

    [Fact]
    public void AliasingLoad_DependsOn_MostRecentStore_ToAddress() {
        // Two stores to 0x10; lw should depend on the second (seqno 3), not the first (seqno 2)
        byte[] program = Encode(
            0x10000113u, // addi x2, x0, 256
            0x02A00093u, // addi x1, x0, 42
            0x00112023u, // sw x1, 0(x2)   seqno 2
            0x00112023u, // sw x1, 0(x2)   seqno 3
            0x00012183u, // lw x3, 0(x2)   seqno 4
            0x00100073u  // ebreak
        );
        ElasticTraceRecord[] recs = Record(program);

        ElasticTraceRecord lw = recs[4];
        Assert.Equal(ElasticTraceType.Load, lw.Type);
        Assert.Contains(3UL, lw.AddrDeps);       // most recent store
        Assert.DoesNotContain(2UL, lw.AddrDeps); // older store displaced
    }

    // ── Replayer ──────────────────────────────────────────────────────────────

    [Fact]
    public void Replay_LinearChain_CriticalPath_Equals_SumOfDelays() {
        // A → B (dep on A) → C (dep on B); delay=1 each → critical path = 3
        var records = new ElasticTraceRecord[] {
            new(0, 0, 0, ElasticTraceType.Comp, 1, 0, 0, [], []),
            new(1, 4, 0, ElasticTraceType.Comp, 1, 0, 0, [0,], []),
            new(2, 8, 0, ElasticTraceType.Comp, 1, 0, 0, [1,], []),
        };
        ReplayResult result = ElasticTraceReplayer.Replay(records);

        Assert.Equal(3UL, result.TotalCycles);
        Assert.Equal(3L, result.InstructionCount);
    }

    [Fact]
    public void Replay_IndependentInstructions_CriticalPath_Equals_MaxDelay() {
        // Three independent instructions with delay=1 each → critical path = 1
        var records = new ElasticTraceRecord[] {
            new(0, 0x00, 0, ElasticTraceType.Comp, 1, 0, 0, [], []),
            new(1, 0x04, 0, ElasticTraceType.Comp, 1, 0, 0, [], []),
            new(2, 0x08, 0, ElasticTraceType.Comp, 1, 0, 0, [], []),
        };
        ReplayResult result = ElasticTraceReplayer.Replay(records);

        Assert.Equal(1UL, result.TotalCycles);
    }

    [Fact]
    public void Replay_FanIn_TakesMaxOfProducers() {
        // A (delay=3) and B (delay=1) both feed C (delay=1)
        // critical path: max(3,1) + 1 = 4
        var records = new ElasticTraceRecord[] {
            new(0, 0x00, 0, ElasticTraceType.Comp, 3, 0, 0, [], []),
            new(1, 0x04, 0, ElasticTraceType.Comp, 1, 0, 0, [], []),
            new(2, 0x08, 0, ElasticTraceType.Comp, 1, 0, 0, [0, 1,], []),
        };
        ReplayResult result = ElasticTraceReplayer.Replay(records);

        Assert.Equal(4UL, result.TotalCycles);
    }

    [Fact]
    public void Replay_AddrDep_CountsLikeRobDep() {
        // store (delay=1, seqno=0) → load (delay=1, seqno=1 with addrDep on 0)
        // critical path = 2
        var records = new ElasticTraceRecord[] {
            new(0, 0x00, 0, ElasticTraceType.Store, 1, 0x100, 4, [], []),
            new(1, 0x04, 0, ElasticTraceType.Load, 1, 0x100, 4, [], [0,]),
        };
        ReplayResult result = ElasticTraceReplayer.Replay(records);

        Assert.Equal(2UL, result.TotalCycles);
    }

    [Fact]
    public void Replay_EmptyTrace_ReturnsZero() {
        ReplayResult result = ElasticTraceReplayer.Replay([]);
        Assert.Equal(0UL, result.TotalCycles);
        Assert.Equal(0L, result.InstructionCount);
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void BinaryRoundTrip_PreservesAllFields() {
        byte[] program = Encode(0x10000113u, 0x02A00093u, 0x00112023u, 0x00012183u, 0x00100073u);
        ElasticTraceRecord[] original = Record(program);

        // Serialize
        using var ms = new MemoryStream();
        var mem = new FlatMemory(0x1000);
        mem.Load(0, program);
        var tracing2 = new TracingMemory(mem);
        var mech2 = new Rv32Mechanism();
        using (var writer = new ElasticTraceWriter(mech2.Decoder, tracing2, ms)) {
            new SingleCycleTrain(mech2, tracing2, commitObserver: writer).Run();
        }

        ms.Position = 0;
        using var reader = new ElasticTraceReader(ms);
        ElasticTraceRecord[] reloaded = [.. reader.ReadAll(),];

        Assert.Equal(original.Length, reloaded.Length);
        for (var i = 0; i < original.Length; i++) {
            Assert.Equal(original[i].SeqNo, reloaded[i].SeqNo);
            Assert.Equal(original[i].Pc, reloaded[i].Pc);
            Assert.Equal(original[i].RawEncoding, reloaded[i].RawEncoding);
            Assert.Equal(original[i].Type, reloaded[i].Type);
            Assert.Equal(original[i].CompDelay, reloaded[i].CompDelay);
            Assert.Equal(original[i].VAddr, reloaded[i].VAddr);
            Assert.Equal(original[i].AccessSize, reloaded[i].AccessSize);
            Assert.Equal(original[i].RobDeps, reloaded[i].RobDeps);
            Assert.Equal(original[i].AddrDeps, reloaded[i].AddrDeps);
        }
    }

    [Fact]
    public void Reader_BadMagic_ThrowsInvalidDataException() {
        using var ms = new MemoryStream([0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x00, 0x00, 0x00,]);
        Assert.Throws<InvalidDataException>(() => new ElasticTraceReader(ms));
    }

    // ── gem5 converter ────────────────────────────────────────────────────────

    // Reads one gem5-framed message: varint32 length + bytes.
    // (gem5 ProtoOutputStream::write uses CodedOutputStream::WriteVarint32)
    private static byte[] ReadGem5Message(BinaryReader r) {
        ulong len = 0;
        var shift = 0;
        while (true) {
            byte b = r.ReadByte();
            len |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }

        return r.ReadBytes((int)len);
    }

    // Reads a protobuf varint from a byte span at position i (updates i).
    private static ulong ReadVarint(byte[] buf, ref int i) {
        ulong v = 0;
        var shift = 0;
        while (true) {
            byte b = buf[i++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
        }
    }

    [Fact]
    public void Gem5Converter_ProducesPacketHeader_ThenRecords() {
        // Record a tiny 3-instruction trace, convert, verify gem5 framing.
        byte[] program = Encode(0x00100093u, 0x00200113u, 0x002080B3u, 0x00100073u);
        Record(program);

        using var helf = new MemoryStream();
        var mem = new FlatMemory(0x1000);
        mem.Load(0, program);
        var tracing = new TracingMemory(mem);
        var mech = new Rv32Mechanism();
        using (var writer = new ElasticTraceWriter(mech.Decoder, tracing, helf)) {
            new SingleCycleTrain(mech, tracing, commitObserver: writer).Run();
        }

        helf.Position = 0;
        using var gem5Out = new MemoryStream();
        long converted = Gem5ElasticTraceConverter.Convert(helf, gem5Out);
        Assert.Equal(3L, converted);

        gem5Out.Position = 0;
        var br = new BinaryReader(gem5Out);

        // File starts with 4-byte LE magic 0x356d6567 ("gem5")
        Assert.Equal(0x356d6567u, br.ReadUInt32());

        // First message = InstDepRecordHeader; must be non-empty
        byte[] header = ReadGem5Message(br);
        Assert.NotEmpty(header);

        // First field in InstDepRecordHeader is obj_id (field 1, wire type 2 = LEN)
        // tag byte = (1 << 3) | 2 = 0x0A
        Assert.Equal(0x0A, header[0]);

        // Three InstDepRecord messages follow
        for (var i = 0; i < 3; i++) {
            byte[] rec = ReadGem5Message(br);
            Assert.NotEmpty(rec);
            // First field = seq_num, tag = (1 << 3) | 0 = 0x08
            Assert.Equal(0x08, rec[0]);
            var pos = 1;
            ulong seqno = ReadVarint(rec, ref pos);
            Assert.Equal((ulong)i, seqno);
        }

        // No more messages
        Assert.Equal(gem5Out.Length, gem5Out.Position);
    }

    [Fact]
    public void Gem5Converter_CompRecord_HasCorrectTypeField() {
        // A COMP record should have type=3 (gem5 COMP enum)
        byte[] program = Encode(0x00100093u, 0x00100073u); // addi x1; ebreak
        Record(program);

        using var helf = new MemoryStream();
        var mem = new FlatMemory(0x1000);
        mem.Load(0, program);
        var tracing = new TracingMemory(mem);
        var mech = new Rv32Mechanism();
        using (var writer = new ElasticTraceWriter(mech.Decoder, tracing, helf)) {
            new SingleCycleTrain(mech, tracing, commitObserver: writer).Run();
        }

        helf.Position = 0;
        using var gem5Out = new MemoryStream();
        Gem5ElasticTraceConverter.Convert(helf, gem5Out);

        gem5Out.Position = 0;
        var br = new BinaryReader(gem5Out);
        br.ReadUInt32();     // skip LE magic 0x356d6567
        ReadGem5Message(br); // skip InstDepRecordHeader

        byte[] rec = ReadGem5Message(br); // the addi x1 record
        // Parse: seq_num (field 1), then type (field 2)
        var pos = 0;
        ulong tag1 = ReadVarint(rec, ref pos); // should be 0x08 (field 1, varint)
        Assert.Equal(0x08UL, tag1);
        ReadVarint(rec, ref pos);              // seqno value
        ulong tag2 = ReadVarint(rec, ref pos); // should be 0x10 (field 2, varint)
        Assert.Equal(0x10UL, tag2);
        ulong typeVal = ReadVarint(rec, ref pos);
        Assert.Equal(3UL, typeVal); // gem5 COMP = 3
    }

    // ── gem5 fetch trace converter ────────────────────────────────────────────

    [Fact]
    public void FetchConverter_ProducesPacketHeader_ThenPackets() {
        byte[] program = Encode(0x00100093u, 0x00200113u, 0x002080B3u, 0x00100073u);

        using var helf = new MemoryStream();
        var mem = new FlatMemory(0x1000);
        mem.Load(0, program);
        var tracing = new TracingMemory(mem);
        var mech = new Rv32Mechanism();
        using (var writer = new ElasticTraceWriter(mech.Decoder, tracing, helf)) {
            new SingleCycleTrain(mech, tracing, commitObserver: writer).Run();
        }

        helf.Position = 0;
        using var fetchOut = new MemoryStream();
        long converted = Gem5FetchTraceConverter.Convert(helf, fetchOut);
        Assert.Equal(3L, converted);

        fetchOut.Position = 0;
        var br = new BinaryReader(fetchOut);

        // File starts with 4-byte LE magic 0x356d6567 ("gem5")
        Assert.Equal(0x356d6567u, br.ReadUInt32());

        // First message = PacketHeader; must be non-empty
        byte[] header = ReadGem5Message(br);
        Assert.NotEmpty(header);
        // First field is obj_id (field 1, wire type 2): tag = 0x0A
        Assert.Equal(0x0A, header[0]);

        // Three Packet messages follow, one per instruction
        for (var i = 0; i < 3; i++) {
            byte[] pkt = ReadGem5Message(br);
            Assert.NotEmpty(pkt);
            // First field is tick (field 1, wire type 0): tag = 0x08
            Assert.Equal(0x08, pkt[0]);
        }

        Assert.Equal(fetchOut.Length, fetchOut.Position);
    }

    [Fact]
    public void FetchConverter_TicksAreMonotonicallyIncreasing() {
        byte[] program = Encode(0x00100093u, 0x00200113u, 0x002080B3u, 0x00100073u);

        using var helf = new MemoryStream();
        var mem = new FlatMemory(0x1000);
        mem.Load(0, program);
        var tracing = new TracingMemory(mem);
        var mech = new Rv32Mechanism();
        using (var writer = new ElasticTraceWriter(mech.Decoder, tracing, helf)) {
            new SingleCycleTrain(mech, tracing, commitObserver: writer).Run();
        }

        helf.Position = 0;
        using var fetchOut = new MemoryStream();
        Gem5FetchTraceConverter.Convert(helf, fetchOut);

        fetchOut.Position = 0;
        var br = new BinaryReader(fetchOut);
        br.ReadUInt32();     // skip magic
        ReadGem5Message(br); // skip PacketHeader

        ulong lastTick = 0;
        while (fetchOut.Position < fetchOut.Length) {
            byte[] pkt = ReadGem5Message(br);
            var pos = 0;
            ReadVarint(pkt, ref pos); // tag (0x08)
            ulong tick = ReadVarint(pkt, ref pos);
            Assert.True(tick > lastTick, $"ticks must be strictly increasing; got {tick} after {lastTick}");
            lastTick = tick;
        }
    }

    [Fact]
    public void FetchConverter_PacketAddr_MatchesPc() {
        // addi x1 at PC=0; addi x2 at PC=4
        byte[] program = Encode(0x00100093u, 0x00200113u, 0x00100073u);

        using var helf = new MemoryStream();
        var mem = new FlatMemory(0x1000);
        mem.Load(0, program);
        var tracing = new TracingMemory(mem);
        var mech = new Rv32Mechanism();
        using (var writer = new ElasticTraceWriter(mech.Decoder, tracing, helf)) {
            new SingleCycleTrain(mech, tracing, commitObserver: writer).Run();
        }

        helf.Position = 0;
        using var fetchOut = new MemoryStream();
        Gem5FetchTraceConverter.Convert(helf, fetchOut);

        fetchOut.Position = 0;
        var br = new BinaryReader(fetchOut);
        br.ReadUInt32();     // skip magic
        ReadGem5Message(br); // skip PacketHeader

        // First packet: tick=field1, cmd=field2, addr=field3
        byte[] pkt0 = ReadGem5Message(br);
        var pos = 0;
        ReadVarint(pkt0, ref pos);
        ReadVarint(pkt0, ref pos);
        ReadVarint(pkt0, ref pos);
        ulong cmd = ReadVarint(pkt0, ref pos); // field 2 tag + value
        ReadVarint(pkt0, ref pos);
        ulong addr = ReadVarint(pkt0, ref pos); // field 3 tag + value
        Assert.Equal(1UL, cmd);                 // MemCmd::ReadReq = 1
        Assert.Equal(0UL, addr);                // PC of first instruction

        byte[] pkt1 = ReadGem5Message(br);
        pos = 0;
        ReadVarint(pkt1, ref pos);
        ReadVarint(pkt1, ref pos); // tick
        ReadVarint(pkt1, ref pos);
        ReadVarint(pkt1, ref pos); // cmd
        ReadVarint(pkt1, ref pos);
        ulong addr1 = ReadVarint(pkt1, ref pos);
        Assert.Equal(4UL, addr1); // PC of second instruction
    }
}