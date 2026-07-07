using System.Text;
using Mechanism;

namespace RiscV32.Trace;

/// <summary>
/// Writes a Simulation Trace Format (STF) binary trace by observing a functional run.
/// Attach as the <see cref="ICommitObserver"/> of a <c>SingleCycleTrain</c> (which
/// executes exactly one instruction per commit) wrapping a <see cref="TracingMemory"/>.
/// <para>
/// The output is compatible with the Sparcians stf_lib and can be replayed through
/// Olympia or any other STF-aware timing model. Register operand values are included
/// (STF_CONTAIN_OPERAND_VALUE). FP registers are tracked via the unified integer+FP
/// register file (indices 32–63 = f0–f31). Vector register records are omitted since
/// VRF values are not accessible through <see cref="IArchState"/>.
/// </para>
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

    private readonly IDecoder _decoder;
    private readonly TracingMemory _mem;
    private readonly BinaryWriter _out;

    /// <summary>Number of instructions recorded.</summary>
    public int Count { get; private set; }

    public StfTraceWriter(IDecoder decoder, TracingMemory mem, Stream output, ulong initialPc) {
        _decoder = decoder;
        _mem = mem;
        _out = new BinaryWriter(output, Encoding.UTF8, true);
        WriteHeader(initialPc);
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

    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        ITooth instr = _decoder.Decode(pc, rawEncoding);

        // PC_TARGET: emit when the instruction is a taken branch/jump.
        // SingleCycleTrain updates state.Pc before calling OnCommit, so state.Pc
        // is already the next-PC for this instruction.
        ulong nextPc = state.Pc;
        if (nextPc != pc + (ulong)instr.SizeBytes) {
            _out.Write(StfTraceWriter.DescInstPcTarget);
            _out.Write(nextPc);
        }

        // Source REG records (integer and FP; skip x0 = hardwired zero).
        foreach (int src in instr.SourceRegisters) {
            if (src <= 0) continue;
            WriteReg(src, StfTraceWriter.OpSource, state);
        }

        // Destination REG record (integer and FP; skip x0 writes).
        int rd = instr.DestinationRegister;
        if (rd > 0) WriteReg(rd, StfTraceWriter.OpDest, state);

        // MEM_ACCESS + MEM_CONTENT for loads, stores, and atomics.
        bool isMem = instr.Class is ToothClass.Load or ToothClass.Store or ToothClass.Atomic;
        if (isMem && _mem.HasAccess) {
            _out.Write(StfTraceWriter.DescInstMemAccess);
            _out.Write(_mem.Address); // uint64 address
            _out.Write((ushort)_mem.Bytes); // uint16 size
            _out.Write((ushort)0); // uint16 attr (page attributes)
            _out.Write(_mem.IsWrite ? StfTraceWriter.MemWrite : StfTraceWriter.MemRead); // uint8 type

            _out.Write(StfTraceWriter.DescInstMemContent);
            _out.Write(_mem.Value); // uint64 data
        }

        _mem.Reset();

        // OPCODE — the instruction boundary marker; always last.
        if (instr.SizeBytes == 2) {
            _out.Write(StfTraceWriter.DescInstOpcode16);
            _out.Write((ushort)(rawEncoding & 0xFFFF));
        }
        else {
            _out.Write(StfTraceWriter.DescInstOpcode32);
            _out.Write(rawEncoding);
        }

        Count++;
    }

    // Write one INST_REG record. regIdx 0–31 = integer x_n; 32–63 = FP f_{n-32}.
    private void WriteReg(int regIdx, byte operandType, IArchState state) {
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
        ulong value = state.IntegerRegisters.Read(regIdx);

        _out.Write(StfTraceWriter.DescInstReg);
        _out.Write(packed);   // uint16 packed register number
        _out.Write(metadata); // uint8 (operand_type<<4 | reg_type)
        _out.Write(value);    // uint64 register value
    }

    public void Dispose() => _out.Dispose();
}