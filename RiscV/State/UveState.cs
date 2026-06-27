using Mechanism;

namespace RiscV.State;

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
/// Mutable cursor for one affine store stream (ss.st.*).
/// Tracks the current write position so consecutive so.a.* outputs land at
/// the correct addresses.
/// </summary>
public sealed class UveStoreStream {
    public ulong BaseAddress;
    public int ElementBytes;
    public long Count;
    public long Stride;
    public long NextIndex;

    public bool IsExhausted => NextIndex >= Count;

    public ulong CurrentAddress => (ulong)((long)BaseAddress + NextIndex * Stride);

    public void Advance() => NextIndex++;
}
