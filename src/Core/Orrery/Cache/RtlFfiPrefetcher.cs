using System.Runtime.InteropServices;

namespace Orrery.Cache;

/// <summary>
///     Loads a Verilator-compiled RTL cache prefetcher wrapped by
///     <c>native/RtlFu/rtl_pf_shim.cpp</c> and drives it through <c>rtl_pf_access</c>
///     via P/Invoke — the prefetcher counterpart of the other RTL substitutions,
///     letting a synthesizable prefetcher (e.g. the Chisel RPT stride prefetcher in
///     <c>native/RtlFu/StridePf.scala</c>) stand in for a C# <see cref="IPrefetcher" />.
///     <para>
///         Each demand access is presented to the model once: the prefetch decision is
///         read combinationally (the model applies post-update semantics internally)
///         and the prediction-table update commits on one clock edge.
///     </para>
///     <para>
///         Not thread-safe (one verilated model per instance) and desktop-only
///         (<see cref="NativeLibrary" />) — same constraints as the other RTL FFI
///         wrappers; each memory port builds its own instance via the MemoryConfig
///         factory.
///     </para>
/// </summary>
public sealed unsafe class RtlFfiPrefetcher : IPrefetcher, IDisposable {
    private readonly delegate* unmanaged[Cdecl]<void*, ulong, ulong, int, ulong*, int, int> _access;
    private readonly delegate* unmanaged[Cdecl]<void*, void> _destroy;
    private readonly void* _handle;
    private readonly nint _library;
    private bool _disposed;

    /// <summary>
    ///     Loads the native shim library at <paramref name="libraryPath" /> and constructs
    ///     (and resets) the wrapped verilated model.
    /// </summary>
    /// <exception cref="DllNotFoundException">The library could not be loaded.</exception>
    public RtlFfiPrefetcher(string libraryPath) {
        _library = NativeLibrary.Load(libraryPath);
        var create = (delegate* unmanaged[Cdecl]<void*>)NativeLibrary.GetExport(_library, "rtl_pf_create");
        _destroy = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(_library, "rtl_pf_destroy");
        _access =
            (delegate* unmanaged[Cdecl]<void*, ulong, ulong, int, ulong*, int, int>)NativeLibrary.GetExport(
                _library, "rtl_pf_access"
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
    public int OnAccess(ulong pc, ulong address, bool wasHit, Span<ulong> targets) {
        fixed (ulong* p = targets) { return _access(_handle, pc, address, wasHit ? 1 : 0, p, targets.Length); }
    }
}