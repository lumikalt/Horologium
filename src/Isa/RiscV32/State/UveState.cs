#region

using Mechanism;

#endregion

namespace RiscV32.State;

/// <summary>
///     Architectural state for the UVE extension: 32 vector accumulator registers
///     (u0–u31, each holding up to VLEN=128 bits of element data) and 32 store-stream configurations.
///     <para>
///         Each u-register is 128 bits wide (two 64-bit words). Lane i of element width W bytes occupies
///         bits [(i*W*8)..(i*W*8 + W*8 - 1)] within the 128-bit register. The dominant width is 4 bytes
///         (float32/int32) giving 4 lanes per register.
///     </para>
///     <para>
///         Load streams are managed by the ISA-agnostic StreamingEngine; this class holds the complementary
///         per-u-register state that cannot live there: lane values, per-register scalar/vector mode and
///         valid-element counts, store-stream cursors, pending multi-dim config, and per-dim completion flags.
///     </para>
/// </summary>
public sealed class UveState : IUveScalars {
    public const int Count = 32;
    public const int MaxDims = 8;

    /// <summary>
    ///     Recommended <c>StreamingEngine</c> capacity for RiscV32/UVE: high enough for every ported
    ///     benchmark kernel's concurrent load-stream register ids (the tightest, <c>convolution</c>,
    ///     reaches u9), while staying well below the 32-register ceiling imposed by the 5-bit
    ///     ud/rs1/rs2/rs3 encoding fields (<see cref="Count" />) — ids at or above this value remain
    ///     free for arithmetic-only scratch/broadcast operands, which are never bound to a real stream.
    ///     Not itself a hard limit: any caller building an <c>OooTrain</c> for RV32/UVE can pass a
    ///     different <c>streamMaxCount</c> if a future kernel needs more.
    /// </summary>
    public const int RecommendedStreamCapacity = 16;

    /// <summary>VLEN in bytes. Each u-register holds PredBytes worth of element data.</summary>
    public const int PredBytes = 16; // VLEN/8 = 128-bit VLEN

    // Predicate register file: 16 registers, each holding VLEN/8 = 16 bytes.
    // Predicate for element i of width W bytes is at index (i+1)*W - 1.
    // Register 0 is initialized all-true (Spike invariant).
    public const int PredCount = 16;

    // ── Per-u-register vector storage ─────────────────────────────────────────
    // Each register: two ulong words = 128 bits = VLEN.
    // _lanes[uid, 0] = bits [63:0], _lanes[uid, 1] = bits [127:64].
    // Lane i (32-bit): GetLane32 / SetLane32.
    private readonly ulong[,] _lanes = new ulong[UveState.Count, 2];

    // Per-dimension pass-complete flags, synced by the pipeline for so.b.ndc.*:
    // DimDone[uid, dim] = true when dimension dim of stream uid wrapped on last consume.
    public readonly bool[,] DimDone = new bool[UveState.Count, UveState.MaxDims];

    // Pending multi-dim stream config being built by ss.sta → ss.app* → ss.end.
    public readonly PendingStreamConfig?[] PendingConfig = new PendingStreamConfig?[UveState.Count];
    public readonly bool[][] PredicateRegs;

    // Per-predicate-register zeroing mode: written by so.p.{ge,eq,lt}._z comparison variants.
    // true = Zeroing (inactive governing-pred elements → 0), false = Merging (keep old value).
    // Register 0 defaults to Merging. Spike: predRegister_t default pm = PredicateMode::Merging.
    public readonly bool[] PredZeroing = new bool[UveState.PredCount];

    // Per-register source element width in bytes, set at stream-injection time.
    // Used by so.v.cv to know how wide each lane value actually is (1/2/4/8).
    // 0 = unknown (default to 4).
    public readonly int[] RegElemBytes = new int[UveState.Count];

    // Tracks which kind of entity each u-reg slot holds.
    public readonly UveRegKind[] RegKind = new UveRegKind[UveState.Count];

    // Per-register merging predication flag (from stream pm bit): false = Zeroing (out-of-range
    // lanes → 0), true = Merging (out-of-range lanes keep old value). Default Zeroing.
    public readonly bool[] RegMerging = new bool[UveState.Count];

    // Per-register scalar/vector mode (Scalar = only lane 0 valid; Vector = ValidElements valid).
    public readonly UveRegMode[] RegMode = new UveRegMode[UveState.Count];

    // ── Per-u-reg store-stream configuration ──────────────────────────────────

    public readonly UveStoreStream?[] StoreStreams = new UveStoreStream?[UveState.Count];

    // Whole-stream exhaustion state synced by the pipeline for so.b.nc.
    public readonly bool[] StreamDone = new bool[UveState.Count];

