#region

using System.Runtime.InteropServices;

#endregion

namespace Mechanism.BranchPredictModels;

/// <summary>
///     Loads a CBP2025/CBP-NG (AmpereComputing/cbp-ng) predictor submission compiled to a native
///     shared library (see <c>native/CbpNgShim/</c>) and drives it through
///     <c>cbpng_predict</c>/<c>cbpng_update</c> via P/Invoke.
///     <para>
///         Unlike the CBP-3/5 shim (<see cref="CbpFfiPredictor" />), cbp-ng's harcom-based
///         predictors keep per-block state in native registers written by <c>predict1</c>/
///         <c>predict2</c> and read back by <c>update_cycle</c>. A second prediction block
///         started before the first one's <see cref="Update" /> fires clobbers that state. This
///         adapter does not defend against that itself — it is safe only when the caller
///         guarantees a branch's <see cref="Update" /> fires before the next branch's
///         <see cref="Predict" /> is issued, which holds for <c>SingleCycleTrain</c> (no branch
///         prediction at all — instructions resolve one at a time) but not <c>FiveStageTrain</c>
///         (IF/ID/EX can each hold an unresolved branch simultaneously) or <c>OooeTrain</c>/
///         <c>CprTrain</c> (many outstanding predictions). Use
///         <see cref="CbpNgCommitDrivenPredictor" /> for any pipeline that overlaps fetch with
///         commit — see README.md "CBP2025/CBP-NG predictor integration" for why a generic
///         snapshot/restore isn't possible instead.
///     </para>
///     <para>
///         The shim's C ABI has no target field — cbp-ng predictors only report taken/not-taken.
///         This predictor uses the statically-decoded <c>knownTarget</c> when
///         available (the common case for PC-relative branches) and otherwise falls back to the
///         last observed target per PC (populated on <see cref="Update" />), then <c>pc + 4</c>
///         for a previously unseen indirect branch — the same convention <see cref="CbpFfiPredictor" />
///         uses.
///     </para>
///     <para>
///         Desktop-only: the native library is loaded with <see cref="NativeLibrary" />, which
///         requires <c>dlopen</c>/<c>LoadLibrary</c> — unavailable under browser-wasm (FaceWeb).
///     </para>
/// </summary>
public sealed unsafe class CbpNgFfiPredictor : IBranchKindAwareBranchPredictor, IDisposable {
    private readonly delegate* unmanaged[Cdecl]<void*, void> _destroy;
    private readonly void* _handle;
    private readonly Dictionary<ulong, ulong> _lastTarget = new();
    private readonly nint _library;
    private readonly Dictionary<ulong, BranchKind> _pendingKind = new();
    private readonly delegate* unmanaged[Cdecl]<void*, ulong, int> _predict;
    private readonly delegate* unmanaged[Cdecl]<void*, ulong, int, ulong, int, void> _update;
    private bool _disposed;

    /// <summary>
    ///     Loads the native shim library at <paramref name="libraryPath" /> and constructs the
    ///     wrapped cbp-ng predictor instance.
    /// </summary>
    /// <exception cref="DllNotFoundException">The library could not be loaded.</exception>
    public CbpNgFfiPredictor(string libraryPath) {
        _library = NativeLibrary.Load(libraryPath);
        var create = (delegate* unmanaged[Cdecl]<void*>)NativeLibrary.GetExport(_library, "cbpng_create");
        _destroy = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(_library, "cbpng_destroy");
        _predict =
            (delegate* unmanaged[Cdecl]<void*, ulong, int>)NativeLibrary.GetExport(_library, "cbpng_predict");
        _update =
            (delegate* unmanaged[Cdecl]<void*, ulong, int, ulong, int, void>)NativeLibrary.GetExport(
                _library, "cbpng_update"
            );
        _handle = create();
    }

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        bool taken = _predict(_handle, pc) != 0;
        ulong target = knownTarget.HasValue
            ? knownTarget.Value
            : _lastTarget.TryGetValue(pc, out ulong t)
                ? t
                : pc + 4;
        return new BranchPrediction(taken, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        BranchKind kind = _pendingKind.Remove(pc, out BranchKind cached) ? cached : BranchKind.None;
        if (taken) _lastTarget[pc] = actualTarget;
        _update(_handle, pc, taken ? 1 : 0, actualTarget, (int)kind);
    }

    /// <inheritdoc />
    public void NotifyBranchKind(ulong pc, BranchKind kind) => _pendingKind[pc] = kind;

    /// <inheritdoc />
    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _destroy(_handle);
        NativeLibrary.Free(_library);
    }
}