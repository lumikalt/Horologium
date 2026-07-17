using System.Runtime.InteropServices;

namespace Mechanism.RtlFu;

/// <summary>
///     Loads a Verilator-compiled RTL branch predictor wrapped by
///     <c>native/RtlFu/rtl_bp_shim.cpp</c> and drives it through
///     <c>rtl_bp_predict</c>/<c>rtl_bp_update</c> via P/Invoke — the branch-predictor
///     counterpart of <see cref="RtlFfiFunctionalUnit" />, letting a synthesizable
///     predictor design (e.g. the Chisel gshare in <c>native/RtlFu/GshareBp.scala</c>)
///     stand in for a C# <see cref="IBranchPredictor" />.
///     <para>
///         Prediction is a combinational table read in the model (no clock edge);
///         Update consumes one clock edge, matching commit-time training. Like
///         <see cref="BranchPredictModels.CbpFfiPredictor" />, the RTL keeps only
///         committed global history — the speculative-history hooks use their
///         interface defaults (no-ops), which is architecturally safe on every train.
///     </para>
///     <para>
///         Not thread-safe (one verilated model per instance) and desktop-only
///         (<see cref="NativeLibrary" />) — same constraints as
///         <see cref="RtlFfiFunctionalUnit" />.
///     </para>
/// </summary>
public sealed unsafe class RtlFfiBranchPredictor : IBranchPredictor, IDisposable {
    private readonly delegate* unmanaged[Cdecl]<void*, void> _destroy;
    private readonly void* _handle;
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<void*, ulong, int*, ulong*, void> _predict;
    private readonly delegate* unmanaged[Cdecl]<void*, ulong, int, ulong, void> _update;
    private bool _disposed;

    /// <summary>
    ///     Loads the native shim library at <paramref name="libraryPath" /> and constructs
    ///     (and resets) the wrapped verilated model.
    /// </summary>
    /// <exception cref="DllNotFoundException">The library could not be loaded.</exception>
    public RtlFfiBranchPredictor(string libraryPath) {
        _library = NativeLibrary.Load(libraryPath);
        var create = (delegate* unmanaged[Cdecl]<void*>)NativeLibrary.GetExport(_library, "rtl_bp_create");
        _destroy = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(_library, "rtl_bp_destroy");
        _predict =
            (delegate* unmanaged[Cdecl]<void*, ulong, int*, ulong*, void>)NativeLibrary.GetExport(
                _library, "rtl_bp_predict"
            );
        _update =
            (delegate* unmanaged[Cdecl]<void*, ulong, int, ulong, void>)NativeLibrary.GetExport(
                _library, "rtl_bp_update"
            );
        _handle = create();
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _destroy(_handle);
        NativeLibrary.Free(_library);
    }

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        int taken;
        ulong target;
        _predict(_handle, pc, &taken, &target);
        // Fall through on not-taken, exactly like the C# table predictors; a taken
        // prediction uses the model's BTB output as-is (including a cold-entry 0).
        return new BranchPrediction(taken != 0, taken != 0 ? target : pc + 4);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) =>
        _update(_handle, pc, taken ? 1 : 0, actualTarget);
}
