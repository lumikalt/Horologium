using Mechanism;

namespace RiscV32.State;

/// <summary>
/// Architectural state for the UVE extension: 32 scalar accumulator registers
/// (u0–u31, each holding one float32 value) and 32 store-stream configurations.
///
/// Load streams are managed by the ISA-agnostic StreamingEngine; this class holds
/// the complementary per-u-register state that cannot live there: scalar values
/// (so.v.dp.w, so.a.* results), store-stream cursors (ss.st.*), pending multi-dim
/// config (ss.sta → ss.app → ss.end), and per-dim completion flags (so.b.ndc.*).
/// </summary>
public sealed class UveState : IUveScalars {
    public const int Count = 32;
    public const int MaxDims = 8;

    // Float accumulator for each u-slot.
    // Load-stream sources: the pipeline overwrites Scalars[uid] with the consumed
    // element value before calling the executor. Scalar sources: value set by so.v.dp.w.
    // Compute results: written via SideEffect from so.a.*.
    public readonly float[] Scalars = new float[Count];

    // Per-u-reg store-stream configuration. Non-null only for u-regs configured
    // via ss.st.* that have not been deactivated.
    public readonly UveStoreStream?[] StoreStreams = new UveStoreStream?[Count];

    // Tracks which kind of entity each u-reg slot holds.
    public readonly UveRegKind[] RegKind = new UveRegKind[Count];

    // Whole-stream exhaustion state synced by the pipeline for so.b.nc.
    public readonly bool[] StreamDone = new bool[Count];

    // Per-dimension pass-complete flags, synced by the pipeline for so.b.ndc.*:
    // DimDone[uid, dim] = true when dimension dim of stream uid wrapped on last consume.
    public readonly bool[,] DimDone = new bool[Count, MaxDims];

    // Pending multi-dim stream config being built by ss.sta → ss.app* → ss.end.
    // Non-null while a configuration sequence is in progress for that u-reg.
    public readonly PendingStreamConfig?[] PendingConfig = new PendingStreamConfig?[Count];

    // IUveScalars implementation — used by the pipeline.
    public float GetScalar(int uid) => Scalars[uid];
    public void SetScalar(int uid, float value) => Scalars[uid] = value;
    public bool GetStreamDone(int uid) => StreamDone[uid];
    public void SetStreamDone(int uid, bool done) => StreamDone[uid] = done;
    public bool GetDimDone(int streamId, int dim) => DimDone[streamId, dim];
    public void SetDimDone(int streamId, int dim, bool done) => DimDone[streamId, dim] = done;

    public void Reset() {
        Array.Clear(Scalars);
        Array.Clear(StoreStreams);
        Array.Clear(RegKind);
        Array.Clear(StreamDone);
        Array.Clear(DimDone);
        Array.Clear(PendingConfig);
    }
}

/// <summary>
/// Accumulated configuration for a multi-dim stream being built by ss.sta → ss.app* → ss.end.
/// Written by ss.sta SideEffect, mutated by ss.app SideEffects, consumed by ss.end.
/// </summary>
public sealed class PendingStreamConfig {
    public ulong BaseAddress;
    public int ElementBytes;
    public bool IsLoad;
    public bool IsVector;
    public readonly List<StreamDimension> Dimensions = [];
}

public enum UveRegKind { None, LoadStream, StoreStream, Scalar }

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
            for (int i = 0; i < Dimensions.Length; i++)
                offset += Indices[i] * Dimensions[i].Stride;
            return (ulong)((long)BaseAddress + offset);
        }
    }

    public void Advance() {
        _totalConsumed++;
        for (int d = 0; d < Dimensions.Length; d++) {
            Indices[d]++;
            if (Indices[d] < Dimensions[d].Count) return;
            Indices[d] = 0;
        }
    }
}
