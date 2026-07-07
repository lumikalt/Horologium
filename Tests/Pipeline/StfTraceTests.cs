using Pipeline;
using RiscV32;
using RiscV32.Memory;
using RiscV32.Trace;

namespace Tests.Pipeline;

/// <summary>
/// STF binary trace writer: header structure, register records, branch targets,
/// and memory access records.
/// </summary>
public class StfTraceTests {
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] Encode(params uint[] words) {
        var b = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), words[i]);
        return b;
    }

    // Run a program and return the raw STF bytes (all records including header).
    private static byte[] RunProgram(byte[] program, ulong entryPoint = 0, int memSize = 0x2000) {
        var mem = new FlatMemory(memSize);
        mem.Load(entryPoint, program);
        var tracing = new TracingMemory(mem);
        var mech = new Rv32Mechanism();
        using var ms = new MemoryStream();
        using (var writer = new StfTraceWriter(mech.Decoder, tracing, ms, entryPoint)) {
            new SingleCycleTrain(mech, tracing, entryPoint, commitObserver: writer).Run();
        }

        return ms.ToArray();
    }

    // Minimal STF byte reader: returns list of (descriptor, payload) pairs.
    private static List<(byte Desc, byte[] Payload)> ParseStf(byte[] bytes) {
        var records = new List<(byte, byte[])>();
        using var br = new BinaryReader(new MemoryStream(bytes));
        while (br.BaseStream.Position < br.BaseStream.Length) {
            byte desc = br.ReadByte();
            byte[] payload = desc switch {
                0x01 => br.ReadBytes(3),   // IDENTIFIER: 'S','T','F'
                0x02 => br.ReadBytes(8),   // VERSION: uint32+uint32
                0x04 => br.ReadBytes(4),   // ISA: uint32
                0x05 => br.ReadBytes(2),   // INST_IEM: uint16
                0x06 => ReadTraceInfo(br), // TRACE_INFO: variable length
                0x07 => br.ReadBytes(8),   // TRACE_INFO_FEATURE: uint64
                0x09 => br.ReadBytes(8),   // FORCE_PC: uint64
                0x13 => [],                // END_HEADER: no payload
                0x1F => br.ReadBytes(8),   // INST_PC_TARGET: uint64
                0x28 => br.ReadBytes(11),  // INST_REG: uint16+uint8+uint64
                0x3C => br.ReadBytes(13),  // INST_MEM_ACCESS: uint64+uint16+uint16+uint8
                0x3D => br.ReadBytes(8),   // INST_MEM_CONTENT: uint64
                0xF0 => br.ReadBytes(4),   // INST_OPCODE32: uint32
                0xF1 => br.ReadBytes(2),   // INST_OPCODE16: uint16
                _ => throw new InvalidDataException(
                    $"Unknown STF descriptor 0x{desc:X2} at offset {br.BaseStream.Position - 1}"
                ),
            };
            records.Add((desc, payload));
        }

        return records;
    }

    private static byte[] ReadTraceInfo(BinaryReader br) {
        var data = new List<byte>();
        // gen(1) + major(1) + minor(1) + minor_minor(1) + len(2) + string(len)
        data.AddRange(br.ReadBytes(4));
        ushort len = br.ReadUInt16();
        data.Add((byte)(len & 0xFF));
        data.Add((byte)(len >> 8));
        if (len > 0) data.AddRange(br.ReadBytes(len));
        return [.. data,];
    }

    // Read a uint64 LE from a payload byte array.
    private static ulong ReadU64(byte[] b, int offset = 0) => BitConverter.ToUInt64(b, offset);
    private static uint ReadU32(byte[] b, int offset = 0) => BitConverter.ToUInt32(b, offset);
    private static ushort ReadU16(byte[] b, int offset = 0) => BitConverter.ToUInt16(b, offset);

    // ── Header ────────────────────────────────────────────────────────────────

    [Fact]
    public void Header_Identifier_Is_STF() {
        byte[] stf = RunProgram(Encode(0x00100073u)); // ebreak
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        (byte Desc, byte[] Payload) id = records.First(r => r.Desc == 0x01);
        Assert.Equal(new[] { (byte)'S', (byte)'T', (byte)'F', }, id.Payload);
    }

    [Fact]
    public void Header_Version_Is_1_5() {
        byte[] stf = RunProgram(Encode(0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        (byte Desc, byte[] Payload) ver = records.First(r => r.Desc == 0x02);
        Assert.Equal(1u, ReadU32(ver.Payload));    // major
        Assert.Equal(5u, ReadU32(ver.Payload, 4)); // minor
    }

    [Fact]
    public void Header_ISA_Is_RISCV() {
        byte[] stf = RunProgram(Encode(0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        (byte Desc, byte[] Payload) isa = records.First(r => r.Desc == 0x04);
        Assert.Equal(1u, ReadU32(isa.Payload)); // RISCV=1
    }

    [Fact]
    public void Header_IEM_Is_RV32() {
        byte[] stf = RunProgram(Encode(0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        (byte Desc, byte[] Payload) iem = records.First(r => r.Desc == 0x05);
        Assert.Equal((ushort)1, ReadU16(iem.Payload)); // RV32=1
    }

    [Fact]
    public void Header_TraceInfoFeature_Contains_OperandValue() {
        byte[] stf = RunProgram(Encode(0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        (byte Desc, byte[] Payload) feat = records.First(r => r.Desc == 0x07);
        ulong features = ReadU64(feat.Payload);
        Assert.True((features & 0x04UL) != 0, "STF_CONTAIN_OPERAND_VALUE (0x04) must be set");
    }

    [Fact]
    public void Header_ForcePC_Matches_EntryPoint() {
        byte[] stf = RunProgram(Encode(0x00100073u), 0x1000);
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        (byte Desc, byte[] Payload) fpc = records.First(r => r.Desc == 0x09);
        Assert.Equal(0x1000UL, ReadU64(fpc.Payload));
    }

    [Fact]
    public void Header_Ends_With_EndHeader() {
        byte[] stf = RunProgram(Encode(0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        // END_HEADER (0x13) must appear exactly once, after all other header records.
        int endIdx = records.FindIndex(r => r.Desc == 0x13);
        Assert.True(endIdx >= 0, "END_HEADER (0x13) not found");
        // Instruction records follow after it.
        // All header records (0x01..0x13) must precede END_HEADER.
        for (int i = endIdx + 1; i < records.Count; i++)
            Assert.True(records[i].Desc >= 0x1F, $"Header record 0x{records[i].Desc:X2} found after END_HEADER");
    }

    // ── Instruction opcode record ─────────────────────────────────────────────

    [Fact]
    public void Opcode32_Record_Emitted_For_Each_Committed_Instruction() {
        //  addi x1, x0, 1    0x00100093
        //  addi x2, x0, 2    0x00200113
        //  ebreak             0x00100073
        byte[] stf = RunProgram(Encode(0x00100093u, 0x00200113u, 0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        List<(byte Desc, byte[] Payload)> opcodes = records.Where(r => r.Desc == 0xF0).ToList();
        Assert.Equal(2, opcodes.Count); // ebreak never commits
        Assert.Equal(0x00100093u, ReadU32(opcodes[0].Payload));
        Assert.Equal(0x00200113u, ReadU32(opcodes[1].Payload));
    }

    [Fact]
    public void Count_Matches_Committed_Instruction_Count() {
        var mem = new FlatMemory(0x1000);
        mem.Load(0, Encode(0x00100093u, 0x00200113u, 0x00100073u));
        var tracing = new TracingMemory(mem);
        var mech = new Rv32Mechanism();
        using var ms = new MemoryStream();
        int count;
        using (var writer = new StfTraceWriter(mech.Decoder, tracing, ms, 0)) {
            new SingleCycleTrain(mech, tracing, commitObserver: writer).Run();
            count = writer.Count;
        }

        Assert.Equal(2, count); // addi x1 + addi x2; ebreak is halt
    }

    // ── Register records ──────────────────────────────────────────────────────

    [Fact]
    public void Dest_Reg_Record_Emitted_With_Correct_Value() {
        // addi x1, x0, 5 → x1 = 5 after commit
        // Encoding: imm=5, rs1=0, rd=1, funct3=0, opcode=0x13 → 0x00500093
        byte[] stf = RunProgram(Encode(0x00500093u, 0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);

        // Find instruction records (after END_HEADER)
        int endIdx = records.FindIndex(r => r.Desc == 0x13);
        List<(byte Desc, byte[] Payload)> instrRecords = records.Skip(endIdx + 1).ToList();

        List<(byte Desc, byte[] Payload)> regRecords = instrRecords.Where(r => r.Desc == 0x28).ToList();
        // addi x0-source is skipped; only dest x1 is emitted
        Assert.Single(regRecords);

        (byte Desc, byte[] Payload) destReg = regRecords[0];
        ushort packed = ReadU16(destReg.Payload);
        byte metadata = destReg.Payload[2];
        ulong value = ReadU64(destReg.Payload, 3);

        Assert.Equal(1, packed); // x1
        // metadata: (DEST<<4 | INTEGER) = (3<<4 | 1) = 0x31
        Assert.Equal(0x31, metadata);
        Assert.Equal(5UL, value);
    }

    [Fact]
    public void Source_Reg_Records_Emitted_For_Non_X0_Sources() {
        // addi x1, x0, 3   → x1=3 (source x0 skipped, dest x1 emitted)
        // add  x3, x1, x1  → sources x1,x1; dest x3
        //   add rd=x3, rs1=x1, rs2=x1: funct7=0,rs2=1,rs1=1,funct3=0,rd=3,opcode=0x33
        //   = (1<<20)|(1<<15)|(3<<7)|0x33 = 0x00100000|0x00008000|0x00000180|0x33 = 0x001081B3
        // ebreak
        byte[] stf = RunProgram(Encode(0x00300093u, 0x001081B3u, 0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        int endIdx = records.FindIndex(r => r.Desc == 0x13);
        List<(byte Desc, byte[] Payload)> instrRecords = records.Skip(endIdx + 1).ToList();

        // Second instruction's records: between first OPCODE32 and second OPCODE32
        int op1 = instrRecords.FindIndex(r => r.Desc == 0xF0);
        int op2 = instrRecords.FindIndex(op1 + 1, r => r.Desc == 0xF0);
        List<(byte Desc, byte[] Payload)> secondInstrRecords = instrRecords.Skip(op1 + 1).Take(op2 - op1 - 1).ToList();

        List<(byte Desc, byte[] Payload)> regRecords = secondInstrRecords.Where(r => r.Desc == 0x28).ToList();
        // add x3, x1, x1: source x1 (appears twice as rs1 and rs2) + dest x3
        // SourceRegisters = [1, 1], DestinationRegister = 3
        // Both source records emitted (even if duplicated), plus one dest
        Assert.True(regRecords.Count >= 2, $"Expected at least 2 REG records, got {regRecords.Count}");

        // At least one source record (operand_type=SOURCE, upper nibble=2)
        Assert.Contains(regRecords, r => r.Payload[2] >> 4 == 2); // SOURCE
        // Dest record (operand_type=DEST, upper nibble=3)
        List<(byte Desc, byte[] Payload)> destRecs = regRecords.Where(r => r.Payload[2] >> 4 == 3).ToList();
        Assert.Single(destRecs);
        Assert.Equal(3, ReadU16(destRecs[0].Payload)); // x3
    }

    [Fact]
    public void X0_Source_Is_Skipped() {
        // addi x1, x0, 1 — x0 is source but must be skipped
        byte[] stf = RunProgram(Encode(0x00100093u, 0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        int endIdx = records.FindIndex(r => r.Desc == 0x13);
        List<(byte Desc, byte[] Payload)> instrRecords = records.Skip(endIdx + 1).ToList();
        int op1 = instrRecords.FindIndex(r => r.Desc == 0xF0);
        List<(byte Desc, byte[] Payload)> firstInstrRegs = instrRecords.Take(op1).Where(r => r.Desc == 0x28).ToList();

        // No source records (x0 skipped); only dest x1
        Assert.All(firstInstrRegs, r => Assert.Equal(3, r.Payload[2] >> 4)); // all DEST
        List<(byte Desc, byte[] Payload)> srcRecs = firstInstrRegs.Where(r => r.Payload[2] >> 4 == 2).ToList();
        Assert.Empty(srcRecs);
    }

    // ── PC_TARGET ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sequential_Instructions_Emit_No_PC_Target() {
        // addi x1, x0, 1 → addi x2, x0, 2 → ebreak (no branches)
        byte[] stf = RunProgram(Encode(0x00100093u, 0x00200113u, 0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        Assert.DoesNotContain(records, r => r.Desc == 0x1F);
    }

    [Fact]
    public void Taken_Branch_Emits_PC_Target() {
        // beq x0, x0, +8   — always-taken branch at pc=0 → target pc=8
        //   encoding: 0x00000463
        // addi x1, x0, 1   — skipped (at pc=4)
        // addi x2, x0, 2   — executed (at pc=8)
        // ebreak            — halt (at pc=12)
        byte[] stf = RunProgram(Encode(0x00000463u, 0x00100093u, 0x00200113u, 0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);

        // Find PC_TARGET records among instruction records
        int endIdx = records.FindIndex(r => r.Desc == 0x13);
        List<(byte Desc, byte[] Payload)> instrRecords = records.Skip(endIdx + 1).ToList();
        List<(byte Desc, byte[] Payload)> pcTargets = instrRecords.Where(r => r.Desc == 0x1F).ToList();

        Assert.Single(pcTargets); // exactly one taken branch
        ulong target = ReadU64(pcTargets[0].Payload);
        Assert.Equal(8UL, target); // jumps to pc=8
    }

    [Fact]
    public void PC_Target_Precedes_Opcode_For_Its_Instruction() {
        // beq x0, x0, +8 → PC_TARGET must come before OPCODE32 in the same instruction group
        byte[] stf = RunProgram(Encode(0x00000463u, 0x00100093u, 0x00200113u, 0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        int endIdx = records.FindIndex(r => r.Desc == 0x13);
        List<(byte Desc, byte[] Payload)> instrRecords = records.Skip(endIdx + 1).ToList();

        int pcTargetIdx = instrRecords.FindIndex(r => r.Desc == 0x1F);
        int firstOpcodeIdx = instrRecords.FindIndex(r => r.Desc == 0xF0);
        Assert.True(pcTargetIdx < firstOpcodeIdx, "PC_TARGET must appear before OPCODE32 of its instruction");
    }

    // ── Memory records ────────────────────────────────────────────────────────

    [Fact]
    public void Store_Emits_MemAccess_And_MemContent() {
        // addi x2, x0, 256  (0x10000113) — address: x2=256
        // addi x1, x0, 42   (0x02A00093) — data: x1=42
        // sw x1, 0(x2)      (0x00112023) — store word x1 to [x2+0]
        // ebreak             (0x00100073)
        byte[] stf = RunProgram(Encode(0x10000113u, 0x02A00093u, 0x00112023u, 0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        int endIdx = records.FindIndex(r => r.Desc == 0x13);
        List<(byte Desc, byte[] Payload)> instrRecords = records.Skip(endIdx + 1).ToList();

        List<(byte Desc, byte[] Payload)> memAccess = instrRecords.Where(r => r.Desc == 0x3C).ToList();
        List<(byte Desc, byte[] Payload)> memContent = instrRecords.Where(r => r.Desc == 0x3D).ToList();

        Assert.Single(memAccess);
        Assert.Single(memContent);

        // MEM_ACCESS: address=256, size=4, attr=0, type=WRITE(2)
        ulong addr = ReadU64(memAccess[0].Payload);
        ushort size = ReadU16(memAccess[0].Payload, 8);
        ushort attr = ReadU16(memAccess[0].Payload, 10);
        byte type = memAccess[0].Payload[12];
        Assert.Equal(256UL, addr);
        Assert.Equal((ushort)4, size);
        Assert.Equal((ushort)0, attr);
        Assert.Equal(2, type); // WRITE

        // MEM_CONTENT: value=42
        ulong data = ReadU64(memContent[0].Payload);
        Assert.Equal(42UL, data);
    }

    [Fact]
    public void Load_Emits_MemAccess_With_ReadType() {
        // addi x2, x0, 256  (0x10000113) — base address
        // addi x1, x0, 99   (0x06300093) — store data
        // sw   x1, 0(x2)    (0x00112023) — store 99 to addr 256
        // lw   x3, 0(x2)    (0x00012183) — load from addr 256
        // ebreak             (0x00100073)
        byte[] stf = RunProgram(Encode(0x10000113u, 0x06300093u, 0x00112023u, 0x00012183u, 0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        int endIdx = records.FindIndex(r => r.Desc == 0x13);
        List<(byte Desc, byte[] Payload)> instrRecords = records.Skip(endIdx + 1).ToList();

        List<(byte Desc, byte[] Payload)> memAccesses = instrRecords.Where(r => r.Desc == 0x3C).ToList();
        // Two mem accesses: one store + one load
        Assert.Equal(2, memAccesses.Count);

        // First is a store (type=2), second is a load (type=1)
        Assert.Equal(2, memAccesses[0].Payload[12]); // WRITE
        Assert.Equal(1, memAccesses[1].Payload[12]); // READ

        // Load address matches store address
        Assert.Equal(256UL, ReadU64(memAccesses[1].Payload));

        // Load data matches stored value (99)
        List<(byte Desc, byte[] Payload)> memContents = instrRecords.Where(r => r.Desc == 0x3D).ToList();
        Assert.Equal(2, memContents.Count);
        Assert.Equal(99UL, ReadU64(memContents[1].Payload));
    }

    [Fact]
    public void MemContent_Immediately_Follows_MemAccess() {
        byte[] stf = RunProgram(Encode(0x10000113u, 0x02A00093u, 0x00112023u, 0x00100073u));
        List<(byte Desc, byte[] Payload)> records = ParseStf(stf);
        int endIdx = records.FindIndex(r => r.Desc == 0x13);
        List<(byte Desc, byte[] Payload)> instrRecords = records.Skip(endIdx + 1).ToList();

        int accessIdx = instrRecords.FindIndex(r => r.Desc == 0x3C);
        int contentIdx = instrRecords.FindIndex(r => r.Desc == 0x3D);
        Assert.True(
            accessIdx >= 0 && contentIdx == accessIdx + 1,
            "MEM_CONTENT must immediately follow MEM_ACCESS"
        );
    }
}