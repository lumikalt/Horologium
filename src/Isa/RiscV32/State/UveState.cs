using Mechanism;

namespace RiscV32.State;

/// <summary>
/// Architectural state for the UVE extension: 32 vector accumulator registers
/// (u0–u31, each holding up to VLEN=128 bits of element data) and 32 store-stream configurations.
/// <para>
/// Each u-register is 128 bits wide (two 64-bit words). Lane i of element width W bytes occupies
/// bits [(i*W*8)..(i*W*8 + W*8 - 1)] within the 128-bit register. The dominant width is 4 bytes
/// (float32/int32) giving 4 lanes per register.
/// </para>
/// <para>
/// Load streams are managed by the ISA-agnostic StreamingEngine; this class holds the complementary
/// per-u-register state that cannot live there: lane values, per-register scalar/vector mode and
/// valid-element counts, store-stream cursors, pending multi-dim config, and per-dim completion flags.
/// </para>
/// </summary>
public sealed class UveState : IUveScalars {
    public const int Count = 32;
    public const int MaxDims = 8;

    /// <summary>VLEN in bytes. Each u-register holds PredBytes worth of element data.</summary>
    public const int PredBytes = 16; // VLEN/8 = 128-bit VLEN

    // Predicate register file: 16 registers, each holding VLEN/8 = 16 bytes.
    // Predicate for element i of width W bytes is at index (i+1)*W - 1.
    // Register 0 is initialized all-true (Spike invariant).
    public const int PredCount = 16;
    public readonly bool[][] PredicateRegs;

    // Per-predicate-register zeroing mode: written by so.p.{ge,eq,lt}._z comparison variants.
    // true = Zeroing (inactive governing-pred elements → 0), false = Merging (keep old value).
    // Register 0 defaults to Merging. Spike: predRegister_t default pm = PredicateMode::Merging.
    public readonly bool[] PredZeroing = new bool[UveState.PredCount];

    public UveState() {
        PredicateRegs = new bool[UveState.PredCount][];
        for (var i = 0; i < UveState.PredCount; i++) PredicateRegs[i] = new bool[UveState.PredBytes];
        Array.Fill(PredicateRegs[0], true);
    }

    // ── Per-u-register vector storage ─────────────────────────────────────────
    // Each register: two ulong words = 128 bits = VLEN.
    // _lanes[uid, 0] = bits [63:0], _lanes[uid, 1] = bits [127:64].
    // Lane i (32-bit): GetLane32 / SetLane32.
    private readonly ulong[,] _lanes = new ulong[UveState.Count, 2];

    // Per-register scalar/vector mode (Scalar = only lane 0 valid; Vector = ValidElements valid).
    public readonly UveRegMode[] RegMode = new UveRegMode[UveState.Count];

    // Per-register valid lane count: 1 in scalar mode, up to VLEN/ElementBytes in vector mode.
    public readonly int[] ValidElements = new int[UveState.Count];

    // Per-register merging predication flag (from stream pm bit): false = Zeroing (out-of-range
    // lanes → 0), true = Merging (out-of-range lanes keep old value). Default Zeroing.
    public readonly bool[] RegMerging = new bool[UveState.Count];

    /// <summary>Returns the raw 32-bit bits of lane <paramref name="lane"/> of u-register <paramref name="uid"/>.</summary>
    public uint GetLane32(int uid, int lane) =>
        (uint)(_lanes[uid, lane >> 1] >> ((lane & 1) * 32));

    /// <summary>Sets lane <paramref name="lane"/> of u-register <paramref name="uid"/> to the raw 32-bit value.</summary>
    public void SetLane32(int uid, int lane, uint value) {
        int word = lane >> 1;
        int shift = (lane & 1) * 32;
        _lanes[uid, word] = (_lanes[uid, word] & ~(0xFFFFFFFFUL << shift)) | ((ulong)value << shift);
    }

    // ── Per-u-reg store-stream configuration ──────────────────────────────────

    public readonly UveStoreStream?[] StoreStreams = new UveStoreStream?[UveState.Count];

    // Tracks which kind of entity each u-reg slot holds.
    public readonly UveRegKind[] RegKind = new UveRegKind[UveState.Count];

    // Whole-stream exhaustion state synced by the pipeline for so.b.nc.
    public readonly bool[] StreamDone = new bool[UveState.Count];

