using Mechanism;
using Pipeline.Ooo;

namespace RiscV32.Trace;

/// <summary>
/// Writes a Horologium elastic DDG trace in the HELF binary format.
/// Attach as the <see cref="ICommitObserver"/> of a <c>SingleCycleTrain</c> (which
/// executes exactly one instruction per commit) wrapping a <see cref="TracingMemory"/>.
/// <para>
/// Each committed instruction becomes one <see cref="ElasticTraceRecord"/>. Register
/// RAW edges are tracked across the unified 0–63 register namespace (0–31 = integer,
/// 32–63 = floating-point) and the 0–31 vector register namespace. Memory RAW edges
/// (store → load) are tracked by effective address reported by <see cref="TracingMemory"/>.
/// </para>
/// <para>
/// The critical-path replay of the resulting trace is an IPC <b>upper bound</b>
/// (infinite-width, zero structural hazards): it models only dataflow latency.
/// </para>
/// </summary>
public sealed class ElasticTraceWriter : ICommitObserver, IDisposable {
    public const uint Magic   = 0x464C4548; // "HELF"
    public const uint Version = 1;

    // Header layout: magic(4) + version(4) + tickFreq(8) + reserved(8) = 24 bytes
    public const int HeaderBytes = 24;

    private readonly IDecoder     _decoder;
    private readonly TracingMemory _mem;
    private readonly BinaryWriter  _out;

    private long _seqno;

    // Unified integer+FP producer table (0–31 = int, 32–63 = FP); -1 = no producer.
    private readonly long[] _lastWriter    = new long[64];
    // Vector producer table (v0–v31); -1 = no producer.
    private readonly long[] _lastVecWriter = new long[32];
    // Memory producer table: effective address → seqno of last store.
    private readonly Dictionary<ulong, long> _lastStore = new();

    /// <summary>Number of instructions recorded.</summary>
    public int Count { get; private set; }

    public ElasticTraceWriter(IDecoder decoder, TracingMemory mem, Stream output) {
        _decoder = decoder;
        _mem     = mem;
        _out     = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        Array.Fill(_lastWriter,    -1L);
        Array.Fill(_lastVecWriter, -1L);
        WriteHeader();
    }

    private void WriteHeader() {
        _out.Write(Magic);
        _out.Write(Version);
        _out.Write(0UL); // tickFreq — reserved for a future calibrated time base
        _out.Write(0UL); // reserved
    }

    public void OnCommit(ulong pc, uint rawEncoding, IArchState state) {
        ITooth instr = _decoder.Decode(pc, rawEncoding);
        long seqno = _seqno++;

        // ── Register RAW deps ─────────────────────────────────────────────────
        // x0 (index 0) is hardwired to zero; skip it for both reads and writes.
        var robDepsSet = new HashSet<long>(4);
        foreach (int src in instr.SourceRegisters)
            if (src > 0 && src < _lastWriter.Length && _lastWriter[src] >= 0)
                robDepsSet.Add(_lastWriter[src]);
        foreach (int vsrc in instr.VectorSourceRegisters)
            if (vsrc >= 0 && vsrc < _lastVecWriter.Length && _lastVecWriter[vsrc] >= 0)
                robDepsSet.Add(_lastVecWriter[vsrc]);

        // Update write tables
        int rd = instr.DestinationRegister;
        if (rd > 0 && rd < _lastWriter.Length) _lastWriter[rd] = seqno;
        int vd = instr.VectorDestinationRegister;
        if (vd >= 0 && vd < _lastVecWriter.Length) _lastVecWriter[vd] = seqno;

        // ── Instruction type ──────────────────────────────────────────────────
        byte type = instr.Class switch {
            ToothClass.Load or ToothClass.Atomic => (byte)ElasticTraceType.Load,
            ToothClass.Store                     => (byte)ElasticTraceType.Store,
            _                                    => (byte)ElasticTraceType.Comp,
        };

        // ── Memory RAW deps ───────────────────────────────────────────────────
        // Gate on class, NOT on _mem.HasAccess: single-cycle fetch also touches
        // TracingMemory, so HasAccess is true even for ALU ops after the fetch.
        ulong vAddr     = 0;
        byte  accessSize = 0;
        var   addrDeps  = new List<long>(1);

        if (type != (byte)ElasticTraceType.Comp && _mem.HasAccess) {
            vAddr      = _mem.Address;
            accessSize = (byte)_mem.Bytes;

            if (type == (byte)ElasticTraceType.Load) {
                if (_lastStore.TryGetValue(vAddr, out long prod)) addrDeps.Add(prod);
            } else {
                _lastStore[vAddr] = seqno;
            }
        }
        _mem.Reset(); // always reset — clears stale fetch address for next instruction

        // ── comp_delay from pipeline defaults ─────────────────────────────────
        uint compDelay = (uint)FuLatencyConfig.Default.LatencyFor(instr.Class);

        // ── Serialize ─────────────────────────────────────────────────────────
        long[] robDeps = [.. robDepsSet];
        _out.Write((ulong)seqno);
        _out.Write(pc);
        _out.Write(rawEncoding);
        _out.Write(type);
        _out.Write(compDelay);
        _out.Write(vAddr);
        _out.Write(accessSize);
        _out.Write((byte)robDeps.Length);
        _out.Write((byte)addrDeps.Count);
        foreach (long dep in robDeps)  _out.Write((ulong)dep);
        foreach (long dep in addrDeps) _out.Write((ulong)dep);
        Count++;
    }

    public void Dispose() => _out.Dispose();
}
