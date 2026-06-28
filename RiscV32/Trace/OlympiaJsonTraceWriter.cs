using System.Globalization;
using System.Text;
using Mechanism;
using RiscV32.Decode;

namespace RiscV32.Trace;

/// <summary>
/// Emits an Olympia-compatible JSON instruction trace by observing a functional
/// run. Attach as the <see cref="ICommitObserver"/> of a <c>SingleCycleTrain</c>,
/// which executes one instruction to completion per commit, so each
/// <see cref="OnCommit"/> corresponds to exactly one retired instruction.
///
/// Olympia is trace-driven: it replays this trace through its timing model and
/// does not execute functionally, so each record carries only the decoded
/// instruction and — for loads/stores — the effective address:
/// <code>
/// [
///   { "mnemonic": "lw", "rs1": 8, "rd": 15, "vaddr": "0x80001f90" },
///   { "mnemonic": "add", "rs1": 15, "rs2": 14, "rd": 15 }
/// ]
/// </code>
///
/// Reuses the existing decode/disassembly: the mnemonic is the first token of
/// <see cref="RvDisassembler"/> (compressed instructions decode to their expanded
/// base ops, so mnemonics are standard); registers come from <see cref="ITooth"/>;
/// the effective address comes from the <see cref="TracingMemory"/> the run is
/// wrapped in. <see cref="Dispose"/> closes the JSON array (it flushes but does
/// not close the underlying writer).
/// </summary>
public sealed class OlympiaJsonTraceWriter : ICommitObserver, IDisposable {
    private readonly IDecoder _decoder;
    private readonly TracingMemory _mem;
    private readonly TextWriter _out;
    private bool _first = true;

    /// <summary>Number of instructions written so far.</summary>
    public int Count { get; private set; }

    public OlympiaJsonTraceWriter(IDecoder decoder, TracingMemory mem, TextWriter output) {
        _decoder = decoder;
        _mem = mem;
        _out = output;
        _out.Write('[');
    }

    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        ITooth instr = _decoder.Decode(pc, rawEncoding);

        var sb = new StringBuilder(_first ? "\n  " : ",\n  ");
        _first = false;

        sb.Append("{ \"mnemonic\": \"").Append(Mnemonic(instr.Payload, pc)).Append('"');

        IReadOnlyList<int> srcs = instr.SourceRegisters;
        if (srcs.Count > 0) sb.Append(", \"rs1\": ").Append(srcs[0]);
        if (srcs.Count > 1) sb.Append(", \"rs2\": ").Append(srcs[1]);
        if (instr.DestinationRegister >= 0) sb.Append(", \"rd\": ").Append(instr.DestinationRegister);

        if (CsrOf(instr.Payload) is { } csr) sb.Append(", \"csr\": ").Append(csr);

        // The effective address is the data access. Loads/stores reach memory
        // during execute, after the fetch, so it is the last recorded access.
        if (instr.Class is ToothClass.Load or ToothClass.Store && _mem.HasAccess)
            sb.Append(", \"vaddr\": \"0x")
              .Append(_mem.Address.ToString("x", CultureInfo.InvariantCulture))
              .Append('"');

        sb.Append(" }");
        _out.Write(sb.ToString());
        Count++;
        _mem.Reset();
    }

    public void Dispose() {
        _out.Write(_first ? "]\n" : "\n]\n");
        _out.Flush();
    }

    // Mnemonic = first token of the disassembly (e.g. "add x3, x1, x2" → "add",
    // "ecall" → "ecall"). Reuses RvDisassembler's exhaustive op→string mapping.
    private static string Mnemonic(object? payload, ulong pc) {
        string dis = RvDisassembler.Disassemble(payload, pc);
        int space = dis.IndexOf(' ');
        return space < 0 ? dis : dis[..space];
    }

    private static uint? CsrOf(object? payload) => payload switch {
        RvCsrrw o  => o.Csr,
        RvCsrrs o  => o.Csr,
        RvCsrrc o  => o.Csr,
        RvCsrrwi o => o.Csr,
        RvCsrrsi o => o.Csr,
        RvCsrrci o => o.Csr,
        _          => null,
    };
}