    // Per-u-reg suspension flag, set by ss.suspend and cleared by ss.resume.
    public readonly bool[] Suspended = new bool[UveState.Count];

    // Per-register valid lane count: 1 in scalar mode, up to VLEN/ElementBytes in vector mode.
    public readonly int[] ValidElements = new int[UveState.Count];

    // Active vector length (element count per vector delivery tick).
    // 0 = not yet configured (natural VL applies: VLEN/elementBytes).
    public int VectorLength;

    public UveState() {
        PredicateRegs = new bool[UveState.PredCount][];
        for (var i = 0; i < UveState.PredCount; i++) PredicateRegs[i] = new bool[UveState.PredBytes];
        Array.Fill(PredicateRegs[0], true);
    }

    // ── IUveScalars implementation ─────────────────────────────────────────────

    // Lane 0 as float32 — scalar value accessor (implements IUveScalars.GetScalar).
    public float GetScalar(int uid) =>
        BitConverter.Int32BitsToSingle((int)GetLane32(uid, 0));

    public void SetScalarRaw(int uid, uint raw, bool merging) {
        SetLane32(uid, 0, raw);
        RegMode[uid] = UveRegMode.Scalar;
        ValidElements[uid] = 1;
        RegMerging[uid] = merging;
    }

    public void SetVectorRaw(int uid, ReadOnlySpan<uint> values, int vl, bool merging) {
        for (var i = 0; i < vl; i++) SetLane32(uid, i, values[i]);
        RegMode[uid] = UveRegMode.Vector;
        ValidElements[uid] = vl;
        RegMerging[uid] = merging;
    }

    public void SetRegElemBytes(int uid, int elemBytes) => RegElemBytes[uid] = elemBytes;
    public bool IsStoreStream(int uid) => RegKind[uid] == UveRegKind.StoreStream;
    public bool StoreStreamExhausted(int uid) => StoreStreams[uid] is not { } ss || ss.IsExhausted;
    public bool GetStreamDone(int uid) => StreamDone[uid];
    public void SetStreamDone(int uid, bool done) => StreamDone[uid] = done;
    public bool GetDimDone(int streamId, int dim) => DimDone[streamId, dim];
    public void SetDimDone(int streamId, int dim, bool done) => DimDone[streamId, dim] = done;
    int IUveScalars.VectorLength => VectorLength;
    public bool IsVectorReg(int uid) => RegMode[uid] == UveRegMode.Vector;
    public int GetValidElements(int uid) => ValidElements[uid] > 0 ? ValidElements[uid] : 1;
    public bool IsRegMerging(int uid) => RegMerging[uid];
    public uint GetRaw(int uid, int lane) => GetLane32(uid, lane);

    /// <summary>Returns the raw 32-bit bits of lane <paramref name="lane" /> of u-register <paramref name="uid" />.</summary>
    public uint GetLane32(int uid, int lane) =>
        (uint)(_lanes[uid, lane >> 1] >> ((lane & 1) * 32));

    /// <summary>Sets lane <paramref name="lane" /> of u-register <paramref name="uid" /> to the raw 32-bit value.</summary>
    public void SetLane32(int uid, int lane, uint value) {
        int word = lane >> 1;
        int shift = (lane & 1) * 32;
        _lanes[uid, word] = (_lanes[uid, word] & ~(0xFFFFFFFFUL << shift)) | ((ulong)value << shift);
    }

    // Write a float32 value to lane 0 and set scalar mode. Used by tests to pre-set register values.
    public void SetScalar(int uid, float value) {
        SetLane32(uid, 0, (uint)BitConverter.SingleToInt32Bits(value));
        RegMode[uid] = UveRegMode.Scalar;
        ValidElements[uid] = 1;
    }

    public void Reset() {
        Array.Clear(_lanes);
        Array.Clear(RegMode);
        Array.Clear(ValidElements);
        Array.Clear(RegMerging);
        Array.Clear(RegElemBytes);
        Array.Clear(StoreStreams);
        Array.Clear(RegKind);
        Array.Clear(StreamDone);
        Array.Clear(DimDone);
        Array.Clear(PendingConfig);
        Array.Clear(Suspended);
        VectorLength = 0;
        for (var i = 0; i < UveState.PredCount; i++) Array.Clear(PredicateRegs[i]);
        Array.Fill(PredicateRegs[0], true);
        Array.Clear(PredZeroing);
    }
}

public enum UveRegMode { Scalar, Vector, }

