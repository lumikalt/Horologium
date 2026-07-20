#region

using System.Runtime.InteropServices;
using Mechanism.BranchPred;

#endregion

namespace Mechanism.RtlFu;

/// <summary>
///     Loads a Verilator-compiled RTL branch predictor that manages its own speculative
///     global history (wrapped by <c>native/RtlFu/rtl_hbp_shim.cpp</c>) and drives the
///     full <see cref="IBranchPredictor" /> contract against it — including
///     <see cref="SpeculativeHistoryUpdate" />, flush recovery, and per-branch history
///     checkpoints for out-of-order partial squashes. The first such model is the Chisel
///     L-TAGE in <c>native/RtlFu/LTageBp.scala</c>, which mirrors the C#
///     <see cref="LTageBp" /> bit-for-bit.
///     <para>
///         The checkpoint is the model's working history value itself (TAGE-family folded
///         indices derive from it), read combinationally through <c>rtl_hbp_history</c>
///         and carried in <see cref="BranchHistoryCheckpoint.Global" /> — the same
///         convention the C# TAGE family uses, so no state lives outside the model.
///     </para>
///     <para>
///         Direction-only: the RTL predicts taken/not-taken, and this wrapper keeps the
///         target BTB as a C# dictionary with the exact semantics of the C# TAGE family
///         (written on taken updates, fall-through otherwise) — an unbounded dictionary
///         is not expressible in RTL.
///     </para>
///     <para>
///         Not thread-safe (one verilated model per instance) and desktop-only
///         (<see cref="NativeLibrary" />) — same constraints as the other RTL FFI wrappers.
///     </para>
/// </summary>
public sealed unsafe class RtlFfiHistoryBp : IBranchPredictor, IDisposable {
    private readonly Dictionary<ulong, ulong> _btb = new();
    private readonly delegate* unmanaged[Cdecl]<void*, void> _destroy;
    private readonly void* _handle;
    private readonly delegate* unmanaged[Cdecl]<void*, ulong> _history;
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<void*, ulong, int> _predict;
    private readonly delegate* unmanaged[Cdecl]<void*, void> _recover;
    private readonly delegate* unmanaged[Cdecl]<void*, ulong, int, void> _restore;
    private readonly delegate* unmanaged[Cdecl]<void*, int, void> _specUpdate;
    private readonly delegate* unmanaged[Cdecl]<void*, ulong, int, void> _update;
    private bool _disposed;

    /// <summary>
    ///     Loads the native shim library at <paramref name="libraryPath" /> and constructs
    ///     (and resets) the wrapped verilated model.
    /// </summary>
    /// <exception cref="DllNotFoundException">The library could not be loaded.</exception>
    public RtlFfiHistoryBp(string libraryPath) {
        _library = NativeLibrary.Load(libraryPath);
        var create = (delegate* unmanaged[Cdecl]<void*>)NativeLibrary.GetExport(_library, "rtl_hbp_create");
        _destroy = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(_library, "rtl_hbp_destroy");
        _predict = (delegate* unmanaged[Cdecl]<void*, ulong, int>)NativeLibrary.GetExport(
            _library, "rtl_hbp_predict"
        );
        _update = (delegate* unmanaged[Cdecl]<void*, ulong, int, void>)NativeLibrary.GetExport(
            _library, "rtl_hbp_update"
        );
        _specUpdate = (delegate* unmanaged[Cdecl]<void*, int, void>)NativeLibrary.GetExport(
            _library, "rtl_hbp_spec_update"
        );
        _recover = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(
            _library, "rtl_hbp_recover"
        );
        _history = (delegate* unmanaged[Cdecl]<void*, ulong>)NativeLibrary.GetExport(
            _library, "rtl_hbp_history"
        );
        _restore = (delegate* unmanaged[Cdecl]<void*, ulong, int, void>)NativeLibrary.GetExport(
            _library, "rtl_hbp_restore"
        );
        _handle = create();
    }

    /// <inheritdoc />
    public BranchPrediction Predict(ulong pc, (ulong Value, bool HasValue) knownTarget = default) {
        bool taken = _predict(_handle, pc) != 0;
        ulong target = taken
            ? _btb.TryGetValue(pc, out ulong t) ? t : pc + 4
            : pc + 4;
        return new BranchPrediction(taken, target);
    }

    /// <inheritdoc />
    public void Update(ulong pc, bool taken, ulong actualTarget) {
        if (taken) _btb[pc] = actualTarget;
        _update(_handle, pc, taken ? 1 : 0);
    }

    /// <inheritdoc />
    public void SpeculativeHistoryUpdate(ulong pc, bool predictedTaken) =>
        _specUpdate(_handle, predictedTaken ? 1 : 0);

    /// <inheritdoc />
    public void RecoverSpeculativeHistory() => _recover(_handle);

    /// <inheritdoc />
    public BranchHistoryCheckpoint CaptureHistory(ulong pc) => new(_history(_handle));

    /// <inheritdoc />
    public void RestoreHistory(in BranchHistoryCheckpoint checkpoint, ulong pc, bool actualTaken) =>
        _restore(_handle, checkpoint.Global, actualTaken ? 1 : 0);

    /// <summary>Destroys the verilated model and unloads the native library.</summary>
    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _destroy(_handle);
        NativeLibrary.Free(_library);
    }
}

/// <summary>
///     Loads an RTL branch-predictor shared library, detecting which shim ABI it exposes:
///     <c>rtl_bp_*</c> (plain predictor, e.g., the gshare) →
///     <see cref="RtlFfiBp" />; <c>rtl_hbp_*</c> (speculative-history
///     predictor, e.g., the L-TAGE) → <see cref="RtlFfiHistoryBp" />.
///     Lets one <c>rtl_bp_plugin</c> config entry serve both kinds.
/// </summary>
public static class RtlBranchPredictorLoader {
    /// <summary>Loads and wraps the predictor library at <paramref name="libraryPath" />.</summary>
    /// <exception cref="ArgumentException">The library exposes neither predictor ABI.</exception>
    public static IBranchPredictor Load(string libraryPath) {
        nint lib = NativeLibrary.Load(libraryPath);
        try {
            if (NativeLibrary.TryGetExport(lib, "rtl_bp_create", out _)) return new RtlFfiBp(libraryPath);
            if (NativeLibrary.TryGetExport(lib, "rtl_hbp_create", out _)) return new RtlFfiHistoryBp(libraryPath);
            throw new ArgumentException(
                $"'{libraryPath}' exposes neither rtl_bp_create nor rtl_hbp_create — "
              + "not an RTL branch-predictor library (see native/RtlFu/README.md)."
            );
        }
        finally { NativeLibrary.Free(lib); }
    }
}