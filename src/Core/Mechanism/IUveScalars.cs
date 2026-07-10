namespace Mechanism;

/// <summary>
/// Pipeline-visible face of the UVE vector accumulator registers (u0–u31).
/// The pipeline injects stream-consumed lane values here before calling the executor,
/// and syncs exhaustion state for branch ops. Non-UVE ISAs return null from
/// <see cref="IArchState.UveScalars"/>.
/// </summary>
public interface IUveScalars {
    /// <summary>
    /// Returns the lane-0 value of u-register <paramref name="uid"/> as a float32.
    /// Used by executor methods that read the single accumulated result.
    /// </summary>
    float GetScalar(int uid);

    /// <summary>
    /// Loads a scalar (single-element) value into u-register <paramref name="uid"/> from a stream.
    /// Sets mode=Scalar, validElements=1, and records the stream's pm bit.
    /// </summary>
    void SetScalarRaw(int uid, uint raw, bool merging);

    /// <summary>
    /// Loads up to <paramref name="vl"/> raw 32-bit lane values into u-register <paramref name="uid"/>
    /// from a vector-mode stream. Sets mode=Vector, validElements=vl, and records pm.
    /// </summary>
    void SetVectorRaw(int uid, ReadOnlySpan<uint> values, int vl, bool merging);

    /// <summary>Returns the raw 32-bit bits of lane <paramref name="lane"/> of u-register <paramref name="uid"/>.</summary>
    uint GetRaw(int uid, int lane) => 0;

    /// <summary>Returns true when u-register <paramref name="uid"/> is in vector mode.</summary>
    bool IsVectorReg(int uid) => false;

    /// <summary>Returns the valid lane count for u-register <paramref name="uid"/> (1 in scalar mode).</summary>
    int GetValidElements(int uid) => 1;

    /// <summary>Returns true when u-register <paramref name="uid"/> uses merging predication (pm=1).</summary>
    bool IsRegMerging(int uid) => false;

    /// <summary>Returns true when stream <paramref name="uid"/> is exhausted.</summary>
    bool GetStreamDone(int uid);

    /// <summary>Sets the exhaustion flag for stream <paramref name="uid"/>.</summary>
    void SetStreamDone(int uid, bool done);

    /// <summary>
    /// Returns true when dimension <paramref name="dim"/> of stream <paramref name="streamId"/>
    /// completed its pass on the most recent consume.
    /// </summary>
    bool GetDimDone(int streamId, int dim);

    /// <summary>Sets the dimension-done flag for dimension <paramref name="dim"/> of stream <paramref name="streamId"/>.</summary>
    void SetDimDone(int streamId, int dim, bool done);

    /// <summary>
    /// Active vector length (number of elements per vector operation).
    /// Set by ss.setvl; 0 = use natural VL (VLEN/elementBytes).
    /// </summary>
    int VectorLength => 0;
}
