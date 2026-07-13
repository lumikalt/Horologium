using System.Runtime.InteropServices;

namespace Mechanism.BranchPredictModels;

/// <summary>
///     Loads a CBP-3/CBP-5 (2016)-style <c>class PREDICTOR</c> submission compiled to a native
///     shared library (see <c>native/CbpShim/</c>) and drives it through
///     <c>GetPrediction</c>/<c>UpdatePredictor</c> via P/Invoke, so third-party CBP predictors can
///     run inside Horologium without hand-porting.
///     <para>
///         The shim's C ABI has no target field — CBP predictors only report taken/not-taken. This
///         predictor caches the last observed target per PC (populated on <see cref="Update" />,
///         the same convention <see cref="CorrelatedPredictor" /> uses for its BTB) and falls back
///         to <c>pc + 4</c> when not taken or previously unseen.
///     </para>
///     <para>
///         Desktop-only: the native library is loaded with <see cref="NativeLibrary" />, which
///         requires <c>dlopen</c>/<c>LoadLibrary</c> — unavailable under browser-wasm (FaceWeb).
///     </para>
/// </summary>
public sealed unsafe class CbpFfiPredictor : IBranchPredictor, IDisposable {
    private readonly delegate* unmanaged[Cdecl]<void*, void> _destroy;
    private readonly void* _handle;
    private readonly nint _library;
    private readonly Dictionary<ulong, bool> _pendingPrediction = new();
    private readonly delegate* unmanaged[Cdecl]<void*, ulong, int> _predict;
    private readonly delegate* unmanaged[Cdecl]<void*, ulong, int, int, ulong, void> _update;
    private readonly Dictionary<ulong, ulong> _lastTarget = new();
    private bool _disposed;

    /// <summary>
    ///     Loads the native shim library at <paramref name="libraryPath" /> and constructs the
    ///     wrapped <c>PREDICTOR</c> instance.
    /// </summary>
    /// <exception cref="DllNotFoundException">The library could not be loaded.</exception>
    public CbpFfiPredictor(string libraryPath) {
        _library = NativeLibrary.Load(libraryPath);
        var create = (delegate* unmanaged[Cdecl]<void*>)NativeLibrary.GetExport(_library, "cbp_create");
        _destroy = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(_library, "cbp_destroy");
        _predict = (delegate* unmanaged[Cdecl]<void*, ulong, int>)NativeLibrary.GetExport(_library, "cbp_predict");
        _update =
            (delegate* unmanaged[Cdecl]<void*, ulong, int, int, ulong, void>)NativeLibrary.GetExport(
                _library, "cbp_update"
            );
        _handle = create();
    }

    /// <inheritdoc />
    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _destroy(_handle);
        NativeLibrary.Free(_library);
    }

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        bool taken = _predict(_handle, pc) != 0;
        _pendingPrediction[pc] = taken;
        ulong target = taken && _lastTarget.TryGetValue(pc, out ulong t) ? t : pc + 4;
        return new BranchPrediction(taken, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        bool predDir = _pendingPrediction.Remove(pc, out bool cached) && cached;
        if (taken) _lastTarget[pc] = actualTarget;
        _update(_handle, pc, taken ? 1 : 0, predDir ? 1 : 0, actualTarget);
    }
}