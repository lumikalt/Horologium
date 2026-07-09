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
/// <para>
/// Olympia is trace-driven: it replays this trace through its timing model and
/// does not execute functionally. Each record carries the raw <c>opcode</c> and —
/// for loads/stores — the effective address:
/// <code>
/// [
///   { "opcode": "0x0007a783", "mnemonic": "lw", "vaddr": "0x80001f90" },
///   { "opcode": "0x00e787b3", "mnemonic": "add" }
/// ]
/// </code>
/// </para>
/// <para>
/// The <c>opcode</c> is the source of truth: Olympia's Mavis decodes the operands
/// and the instruction width (including 16-bit RVC) from it, so this covers every
/// instruction — integer, floating-point, vector, compressed — with no
/// register-numbering or mnemonic-vocabulary games. (The earlier
/// mnemonic+registers form mis-encoded FP register operands, which Mavis rejected.)
/// <c>mnemonic</c> is a best-effort human-readable label only (Olympia ignores it
/// when <c>opcode</c> is present). <c>vaddr</c> is required for loads/stores because
/// Olympia, not executing functionally, cannot compute it; it comes from the
/// <see cref="TracingMemory"/> the run is wrapped in. <see cref="Dispose"/> closes
/// the JSON array (it flushes but does not close the underlying writer).
/// </para>
/// </summary>
public sealed class OlympiaJsonTraceWriter : ICommitObserver, IDisposable {
    private readonly IDecoder _decoder;
    private readonly TracingMemory _mem;
    private readonly TextWriter _out;
    private readonly BackgroundTraceChannel<Record> _channel;

    // Consumer-thread state: whether the next record is the first in the array.
    private bool _first = true;

    // Captured on the simulation thread at commit; formatted and written on the
    // consumer thread. The tooth is an immutable record, safe to share.
    private readonly record struct Record(ITooth Instr, ulong Pc, uint Raw, bool HasVaddr, ulong Vaddr);

    /// <summary>Number of instructions written so far.</summary>
    public int Count { get; private set; }

    public OlympiaJsonTraceWriter(IDecoder decoder, TracingMemory mem, TextWriter output) {
        _decoder = decoder;
        _mem = mem;
        _out = output;
        _out.Write('[');
        _channel = new BackgroundTraceChannel<Record>(Emit, "olympia-trace-writer");
    }

    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        // Decode stays on the simulation thread: the decoder cache is shared with
        // the running train and is not thread-safe. The trace contains only
        // committed instructions, so the decode always succeeds; it yields the
        // class (for vaddr gating) and payload (mnemonic).
        ITooth instr = _decoder.Decode(pc, rawEncoding);

        // The effective address is the data access. Loads/stores reach memory
        // during execute, after the fetch, so it is the last recorded access.
        bool hasVaddr = instr.Class is ToothClass.Load or ToothClass.Store or ToothClass.Atomic && _mem.HasAccess;

        _channel.Post(new Record(instr, pc, rawEncoding, hasVaddr, hasVaddr ? _mem.Address : 0));
        Count++;
        _mem.Reset();
    }

    private void Emit(Record r) {
        var sb = new StringBuilder(_first ? "\n  " : ",\n  ");
        _first = false;

        // Raw opcode — the source of truth Mavis decodes (16-bit for RVC).
        sb.Append("{ \"opcode\": \"0x")
          .Append(r.Raw.ToString("x", CultureInfo.InvariantCulture))
          .Append('"');

        // Best-effort human-readable label; omitted if the disassembler doesn't
        // cover the op (it's cosmetic — Olympia uses the opcode).
        if (TryMnemonic(r.Instr.Payload, r.Pc) is { } m) sb.Append(", \"mnemonic\": \"").Append(m).Append('"');

        if (r.HasVaddr)
            sb.Append(", \"vaddr\": \"0x")
              .Append(r.Vaddr.ToString("x", CultureInfo.InvariantCulture))
              .Append('"');

        sb.Append(" }");
        _out.Write(sb.ToString());
    }

    public void Dispose() {
        _channel.Dispose(); // drain and join before writing the footer
        _out.Write(_first ? "]\n" : "\n]\n");
        _out.Flush();
    }

    // Best-effort mnemonic = first token of the disassembly (e.g. "add x3, x1, x2"
    // → "add"). The disassembler has no general fallback, so ops it doesn't cover
    // (some FP/vector) throw; the mnemonic is cosmetic, so swallow and omit it.
    private static string? TryMnemonic(object? payload, ulong pc) {
        try {
            string dis = RvDisassembler.Disassemble(payload, pc);
            int space = dis.IndexOf(' ');
            return space < 0 ? dis : dis[..space];
        }
        catch { return null; }
    }
}