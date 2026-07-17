using System.Runtime.InteropServices;

namespace Mechanism.RtlFu;

/// <summary>
///     Loads a Verilator-compiled RTL functional unit wrapped by
///     <c>native/RtlFu/rtl_fu_shim.cpp</c> and drives it through
///     <c>rtl_execute</c> via P/Invoke, so a cycle-accurate RTL model (e.g. a Chisel
///     divider) can replace the C# functional/latency model for one FU while the
///     surrounding pipeline stays unchanged — the RTL counterpart of
///     <see cref="BranchPredictModels.CbpFfiPredictor" />.
///     <para>
///         Each call runs one operation to completion inside the shim and reports the
///         observed cycle count alongside the result; <see cref="RtlBackedExecutor" />
///         forwards that count to the pipeline as the instruction's FU latency.
///     </para>
///     <para>
///         Not thread-safe: one verilated model backs each instance and rtl_execute
///         clocks its state machine — create one unit per pipeline/thread (parallel
///         config sweeps invoke the mechanism factory once per worker thread).
///     </para>
///     <para>
///         Desktop-only: the native library is loaded with <see cref="NativeLibrary" />,
///         which requires <c>dlopen</c>/<c>LoadLibrary</c> — unavailable under
///         browser-wasm (FaceWeb).
///     </para>
/// </summary>
public sealed unsafe class RtlFfiFunctionalUnit : IDisposable {
    private readonly delegate* unmanaged[Cdecl]<void*, void> _destroy;
    private readonly delegate* unmanaged[Cdecl]<void*, uint, uint, uint, uint*, int*, int> _execute;
    private readonly void* _handle;
    private readonly nint _library;
    private bool _disposed;

    /// <summary>
    ///     Loads the native shim library at <paramref name="libraryPath" /> and constructs
    ///     (and resets) the wrapped verilated model.
    /// </summary>
    /// <exception cref="DllNotFoundException">The library could not be loaded.</exception>
    public RtlFfiFunctionalUnit(string libraryPath) {
        _library = NativeLibrary.Load(libraryPath);
        var create = (delegate* unmanaged[Cdecl]<void*>)NativeLibrary.GetExport(_library, "rtl_create");
        _destroy = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(_library, "rtl_destroy");
        _execute =
            (delegate* unmanaged[Cdecl]<void*, uint, uint, uint, uint*, int*, int>)NativeLibrary.GetExport(
                _library, "rtl_execute"
            );
        _handle = create();
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _destroy(_handle);
        NativeLibrary.Free(_library);
    }

    /// <summary>
    ///     Runs one operation on the RTL model and returns its result together with the
    ///     cycle count from request acceptance until the result was valid.
    /// </summary>
    /// <exception cref="InvalidOperationException">The model hung (never became ready or never produced a result).</exception>
    public (uint Result, int Cycles) Execute(uint op, uint a, uint b) {
        uint result;
        int cycles;
        int rc = _execute(_handle, op, a, b, &result, &cycles);
        if (rc != 0)
            throw new InvalidOperationException(
                $"RTL functional unit did not complete (op={op}, a=0x{a:X8}, b=0x{b:X8})."
            );
        return (result, cycles);
    }
}