    // Per-dimension pass-complete flags, synced by the pipeline for so.b.ndc.*:
    // DimDone[uid, dim] = true when dimension dim of stream uid wrapped on last consume.
    public readonly bool[,] DimDone = new bool[UveState.Count, UveState.MaxDims];

    // Pending multi-dim stream config being built by ss.sta → ss.app* → ss.end.
    public readonly PendingStreamConfig?[] PendingConfig = new PendingStreamConfig?[UveState.Count];

    // Per-u-reg suspension flag, set by ss.suspend and cleared by ss.resume.
    public readonly bool[] Suspended = new bool[UveState.Count];

    // Active vector length (element count per vector delivery tick).
    // 0 = not yet configured (natural VL applies: VLEN/elementBytes).
    public int VectorLength;

    // ── IUveScalars implementation ─────────────────────────────────────────────

    // Lane 0 as float32 — scalar value accessor (implements IUveScalars.GetScalar).
    public float GetScalar(int uid) =>
        BitConverter.Int32BitsToSingle((int)GetLane32(uid, 0));

    // Write a float32 value to lane 0 and set scalar mode. Used by tests to pre-set register values.
    public void SetScalar(int uid, float value) {
        SetLane32(uid, 0, (uint)BitConverter.SingleToInt32Bits(value));
        RegMode[uid] = UveRegMode.Scalar;
        ValidElements[uid] = 1;
    }

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

    public bool GetStreamDone(int uid) => StreamDone[uid];
    public void SetStreamDone(int uid, bool done) => StreamDone[uid] = done;
    public bool GetDimDone(int streamId, int dim) => DimDone[streamId, dim];
    public void SetDimDone(int streamId, int dim, bool done) => DimDone[streamId, dim] = done;
    int IUveScalars.VectorLength => VectorLength;
    public bool IsVectorReg(int uid) => RegMode[uid] == UveRegMode.Vector;
    public int GetValidElements(int uid) => ValidElements[uid] > 0 ? ValidElements[uid] : 1;
    public bool IsRegMerging(int uid) => RegMerging[uid];
    public uint GetRaw(int uid, int lane) => GetLane32(uid, lane);

    public void Reset() {
        Array.Clear(_lanes);
        Array.Clear(RegMode);
        Array.Clear(ValidElements);
        Array.Clear(RegMerging);
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
/// Accumulated configuration for a multi-dim stream being built by ss.sta → ss.app* → ss.end.
/// Written by ss.sta SideEffect, mutated by ss.app SideEffects, consumed by ss.end.
/// </summary>
public sealed class PendingStreamConfig {
    public ulong BaseAddress;
    public int ElementBytes;
    public bool IsLoad;
    public bool IsVector;
    public int VecCfgDim = -1;
    public bool MergingPredication;
    public long OffsetBytes;
    public bool IsIndSource;
    public readonly List<StreamDimension> Dimensions = [];
    public readonly List<StreamModifier> Modifiers = [];
    public (int SourceStreamId, StreamModifierBehavior Behavior)? SgiMod;
}

public enum UveRegKind {
    None,
    LoadStream,
    StoreStream,
    Scalar,
    IndSource,
}

/// <summary>
/// Mutable cursor for one affine store stream (ss.st.* / ss.sta.st.* → ss.end).
/// Supports N-dimensional layouts: innermost dimension first, matching StreamState
/// in StreamingEngine. <see cref="CurrentAddress"/> computes the flat memory address
/// from per-dim indices; <see cref="Advance"/> carries across dimension boundaries.
/// </summary>
public sealed class UveStoreStream {
    public ulong BaseAddress;
    public int ElementBytes;
    public StreamDimension[] Dimensions = [];
    public long[] Indices = [];
    private long _totalConsumed;
    private long _totalCount;

    public void Initialize() {
        _totalConsumed = 0;
        _totalCount = 1;
        foreach (StreamDimension d in Dimensions) _totalCount *= d.Count;
    }

    public bool IsExhausted => _totalConsumed >= _totalCount;

    public ulong CurrentAddress {
        get {
            long offset = 0;
            for (var i = 0; i < Dimensions.Length; i++) offset += Indices[i] * Dimensions[i].Stride;
            return (ulong)((long)BaseAddress + offset);
        }
    }

    public void Advance() {
        _totalConsumed++;
        for (var d = 0; d < Dimensions.Length; d++) {
            Indices[d]++;
            if (Indices[d] < Dimensions[d].Count) return;
            Indices[d] = 0;
        }
    }
}