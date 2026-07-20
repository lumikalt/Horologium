#region

using System.Text;
using Mechanism;

#endregion

namespace RiscV32.Trace;

/// <summary>
///     Writes a Simulation Trace Format (STF) binary trace by observing a functional run.
///     Attach as the <see cref="ICommitObserver" /> of a <c>SingleCycleTrain</c> (which
///     executes exactly one instruction per commit) wrapping a <see cref="TracingMemory" />.
///     <para>
///         The output is compatible with the Sparcians stf_lib and can be replayed through
///         Olympia or any other STF-aware timing model. Register operand values are included
///         (STF_CONTAIN_OPERAND_VALUE). FP registers are tracked via the unified integer+FP
///         register file (indices 32–63 = f0–f31). Vector register records are omitted since
///         VRF values are not accessible through <see cref="IArchState" />.
///     </para>
/// </summary>
public sealed class StfTraceWriter : ICommitObserver, IDisposable {
    // STF record descriptor bytes (stf_descriptor.hpp encoded::Descriptor)
    private const byte DescIdentifier = 0x01;
    private const byte DescVersion = 0x02;
    private const byte DescIsa = 0x04;
    private const byte DescInstIem = 0x05;
    private const byte DescTraceInfo = 0x06;
    private const byte DescTraceInfoFeature = 0x07;
    private const byte DescForcePc = 0x09;
    private const byte DescEndHeader = 0x13;
    private const byte DescInstPcTarget = 0x1F;
    private const byte DescInstReg = 0x28;
    private const byte DescInstMemAccess = 0x3C;
    private const byte DescInstMemContent = 0x3D;
    private const byte DescInstOpcode32 = 0xF0;
    private const byte DescInstOpcode16 = 0xF1;

    // STF register type nibble (lower nibble of metadata byte)
    private const byte RegTypeInteger = 1;
    private const byte RegTypeFp = 2;

    // Operand type nibble (upper nibble of metadata byte): SOURCE=2, DEST=3
    private const byte OpSource = 2;
    private const byte OpDest = 3;

    // INST_MEM_ACCESS type: READ=1, WRITE=2
    private const byte MemRead = 1;
    private const byte MemWrite = 2;

    // STF_CONTAIN_OPERAND_VALUE feature flag (stf_enums.hpp)
    private const ulong FeatureOperandValue = 0x04UL;
    private readonly BackgroundTraceChannel<Record> _channel;

    private readonly IDecoder _decoder;
    private readonly TracingMemory _mem;
    private readonly BinaryWriter _out;

    public StfTraceWriter(IDecoder decoder, TracingMemory mem, Stream output, ulong initialPc) {
        _decoder = decoder;
        _mem = mem;
        _out = new BinaryWriter(output, Encoding.UTF8, true);
        WriteHeader(initialPc);
        _channel = new BackgroundTraceChannel<Record>(Emit, "stf-trace-writer");
    }

    /// <summary>Number of instructions recorded.</summary>
    public int Count { get; private set; }

    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        // Decode stays on the simulation thread: the decoder cache is shared with
        // the running train and is not thread-safe.
        ITooth instr = _decoder.Decode(pc, rawEncoding);

        // Source register values (integer and FP; skip x0 = hardwired zero).
        IReadOnlyList<int> srcs = instr.SourceRegisters;
        var srcCount = 0;
        for (var i = 0; i < srcs.Count; i++)
            if (srcs[i] > 0)
                srcCount++;
        ulong[] srcValues = srcCount == 0 ? [] : new ulong[srcCount];
        var k = 0;
        for (var i = 0; i < srcs.Count; i++)
            if (srcs[i] > 0)
                srcValues[k++] = state.IntegerRegisters.Read(srcs[i]);

        int rd = instr.DestinationRegister;
        ulong destValue = rd > 0 ? state.IntegerRegisters.Read(rd) : 0;

        bool isMem = instr.Class is ToothClass.Load or ToothClass.Store or ToothClass.Atomic;
        bool hasMem = isMem && _mem.HasAccess;

