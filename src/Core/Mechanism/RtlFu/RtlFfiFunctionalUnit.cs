#region

using System.Runtime.InteropServices;
using Mechanism.BranchPred;

#endregion

namespace Mechanism.RtlFu;

/// <summary>
///     Loads a Verilator-compiled RTL functional unit wrapped by
///     <c>native/RtlFu/rtl_fu_shim.cpp</c> and drives it through
///     <c>rtl_execute</c> via P/Invoke, so a cycle-accurate RTL model (e.g. a Chisel
///     divider) can replace the C# functional/latency model for one FU while the
///     surrounding pipeline stays unchanged — the RTL counterpart of
///     <see cref="CbpFfiBp" />.
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
///         browser-wasm.
///     </para>
/// </summary>
public sealed unsafe class RtlFfiFunctionalUnit : IDisposable {
    private readonly delegate* unmanaged[Cdecl]<void*, void> _destroy;
    private readonly delegate* unmanaged[Cdecl]<void*, uint, uint, uint, uint*, int*, int> _execute;
    private readonly delegate* unmanaged[Cdecl]<void*, uint, uint, uint, uint*, uint*, int*, int> _executeFlags;
    private readonly void* _handle;
    private readonly nint _library;
    private bool _disposed;

    /// <summary>
    ///     Loads the native shim library at <paramref name="libraryPath" /> and constructs
    ///     (and resets) the wrapped verilated model. A library exposes <c>rtl_execute</c>
    ///     (plain units, rtl_fu_shim), <c>rtl_execute_flags</c> (flag-reporting FP units,
    ///     rtl_fpu_shim), or both.
    /// </summary>
    /// <exception cref="DllNotFoundException">The library could not be loaded.</exception>
    /// <exception cref="ArgumentException">The library exposes neither execute export.</exception>
    public RtlFfiFunctionalUnit(string libraryPath) {
        _library = NativeLibrary.Load(libraryPath);
        var create = (delegate* unmanaged[Cdecl]<void*>)NativeLibrary.GetExport(_library, "rtl_create");
        _destroy = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(_library, "rtl_destroy");
        _execute = NativeLibrary.TryGetExport(_library, "rtl_execute", out nint plain)
            ? (delegate* unmanaged[Cdecl]<void*, uint, uint, uint, uint*, int*, int>)plain
            : null;
        _executeFlags = NativeLibrary.TryGetExport(_library, "rtl_execute_flags", out nint flagged)
            ? (delegate* unmanaged[Cdecl]<void*, uint, uint, uint, uint*, uint*, int*, int>)flagged
            : null;
        if (_execute is null && _executeFlags is null) {
            NativeLibrary.Free(_library);
            throw new ArgumentException(
                $"'{libraryPath}' exposes neither rtl_execute nor rtl_execute_flags — "
              + "not an RTL functional-unit library (see native/RtlFu/README.md)."
            );
        }

        _handle = create();
    }

    /// <summary>Destroys the verilated model and unloads the native library.</summary>
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
    /// <exception cref="InvalidOperationException">
    ///     The library has no <c>rtl_execute</c> export, or the model hung.
    /// </exception>
    public (uint Result, int Cycles) Execute(uint op, uint a, uint b) {
        if (_execute is null)
            throw new InvalidOperationException(
                "This RTL unit only exposes rtl_execute_flags — use ExecuteWithFlags."
            );
        uint result;
        int cycles;
        int rc = _execute(_handle, op, a, b, &result, &cycles);
        if (rc != 0)
            throw new InvalidOperationException(
                $"RTL functional unit did not complete (op={op}, a=0x{a:X8}, b=0x{b:X8})."
            );
        return (result, cycles);
    }

    /// <summary>
    ///     Runs one operation on a flag-reporting RTL model (rtl_fpu_shim), returning the
    ///     result, the IEEE exception flags it raised (fflags bit layout: NV|DZ|OF|UF|NX),
    ///     and the cycle count.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The library has no <c>rtl_execute_flags</c> export, or the model hung.
    /// </exception>
    public (uint Result, uint Flags, int Cycles) ExecuteWithFlags(uint op, uint a, uint b) {
        if (_executeFlags is null)
            throw new InvalidOperationException(
                "This RTL unit does not expose rtl_execute_flags — use Execute."
            );
        uint result, flags;
        int cycles;
        int rc = _executeFlags(_handle, op, a, b, &result, &flags, &cycles);
        if (rc != 0)
            throw new InvalidOperationException(
                $"RTL functional unit did not complete (op={op}, a=0x{a:X8}, b=0x{b:X8})."
            );
        return (result, flags, cycles);
    }
}