/// <summary>
///     Accumulated configuration for a multi-dim stream being built by ss.sta → ss.app* → ss.end.
///     Written by ss.sta SideEffect, mutated by ss.app SideEffects, consumed by ss.end.
/// </summary>
public sealed class PendingStreamConfig {
    public readonly List<StreamDimension> Dimensions = [];
    public readonly List<StreamModifier> Modifiers = [];
    public ulong BaseAddress;
    public int ElementBytes;
    public bool IsIndSource;
    public bool IsLoad;
    public bool IsVector;
    public bool MergingPredication;
    public long OffsetBytes;
    public (int SourceStreamId, StreamModifierBehavior Behavior)? SgiMod;
    public int VecCfgDim = -1;
}

public enum UveRegKind {
    None,
    LoadStream,
    StoreStream,
    Scalar,
    IndSource,
}

/// <summary>
///     Mutable cursor for one affine store stream (ss.st.* / ss.sta.st.* → ss.end).
///     Supports N-dimensional layouts: innermost dimension first, matching StreamState
///     in StreamingEngine. <see cref="CurrentAddress" /> computes the flat memory address
///     from per-dim indices; <see cref="Advance" /> carries across dimension boundaries.
///     <see cref="Modifiers" /> supports the same static (non-indirect) Size/Stride/Offset
///     modifiers as load streams (<see cref="StreamModifier.SourceStreamId" /> &lt; 0 only —
///     indirect modifiers on a store stream are not yet implemented).
/// </summary>
public sealed class UveStoreStream {
    private long[] _baseCounts = [];
    private long[] _baseStrides = [];
    private long[] _offsets = [];
    public ulong BaseAddress;
    public StreamDimension[] Dimensions = [];
    public int ElementBytes;
    public long[] Indices = [];
    public StreamModifier[]? Modifiers;

    public bool IsExhausted { get; private set; }

    public ulong CurrentAddress {
        get {
            long offset = Dimensions.Select((t, i) => Indices[i] * t.Stride + _offsets[i]).Sum();
            return (ulong)((long)BaseAddress + offset);
        }
    }

    public void Initialize() {
        IsExhausted = false;
        _baseCounts = new long[Dimensions.Length];
        _baseStrides = new long[Dimensions.Length];
        for (var i = 0; i < Dimensions.Length; i++) {
            _baseCounts[i] = Dimensions[i].Count;
            _baseStrides[i] = Dimensions[i].Stride;
        }

        _offsets = new long[Dimensions.Length];
    }

    public void Advance() {
        for (var d = 0; d < Dimensions.Length; d++) {
            Indices[d]++;
            if (Indices[d] < Dimensions[d].Count) return;
            Indices[d] = 0;
            ResetModifiers(d - 1);
            ApplyModifiers(d);
            if (d == Dimensions.Length - 1) {
                IsExhausted = true;
                return;
            }
        }
    }

    // Applies every static modifier whose TriggerDim just wrapped. Indirect modifiers
    // (SourceStreamId >= 0) are skipped — not supported on store streams yet.
    private void ApplyModifiers(int wrappedDim) {
        if (Modifiers is not { Length: > 0, } mods) return;
        foreach ((int triggerDim, int t, StreamModifierTarget streamModifierTarget,
                  StreamModifierBehavior streamModifierBehavior, long displacement, int sourceStreamId) in mods) {
            if (triggerDim != wrappedDim || sourceStreamId >= 0) continue;
            long delta = streamModifierBehavior == StreamModifierBehavior.Inc ? displacement : -displacement;
            switch (streamModifierTarget) {
                case StreamModifierTarget.Size:
                    Dimensions[t] = Dimensions[t] with { Count = Math.Max(0, Dimensions[t].Count + delta), };
                    break;
                case StreamModifierTarget.Stride:
                    Dimensions[t] = Dimensions[t] with { Stride = Dimensions[t].Stride + delta, };
                    break;
                case StreamModifierTarget.Offset: _offsets[t] = Math.Max(0, _offsets[t] + delta * ElementBytes); break;
                default:                          throw new ArgumentOutOfRangeException();
            }
        }
    }

    // Restores the target dimension's modified fields when the dimension one level outside the
    // trigger wraps — mirrors StreamState.ResetFetchModifiers.
    private void ResetModifiers(int triggerDim) {
        if (Modifiers is not { Length: > 0, } mods) return;
        foreach (StreamModifier m in mods) {
            if (m.TriggerDim != triggerDim) continue;
            switch (m.Target) {
                case StreamModifierTarget.Size:
                    Dimensions[m.TargetDim] = Dimensions[m.TargetDim] with { Count = _baseCounts[m.TargetDim], };
                    break;
                case StreamModifierTarget.Stride:
                    Dimensions[m.TargetDim] = Dimensions[m.TargetDim] with { Stride = _baseStrides[m.TargetDim], };
                    break;
                case StreamModifierTarget.Offset: _offsets[m.TargetDim] = 0; break;
            }
        }
    }
}