        // SingleCycleTrain updates state.Pc before calling OnCommit, so state.Pc
        // is already the next-PC for this instruction.
        _channel.Post(
            new Record(
                instr, pc, rawEncoding, state.Pc, srcValues, destValue,
                hasMem,
                hasMem ? _mem.Address : 0,
                hasMem ? (ushort)_mem.Bytes : (ushort)0,
                hasMem && _mem.IsWrite,
                hasMem ? _mem.Value : 0
            )
        );
        Count++;
        _mem.Reset();
    }

    public void Dispose() {
        _channel.Dispose(); // drain and join before releasing the writer
        _out.Dispose();
    }

    private void WriteHeader(ulong initialPc) {
        // IDENTIFIER: 3-byte magic 'S','T','F'
        _out.Write(StfTraceWriter.DescIdentifier);
        _out.Write((byte)'S');
        _out.Write((byte)'T');
        _out.Write((byte)'F');

        // VERSION: uint32 major=1, uint32 minor=5  (version 1.5)
        _out.Write(StfTraceWriter.DescVersion);
        _out.Write(1u);
        _out.Write(5u);

        // ISA: uint32 RISCV=1
        _out.Write(StfTraceWriter.DescIsa);
        _out.Write(1u);

        // INST_IEM: uint16 RV32=1
        _out.Write(StfTraceWriter.DescInstIem);
        _out.Write((ushort)1);

        // TRACE_INFO: gen(uint8) + major(uint8) + minor(uint8) + minor_minor(uint8) + len(uint16) + comment
        // STF_GEN_RESERVED=0; version 1.0.0; empty comment.
        _out.Write(StfTraceWriter.DescTraceInfo);
        _out.Write((byte)0);   // STF_GEN_RESERVED
        _out.Write((byte)1);   // generator major
        _out.Write((byte)0);   // generator minor
        _out.Write((byte)0);   // generator minor_minor
        _out.Write((ushort)0); // comment length 0

        // TRACE_INFO_FEATURE: uint64 feature flags
        _out.Write(StfTraceWriter.DescTraceInfoFeature);
        _out.Write(StfTraceWriter.FeatureOperandValue);

        // FORCE_PC: uint64 initial PC
        _out.Write(StfTraceWriter.DescForcePc);
        _out.Write(initialPc);

        // END_HEADER: no payload
        _out.Write(StfTraceWriter.DescEndHeader);
    }

    private void Emit(Record r) {
        // PC_TARGET: emit when the instruction is a taken branch/jump.
        if (r.NextPc != r.Pc + (ulong)r.Instr.SizeBytes) {
            _out.Write(StfTraceWriter.DescInstPcTarget);
            _out.Write(r.NextPc);
        }

        // Source REG records.
        var k = 0;
        foreach (int src in r.Instr.SourceRegisters) {
            if (src <= 0) continue;
            WriteReg(src, StfTraceWriter.OpSource, r.SrcValues[k++]);
        }

        // Destination REG record (integer and FP; skip x0 writes).
        int rd = r.Instr.DestinationRegister;
        if (rd > 0) WriteReg(rd, StfTraceWriter.OpDest, r.DestValue);

        // MEM_ACCESS + MEM_CONTENT for loads, stores, and atomics.
        if (r.HasMem) {
            _out.Write(StfTraceWriter.DescInstMemAccess);
            _out.Write(r.MemAddr); // uint64 address
            _out.Write(r.MemBytes); // uint16 size
            _out.Write((ushort)0); // uint16 attr (page attributes)
            _out.Write(r.MemIsWrite ? StfTraceWriter.MemWrite : StfTraceWriter.MemRead); // uint8 type

            _out.Write(StfTraceWriter.DescInstMemContent);
            _out.Write(r.MemValue); // uint64 data
        }

        // OPCODE — the instruction boundary marker; always last.
        if (r.Instr.SizeBytes == 2) {
            _out.Write(StfTraceWriter.DescInstOpcode16);
            _out.Write((ushort)(r.Raw & 0xFFFF));
        }
        else {
            _out.Write(StfTraceWriter.DescInstOpcode32);
            _out.Write(r.Raw);
        }
    }

    // Write one INST_REG record. regIdx 0–31 = integer x_n; 32–63 = FP f_{n-32}.
    private void WriteReg(int regIdx, byte operandType, ulong value) {
        byte regType;
        ushort packed;
        if (regIdx < 32) {
            regType = StfTraceWriter.RegTypeInteger;
            packed = (ushort)regIdx;
        }
        else {
            regType = StfTraceWriter.RegTypeFp;
            packed = (ushort)(regIdx - 32);
        }

        var metadata = (byte)((operandType << 4) | regType);

        _out.Write(StfTraceWriter.DescInstReg);
        _out.Write(packed);   // uint16 packed register number
        _out.Write(metadata); // uint8 (operand_type<<4 | reg_type)
        _out.Write(value);    // uint64 register value
    }

    // Captured on the simulation thread at commit — register values and next-PC
    // must be read there because the architectural state is live and mutates as
    // soon as the next instruction commits. Serialization happens on the consumer
    // thread. SrcValues holds one value per qualifying (index > 0) source register,
    // in SourceRegisters order.
    private readonly record struct Record(
        ITooth Instr,
        ulong Pc,
        uint Raw,
        ulong NextPc,
        ulong[] SrcValues,
        ulong DestValue,
        bool HasMem,
        ulong MemAddr,
        ushort MemBytes,
        bool MemIsWrite,
        ulong MemValue
    );
}