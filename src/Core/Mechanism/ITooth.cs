namespace Mechanism;

/// <summary>
/// A decoded instruction — the unit of work the pipeline operates on.
/// <para>
/// The pipeline reads the structural fields (PC, register indices, class)
/// for hazard detection and scheduling. The ISA-opaque payload carries
/// everything the executor needs to actually compute the result.
/// </para>
/// </summary>
public interface ITooth {
    /// <summary>The address this instruction was fetched from.</summary>
    ulong Pc { get; }

    /// <summary>The raw encoding as fetched from memory.</summary>
    uint RawEncoding { get; }

    /// <summary>The size of this instruction in bytes.</summary>
    int SizeBytes { get; }

    /// <summary>
    /// The destination register index, or -1 if this instruction
    /// does not write an integer register.
    /// </summary>
    int DestinationRegister { get; }

    /// <summary>
    /// The source register indices read by this instruction.
    /// May be empty. Never contains -1.
    /// </summary>
    IReadOnlyList<int> SourceRegisters { get; }

    /// <summary>The broad class of this instruction, used for scheduling.</summary>
    ToothClass Class { get; }

    /// <summary>
    /// ISA-specific payload. The executor casts this to its concrete type.
    /// The pipeline never inspects this field.
    /// </summary>
    object? Payload { get; }

    /// <summary>
    /// The vector destination register index, or -1 if this instruction does
    /// not write a vector register. Non-vector instructions return -1.
    /// </summary>
    int VectorDestinationRegister => -1;

    /// <summary>
    /// The vector source register indices read by this instruction.
    /// Non-vector instructions return an empty list.
    /// </summary>
    IReadOnlyList<int> VectorSourceRegisters => [];

    /// <summary>
    /// A second architectural integer register index written by this instruction, or -1
    /// if it has only the one destination. Exists for ISAs whose register width can't
    /// hold a full result in a single register — e.g. RV32's Zacas amocas.d, which
    /// operates on a 64-bit value split across an even/odd register pair. The write
    /// itself is delivered through <c>ExecuteResult.SideEffect</c> (like any other
    /// auxiliary state mutation); this property exists purely so pipelines can detect
    /// the RAW hazard it creates. Most instructions, and most ISAs, never set this.
    /// </summary>
    int SecondaryDestinationRegister => -1;

    /// <summary>
    /// UVE u-register indices whose stream element this instruction consumes.
    /// The pipeline stalls Issue when the streaming engine has no element ready
    /// for any listed ID that is an active load stream. Non-UVE ops return empty.
    /// </summary>
    IReadOnlyList<int> UveStreamSources => [];

    /// <summary>
    /// UVE u-register indices whose exhaustion state this instruction inspects
    /// (so.b.nc). The pipeline syncs IsExhausted to IUveScalars
    /// before calling the executor. Non-branch UVE ops return empty.
    /// </summary>
    IReadOnlyList<int> UveBranchStreams => [];

    /// <summary>
    /// (stream-id, dimension-index) pairs whose per-dimension pass-complete flag this
    /// instruction inspects (so.b.ndc.*). The pipeline syncs IsDimPassComplete to
    /// IUveScalars before calling the executor. Non-NDC ops return empty.
    /// </summary>
    IReadOnlyList<(int StreamId, int Dim)> UveDimBranchSources => [];

    /// <summary>
    /// For signed load instructions: the number of bytes being loaded that require
    /// sign extension into the full register width. Used by the OoO pipeline to
    /// apply the correct extension to store-forwarded values.
    /// Returns 0 for all non-loads and for zero-extending loads (lbu, lhu, lwu).
    /// </summary>
    int LoadSignExtendBytes => 0;

    /// <summary>
    /// True when the load result must be IEEE 754 NaN-boxed before writing to the
    /// destination register (i.e. the upper 32 bits must be set to 0xFFFFFFFF).
    /// Used by the OoO pipeline to re-apply NaN-boxing when a store-forwarded value
    /// replaces the executor's result (which was already boxed before forwarding stripped it).
    /// Only FLW sets this; all other loads and non-load instructions return false.
    /// </summary>
    bool NanBoxLoadResult => false;

    /// <summary>
    /// True for integer divide and remainder instructions (DIV/DIVU/REM/REMU and
    /// their variants). Used by the pipeline to apply a separate <c>DivLatency</c>
    /// when modelling the higher-latency division unit independently from multiply.
    /// False for all other instructions, including MUL/MULH/MULHSU/MULHU.
    /// </summary>
    bool IsDiv => false;

    /// <summary>
    /// True for store-conditional instructions (SC.W / SC.D).  The pipeline
    /// head-serializes these: SC may only issue when it is the oldest instruction
    /// in the ROB, so the reservation check in the executor sees a coherent view
    /// of the <c>ReservationTable</c> — all older intra-hart stores have committed
    /// and any cross-hart cancellation from an earlier outer tick is already visible.
    /// </summary>
    bool IsStoreConditional => false;

    /// <summary>
    /// True for memory-ordering fences that order older stores before younger loads
    /// (e.g. RISC-V <c>FENCE</c> with W in the predecessor set and R in the successor
    /// set). Under TSO, store→load is the only reordering the out-of-order train
    /// performs (committed store write-miss penalties drain asynchronously through the
    /// write buffer while younger loads issue), so this is the only fence flavour with
    /// an observable pipeline effect: the OoO train issues it only at the ROB head once
    /// the write buffer has fully drained, and younger loads may not issue while it is
    /// pending. Fences without W→R ordering — and all non-fence instructions — return
    /// false and execute as timing no-ops.
    /// </summary>
    bool IsStoreLoadFence => false;
}

/// <summary>
/// The broad functional class of an instruction.
/// Used by the pipeline for hazard detection, issue port assignment,
/// and branch prediction — not for execution semantics.
/// </summary>
public enum ToothClass {
    /// <summary>Integer arithmetic and logic.</summary>
    IntegerAlu,

    /// <summary>Integer multiply/divide — may have longer latency.</summary>
    IntegerMulDiv,

    /// <summary>Load from memory.</summary>
    Load,

    /// <summary>Store to memory.</summary>
    Store,

    /// <summary>Unconditional branch or jump.</summary>
    Branch,

    /// <summary>Conditional branch.</summary>
    ConditionalBranch,

    /// <summary>System call, CSR access, or privileged operation.</summary>
    System,

    /// <summary>Fence or memory ordering instruction.</summary>
    Fence,

    /// <summary>Atomic read-modify-write (LR/SC/AMO).</summary>
    Atomic,

    /// <summary>Vector operation.</summary>
    Vector,

    /// <summary>Halt instruction (e.g. EBREAK). Signals end-of-execution at commit.</summary>
    Halt,

    /// <summary>Pipelined floating-point operation: add, sub, mul, fused-multiply-add, min, max, sign injection, conversions, comparisons.</summary>
    FloatingPoint,

    /// <summary>Non-pipelined floating-point divide or square root — substantially higher latency than other FP ops.</summary>
    FloatDivSqrt,

    /// <summary>
    /// UVE stream operation — stream setup (ss.*), stream compute (so.a.*, so.v.*),
    /// or stream branch (so.b.*). Head-serialized in OoO; stalls Issue when a
    /// required load stream has no buffered element. Latency must be 1: eager
    /// memory writes and SideEffect application are only flush-safe because the
    /// next UVE op cannot issue until this one retires.
    /// </summary>
    Uve,
}