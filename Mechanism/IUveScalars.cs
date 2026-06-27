namespace Mechanism;

/// <summary>
/// Pipeline-visible face of the UVE scalar accumulator registers (u0–u31).
/// The pipeline injects stream-consumed values here before calling the executor,
/// and syncs exhaustion state for branch ops. Non-UVE ISAs return null from
/// <see cref="IArchState.UveScalars"/>.
/// </summary>
public interface IUveScalars {
    float GetScalar(int uid);
    void SetScalar(int uid, float value);
    bool GetStreamDone(int uid);
    void SetStreamDone(int uid, bool done);
}
