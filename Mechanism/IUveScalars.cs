namespace Mechanism;

/// <summary>
/// Pipeline-visible face of the UVE scalar accumulator registers (u0–u31).
/// The pipeline injects stream-consumed values here before calling the executor,
/// and syncs exhaustion state for branch ops. Non-UVE ISAs return null from
/// <see cref="IArchState.UveScalars"/>.
/// </summary>
public interface IUveScalars {
    /// <summary>Returns the scalar accumulator value for stream <paramref name="uid"/>.</summary>
    float GetScalar(int uid);

    /// <summary>Sets the scalar accumulator for stream <paramref name="uid"/> to <paramref name="value"/>.</summary>
    void SetScalar(int uid, float value);

    /// <summary>Returns true when stream <paramref name="uid"/> is exhausted.</summary>
    bool GetStreamDone(int uid);

    /// <summary>Sets the exhaustion flag for stream <paramref name="uid"/>.</summary>
    void SetStreamDone(int uid, bool done);

    /// <summary>
    /// Returns true when dimension <paramref name="dim"/> of stream <paramref name="streamId"/>
    /// completed its pass on the most recent consume (i.e. that dimension's index wrapped).
    /// </summary>
    bool GetDimDone(int streamId, int dim);

    /// <summary>Sets the dimension-done flag for dimension <paramref name="dim"/> of stream <paramref name="streamId"/>.</summary>
    void SetDimDone(int streamId, int dim, bool done);
}